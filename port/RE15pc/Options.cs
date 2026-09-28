namespace RE15pc;

/// <summary>Command-line options for the host application.</summary>
public sealed class Options
{
    /// <summary>Disc cue sheet to boot from.</summary>
    public string Cue { get; private init; } = "Bio2Nov96.cue";

    /// <summary>
    /// When set, stop after this many seconds, write diagnostics and exit with a
    /// pass/fail code instead of running until the game thread ends.
    /// </summary>
    public double? SmokeSeconds { get; private init; }
    public long? Frames { get; private init; }
    public double TimeoutSeconds { get; private init; } = 300;
    public bool TraceInput { get; private init; }

    /// <summary>Log unmapped calls and continue, rather than throwing.</summary>
    public bool Tolerant { get; private init; }

    /// <summary>Verify the whole-image SHA-256, not just its geometry.</summary>
    public bool FullHash { get; private init; }

    /// <summary>Where diagnostics are written.</summary>
    public string OutDir { get; private init; } = Path.Combine("out", "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Comma-separated runtime log categories to enable, or empty for none. Names
    /// map onto RecompOne's <c>Log</c> flags: bios, spu, gpu, dma, cd, sdk, vsync,
    /// mdec, irq, all.
    /// </summary>
    public string Log { get; private init; } = "";

    /// <summary>Seconds between guest-memory progress samples.</summary>
    public double SampleSeconds { get; private init; } = 0.5;

    /// <summary>
    /// Scripted controller input as a comma-separated list of <c>&lt;frame&gt;:&lt;buttons&gt;</c>
    /// steps, or empty to leave the pad alone. See <see cref="ScriptedInput"/>.
    /// </summary>
    public string Input { get; private init; } = "";

    /// <summary>
    /// Disable the GPU HLE so the software rasteriser draws into the shadow VRAM. A
    /// diagnostic for isolating the GL path; see <c>GpuActivity.Attach</c>.
    /// </summary>
    public bool SoftwareGpu { get; private init; }

    /// <summary>
    /// Suppress classes of drawing primitive, keeping uploads, VRAM copies and fills. Values are
    /// <c>all</c>, <c>textured</c> or <c>flat</c>; empty means draw normally. A diagnostic that
    /// separates "the content never arrives" from "drawing covers it".
    /// </summary>
    public string SkipDraws { get; private init; } = "";

    /// <summary>
    /// Force neutral modulation for every textured primitive, as if each were raw. A diagnostic for
    /// the room tiles' dark modulation colour, which five-bit quantisation collapses to black.
    /// </summary>
    public bool NeutralModulation { get; private init; }

    /// <summary>
    /// Exercise overlay dispatch for every registered overlay at the end of the run and report it,
    /// without needing the gameplay that reaching the later stages would require.
    /// </summary>
    public bool VerifyOverlays { get; private init; }

    /// <summary>
    /// Render a block from the SPU at the end of the run and measure it, so that "audio plays" is
    /// evidence rather than an untested assumption.
    /// </summary>
    public bool VerifyAudio { get; private init; }

    public bool Help { get; private init; }

    public static bool TryParse(string[] args, out Options options, out string error)
    {
        var cue = "Bio2Nov96.cue";
        double? smoke = null;
        long? frames = null;
        double timeout = 300;
        var traceInput = false;
        var tolerant = false;
        var fullHash = false;
        var outDir = Path.Combine("out", "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var log = "";
        var sample = 0.5;
        var input = "";
        var softwareGpu = false;
        var skipDraws = "";
        var neutralModulation = false;
        var verifyOverlays = false;
        var verifyAudio = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--frames":
                    if (++i >= args.Length || !long.TryParse(args[i], out var count) || count <= 0)
                    { error = "--frames needs a positive integer"; options = new Options(); return false; }
                    frames = count;
                    break;
                case "--timeout":
                    if (++i >= args.Length || !double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out timeout) ||
                            !double.IsFinite(timeout) || timeout <= 0 || timeout > 86400)
                    { error = "--timeout needs finite seconds in (0, 86400]"; options = new Options(); return false; }
                    break;
                case "--trace-input":
                    traceInput = true;
                    break;
                case "--cue":
                    if (++i >= args.Length) { error = "--cue needs a path"; options = new Options(); return false; }
                    cue = args[i];
                    break;

                case "--smoke":
                    if (++i >= args.Length) { error = "--smoke needs a number of seconds"; options = new Options(); return false; }
                    if (!double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
                        !double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
                    {
                        error = $"--smoke expects a positive number of seconds, got '{args[i]}'";
                        options = new Options();
                        return false;
                    }
                    smoke = seconds;
                    break;

                case "--tolerant":
                    tolerant = true;
                    break;

                case "--full-hash":
                    fullHash = true;
                    break;

                case "--out":
                    if (++i >= args.Length) { error = "--out needs a directory"; options = new Options(); return false; }
                    outDir = args[i];
                    break;

                case "--log":
                    if (++i >= args.Length) { error = "--log needs a category list"; options = new Options(); return false; }
                    log = args[i];
                    break;

                case "--sample":
                    if (++i >= args.Length) { error = "--sample needs a number of seconds"; options = new Options(); return false; }
                    if (!double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out sample) || !double.IsFinite(sample) || sample <= 0 || sample > 86400)
                    {
                        error = $"--sample expects a positive number of seconds, got '{args[i]}'";
                        options = new Options();
                        return false;
                    }
                    break;

                case "--input":
                    if (++i >= args.Length) { error = "--input needs a script"; options = new Options(); return false; }
                    input = args[i];
                    break;

                case "--software-gpu":
                    softwareGpu = true;
                    break;

                case "--skip-draws":
                    if (++i >= args.Length) { error = "--skip-draws needs a mode: all, textured or flat"; options = new Options(); return false; }
                    skipDraws = args[i].ToLowerInvariant();
                    if (skipDraws is not ("all" or "textured" or "flat" or "subtract" or "flatblend0" or "blend0"
                        or "blackflat" or "greyflat"))
                    {
                        error = $"--skip-draws expects all, textured, flat, subtract, flatblend0, blend0, blackflat or greyflat; got '{args[i]}'";
                        options = new Options();
                        return false;
                    }
                    break;

                case "--neutral-modulation":
                    neutralModulation = true;
                    break;

                case "--verify-overlays":
                    verifyOverlays = true;
                    break;

                case "--verify-audio":
                    verifyAudio = true;
                    break;

                case "--help":
                case "-h":
                    help = true;
                    break;

                default:
                    error = $"unknown argument: {args[i]}";
                    options = new Options();
                    return false;
            }
        }

        if (frames is not null && smoke is not null)
        { error = "--frames and --smoke are mutually exclusive"; options = new Options(); return false; }
        if (!string.IsNullOrWhiteSpace(input) && !InputSchedule.TryParse(input, out _, out error))
        { options = new Options(); return false; }
        options = new Options
        {
            Cue = cue,
            SmokeSeconds = smoke,
            Frames = frames,
            TimeoutSeconds = timeout,
            TraceInput = traceInput,
            Tolerant = tolerant,
            FullHash = fullHash,
            OutDir = outDir,
            Log = log,
            SampleSeconds = sample,
            Input = input,
            SoftwareGpu = softwareGpu,
            SkipDraws = skipDraws,
            NeutralModulation = neutralModulation,
            VerifyOverlays = verifyOverlays,
            VerifyAudio = verifyAudio,
            Help = help
        };
        error = "";
        return true;
    }

    /// <summary>
    /// Applies the <c>--log</c> category list to RecompOne's <c>Log</c> flags.
    /// Returns the names it did not recognise, so a typo is visible rather than
    /// silently producing no logging.
    /// </summary>
    public IReadOnlyList<string> ApplyLogFlags()
    {
        var unknown = new List<string>();
        if (string.IsNullOrWhiteSpace(Log)) return unknown;

        foreach (var raw in Log.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "all":
                    RecompOne.Runtime.Log.BiosOn = true;
                    RecompOne.Runtime.Log.SpuOn = true;
                    RecompOne.Runtime.Log.GpuOn = true;
                    RecompOne.Runtime.Log.DmaOn = true;
                    RecompOne.Runtime.Log.CdOn = true;
                    RecompOne.Runtime.Log.SdkOn = true;
                    RecompOne.Runtime.Log.VSyncOn = true;
                    RecompOne.Runtime.Log.MdecOn = true;
                    RecompOne.Runtime.Log.IrqOn = true;
                    break;

                case "bios": RecompOne.Runtime.Log.BiosOn = true; break;
                case "spu": RecompOne.Runtime.Log.SpuOn = true; break;
                case "gpu": RecompOne.Runtime.Log.GpuOn = true; break;
                case "dma": RecompOne.Runtime.Log.DmaOn = true; break;
                case "cd": RecompOne.Runtime.Log.CdOn = true; break;
                case "sdk": RecompOne.Runtime.Log.SdkOn = true; break;
                case "vsync": RecompOne.Runtime.Log.VSyncOn = true; break;
                case "mdec": RecompOne.Runtime.Log.MdecOn = true; break;
                case "irq": RecompOne.Runtime.Log.IrqOn = true; break;

                default: unknown.Add(raw); break;
            }
        }

        return unknown;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("RE15pc - native PC port of the Biohazard 1.5 (Nov 6, 1996) prototype.");
        Console.WriteLine();
        Console.WriteLine("usage: RE15pc [options]");
        Console.WriteLine();
        Console.WriteLine("  --cue <path>        disc cue sheet to boot (default Bio2Nov96.cue)");
        Console.WriteLine("  --frames <count>    stop at this completed guest frame; exact-count acceptance");
        Console.WriteLine("  --timeout <secs>    frame-run wall timeout, default 300; timeout fails");
        Console.WriteLine("  --trace-input      log pad values delivered to the guest");
        Console.WriteLine("  --smoke <seconds>   run for N seconds, write diagnostics, then exit");
        Console.WriteLine("                      non-zero if the run crashed or made an unmapped call");
        Console.WriteLine("  --tolerant          log unmapped calls and continue instead of throwing;");
        Console.WriteLine("                      a debugging aid that hides real bugs, so off by default");
        Console.WriteLine("  --full-hash         verify the whole disc SHA-256, not just its geometry");
        Console.WriteLine("  --out <dir>         empty diagnostics directory (default unique out/runs/<id>)");
        Console.WriteLine("  --log <categories>  enable runtime logging. One or more of:");
        Console.WriteLine("                        bios spu gpu dma cd sdk vsync mdec irq all");
        Console.WriteLine("                      e.g. --log irq,vsync,cd");
        Console.WriteLine("  --sample <seconds>  guest-memory progress sample interval (default 0.5)");
        Console.WriteLine("  --input <script>    press buttons on a frame schedule, e.g.");
        Console.WriteLine("                        --input 90:start,210:cross,330:cross");
        Console.WriteLine("                      buttons: select start up down left right l1 r1 l2 r2");
        Console.WriteLine("                               l3 r3 triangle circle cross square");
        Console.WriteLine("                      frame:buttons[:duration], default 12; none releases; latest step wins");
        Console.WriteLine("  --software-gpu      disable the GPU HLE so the software rasteriser draws into");
        Console.WriteLine("                      shadow VRAM; a diagnostic for isolating the GL path");
        Console.WriteLine("  --help              show this message");
        Console.WriteLine();
        Console.WriteLine("Run from the repository root so settings.json and out/ land predictably.");
        Console.WriteLine();
        Console.WriteLine("Requires your own copy of the prototype disc. It is not distributed");
        Console.WriteLine("with this project; see README.md.");
    }
}
