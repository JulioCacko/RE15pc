using RecompOne.Runtime.Events;

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

    public static long Primitives => Interlocked.Read(ref _prims);
    public static long Textured => Interlocked.Read(ref _textured);

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        Event.AddListener<RenderPrimEvent>(OnPrimitive);
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
        sb.AppendLine($"  drawing area(s) seen    : {areas}");
        sb.AppendLine($"  cluts seen              : {Cluts.Count}");
        return sb.ToString().TrimEnd();
    }
}
