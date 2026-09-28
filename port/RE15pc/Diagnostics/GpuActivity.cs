using RecompOne.Runtime;
using RecompOne.Runtime.Assets.Textures;
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

    /// <summary>
    /// When set, primitives are suppressed through <c>RenderPrimEvent.Skip</c>, which
    /// <c>GpuRaster</c> honours before consuming the vertices.
    ///
    /// This separates two very different explanations for a black frame: uploads and VRAM copies
    /// never arriving, versus drawing covering the result. With every draw suppressed the frame
    /// comes out correct, which says the content arrived and something the guest draws is painted
    /// over it - so the interesting question becomes which class of primitive does that.
    ///
    /// Values: empty for none, <c>all</c>, <c>textured</c>, or <c>flat</c> (everything that is not
    /// textured). Suppressing one class at a time identifies the culprit.
    /// </summary>
    public static string SuppressMode = "";
    private static long _textured;
    private static long _semiTransparent;
    private static long _skipped;
    private static int _minX = int.MaxValue, _maxX = int.MinValue;
    private static int _minY = int.MaxValue, _maxY = int.MinValue;
    /// <summary>Primitives discarded because <c>SuppressDraws</c> is set.</summary>
    private static long _suppressed;

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

    /// <summary>
    /// Bounds and semi-transparency of the flat (untextured) primitives, and the same for the
    /// textured ones. The two classes have opposite effects on the frame, so describing them
    /// separately is what says which is which.
    /// </summary>
    private static long _flatCount, _flatSemi;
    private static int _flatMinX = int.MaxValue, _flatMaxX = int.MinValue;
    private static int _flatMinY = int.MaxValue, _flatMaxY = int.MinValue;

    /// <summary>
    /// Texture page and CLUT combinations used by textured primitives that land in a framebuffer,
    /// with counts. <c>RenderPrimEvent.TexPage</c> is hardcoded to 0 in the runtime, so the page is
    /// read from <c>Gpu.ReadStat()</c> instead - and because this listener runs immediately before
    /// the primitive is drawn, that returns the same page the draw will use.
    /// </summary>
    private static readonly Dictionary<(int TPage, int Clut), long> TexturedPages = [];

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
        var skip = SuppressMode switch
        {
            "all" => true,
            "textured" => e.Textured,
            "flat" => !e.Textured,
            _ => false
        };

        if (skip)
        {
            e.Skip = true;
            Interlocked.Increment(ref _suppressed);
        }

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

                // Texture page actually in force for this primitive. ReadStat packs page X in bits
                // 0-3, page Y in bit 4, blend in bits 5-6 and depth in bits 7-8, which is exactly
                // how the runtime assembles PrimFlags.TPage, so reassembling it here reproduces
                // what the backend will be handed.
                if (e.Textured && RecompOne.Runtime.Runtime.Gpu is { } primGpu)
                {
                    var stat = primGpu.ReadStat();
                    var tpage = (int)(stat & 0xF) | (int)((stat >> 4) & 1) << 4
                                | (int)((stat >> 5) & 3) << 5 | (int)((stat >> 7) & 3) << 7;
                    var key = (tpage, e.Clut);

                    if (TexturedPages.Count < 64 || TexturedPages.ContainsKey(key))
                        TexturedPages[key] = TexturedPages.TryGetValue(key, out var c) ? c + 1 : 1;
                }
                else if (!e.Textured)
                {
                    _flatCount++;
                    if (e.SemiTransparent) _flatSemi++;

                    for (var i = 0; i < 3; i++)
                    {
                        if (e.X[i] < _flatMinX) _flatMinX = e.X[i];
                        if (e.X[i] > _flatMaxX) _flatMaxX = e.X[i];
                        if (e.Y[i] < _flatMinY) _flatMinY = e.Y[i];
                        if (e.Y[i] > _flatMaxY) _flatMaxY = e.Y[i];
                    }
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
                      (_forceSoftware ? "  (software rasteriser forced)" : "") +
                      (SuppressMode.Length > 0 ? $"  (drawing suppressed: {SuppressMode}, {_suppressed} primitives skipped)" : ""));
        sb.AppendLine($"  widescreen              : WideAspect={GpuHle.WideAspect:0.###}, " +
                      $"SourceAspect={GpuHle.SourceAspect:0.###}, WideMargin(320)={GpuHle.WideMargin(320)}");
        sb.AppendLine($"  drawing area(s) seen    : {areas}");
        sb.AppendLine($"  cluts seen              : {Cluts.Count}");
        sb.AppendLine($"  flat primitives         : {_flatCount} ({_flatSemi} semi-transparent), " +
                      $"bounds x {(_flatCount > 0 ? _flatMinX : 0)}..{(_flatCount > 0 ? _flatMaxX : 0)}, " +
                      $"y {(_flatCount > 0 ? _flatMinY : 0)}..{(_flatCount > 0 ? _flatMaxY : 0)}");

        // The decisive question: for the texture pages these primitives actually use, does the VRAM
        // region that page resolves to contain any image, or is it empty? An empty source explains
        // a black result with perfect geometry and is the difference between a page-resolution bug
        // and something further down.
        if (TexturedPages.Count > 0 && RecompOne.Runtime.Runtime.Gpu is { } gpu)
        {
            var vram = gpu.Vram;

            sb.AppendLine();
            sb.AppendLine("  texture pages used by in-framebuffer textured primitives, and what they resolve to:");

            foreach (var ((tpage, clut), count) in TexturedPages.OrderByDescending(k => k.Value).Take(8))
            {
                var pageX = tpage & 0xF;
                var pageY = (tpage >> 4) & 1;
                var blend = (tpage >> 5) & 3;
                var depth = (tpage >> 7) & 3;
                var bpp = depth switch { 0 => 4, 1 => 8, 2 => 16, _ => 4 };

                TileRect rect;
                try
                {
                    rect = TextureTile.Describe(tpage, clut, 0, 0, 16, 16);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"    page {pageX},{pageY} clut 0x{clut:X4}: Describe threw {ex.GetType().Name}");
                    continue;
                }

                var lit = 0;
                var total = rect.VramW * rect.H;
                if (rect.VramW > 0 && rect.H > 0)
                {
                    for (var y = 0; y < rect.H; y++)
                    {
                        var row = ((rect.VramY + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                        for (var x = 0; x < rect.VramW; x++)
                        {
                            var px = vram[row + ((rect.VramX + x) & (Gpu.VramWidth - 1))];
                            if (px != 0 && px != 0x8000) lit++;
                        }
                    }
                }

                var pct = total > 0 ? 100.0 * lit / total : 0.0;
                sb.AppendLine($"    page X={pageX} Y={pageY} blend={blend} {bpp}bpp clut=0x{clut:X4}  x{count} prims" +
                              $"  -> samples VRAM ({rect.VramX},{rect.VramY}) {rect.VramW}x{rect.H}, " +
                              $"{lit}/{total} non-black ({pct:0.0}%)");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
