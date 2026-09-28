using System.Text;
using RecompOne.Runtime;
using RecompOne.Runtime.Hardware;

namespace RE15pc.Diagnostics;

/// <summary>
/// Verifies that the audio path actually produces sound, rather than merely being present.
/// </summary>
/// <remarks>
/// This project has recorded "audio not proven to play at all" for many rounds, which is a statement
/// about absence of evidence rather than evidence of absence. The runtime has a full audio path - an
/// SPU, XA streaming, and an OpenAL host output - and enough of it is public to test directly.
///
/// The strongest test is not to read configuration but to render a block and look at it: <c>Spu.Mix</c>
/// is public, so mixing frames and measuring their amplitude says whether the SPU produces samples at
/// all. Voice phases and the global register then say where the sound is coming from.
///
/// Runs at the end of a run, because mixing advances voice state and would otherwise disturb the
/// audio the guest is playing.
/// </remarks>
public static class AudioVerification
{
    public static string Verify()
    {
        var sb = new StringBuilder();
        var spu = Runtime.Spu;

        if (spu is null)
        {
            sb.AppendLine("  spu                     : not present, so audio cannot be produced");
            return sb.ToString().TrimEnd();
        }

        var voices = new Spu.VoiceDebug[24];
        spu.CaptureDebug(voices, out var state);

        var sounding = 0;
        for (var i = 0; i < voices.Length; i++)
            if (voices[i].Phase != Spu.AdsrPhase.Off)
                sounding++;

        sb.AppendLine($"  spu voices keyed on     : {sounding} of {voices.Length}");

        // The per-voice detail is what makes the result actionable: a voice that is keyed on but has
        // zero ADSR or L/R volume is an envelope problem, while one with volume but silence is a
        // sample-data problem in SPU RAM.
        for (var i = 0; i < voices.Length; i++)
        {
            var v = voices[i];
            if (v.Phase == Spu.AdsrPhase.Off) continue;

            sb.AppendLine($"    voice {i,2}  phase={v.Phase,-8} adsrVol={v.AdsrVol,6} " +
                          $"vol={v.VolL}/{v.VolR} pitch=0x{v.Pitch:X4} cur=0x{v.CurAddr:X5} " +
                          $"start=0x{v.StartAddr:X4} endx={v.EndX} noise={v.Noise}");
        }
        sb.AppendLine($"  spucnt                  : 0x{state.Spucnt:X4}" +
                      $"  enable={(state.Spucnt & 0x8000) != 0}" +
                      $"  unmute={(state.Spucnt & 0x4000) != 0}" +
                      $"  reverb={(state.Spucnt & 0x0080) != 0}" +
                      $"  cdAudio={(state.Spucnt & 0x0001) != 0}");
        sb.AppendLine($"  main volume             : L={state.MainVolL} R={state.MainVolR}" +
                      $"   cd L={state.CdVolL} R={state.CdVolR}");

        // Render a block and measure it. This is the actual evidence: configuration can be plausible
        // and still produce silence.
        const int frames = 1024;
        var block = new short[frames * 2];

        try
        {
            spu.Mix(block, frames);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  mixed block             : Mix threw {ex.GetType().Name}: {ex.Message}");
            return sb.ToString().TrimEnd();
        }

        var peak = 0;
        var nonZero = 0;
        double sumSquares = 0;

        foreach (var sample in block)
        {
            var magnitude = Math.Abs((int)sample);
            if (magnitude > peak) peak = magnitude;
            if (sample != 0) nonZero++;
            sumSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / block.Length);

        sb.AppendLine($"  mixed block             : {frames} frames, peak={peak} of 32767, " +
                      $"rms={rms:0.0}, non-zero samples={nonZero} of {block.Length}");

        sb.AppendLine($"  xa audio                : playing={XaAudio.Playing}" +
                      $"  buffered={XaAudio.BufferedSamples}  rate={XaAudio.SourceRate}");

        var verdict = peak > 0 && nonZero > 0
            ? "audible: the SPU produces samples"
            : "SILENT: the SPU produced no samples in this block";

        sb.AppendLine($"  audio verdict           : {verdict}");

        return sb.ToString().TrimEnd();
    }
}
