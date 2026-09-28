using RecompOne.Runtime.Events;

namespace RE15pc;

/// <summary>Frame-scheduled BIOS input, in the byte order PadReadEvent exposes.</summary>
public static class ScriptedInput
{
    private static InputSchedule? _schedule;
    private static long _frame, _reads;
    private static bool _attached, _trace;
    private static ushort? _last;
    private static readonly List<object> Changes = [];
    public static void Attach(string script, bool trace = false)
    {
        if (_attached) return;
        if (!string.IsNullOrWhiteSpace(script))
        {
            if (!InputSchedule.TryParse(script, out var schedule, out var error))
                throw new ArgumentException(error);
            _schedule = schedule;
        }
        _attached = true;
        _trace = trace;
        Event.AddListener<VSyncEvent>(e => _frame = e.Frame);
        Event.AddListener<PadReadEvent>(OnPadRead);
    }
    private static void OnPadRead(PadReadEvent e)
    {
        if (e.Port != 0) return;
        _reads++;
        // A VSync event occurs after its pad poll, so named step N affects the next poll.
        if (_schedule is not null) e.Buttons = Swap(_schedule.At(_frame));
        if (_last == e.Buttons) return;
        _last = e.Buttons;
        if (_trace)
        {
            Changes.Add(new { frame = _frame, buttons = $"0x{Swap(e.Buttons):X4}",
                biosButtons = $"0x{e.Buttons:X4}" });
            Console.WriteLine($"[input] completedFrame={_frame} nextPoll=0x{Swap(e.Buttons):X4} bios=0x{e.Buttons:X4}");
        }
    }
    public static object[] Trace() => Changes.ToArray();
    public static string Describe()
        => $"  input: steps={_schedule?.Steps.Count ?? 0}, reads={_reads}, completedFrame={_frame}";
    private static ushort Swap(ushort value) => (ushort)((value >> 8) | (value << 8));
}
