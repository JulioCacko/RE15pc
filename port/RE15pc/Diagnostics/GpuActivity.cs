using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;

namespace RE15pc.Diagnostics;

/// <summary>
/// Records what the guest actually asks the GPU to draw.
/// </summary>
/// <remarks>
/// The framebuffer being black has two very different causes: the guest draws and the
/// rasteriser fails, or the guest never draws at all. Guest memory changing and interrupts
/// arriving do not distinguish them, and there is no program counter to sample, so the
/// answer has to come from the GPU interface itself.
///
/// <c>GpuRaster</c> dispatches a <c>RenderPrimEvent</c> for every primitive before it is
/// drawn, carrying the vertices, the texture flags and the current drawing area. Counting
/// and bounding those says directly whether anything is being drawn and where.
///
/// The drawing area is the most valuable field. A mismatched drawing area and display
/// origin is the classic double-buffering fault: the guest draws into one half of VRAM while
/// the display reads the other, which produces a screen that is exactly, uniformly wrong.
/// </remarks>
public static class GpuActivity
{
    private static readonly object Gate = new();

    private static bool _attached;
    private static bool _forceSoftware;
    private static long _prims;
    private static long _textured;
    private static long _semiTransparent;
    private static long _skipped;
    private static int _minX = int.MaxValue, _maxX = int.MinValue;
    private static int _minY = int.MaxValue, _maxY = int.MinValue;
    private static readonly HashSet<string> DrawAreas = [];
    private static readonly HashSet<int> Cluts = [];
    private static readonly HashSet<int> TexPages = [];

    /// <summary>Primitives whose vertices all land inside a 320x480 framebuffer.</summary>
    private static long _onScreen;

    /// <summary>Primitives with a vertex outside the whole 1024x512 VRAM space.</summary>
    private static long _offVram;

    /// <summary>Primitives with all three vertices at the same point.</summary>
    private static long _degenerate;

    private static int _onMinX = int.MaxValue, _onMaxX = int.MinValue;
    private static int _onMinY = int.MaxValue, _onMaxY = int.MinValue;

    /// <summary>
    /// Primitives whose vertices sit in the SECOND buffer's screen space (y 0..239) while the
    /// drawing area / clip rectangle is the second buffer (y 240..479).
    ///
    /// This is the measurement that decides whether the drawing offset is being applied to the
    /// vertices or only to the clip. On hardware a vertex at screen (0,0) with offset (0,240)
    /// rasterises at VRAM (0,240), so screen-space vertices against a VRAM-space clip rectangle
    /// means every primitive is clipped away.
    /// </summary>
    private static long _screenSpaceVsVramClip;

    /// <summary>The same, but where the vertices are already in VRAM space.</summary>
    private static long _vramSpaceVsVramClip;

    /// <summary>Vertices in the first buffer's screen space (y 0..239) under any clip.</summary>
    private static long _anyScreenSpace;

    /// <summary>Draw-area top values seen, with counts.</summary>
    private static readonly Dictionary<int, long> DrawTops = [];

    public static long Primitives => Interlocked.Read(ref _prims);
    public static long Textured => Interlocked.Read(ref _textured);

    /// <summary>
    /// Attaches the recorder.
    /// </summary>
    /// <param name="forceSoftwareRendering">
    /// When true, turns the GPU HLE off so <c>GpuRaster</c>'s own rasteriser draws into the
    /// software shadow instead. This is a diagnostic that isolates the GL path: the software
    /// path is self-contained - <c>GpuCommands.CopyVramToVram</c> performs the shadow copy
    /// itself and only *notifies* the HLE afterwards - so with the HLE off the shadow holds a
    /// complete, independently produced frame.
    ///
    /// It has to be re-asserted every frame, because <c>HostWindow</c> assigns
    /// <c>GpuHle.Active</c> from backend readiness. <c>VSyncEvent</c> fires on the game thread
    /// after the frame's presentation work, which is late enough to win for the rest of the
    /// frame.
    /// </param>
    public static void Attach(bool forceSoftwareRendering = false)
    {
        if (_attached) return;
        _attached = true;

        Event.AddListener<RenderPrimEvent>(OnPrimitive);

        if (!forceSoftwareRendering) return;

        _forceSoftware = true;
        GpuHle.Active = false;
        Event.AddListener<VSyncEvent>(_ =>
        {
            if (_forceSoftware) GpuHle.Active = false;
        });

        Console.WriteLine("[gpu] forcing the software rasteriser: GPU HLE disabled for this run");
    }

    private static void OnPrimitive(RenderPrimEvent e)
    {
        Interlocked.Increment(ref _prims);
        if (e.Textured) Interlocked.Increment(ref _textured);
        if (e.SemiTransparent) Interlocked.Increment(ref _semiTransparent);
        if (e.Skip) Interlocked.Increment(ref _skipped);

        // Vertices 0..2 only. A quad's fourth vertex is left at zero for triangles, and
        // including it would drag the bounding box to the origin.
        lock (Gate)
        {
            for (var i = 0; i < 3; i++)
            {
                var x = e.X[i];
                var y = e.Y[i];

                if (x < _minX) _minX = x;
                if (x > _maxX) _maxX = x;
                if (y < _minY) _minY = y;
                if (y > _maxY) _maxY = y;
            }

            // Distinct drawing areas seen. More than one means the game is swapping buffers
            // by moving the drawing area, which is how a flip is implemented here.
            DrawAreas.Add($"{e.DrawLeft}..{e.DrawRight} x {e.DrawTop}..{e.DrawBottom}");
            if (Cluts.Count < 16) Cluts.Add(e.Clut);
            if (TexPages.Count < 16) TexPages.Add(e.TexPage);

            // Where the primitives actually are, which is what separates "the guest is
            // drawing a scene and the rasteriser produces black" from "the guest is feeding
            // the FIFO something that is not a scene at all".
            var onScreen = true;
            var offVram = false;

            for (var i = 0; i < 3; i++)
            {
                var x = e.X[i];
                var y = e.Y[i];

                if (x < 0 || x > 319 || y < 0 || y > 479) onScreen = false;
                if (x < 0 || x > 1023 || y < 0 || y > 511) offVram = true;
            }

            if (offVram) _offVram++;

            if (onScreen)
            {
                _onScreen++;
                for (var i = 0; i < 3; i++)
                {
                    if (e.X[i] < _onMinX) _onMinX = e.X[i];
                    if (e.X[i] > _onMaxX) _onMaxX = e.X[i];
                    if (e.Y[i] < _onMinY) _onMinY = e.Y[i];
                    if (e.Y[i] > _onMaxY) _onMaxY = e.Y[i];
                }
            }

            if (e.X[0] == e.X[1] && e.X[1] == e.X[2] && e.Y[0] == e.Y[1] && e.Y[1] == e.Y[2])
                _degenerate++;

            // Vertex space versus clip space, which is the whole question.
            var maxY = Math.Max(e.Y[0], Math.Max(e.Y[1], e.Y[2]));
            var minY = Math.Min(e.Y[0], Math.Min(e.Y[1], e.Y[2]));
            var allScreenY = maxY < 240;
            var allVramY = minY >= 240;

            if (allScreenY) _anyScreenSpace++;

            var clipIsSecondBuffer = e.DrawTop >= 240;

            if (clipIsSecondBuffer && allScreenY) _screenSpaceVsVramClip++;
            else if (clipIsSecondBuffer && allVramY) _vramSpaceVsVramClip++;

            if (!DrawTops.TryGetValue(e.DrawTop, out var n)) n = 0;
            DrawTops[e.DrawTop] = n + 1;
        }
    }

    public static string Describe()
    {
        if (!_attached) return "  gpu activity            : not attached";

        var prims = Primitives;
        if (prims == 0)
            return "  gpu activity            : NO PRIMITIVES DRAWN - the guest never asked the GPU to draw anything";

        string areas;
        int minX, maxX, minY, maxY;

        lock (Gate)
        {
            areas = DrawAreas.Count == 0 ? "(none)" : string.Join(" | ", DrawAreas.OrderBy(a => a));
            minX = _minX;
            maxX = _maxX;
            minY = _minY;
            maxY = _maxY;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  gpu primitives          : {prims} ({Textured} textured, {_semiTransparent} semi-transparent, {_skipped} skipped)");
        sb.AppendLine($"  primitive vertex bounds : x {minX}..{maxX}, y {minY}..{maxY}");
        sb.AppendLine($"  primitives in framebuf  : {_onScreen}" +
                      (_onScreen > 0 ? $" (bounds x {_onMinX}..{_onMaxX}, y {_onMinY}..{_onMaxY})" : ""));
        sb.AppendLine($"  primitives off VRAM     : {_offVram}");
        sb.AppendLine($"  degenerate primitives   : {_degenerate}");
        sb.AppendLine($"  vertices in screen space: {_anyScreenSpace} (max vertex y < 240)");
        sb.AppendLine($"  SCREEN vs VRAM clip     : {_screenSpaceVsVramClip}   <- non-zero means the offset is not reaching the vertices");
        sb.AppendLine($"  VRAM   vs VRAM clip     : {_vramSpaceVsVramClip}");
        sb.AppendLine($"  draw-area tops (top:count): {string.Join(", ", DrawTops.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"))}");

        // The HLE render-target path. A non-zero widescreen margin changes the render target's
        // width and shifts vertices, which is a strong candidate for a blank frame.
        sb.AppendLine($"  gpu hle                 : active={GpuHle.Active}, backend={GpuHle.Backend?.GetType().Name ?? "null"}" +
                      (_forceSoftware ? "  (software rasteriser forced)" : ""));
        sb.AppendLine($"  widescreen              : WideAspect={GpuHle.WideAspect:0.###}, " +
                      $"SourceAspect={GpuHle.SourceAspect:0.###}, WideMargin(320)={GpuHle.WideMargin(320)}");
        sb.AppendLine($"  drawing area(s) seen    : {areas}");
        sb.AppendLine($"  cluts seen              : {Cluts.Count}");
        return sb.ToString().TrimEnd();
    }
}
