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

    /// <summary>Log unmapped calls and continue, rather than throwing.</summary>
    public bool Tolerant { get; private init; }

    /// <summary>Verify the whole-image SHA-256, not just its geometry.</summary>
    public bool FullHash { get; private init; }

    /// <summary>Where diagnostics are written.</summary>
    public string OutDir { get; private init; } = Path.Combine("out", "diagnostics");

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

    public bool Help { get; private init; }

    public static bool TryParse(string[] args, out Options options, out string error)
    {
        var cue = "Bio2Nov96.cue";
        double? smoke = null;
        var tolerant = false;
        var fullHash = false;
        var outDir = Path.Combine("out", "diagnostics");
        var log = "";
        var sample = 0.5;
        var input = "";
        var softwareGpu = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cue":
                    if (++i >= args.Length) { error = "--cue needs a path"; options = new Options(); return false; }
                    cue = args[i];
                    break;

                case "--smoke":
                    if (++i >= args.Length) { error = "--smoke needs a number of seconds"; options = new Options(); return false; }
                    if (!double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
                        seconds <= 0)
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
                            System.Globalization.CultureInfo.InvariantCulture, out sample) || sample <= 0)
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

        options = new Options
        {
            Cue = cue,
            SmokeSeconds = smoke,
            Tolerant = tolerant,
            FullHash = fullHash,
            OutDir = outDir,
            Log = log,
            SampleSeconds = sample,
            Input = input,
            SoftwareGpu = softwareGpu,
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
        Console.WriteLine("  --smoke <seconds>   run for N seconds, write diagnostics, then exit");
        Console.WriteLine("                      non-zero if the run crashed or made an unmapped call");
        Console.WriteLine("  --tolerant          log unmapped calls and continue instead of throwing;");
        Console.WriteLine("                      a debugging aid that hides real bugs, so off by default");
        Console.WriteLine("  --full-hash         verify the whole disc SHA-256, not just its geometry");
        Console.WriteLine("  --out <dir>         diagnostics directory (default out/diagnostics)");
        Console.WriteLine("  --log <categories>  enable runtime logging. One or more of:");
        Console.WriteLine("                        bios spu gpu dma cd sdk vsync mdec irq all");
        Console.WriteLine("                      e.g. --log irq,vsync,cd");
        Console.WriteLine("  --sample <seconds>  guest-memory progress sample interval (default 0.5)");
        Console.WriteLine("  --input <script>    press buttons on a frame schedule, e.g.");
        Console.WriteLine("                        --input 90:start,210:cross,330:cross");
        Console.WriteLine("                      buttons: select start up down left right l1 r1 l2 r2");
        Console.WriteLine("                               l3 r3 triangle circle cross square");
        Console.WriteLine("                      each press is held for 12 frames");
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
