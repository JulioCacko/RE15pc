using RecompOne.Runtime.Diagnostics;

namespace RE15pc.Diagnostics;

public sealed record CheckResult(string Name, bool Passed, string Detail);

/// <summary>Run-long counters independent of the runtime's rolling UI log.</summary>
public sealed class RunAccumulator : IDisposable
{
    private readonly object _gate = new();
    private RunFindings _findings = new(false, 0, 0, 0, 0, 0, 0, 0);
    public RunAccumulator() => ConsoleMirror.LineWritten += Observe;
    public void Observe(string line)
    {
        var f = RunReport.Analyse([line]);
        lock (_gate)
        {
            var a = _findings;
            _findings = new(a.Crashed || f.Crashed, a.UnmappedCalls + f.UnmappedCalls,
                a.OverlayLoads + f.OverlayLoads, a.OverlayEvictions + f.OverlayEvictions,
                a.RegionOverwrites + f.RegionOverwrites, a.VramCollisions + f.VramCollisions,
                a.DiscWarnings + f.DiscWarnings, a.ListenerErrors + f.ListenerErrors);
        }
    }
    public RunFindings Snapshot() { lock (_gate) return _findings; }
    public void Dispose() => ConsoleMirror.LineWritten -= Observe;
}

public static class Acceptance
{
    public static CheckResult Completion(long? target, long actual, bool timedOut, bool stopped)
        => new("completion", stopped && !timedOut && (target is null || actual == target),
            $"guestStopped={stopped}, targetFrames={target}, actualFrames={actual}, timedOut={timedOut}");

    public static bool Passed(RunFindings findings, IEnumerable<CheckResult> checks)
        => !findings.Failed && checks.All(c => c.Passed);
}
