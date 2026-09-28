using RecompOne.Runtime.Dispatch;

namespace RE15pc.Diagnostics;

/// <summary>Synthetic dispatch-map checks. These do not establish gameplay coverage.</summary>
public static class OverlayVerification
{
    public static CheckResult Verify()
    {
        var registered = Dispatcher.Overlays;
        var expected = new[] { "main", "title", "stage1", "stage2", "stage3", "stage4", "stage5", "stage6" };
        var failures = new List<string>();
        if (!expected.All(registered.ContainsKey) || registered.Count != expected.Length)
            return new("overlays", false, "expected main and exactly seven disc overlays");
        var before = Dispatcher.ActiveNames;
        var pairs = 0;
        try
        {
            foreach (var previous in expected.Skip(1))
            foreach (var next in expected.Skip(1))
            {
                Dispatcher.Reset();
                Dispatcher.Load("main");
                var outgoing = registered[previous];
                var incoming = registered[next];
                Dispatcher.Load(previous);
                Dispatcher.LoadByLba(incoming.LbaStart);
                if (previous != next && Dispatcher.ActiveNames.Contains(next))
                    failures.Add($"{previous}->{next}: activated before RAM write");
                Dispatcher.NotifyWrite((incoming.Base & 0x1fffffff) + 0x800);
                if (previous != next && Dispatcher.ActiveNames.Contains(next))
                    failures.Add($"{previous}->{next}: activated outside promotion window");
                Dispatcher.NotifyWrite(incoming.Base & 0x1fffffff);
                if (!Dispatcher.ActiveNames.Order().SequenceEqual(new[] { "main", next }.Order()))
                    failures.Add($"{previous}->{next}: wrong active overlays");
                if (incoming.Functions.Keys.Any(a => !Dispatcher.CanCall(a)))
                    failures.Add($"{previous}->{next}: missing incoming function");
                if (registered["main"].Functions.Keys.Any(a => !Dispatcher.CanCall(a)))
                    failures.Add($"{previous}->{next}: resident function evicted");
                if (outgoing.Functions.Keys.Except(incoming.Functions.Keys)
                    .Except(registered["main"].Functions.Keys).Any(Dispatcher.CanCall))
                    failures.Add($"{previous}->{next}: stale outgoing function");
                pairs++;
            }
        }
        finally
        {
            Dispatcher.Reset();
            foreach (var name in before) Dispatcher.Load(name);
        }
        return new("overlays", failures.Count == 0,
            $"synthetic pairs={pairs}, failures={failures.Count}; " + string.Join("; ", failures));
    }
}
