using System.Globalization;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;

namespace RE15pc;

/// <summary>
/// Presses buttons on a schedule, so an unattended run can drive the game forward.
/// </summary>
/// <remarks>
/// Without this the port can only reach the first screen that waits for a pad. The title
/// and character select both do, so nothing past them is reachable in a smoke run, which
/// makes every later phase untestable.
///
/// **How the input is injected.** <c>BiosB.FirePad</c> dispatches a <c>PadReadEvent</c> and
/// then returns <c>e.Buttons</c> rather than the value it was given:
///
/// <code>
/// e.Buttons = buttons;
/// Event.Dispatch(e);
/// return e.Buttons;      // a listener may have replaced it
/// </code>
///
/// so replacing <c>e.Buttons</c> substitutes the pad value for this read. That is the
/// documented purpose of the event and needs no reflection and no fork.
///
/// Two earlier approaches were wrong and are recorded here so they are not retried:
///
/// - Writing <c>Controller.State</c> races <c>InputManager.PollKeyboard</c>, which
///   rewrites that field from the real keyboard once per frame.
/// - Writing the libpad buffers in <c>LibPad</c> affects nothing here, because this game
///   polls through the **BIOS** pad driver, `BiosB.PadRead`, which reads
///   <c>Controller.State</c> directly and writes its own buffer. The libpad buffers are
///   not on the path the game uses.
///
/// **On byte order.** <c>PadRead</c> byte-swaps the controller state before handing it to
/// <c>FirePad</c>, and <c>PadCardIrq</c> does the same before unswapping the result. So the
/// domain of <c>e.Buttons</c> is byte-swapped in both call sites, and the desired state has
/// to be swapped to match. <c>0xFFFF</c> is symmetric, so "nothing pressed" needs no care;
/// a real button mask does.
///
/// Button bits are active low: <c>0xFFFF</c> is nothing pressed.
/// </remarks>
public static class ScriptedInput
{
    private sealed record Step(int Frame, ushort State, string Label);

    private static readonly List<Step> Steps = [];
    private static readonly HashSet<int> Started = [];

    private static bool _attached;
    private static long _lastFrame = -1;
    private static int _padReads;
    private static int _overrides;

    /// <summary>Frames a scripted press is held for.</summary>
    private const int HoldFrames = 12;

    /// <summary>Active-low "nothing pressed".</summary>
    private const ushort Released = 0xFFFF;

    /// <summary>
    /// Default script for getting through the first pad-driven screens unattended.
    /// </summary>
    public const string DefaultScript =
        "90:start,240:cross,390:cross,540:cross,690:cross,840:cross,990:cross,1140:cross";

    /// <summary>Attaches the driver. Safe to call more than once.</summary>
    public static void Attach(string script)
    {
        if (_attached) return;
        _attached = true;

        if (!Parse(script))
        {
            Console.Error.WriteLine($"[input] no usable steps in '{script}'; input disabled");
            return;
        }

        Event.AddListener<VSyncEvent>(OnVSync);
        Event.AddListener<PadReadEvent>(OnPadRead);

        Console.WriteLine($"[input] scripted input active: {string.Join(", ", Steps.Select(s => s.Label))}");
    }

    private static bool Parse(string script)
    {
        Steps.Clear();

        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(':', 2);
            if (parts.Length != 2)
            {
                Console.Error.WriteLine($"[input] ignoring step '{raw}': expected <frame>:<buttons>");
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame) ||
                frame < 0)
            {
                Console.Error.WriteLine($"[input] ignoring step '{raw}': '{parts[0]}' is not a frame number");
                continue;
            }

            var state = Released;
            var ok = true;

            foreach (var name in parts[1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryBit(name, out var bit))
                {
                    state &= (ushort)~bit;
                }
                else
                {
                    Console.Error.WriteLine($"[input] ignoring step '{raw}': unknown button '{name}'");
                    ok = false;
                    break;
                }
            }

            if (ok) Steps.Add(new Step(frame, state, $"{frame}:{parts[1]}"));
        }

        Steps.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        return Steps.Count > 0;
    }

    private static bool TryBit(string name, out ushort bit)
    {
        switch (name.ToLowerInvariant())
        {
            case "select": bit = Controller.Select; return true;
            case "start": bit = Controller.Start; return true;
            case "up": bit = Controller.Up; return true;
            case "down": bit = Controller.Down; return true;
            case "left": bit = Controller.Left; return true;
            case "right": bit = Controller.Right; return true;
            case "l1": bit = Controller.L1; return true;
            case "r1": bit = Controller.R1; return true;
            case "l2": bit = Controller.L2; return true;
            case "r2": bit = Controller.R2; return true;
            case "l3": bit = Controller.L3; return true;
            case "r3": bit = Controller.R3; return true;
            case "triangle": bit = Controller.Triangle; return true;
            case "circle": bit = Controller.Circle; return true;
            case "cross": bit = Controller.Cross; return true;
            case "square": bit = Controller.Square; return true;
            default: bit = 0; return false;
        }
    }

    /// <summary>Number of steps whose hold window has begun.</summary>
    public static int Applied
    {
        get
        {
            lock (Started)
            {
                return Started.Count;
            }
        }
    }

    public static string Describe()
        => Steps.Count == 0
            ? "  scripted input          : disabled"
            : $"  scripted input          : {Applied}/{Steps.Count} step(s) fired, " +
              $"{_overrides} override(s) across {_padReads} pad read(s), last guest frame {_lastFrame}";

    /// <summary>
    /// Tracks the guest frame number. The pad for a frame is built before this fires, so a
    /// scripted step takes effect on the frame after the one it names. One frame of lag,
    /// deliberately, because counting pad reads instead would run the script faster than
    /// real frames whenever the game makes an extra BIOS pad call.
    /// </summary>
    private static void OnVSync(VSyncEvent e)
    {
        _lastFrame = e.Frame;

        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Frame > e.Frame) break;
            lock (Started)
            {
                Started.Add(i);
            }
        }
    }

    /// <summary>Substitutes the scripted pad value for this read.</summary>
    private static void OnPadRead(PadReadEvent e)
    {
        _padReads++;

        // Port 1 only: the second port is unused by this game and overriding it would be
        // inventing input nobody asked for.
        if (e.Port != 0) return;

        ushort? state = null;

        // The latest step whose hold window covers this frame wins.
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (_lastFrame < step.Frame) break;
            if (_lastFrame < step.Frame + HoldFrames) state = step.State;
        }

        if (state is null) return;

        // FirePad receives the already byte-swapped controller state, and the result is
        // written straight into the BIOS pad buffer, so match that domain.
        e.Buttons = Swap(state.Value);
        _overrides++;
    }

    private static ushort Swap(ushort v) => (ushort)((v >> 8) | (v << 8));
}
