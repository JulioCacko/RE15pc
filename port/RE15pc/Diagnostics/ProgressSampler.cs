using System.Diagnostics;
using System.Security.Cryptography;
using RecompOne.Runtime;
using RecompOne.Runtime.Assets.Textures;
using RecompOne.Runtime.Memory;

namespace RE15pc.Diagnostics;

/// <summary>
/// Samples guest memory over time to answer one question: is the recompiled code
/// still making progress, or has it wedged?
/// </summary>
/// <remarks>
/// This exists because the usual progress signal is unavailable. Recompiled code has
/// no program counter - control flow is the C# call stack, and <c>CpuContext</c> only
/// carries the GPRs plus SR/EPC/BadVAddr. So there is nothing to sample that says
/// "where is it now".
///
/// What a wedged guest cannot hide is memory. A guest looping forever waiting on an
/// interrupt it will never receive stops writing to RAM, so the hash of guest memory
/// goes constant. A guest actually running the title screen keeps mutating state and
/// the hashes keep changing. Counting distinct hashes therefore distinguishes the two
/// cases without any instrumentation inside the recompiled code.
///
/// Note that a guest spin-waiting on a hardware register read is invisible to this:
/// reads leave no trace. Pair it with the runtime's own verbose flags
/// (<c>--log irq,vsync,cd</c>), which show whether the runtime is delivering
/// interrupts at all.
/// </remarks>
public sealed class ProgressSampler : IDisposable
{
    private readonly PSMemory _memory;
    private readonly Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<Observation> _samples = [];

    /// <summary>
    /// Distinct display geometries seen, as "x,y w*h". The display origin is only readable
    /// at the instant of a dump, so sampling it over time is the only way to find out whether
    /// the game ever points the display somewhere other than where it was at the end.
    /// </summary>
    private readonly HashSet<string> _displayGeometries = [];

    /// <summary>
    /// Distinct draw offsets seen. The PS1 drawing offset is where a primitive at screen
    /// (0,0) actually lands in VRAM, and it is the field the HLE draw path has no way to
    /// receive, so sampling it over time is how that gets confirmed rather than assumed.
    /// </summary>
    private readonly HashSet<string> _drawOffsets = [];

    /// <summary>
    /// Timeline of non-black pixels in the display region, per store.
    ///
    /// This is the measurement that distinguishes the two candidate mechanisms for the black
    /// frame: a shadow that fills and is then overwritten by a backend readback, versus one that
    /// is never filled at all. Sampling only at the end cannot tell them apart, and both look
    /// identical in a single dump.
    ///
    /// The display origin is recorded with each reading, because this game double-buffers by
    /// moving it. Both stores are read at the same origin within a tick, so a disagreement
    /// between them in one row is real evidence, but comparing counts ACROSS rows is not: a jump
    /// from 31801 to 61135 is the region changing, not content appearing.
    /// </summary>
    private readonly List<(double Seconds, int OriginX, int OriginY, int ShadowLit, int BackendLit)> _vramTimeline = [];
    private int _ticks;

    /// <summary>Backend VRAM reads need the GL thread, so they are taken far less often.</summary>
    private const int BackendEvery = 8;

    /// <summary>
    /// Whether the GPU is marking writes into each framebuffer region, and whether that region is
    /// being touched at all.
    ///
    /// <c>VramTracker</c> is public and <c>GlCore</c> calls <c>MarkGpuWrite</c> both when it writes a
    /// render target back into VRAM and when it draws without one. That makes it an observable proxy
    /// for the private <c>Classify</c>: if textured draws are being classified into a target that is
    /// never composited, nothing marks this region dirty after the screen clear. If instead the
    /// region is marked dirty continuously, the draws are landing there and producing black, which
    /// is a completely different defect.
    /// </summary>
    private readonly Dictionary<int, (int Ticks, int GpuDirtyTicks, long LastGeneration, int GenerationChanges,
        int MaxLit, int LastLit)> _fbWrites = [];

    /// <summary>
    /// VRAM contents and the draw destination from the same instant, so they can be correlated.
    /// Taken together deliberately: a display region that stays black while the target origin is
    /// elsewhere says something different from one that stays black with the target in place.
    /// </summary>
    private readonly List<(double Seconds, int OriginX, int OriginY, int ShadowLit, int BackendLit, string Gl)>
        _combined = [];

    /// <summary>
    /// Content of each framebuffer over time, collapsed to the moments it changes.
    ///
    /// Both buffers reached 61135 non-black pixels and then fell to zero, and where that happens in
    /// time separates the candidates: a single wipe points at a render target being destroyed and
    /// recreated, a wipe every frame points at a fill or a stale whole-surface writeback.
    /// </summary>
    private readonly List<(double Seconds, int Lit0, int Lit240)> _bufferTimeline = [];

    /// <summary>
    /// Non-black pixels in each framebuffer of the BACKEND store, tracked over the run.
    /// </summary>
    /// <remarks>
    /// The software shadow only receives CPU writes - uploads, fills and VRAM copies - so its contents
    /// say nothing about whether anything was drawn. Its fall to zero is fully explained by the guest's
    /// own per-frame black uploads and is not evidence about rendering at all.
    ///
    /// Drawn output reaches the backend store instead, and that has only ever been read at the end of a
    /// run. Two measurements in this project have now been right about what they sampled and wrong about
    /// when, so the peak matters more than the endpoint: if the backend ever holds the room, then it
    /// renders and the end-of-run reading was simply taken at a bad moment.
    /// </remarks>
    private readonly int[] _gpuBufferNow = new int[2];
    private readonly int[] _gpuBufferPeak = new int[2];
    private int _gpuSamples;

    /// <summary>
    /// Non-black pixels on the surface draws actually go into, tracked by peak.
    ///
    /// This is the reading that splits the remaining space: either the room is present in the render
    /// target and the writeback is what fails, or the target is empty as well and the draw itself
    /// produces nothing. Every earlier reading came from VRAM rather than from the drawn surface.
    /// </summary>
    private int _targetNow, _targetPeak;
    private string _targetNote = "";

    private readonly record struct Observation(double Seconds, string Hash);

    public ProgressSampler(PSMemory memory, double intervalSeconds)
    {
        _memory = memory;

        // Sample immediately as well as on the interval, so a run that dies early
        // still has a baseline to compare against.
        var interval = TimeSpan.FromSeconds(Math.Max(0.05, intervalSeconds));
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, interval);
    }

    private void Sample()
    {
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(_memory.Ram))[..16];

            lock (_gate)
            {
                // Only record transitions. A guest sitting still produces thousands
                // of identical samples and no information.
                if (_samples.Count > 0 && _samples[^1].Hash == hash) return;
                _samples.Add(new Observation(_clock.Elapsed.TotalSeconds, hash));
            }

            if (RecompOne.Runtime.Runtime.Gpu is { } gpu)
                lock (_gate)
                {
                    _displayGeometries.Add(
                        $"{gpu.DisplayX},{gpu.DisplayY} {gpu.DisplayWidth}x{gpu.DisplayHeight}" +
                        (gpu.DisplayEnabled ? "" : " (disabled)"));

                    _drawOffsets.Add($"{gpu.DrawOffsetX},{gpu.DrawOffsetY}");
                }

            SampleVram();
            SampleFramebufferWrites();
        }
        catch
        {
            // Diagnostics must never take down the run they are describing.
        }
    }

    /// <summary>
    /// Counts non-black pixels in the display region of each VRAM store, to build a timeline.
    /// </summary>
    private void SampleVram()
    {
        if (RecompOne.Runtime.Runtime.Gpu is not { } gpu) return;

        const int width = Gpu.VramWidth;
        const int height = Gpu.VramHeight;

        var dx = gpu.DisplayX;
        var dy = gpu.DisplayY;
        var dw = Math.Clamp(gpu.DisplayWidth, 0, width);
        var dh = Math.Clamp(gpu.DisplayHeight, 0, height);
        if (dw <= 0 || dh <= 0) return;

        var shadow = gpu.Vram;
        if (shadow.Length < width * height) return;

        var shadowLit = CountLit(shadow, width, height, dx, dy, dw, dh);

        var tick = ++_ticks;
        var backendLit = -1;

        if (tick % BackendEvery == 0)
        {
            var (gl, _) = VramDump.TryReadBackend(width, height);

            if (gl != null)
            {
                backendLit = CountLit(gl, width, height, dx, dy, dw, dh);

                // Both buffers of the backend store, tracked by peak as well as current value.
                for (var b = 0; b < 2; b++)
                {
                    var lit = CountLit(gl, width, height, 0, b * 240, 320, 240);

                    lock (_gate)
                    {
                        _gpuBufferNow[b] = lit;
                        if (lit > _gpuBufferPeak[b]) _gpuBufferPeak[b] = lit;
                        if (b == 0) _gpuSamples++;
                    }
                }
            }

            // Reads real GL state, so it is on the same slow cadence as the backend read.
            GlStateSampler.Sample();

            var (targetLit, _, targetNote) = TargetProbe.Read();

            lock (_gate)
            {
                _targetNow = targetLit;
                _targetNote = targetNote == "ok" ? "" : targetNote;
                if (targetLit > _targetPeak) _targetPeak = targetLit;
            }

            lock (_gate)
            {
                _combined.Add((_clock.Elapsed.TotalSeconds, dx, dy, shadowLit, backendLit,
                    GlStateSampler.LastReading));
            }
        }

        lock (_gate)
        {
            // Collapse runs of identical readings; a static screen otherwise produces a
            // timeline of thousands of rows and no information.
            var sameAsLast = _vramTimeline.Count > 0
                             && _vramTimeline[^1].OriginX == dx
                             && _vramTimeline[^1].OriginY == dy
                             && _vramTimeline[^1].ShadowLit == shadowLit
                             && (backendLit < 0 || _vramTimeline[^1].BackendLit == backendLit);

            if (sameAsLast && backendLit < 0) return;

            _vramTimeline.Add((_clock.Elapsed.TotalSeconds, dx, dy, shadowLit, backendLit));
        }
    }

    /// <summary>
    /// Samples whether each framebuffer region is being written by the GPU, and whether it is being
    /// touched at all. Taken for both buffers regardless of which one is displayed, because the game
    /// alternates and a measurement that follows the display would confound the two.
    /// </summary>
    private void SampleFramebufferWrites()
    {
        foreach (var top in new[] { 0, 240 })
        {
            var dirty = VramTracker.IsGpuDirty(0, top, 320, 240);
            var generation = (long)VramTracker.Generation(0, top, 320, 240);

            // Content as well as write activity, for BOTH buffers. Every earlier measurement counted
            // only the DISPLAYED buffer, and the display origin is the opposite of the draw target -
            // which is correct double buffering, but it means those counts have all been reading the
            // front buffer, the one that is not being drawn into. A black front buffer over a correct
            // back buffer is indistinguishable from a black frame if only the front is ever counted.
            var lit = 0;
            if (RecompOne.Runtime.Runtime.Gpu is { } gpu && gpu.Vram.Length >= Gpu.VramWidth * Gpu.VramHeight)
            {
                var vram = gpu.Vram;
                for (var y = 0; y < 240; y++)
                {
                    var row = ((top + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                    for (var x = 0; x < 320; x++)
                    {
                        var px = vram[row + x];
                        if (px != 0 && px != 0x8000) lit++;
                    }
                }
            }

            lock (_gate)
            {
                if (!_fbWrites.TryGetValue(top, out var s)) s = (0, 0, generation, 0, 0, 0);

                s.Ticks++;
                if (dirty) s.GpuDirtyTicks++;
                if (s.LastGeneration != generation)
                {
                    s.GenerationChanges++;
                    s.LastGeneration = generation;
                }

                if (lit > s.MaxLit) s.MaxLit = lit;
                s.LastLit = lit;

                _fbWrites[top] = s;
            }
        }

        lock (_gate)
        {
            var lit0 = _fbWrites.TryGetValue(0, out var a) ? a.LastLit : -1;
            var lit240 = _fbWrites.TryGetValue(240, out var b) ? b.LastLit : -1;

            if (_bufferTimeline.Count == 0
                || _bufferTimeline[^1].Lit0 != lit0
                || _bufferTimeline[^1].Lit240 != lit240)
            {
                _bufferTimeline.Add((_clock.Elapsed.TotalSeconds, lit0, lit240));
            }
        }
    }

    private static int CountLit(ushort[] vram, int width, int height, int dx, int dy, int dw, int dh)
    {
        var lit = 0;

        for (var y = 0; y < dh; y++)
        {
            var row = ((dy + y) & (height - 1)) * width;

            for (var x = 0; x < dw; x++)
            {
                var px = vram[row + ((dx + x) & (width - 1))];
                if (px != 0 && px != 0x8000) lit++;
            }
        }

        return lit;
    }

    /// <summary>Distinct memory states observed, i.e. how many times RAM actually changed.</summary>
    public int Transitions
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>
    /// True when the guest stopped changing memory for the whole observation window,
    /// which is the signature of a wedge rather than a healthy title screen.
    /// </summary>
    public bool Stalled
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < 2) return true;

                var quietFor = _clock.Elapsed.TotalSeconds - _samples[^1].Seconds;
                return quietFor > 5.0;
            }
        }
    }

    public double SecondsSinceLastChange
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count == 0 ? 0.0 : _clock.Elapsed.TotalSeconds - _samples[^1].Seconds;
            }
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"guest memory states seen : {_samples.Count} distinct");
            sb.AppendLine($"last change at          : {(_samples.Count == 0 ? "never" : $"{_samples[^1].Seconds:0.0}s")}");
            sb.AppendLine($"seconds since change    : {SecondsSinceLastChange:0.0}");
            sb.AppendLine($"verdict                 : {(Stalled ? "STALLED - guest stopped writing memory" : "PROGRESSING")}");
            sb.AppendLine($"display geometries seen : {(_displayGeometries.Count == 0 ? "(none sampled)" : string.Join(" | ", _displayGeometries.OrderBy(d => d)))}");
            sb.AppendLine($"draw offsets seen       : {(_drawOffsets.Count == 0 ? "(none sampled)" : string.Join(" | ", _drawOffsets.OrderBy(d => d)))}");

            if (_bufferTimeline.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  framebuffer contents over time, at each change (0..76800 non-black):");

                var rows = _bufferTimeline.Count <= 24 ? _bufferTimeline : _bufferTimeline.TakeLast(24).ToList();
                if (_bufferTimeline.Count > 24) sb.AppendLine($"    ... {_bufferTimeline.Count - 24} earlier change(s) omitted");

                foreach (var (sec, lit0, lit240) in rows)
                    sb.AppendLine($"    {sec,6:0.0}s  buffer(0,0) {lit0,6}   buffer(0,240) {lit240,6}");
            }

            var glState = GlStateSampler.Describe();
            if (glState.Length > 0) sb.AppendLine(glState);

            var frameState = FrameSampler.Describe();
            if (frameState.Length > 0) sb.AppendLine(frameState);

            if (_combined.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  VRAM display contents and draw destination, from the same instant:");

                var rows = _combined.Count <= 10 ? _combined : _combined.TakeLast(10).ToList();

                foreach (var (sec, ox, oy, shadowLit, backendLit, gl) in rows)
                    sb.AppendLine($"    {sec,6:0.0}s origin {ox,4},{oy,-4} shadow {shadowLit,6} backend {backendLit,6}  {gl}");
            }

            foreach (var top in _fbWrites.Keys.OrderBy(k => k))
            {
                var s = _fbWrites[top];
                sb.AppendLine($"framebuffer (0,{top,-3})          : gpu-dirty {s.GpuDirtyTicks}/{s.Ticks}, " +
                              $"generation changed {s.GenerationChanges}x, " +
                              $"non-black now {s.LastLit}, peak {s.MaxLit}");
            }

            if (_gpuSamples > 0)
                for (var b = 0; b < 2; b++)
                    sb.AppendLine($"backend store (0,{b * 240,-3})      : non-black now {_gpuBufferNow[b]}, " +
                                  $"peak {_gpuBufferPeak[b]} over {_gpuSamples} sample(s)");

            sb.AppendLine($"render target surface     : non-black now {_targetNow}, peak {_targetPeak}" +
                          $"{(_targetNote.Length > 0 ? $"   ({_targetNote})" : "")}");

            if (_vramTimeline.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"  display-region non-black pixels over time " +
                              $"('-' = backend not sampled; backend sampled every {BackendEvery} ticks):");

                var shown = _vramTimeline.Count <= 14
                    ? _vramTimeline
                    : _vramTimeline.Take(7).Concat(_vramTimeline.TakeLast(7)).ToList();

                for (var i = 0; i < shown.Count; i++)
                {
                    if (_vramTimeline.Count > 14 && i == 7) sb.AppendLine("    ...");

                    var (sec, ox, oy, shadowLit, backendLit) = shown[i];
                    sb.AppendLine($"    {sec,6:0.0}s  origin {ox,4},{oy,-4}  shadow {shadowLit,6}" +
                                  (backendLit < 0 ? "   backend      -" : $"   backend {backendLit,6}"));
                }
            }

            if (_samples.Count > 0)
            {
                sb.Append("  timeline              : ");
                var shown = _samples.Count <= 12
                    ? _samples
                    : _samples.Take(6).Concat(_samples.TakeLast(6)).ToList();

                for (var i = 0; i < shown.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    if (_samples.Count > 12 && i == 6) sb.Append("... ");
                    sb.Append($"{shown[i].Seconds:0.0}s:{shown[i].Hash[..6]}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
