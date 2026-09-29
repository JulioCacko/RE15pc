using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RE15pc.Diagnostics;

namespace RE15pc;

public static class Program
{
    private static long _frame;
    private static int _timedOut, _deadlineReached;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        if (!Options.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        if (options.Help) { Options.PrintUsage(); return 0; }
        var sourceRoot = Directory.GetCurrentDirectory();
        var cue = Path.GetFullPath(options.Cue);
        var output = Path.GetFullPath(options.OutDir);
        if (!File.Exists(cue)) { Console.Error.WriteLine($"disc cue not found: {cue}"); return 2; }
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        { Console.Error.WriteLine($"--out must be empty: {output}"); return 2; }
        Runtime.DiscValidator = DiscIdentity.CreateValidator(options.FullHash || options.Frames is not null);
        if (Runtime.DiscValidator(cue) is { } rejection)
        { Console.Error.WriteLine(rejection); return 2; }

        Directory.CreateDirectory(output);
        var revision = Git(sourceRoot, "rev-parse", "HEAD");
        var dirty = Git(sourceRoot, "status", "--porcelain");
        var configFiles = Directory.GetFiles(Path.Combine(sourceRoot, "port", "config"), "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(sourceRoot, "patches"), "*.patch"));
        var hashes = configFiles.Order().ToDictionary(p => Path.GetRelativePath(sourceRoot, p), Hash);
        foreach (var name in new[] { "settings.json", "interface.ini" })
            if (File.Exists(Path.Combine(sourceRoot, name))) hashes[name] = Hash(Path.Combine(sourceRoot, name));
        // Acceptance runs must not modify the player's settings or memory cards.
        foreach (var name in new[] { "settings.json", "interface.ini", "carda.sav", "cardb.sav" })
            if (File.Exists(Path.Combine(sourceRoot, name)))
                File.Copy(Path.Combine(sourceRoot, name), Path.Combine(output, name));
        var bounded = options.Frames is not null || options.SmokeSeconds is not null;
        if (bounded) Directory.SetCurrentDirectory(output);
        ConsoleMirror.Install();
        using var accumulator = new RunAccumulator();
        var memory = new PSMemory();
        var checks = new List<CheckResult>();
        var states = new List<object>();
        var clock = Stopwatch.StartNew();
        Exception? hostFailure = null;
        ConfigManager.Load();
        ConfigManager.Game.CdPath = cue;
        if (bounded)
        {
            ConfigManager.Game.CardAPath = IsolateCard(ConfigManager.Game.CardAPath, "carda.sav");
            ConfigManager.Game.CardBPath = IsolateCard(ConfigManager.Game.CardBPath, "cardb.sav");
        }
        ConfigManager.SaveGame();
        OverlayPolicy.Attach();
        Dispatcher.Tolerant = options.Tolerant;
        var unknown = options.ApplyLogFlags();
        if (unknown.Count > 0) checks.Add(new("log-options", false, string.Join(", ", unknown)));
        GpuActivity.SuppressMode = options.SkipDraws;
        RecompOne.Runtime.Hle.GpuGlAccess.ForceNeutralModulation = options.NeutralModulation;
        GpuActivity.Attach(options.SoftwareGpu);
        FrameSampler.Attach();
        AudioVerification.Attach();
        ScriptedInput.Attach(options.Input, options.TraceInput);
        var sampleEvery = Math.Max(1L, (long)Math.Ceiling(options.SampleSeconds * 30));
        Runtime.FrameCompleted = frame =>
        {
            Interlocked.Exchange(ref _frame, frame);
            if (frame % sampleEvery == 0 || frame == options.Frames)
                states.Add(new { frame, overlays = Dispatcher.ActiveNames,
                    ramSha256 = Convert.ToHexString(SHA256.HashData(memory.Ram)),
                    // This disc's player structure is passed as 0x800ACA54 by its
                    // input/interaction handlers; XYZ are +0x34/+0x38/+0x3c and yaw +0x6a.
                    player = new { x = unchecked((int)memory.ReadU32(0x800ACA88)),
                        y = unchecked((int)memory.ReadU32(0x800ACA8C)),
                        z = unchecked((int)memory.ReadU32(0x800ACA90)),
                        yaw = memory.ReadU16(0x800ACABE) & 0xfff,
                        character = memory.ReadU8(0x800ACA5C),
                        weapon = memory.ReadU8(0x800ACA5D),
                        health = memory.ReadU16(0x800ACAEE) },
                    camera = memory.ReadU16(0x800B0FE4),
                    inventory = new { open = memory.ReadU8(0x800B5359) is >= 1 and <= 5,
                        mode = memory.ReadU8(0x800B5359),
                        controlBlocked = (memory.ReadU32(0x800ACA3C) & 0x40) != 0,
                        panel = memory.ReadU8(0x800B25BC), selection = memory.ReadU8(0x800B25C8),
                        state = memory.ReadU32(0x800B25C0),
                        slots = Enumerable.Range(0, 10).Select(i => new {
                            id = memory.ReadU8(0x800B10ACu + (uint)i * 4),
                            quantity = memory.ReadU8(0x800B10ADu + (uint)i * 4)
                        }).ToArray() },
                    roomIndex = memory.ReadU8(0x800B0FE2),
                    rdt = $"0x{memory.ReadU32(0x800AC778):X8}" });
            if (frame == options.Frames) Runtime.RequestStop();
        };
        using var emergency = new Timer(_ =>
        {
            // A stuck guest cannot yield a consistent snapshot. Fail without inspecting it.
            try
            {
                File.WriteAllText(Path.Combine(output, "verdict.txt"), "FAIL\nreason: guest failed to stop\n");
                File.WriteAllText(Path.Combine(output, "run.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, revision, dirty, cue, configHashes = hashes,
                    passed = false, completedFrames = Interlocked.Read(ref _frame),
                    failures = new[] { "guest failed to stop within 15 seconds; no final snapshot" }
                }, JsonOptions));
            }
            finally { Halt(1); }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        using var limit = new Timer(_ =>
        {
            Interlocked.Exchange(ref _deadlineReached, 1);
            if (options.Frames is not null) Interlocked.Exchange(ref _timedOut, 1);
            Runtime.RequestStop();
            emergency.Change(TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        }, null, bounded ? TimeSpan.FromSeconds(options.SmokeSeconds ?? options.TimeoutSeconds)
                         : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Console.WriteLine($"[RE15pc] output={output}; targetFrames={options.Frames}; strict={!options.Tolerant}");
        ExecutionCoverage.Reset();
        ExecutionCoverage.Enabled = options.TraceCoverage;
        try { Runtime.Run(() => Recompiled.Entry.Run(memory, cue, "RE15pc - Biohazard 1.5")); }
        catch (Exception ex) { hostFailure = ex; Console.Error.WriteLine(ex); }
        ExecutionCoverage.Enabled = false;
        limit.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        emergency.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var findings = accumulator.Snapshot();
        checks.Add(Acceptance.Completion(options.Frames, _frame, _timedOut != 0, Runtime.GameStopped));
        if (options.SmokeSeconds is not null)
            checks.Add(new("smoke-duration", _deadlineReached != 0, "smoke must reach its requested duration"));
        checks.Add(new("runtime", hostFailure is null && Runtime.GameFailure is null && Runtime.HostFailure is null,
            (hostFailure ?? Runtime.GameFailure ?? Runtime.HostFailure)?.ToString() ?? "guest returned without exception"));
        checks.Add(new("preservation", !options.Tolerant && options.SkipDraws.Length == 0 &&
            !options.NeutralModulation && !options.SoftwareGpu, "acceptance requires strict, unmodified rendering"));
        if (Runtime.GameStopped)
        {
            Check("artifacts", () =>
            {
                var vram = VramDump.Dump(Runtime.Gpu, output);
                RunReport.WriteArtifacts(output, memory, findings, "guest stopped", ScriptedInput.Describe(), vram);
                foreach (var name in new[] { "ram-full.bin", "framebuffer-gl.png", "vram-gl.rgb555.bin" })
                    if (!File.Exists(Path.Combine(output, name))) throw new IOException($"missing {name}");
                File.WriteAllText(Path.Combine(output, "input.json"), JsonSerializer.Serialize(ScriptedInput.Trace(), JsonOptions));
                File.WriteAllText(Path.Combine(output, "states.json"), JsonSerializer.Serialize(states, JsonOptions));
                return new("artifacts", true, "stopped guest RAM, GPU readback, input and state trace written");
            });
            if (options.VerifyAudio) Check("audio", AudioVerification.Verify);
            if (options.TraceCoverage)
                Check("coverage-capture", () => ContentCoverage.Write(Path.Combine(sourceRoot, "disc-manifest.json"), output));
            // Snapshot gameplay findings before synthetic verification mutates the dispatcher.
            if (options.VerifyOverlays) Check("overlays", OverlayVerification.Verify);
        }
        else
        {
            checks.Add(new("artifacts", false, "no snapshot: guest not stopped"));
            if (options.VerifyAudio) checks.Add(new("audio", false, "not evaluated"));
            if (options.VerifyOverlays) checks.Add(new("overlays", false, "not evaluated"));
            if (options.TraceCoverage) checks.Add(new("coverage-capture", false, "not evaluated"));
        }
        var after = accumulator.Snapshot();
        if (after.ListenerErrors > findings.ListenerErrors || (after.Crashed && !findings.Crashed))
            checks.Add(new("verification-runtime", false, "runtime/listener failure during verification"));
        try { if (Runtime.GameStopped) Runtime.Shutdown(); }
        catch (Exception ex) { checks.Add(new("shutdown", false, ex.ToString())); }
        var passed = Acceptance.Passed(findings, checks);
        try
        {
            var report = new
            {
                schemaVersion = 1, revision, dirty, cue,
                failureTracking = "guest-main-bios-and-host-v1",
                discSha256 = DiscIdentity.ExpectedSha256,
                discHashVerified = options.FullHash || options.Frames is not null,
                configHashes = hashes, requested = new { options.Frames, options.SmokeSeconds,
                    options.TimeoutSeconds, options.Input, options.VerifyAudio, options.VerifyOverlays, options.TraceCoverage },
                completedFrames = _frame, elapsedSeconds = clock.Elapsed.TotalSeconds,
                guestStopped = Runtime.GameStopped, findings, checks, passed,
                evidence = Directory.GetFiles(output).Select(Path.GetFileName).Order().ToArray()
            };
            File.WriteAllText(Path.Combine(output, "run.json"), JsonSerializer.Serialize(report, JsonOptions));
            File.WriteAllText(Path.Combine(output, "verdict.txt"), passed ? "PASS\n" : "FAIL\n");
        }
        catch (Exception ex) { passed = false; Console.Error.WriteLine($"report failed: {ex}"); }
        foreach (var check in checks) Console.WriteLine($"[{check.Name}] {(check.Passed ? "PASS" : "FAIL")}: {check.Detail}");
        Console.WriteLine($"SMOKE VERDICT: {(passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"artifacts: {output}");
        Halt(passed ? 0 : 1);
        return passed ? 0 : 1;

        string IsolateCard(string configured, string name)
        {
            var source = Path.GetFullPath(configured, sourceRoot);
            var target = Path.Combine(output, name);
            if (File.Exists(source)) File.Copy(source, target, true);
            return target;
        }

        void Check(string name, Func<CheckResult> action)
        {
            try { checks.Add(action()); }
            catch (Exception ex) { checks.Add(new(name, false, ex.ToString())); }
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var value = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("cannot record git provenance");
        return value.Trim();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    private static void Halt(int code)
    {
        Console.Out.Flush();
        Console.Error.Flush();
        if (OperatingSystem.IsWindows()) TerminateProcess(GetCurrentProcess(), (uint)code);
        else Environment.Exit(code);
    }
}
