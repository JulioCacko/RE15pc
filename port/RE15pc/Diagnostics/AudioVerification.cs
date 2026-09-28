namespace RE15pc.Diagnostics;

/// <summary>Observes samples already queued to the host device; never advances the SPU.</summary>
public static class AudioVerification
{
    private static readonly object Gate = new();
    private static long _samples, _nonZero, _blocks;
    private static int _peak;
    public static void Attach() => RecompOne.Runtime.Runtime.AudioOutputObserved = Observe;
    public static void Observe(ReadOnlySpan<short> samples)
    {
        long nonZero = 0;
        var peak = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
            if (sample != 0) nonZero++;
        }
        lock (Gate)
        {
            _samples += samples.Length;
            _nonZero += nonZero;
            _peak = Math.Max(_peak, peak);
            _blocks++;
        }
    }
    public static CheckResult Verify()
    {
        lock (Gate)
            return new("audio", _samples > 0 && _nonZero > 0,
                $"host output: blocks={_blocks}, samples={_samples}, nonZero={_nonZero}, peak={_peak}; " +
                "nonzero PCM verifies output, not musical correctness or continuity");
    }
}
