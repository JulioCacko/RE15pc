using System.Diagnostics;
using System.Security.Cryptography;
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
                }
        }
        catch
        {
            // Diagnostics must never take down the run they are describing.
        }
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
