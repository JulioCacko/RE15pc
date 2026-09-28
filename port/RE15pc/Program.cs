using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RE15pc.Diagnostics;

namespace RE15pc;

/// <summary>
/// Host application for the recompiled Biohazard 2 (November 6, 1996) prototype.
/// </summary>
/// <remarks>
/// This exists because RecompOne ships a runtime library and no game. A port has
/// to supply the executable that owns the memory, points the runtime at a disc,
/// installs the game's overlay policy, and starts the boot sequence. Everything
/// else - GPU, SPU, CD, memory cards, input, the debug panels - comes from
/// <c>RecompOne.Runtime</c>.
///
/// Run it from the repository root so that <c>settings.json</c> and the diagnostics
/// directory land in predictable places.
/// </remarks>
public static class Program
{
    private const string WindowTitle = "RE15pc - Biohazard 1.5 (Nov 6, 1996 prototype)";

    private static readonly object FinishGate = new();
    private static bool _finished;
    private static ProgressSampler? _sampler;

    public static int Main(string[] args)
    {
        if (!Options.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Options.PrintUsage();
            return 2;
        }

        if (options.Help)
        {
            Options.PrintUsage();
            return 0;
        }

        var cue = Path.GetFullPath(options.Cue);
        if (!File.Exists(cue))
        {
            Console.Error.WriteLine($"disc image not found: {cue}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("RE15pc requires your own copy of the Biohazard 1.5 (Nov 6, 1996)");
            Console.Error.WriteLine("prototype. Place Bio2Nov96.bin and Bio2Nov96.cue in the repository");
            Console.Error.WriteLine("root, or pass --cue <path>.");
            return 2;
        }

        // Validate before anything expensive happens, and do it through the same
        // reader the game itself will use.
        var validator = DiscIdentity.CreateValidator(options.FullHash);
        Runtime.DiscValidator = validator;

        // Validate up front as well as handing the validator to the runtime.
        // Runtime.WaitForValidDisc loops forever on a rejected disc and throws the
        // reason away, so without this an unusable image produces a window that
        // hangs with no explanation at all.
        if (validator(cue) is { } rejection)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"RE15pc cannot boot this disc: {rejection}");
            return 2;
        }

        // Verbose logging is off by default because it is extremely chatty. A typo in
        // a category name would otherwise look identical to "the runtime did nothing",
        // so unknown names are reported rather than ignored.
        var unknownLog = options.ApplyLogFlags();
        if (unknownLog.Count > 0)
        {
            Console.Error.WriteLine($"[RE15pc] unknown --log categories ignored: {string.Join(", ", unknownLog)}");
            Console.Error.WriteLine("[RE15pc] valid: bios spu gpu dma cd sdk vsync mdec irq all");
        }

        var memory = new PSMemory();

        // HostWindow.Initialize calls ConfigManager.Load() itself, and that would
        // overwrite a CdPath set only in memory - and create a fresh settings.json
        // if none exists, losing it entirely. So load, override, and persist, in
        // that order, before the runtime gets a chance to read it back.
        ConfigManager.Load();
        ConfigManager.Game.CdPath = cue;
        ConfigManager.SaveGame();

        OverlayPolicy.Attach();
        GpuActivity.Attach(options.SoftwareGpu);

        // Scripted input is what makes an unattended run able to get past the first screen
        // that waits for a pad. Only attached when asked for, so a normal interactive run
        // is untouched.
        if (!string.IsNullOrWhiteSpace(options.Input))
            ScriptedInput.Attach(options.Input);

        // Tolerant mode logs unmapped calls and continues. It is a debugging aid
        // and it hides real bugs, so it is opt-in and the default is strict.
        if (options.Tolerant)
        {
            Dispatcher.Tolerant = true;
            Console.WriteLine("[RE15pc] tolerant mode: unmapped calls will be logged, not thrown");
        }

        // Distinguishes "the guest is running" from "the guest has wedged", which is
        // otherwise unanswerable: recompiled code has no program counter to sample.
        _sampler = new ProgressSampler(memory, options.SampleSeconds);

        Console.WriteLine($"[RE15pc] disc    : {cue}");
        Console.WriteLine($"[RE15pc] strict  : {!options.Tolerant} (unmapped calls throw)");
        Console.WriteLine($"[RE15pc] sampling: guest memory every {options.SampleSeconds:0.##}s");
        if (options.SmokeSeconds is { } seconds)
            Console.WriteLine($"[RE15pc] smoke   : will stop after {seconds:0.##}s");

        var timer = options.SmokeSeconds is { } smokeSeconds
            ? new Timer(_ =>
            {
                var code = Finish(memory, options, "smoke window elapsed");
                Halt(code);
            }, null, TimeSpan.FromSeconds(smokeSeconds), Timeout.InfiniteTimeSpan)
            : null;

        try
        {
            Runtime.Run(() => Recompiled.Entry.Run(memory, cue, WindowTitle));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RE15pc] Runtime.Run threw: {ex}");
        }
        finally
        {
            timer?.Dispose();
        }

        return Finish(memory, options, "Runtime.Run returned");
    }

    /// <summary>
    /// Evaluates the run once and writes its artifacts. Idempotent, because either
    /// the smoke timer or the returning game thread can get here first.
    /// </summary>
    private static int Finish(PSMemory memory, Options options, string reason)
    {
        lock (FinishGate)
        {
            if (_finished) return 0;
            _finished = true;
        }

        _sampler?.Dispose();

        var findings = RunReport.AnalyseMirror();
        var progress = (_sampler?.Describe() ?? "") + Environment.NewLine + ScriptedInput.Describe();

        // Dump video memory before the process goes away, since the whole point is to
        // see what the guest drew without a human watching the window.
        var vram = "";
        try
        {
            vram = VramDump.Dump(Runtime.Gpu, options.OutDir) + Environment.NewLine + GpuActivity.Describe();
        }
        catch (Exception ex)
        {
            vram = $"  VRAM dump failed: {ex.Message}";
        }

        string report;
        try
        {
            report = RunReport.WriteArtifacts(options.OutDir, memory, findings, reason, progress, vram);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RE15pc] could not write diagnostics: {ex.Message}");
            report = findings.Describe();
        }

        Console.WriteLine();
        Console.WriteLine("================ RE15pc run report ================");
        Console.WriteLine(report);
        Console.WriteLine($"artifacts: {Path.GetFullPath(options.OutDir)}");
        Console.WriteLine($"OverlayPolicy evictions: {OverlayPolicy.Evictions}");
        Console.WriteLine("===================================================");

        // Last line, greppable, and authoritative in a way the exit code is not.
        Console.WriteLine($"SMOKE VERDICT: {(findings.Failed ? "FAIL" : "PASS")}");
        Console.Out.Flush();

        return findings.Failed ? 1 : 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Ends the process immediately with <paramref name="code"/>, deliberately running
    /// no teardown.
    /// </summary>
    /// <remarks>
    /// Both obvious alternatives are broken here, and this was established by reading
    /// the crash rather than guessing:
    ///
    /// Calling <c>Runtime.Shutdown()</c> from this timer gives 0xC0000005, an access
    /// violation, because it walks into GL object deletion from a thread-pool thread
    /// with no current GL context:
    /// <c>GL.DeleteFramebuffers -&gt; Gl45Vram.Dispose -&gt; GlCore.Dispose -&gt;
    /// HostWindow.OnClosing</c>.
    ///
    /// Plain <c>Environment.Exit</c> gives 0xC0000409, because it runs finalizers and
    /// AppDomain shutdown on this thread while the guest thread and the native audio,
    /// video and detour libraries are still live.
    ///
    /// The guest loop never returns on its own - a title screen waits on interrupts
    /// forever - so the harness has to stop the process from outside it. Terminating
    /// outright is the only path that leaves the exit code as the verdict actually
    /// earned. Artifacts and <c>verdict.txt</c> are already written and flushed by
    /// <see cref="Finish"/> before this runs.
    /// </remarks>
    private static void Halt(int code)
    {
        Console.Out.Flush();
        Console.Error.Flush();

        if (OperatingSystem.IsWindows())
        {
            TerminateProcess(GetCurrentProcess(), (uint)code);
            return; // not reached when the call succeeds
        }

        Environment.Exit(code);
    }
}
