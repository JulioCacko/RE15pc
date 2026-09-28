using RE15pc;
using RE15pc.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;

var count = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    count++;
    Console.WriteLine($"PASS {name}");
}

var clean = new RunFindings(false, 0, 0, 0, 0, 0, 0, 0);
Check(Acceptance.Passed(clean, [new("audio", true, "")]), "valid verdict");
Check(!Acceptance.Passed(clean, [new("audio", false, "silent")]), "audio failure propagates");
Check(!Acceptance.Passed(clean, [new("overlays", false, "missing")]), "overlay failure propagates");
Check(!Acceptance.Passed(clean, [new("artifacts", false, "missing")]), "missing evidence fails");
var childCrash = RunReport.Analyse(["[Runtime] thread 1 stopped: System.InvalidOperationException: unmapped call: 0x80100424"]);
Check(childCrash.Crashed && childCrash.UnmappedCalls == 1 && childCrash.Failed,
    "legacy guest-thread failures cannot pass or hide strict unmapped calls");
Check(!Acceptance.Completion(100, 99, false, true).Passed, "early exit fails");
Check(!Acceptance.Completion(100, 101, false, true).Passed, "overshoot fails");
Check(!Acceptance.Completion(100, 100, true, true).Passed, "timeout wins at target");
Check(!Acceptance.Completion(100, 100, false, false).Passed, "live guest cannot pass");
Check(Acceptance.Completion(100, 100, false, true).Passed, "exact frame completion");

Check(InputSchedule.TryParse("0:cross:20,5:up:2,10:none,30:down", out var schedule, out _), "parse holds and releases");
Check(schedule.At(0) == unchecked((ushort)~Controller.Cross) && schedule.At(4) == unchecked((ushort)~Controller.Cross), "hold boundary");
Check(schedule.At(5) == unchecked((ushort)~Controller.Up), "latest step replaces prior hold");
Check(schedule.At(7) == 0xffff && schedule.At(19) == 0xffff, "old hold cannot resume after release");
Check(schedule.At(41) == unchecked((ushort)~Controller.Down) && schedule.At(42) == 0xffff, "legacy twelve-frame press");
foreach (var input in new[] { "1:wat", "1:cross:0", "1:up,1:down", "-1:cross", "1:", "1:up,", "1:none+up", "1:None+up" })
    Check(!InputSchedule.TryParse(input, out _, out _), $"reject malformed input {input}");
foreach (var runArgs in new[] { new[] { "--smoke", "NaN" }, new[] { "--frames", "0" },
    new[] { "--timeout", "Infinity" }, new[] { "--frames", "2", "--smoke", "1" } })
    Check(!Options.TryParse(runArgs, out _, out _), "reject invalid run bounds");

var originalOut = Console.Out;
var originalError = Console.Error;
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
ConsoleMirror.Install();
using (var accumulator = new RunAccumulator())
{
    Console.Error.WriteLine("[Runtime] runtime has crashed: regression");
    Console.Error.WriteLine("[Dispatcher] skipped an unmapped call");
    Console.Error.WriteLine("[Event] Test listener throwed: regression");
    for (var i = 0; i < 5000; i++) Console.WriteLine("ordinary later output");
    var ring = new List<string>();
    ConsoleMirror.SnapshotInto(ring);
    var result = accumulator.Snapshot();
    Console.SetOut(originalOut);
    Console.SetError(originalError);
    Check(!RunReport.Analyse(ring).Failed, "negative control: UI ring lost initial errors");
    Check(result.Crashed && result.UnmappedCalls == 1 && result.ListenerErrors == 1,
        "run-long failures survive log rollover");
}
Check(!AudioVerification.Verify().Passed, "missing audio cannot pass");
AudioVerification.Observe(new short[] { 0, 0 });
Check(!AudioVerification.Verify().Passed, "silent audio cannot pass");
AudioVerification.Observe(new short[] { short.MinValue, 1 });
Check(AudioVerification.Verify().Passed, "passive output accepts signed PCM without overflow");

OverlayPolicy.Attach();
var names = new[] { "main", "title", "stage1", "stage2", "stage3", "stage4", "stage5", "stage6" };
for (var i = 0; i < names.Length; i++) Dispatcher.Register(names[i], new FakeOverlay(names[i], i));
Dispatcher.Load("main");
Dispatcher.Load("stage1");
var overlays = OverlayVerification.Verify();
Check(overlays.Passed, overlays.Detail);
Check(Dispatcher.ActiveNames.SequenceEqual(new[] { "main", "stage1" }), "synthetic verification restores active maps");
GpuReadbackChecks.Run(Check);
CoverageChecks.Run(Check);
DecoderCopyChecks.Run(Check);
var keys = new RecompOne.Runtime.Host.KeyPulseLatch();
Check(!keys.IsPressed(257, 10), "keyboard pulse initially released");
keys.Press(257, 10);
Check(keys.IsPressed(257, 10) && keys.IsPressed(257, 11), "short tap survives the next guest frame");
Check(!keys.IsPressed(257, 12), "short tap expires without sticking");
keys.Press(257, 11);
Check(keys.IsPressed(257, 12) && !keys.IsPressed(257, 13), "new press refreshes the pulse");
Check(!keys.IsPressed(90, 11), "unrelated key is not pressed");
keys.Clear();
Check(!keys.IsPressed(257, 11), "keyboard reset releases pulses");
var hostError = new InvalidOperationException("expected host failure regression");
var guestError = new InvalidOperationException("expected guest failure regression");
RecompOne.Runtime.Runtime.ReportGameFailure(guestError);
RecompOne.Runtime.Runtime.ReportGameFailure(new Exception("later guest error"));
Check(ReferenceEquals(RecompOne.Runtime.Runtime.GameFailure, guestError), "guest failure survives caller-thread resumption");
RecompOne.Runtime.Runtime.ReportHostFailure(hostError);
RecompOne.Runtime.Runtime.ReportHostFailure(new Exception("later error"));
Check(ReferenceEquals(RecompOne.Runtime.Runtime.HostFailure, hostError), "first host event error remains authoritative");
Console.WriteLine($"ALL {count} CHECKS PASSED");

sealed class FakeOverlay(string name, int index) : IOverlay
{
    public string Name => name;
    public int LbaStart => index == 0 ? -1 : 100 + index;
    public uint Base => index == 0 ? 0u : 0x80100000u;
    public uint Size => index == 0 ? 0u : (uint)(0x1000 + index * 0x100);
    public IReadOnlyDictionary<uint, Action<CpuContext, IMemory>> Functions { get; } =
        new Dictionary<uint, Action<CpuContext, IMemory>>
        {
            [index == 0 ? 0x80010000u : 0x80100000u] = (_, _) => { },
            [index == 0 ? 0x80010004u : 0x80100004u + (uint)index * 4] = (_, _) => { }
        };
}
