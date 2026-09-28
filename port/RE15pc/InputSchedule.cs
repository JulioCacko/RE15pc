using System.Globalization;
using RecompOne.Runtime.Hardware;

namespace RE15pc;

public sealed record InputStep(long Frame, ushort Buttons, long Duration);

/// <summary>Latest step replaces previous holds; outside a hold all buttons are released.</summary>
public sealed class InputSchedule(IReadOnlyList<InputStep> steps)
{
    public IReadOnlyList<InputStep> Steps { get; } = steps;
    public ushort At(long frame)
    {
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            var step = Steps[i];
            if (frame < step.Frame) continue;
            return frame - step.Frame < step.Duration ? step.Buttons : (ushort)0xffff;
        }
        return 0xffff;
    }

    public static bool TryParse(string text, out InputSchedule schedule, out string error)
    {
        var steps = new List<InputStep>();
        schedule = new(steps);
        error = "";
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var fields = raw.Split(':');
            long duration = 12;
            if (fields.Length is < 2 or > 3 ||
                !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var frame) ||
                frame < 0 || (fields.Length == 3 &&
                (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out duration) || duration <= 0)) ||
                steps.Any(s => s.Frame == frame))
            { error = $"invalid or duplicate input step: '{raw}' (frame:buttons[:duration])"; return false; }
            ushort buttons = 0xffff;
            foreach (var name in fields[1].Split('+', StringSplitOptions.TrimEntries))
            {
                var bit = name.ToLowerInvariant() switch
                {
                    "none" => 0, "select" => Controller.Select, "start" => Controller.Start,
                    "up" => Controller.Up, "down" => Controller.Down, "left" => Controller.Left,
                    "right" => Controller.Right, "l1" => Controller.L1, "r1" => Controller.R1,
                    "l2" => Controller.L2, "r2" => Controller.R2, "l3" => Controller.L3,
                    "r3" => Controller.R3, "triangle" => Controller.Triangle,
                    "circle" => Controller.Circle, "cross" => Controller.Cross, "square" => Controller.Square,
                    _ => -1
                };
                if (bit < 0 || (name.Equals("none", StringComparison.OrdinalIgnoreCase) && fields[1].Contains('+')))
                { error = $"invalid button in '{raw}'"; return false; }
                buttons &= (ushort)~bit;
            }
            steps.Add(new(frame, buttons, duration));
        }
        steps.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        return steps.Count > 0;
    }
}
