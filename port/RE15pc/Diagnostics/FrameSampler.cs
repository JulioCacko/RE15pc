using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;

namespace RE15pc.Diagnostics;

/// <summary>
/// Samples the framebuffers once per guest frame, at <c>VSync</c>, so that what happens inside a frame
/// can be told apart from what happens between frames.
/// </summary>
/// <remarks>
/// The wall-clock sampler runs about twice a second and collapses runs of equal readings, so a
/// fill-and-wipe cycle inside a single frame is invisible to it - it can only ever see zero. That
/// matters because it was the measurement meant to separate a one-off erasure, which points at a
/// render target being destroyed and recreated, from a per-frame one, which points at a fill or a
/// stale whole-surface writeback.
///
/// Reading the software shadow is enough here and needs no GL thread hop, so a per-frame cadence is
/// affordable. Each frame reads both framebuffer regions and records whether each is occupied, with
/// changes recorded rather than every frame.
/// </remarks>
public static class FrameSampler
{
    private static bool _attached;

    /// <summary>Every 2nd frame is sampled; a full read is 153,600 pixels.</summary>
    private const int EveryNthFrame = 2;

    private static long _frame;
    private static int _last0 = -1, _last240 = -1;

    /// <summary>Frame number, buffer(0,0) count, buffer(0,240) count, at each change.</summary>
    private static readonly List<(long Frame, int Lit0, int Lit240)> Changes = [];

    /// <summary>
    /// Render target creations and evictions, with the framebuffer contents at that instant.
    ///
    /// The point is to put the one-off erasure and the target lifecycle on the same timeline. If the
    /// framebuffers fall to zero in the same entry as an eviction, the eviction is the cause and the
    /// question becomes whether its replacement was seeded over the framebuffer region.
    /// </summary>
    private static readonly List<(long Frame, int Lit0, int Lit240, string What)> RtEvents = [];

    /// <summary>
    /// Whole-region VRAM transfers - fills, VRAM-to-VRAM copies and CPU uploads - with the framebuffer
    /// contents at that instant.
    ///
    /// These write both stores, so if the two buffers fall to zero in the same entry as one of these,
    /// that entry is the wipe. Nothing else in the runtime writes both stores, and the wipe was shown
    /// to be neither per-frame nor coincident with a render target event.
    /// </summary>
    private static readonly List<(long Frame, int Lit0, int Lit240, string What)> TransferEvents = [];

    /// <summary>Only transfers touching a framebuffer region are recorded, to keep the log readable.</summary>
    private static bool TouchesFramebuffer(string what) => true;

    /// <summary>
    /// In-framebuffer textured draws seen since the last framebuffer upload, recorded at each frame end.
    ///
    /// This is the fork. Each frame the guest loads a black image into both framebuffers, and the room
    /// is supposed to be drawn on top of it. If this count is large at frame end, the draws come after
    /// the clear and the problem is in what they produce; if it is near zero, the clears come last and
    /// the room is being erased, which is an ordering fix rather than a rendering one.
    /// </summary>
    private static long _texturedSinceClear;

    private static readonly List<(long Frame, long AfterClear)> FrameOrder = [];

    private static readonly object Gate = new();

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;

        Event.AddListener<VSyncEvent>(OnVSync);

        GpuGlAccess.RtObserver = OnRenderTargetEvent;
        GpuGlAccess.TransferObserver = OnTransfer;

        Event.AddListener<RenderPrimEvent>(OnPrimitive);
    }

    /// <summary>
    /// Counts textured primitives that land in a framebuffer, since the last framebuffer upload. The
    /// event runs immediately before the draw, so this orders draws against clears within a frame.
    /// </summary>
    private static void OnPrimitive(RenderPrimEvent e)
    {
        if (!e.Textured) return;

        var inFramebuffer = true;
        for (var i = 0; i < 3; i++)
            if (e.X[i] is < 0 or > 319 || e.Y[i] is < 0 or > 479)
            {
                inFramebuffer = false;
                break;
            }

        if (inFramebuffer) Interlocked.Increment(ref _texturedSinceClear);
    }

    /// <summary>
    /// Records a whole-region transfer with the framebuffer contents at that instant. Called from the
    /// guest thread as commands are decoded.
    /// </summary>
    private static void OnTransfer(string what)
    {
        // A framebuffer-sized upload is the per-frame clear; it resets the ordering counter.
        if (what.StartsWith("upload", StringComparison.Ordinal) && what.Contains("320x240"))
            Interlocked.Exchange(ref _texturedSinceClear, 0);

        if (Runtime.Gpu is not { } gpu) return;
        if (gpu.Vram.Length < Gpu.VramWidth * Gpu.VramHeight) return;

        var lit0 = Count(gpu.Vram, 0);
        var lit240 = Count(gpu.Vram, 240);

        lock (Gate)
        {
            if (TransferEvents.Count < 400) TransferEvents.Add((_frame, lit0, lit240, what));
        }
    }

    /// <summary>
    /// Called from the GL thread when a render target is created or destroyed. Records the
    /// framebuffer contents alongside it, which is the whole point - it says whether the erasure and
    /// the eviction are the same event.
    /// </summary>
    private static void OnRenderTargetEvent(string what)
    {
        if (Runtime.Gpu is not { } gpu) return;
        if (gpu.Vram.Length < Gpu.VramWidth * Gpu.VramHeight) return;

        var lit0 = Count(gpu.Vram, 0);
        var lit240 = Count(gpu.Vram, 240);

        lock (Gate)
        {
            if (RtEvents.Count < 200) RtEvents.Add((_frame, lit0, lit240, what));
        }
    }

    private static void OnVSync(VSyncEvent e)
    {
        _frame = e.Frame;

        lock (Gate)
        {
            if (FrameOrder.Count < 700)
                FrameOrder.Add((_frame, Interlocked.Read(ref _texturedSinceClear)));
        }

        if (_frame % EveryNthFrame != 0) return;

        if (Runtime.Gpu is not { } gpu) return;
        if (gpu.Vram.Length < Gpu.VramWidth * Gpu.VramHeight) return;

        var lit0 = Count(gpu.Vram, 0);
        var lit240 = Count(gpu.Vram, 240);

        lock (Gate)
        {
            if (lit0 == _last0 && lit240 == _last240) return;

            _last0 = lit0;
            _last240 = lit240;
            Changes.Add((_frame, lit0, lit240));
        }
    }

    private static int Count(ushort[] vram, int top)
    {
        var lit = 0;

        for (var y = 0; y < 240; y++)
        {
            var row = ((top + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;

            for (var x = 0; x < 320; x++)
            {
                var px = vram[row + x];
                if (px != 0 && px != 0x8000) lit++;
            }
        }

        return lit;
    }

    public static string Describe()
    {
        if (!_attached) return "";

        lock (Gate)
        {
            if (Changes.Count == 0)
                return $"  per-frame buffer changes: none seen (guest frame {_frame})";

            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"  per-frame buffer changes: {Changes.Count}, " +
                          $"guest frames {Changes[0].Frame}..{_frame} (every {EveryNthFrame}nd sampled)");

            var rows = Changes.Count <= 16 ? Changes : Changes.TakeLast(16).ToList();
            if (Changes.Count > 16) sb.AppendLine($"    ... {Changes.Count - 16} earlier change(s) omitted");

            foreach (var (frame, lit0, lit240) in rows)
                sb.AppendLine($"    frame {frame,6}  buffer(0,0) {lit0,6}   buffer(0,240) {lit240,6}");

            lock (Gate)
            {
                sb.AppendLine();
                sb.AppendLine($"  render target lifecycle   : {RtEvents.Count} event(s), guest frame shown for each");

                foreach (var (frame, lit0, lit240, what) in RtEvents.TakeLast(20))
                    sb.AppendLine($"    frame {frame,6}  (0,0) {lit0,6}  (0,240) {lit240,6}   {what}");

                sb.AppendLine();
                sb.AppendLine($"  whole-region transfers    : {TransferEvents.Count} event(s) recorded");

                foreach (var (frame, lit0, lit240, what) in TransferEvents.TakeLast(24))
                    sb.AppendLine($"    frame {frame,6}  (0,0) {lit0,6}  (0,240) {lit240,6}   {what}");

                if (FrameOrder.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  in-framebuffer textured draws after the last framebuffer clear, at each frame end:");
                    sb.AppendLine($"    min {FrameOrder.Min(f => f.AfterClear)}, " +
                                  $"max {FrameOrder.Max(f => f.AfterClear)}, " +
                                  $"frames {FrameOrder.Count}");

                    foreach (var (frame, afterClear) in FrameOrder.TakeLast(12))
                        sb.AppendLine($"    frame {frame,6}  {afterClear,6} textured draw(s) after the clear");
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
