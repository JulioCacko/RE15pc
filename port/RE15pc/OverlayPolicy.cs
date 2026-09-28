using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;

namespace RE15pc;

/// <summary>
/// Keeps the overlay table consistent with the way this game uses RAM.
/// </summary>
/// <remarks>
/// All seven overlays load at base <c>0x80100000</c>: they are alternate images
/// of one region, and each one overwrites the last.
///
/// RecompOne's own bookkeeping, in <c>Dispatcher.HandleRegionOverwrites</c>, only
/// retires an active overlay when the incoming one fully <em>covers</em> its
/// region. That is the wrong test here. <c>title</c> is 9,932 bytes and
/// <c>stage1</c> is 137,648, so loading <c>title</c> after <c>stage1</c> leaves
/// every <c>stage1</c> function above <c>0x801026CC</c> still mapped in the
/// dispatcher while the RAM those functions refer to has been replaced by title
/// data. Calls into that range then execute one overlay's code against another
/// overlay's memory, which shows up as corruption that depends on the order rooms
/// were visited - the worst kind of bug to chase.
///
/// The rule this class applies instead is the one the hardware actually implies:
/// two overlays sharing a base occupy the same bytes, so at most one can be
/// active. Anything else sharing the incoming base is unloaded.
///
/// This lives host-side on purpose. It uses only public dispatcher and event API,
/// so RecompOne stays a clean pinned checkout with no local patches.
/// </remarks>
public static class OverlayPolicy
{
    private static readonly object Gate = new();
    private static bool _attached;

    /// <summary>Number of overlays retired by this policy so far.</summary>
    public static int Evictions { get; private set; }

    /// <summary>
    /// Subscribes to overlay loads. Safe to call more than once; only the first
    /// call has any effect.
    /// </summary>
    public static void Attach()
    {
        lock (Gate)
        {
            if (_attached) return;
            _attached = true;
        }

        Event.AddListener<OverlayLoadedEvent>(OnOverlayLoaded);
    }

    private static void OnOverlayLoaded(OverlayLoadedEvent e)
    {
        if (!Dispatcher.Overlays.TryGetValue(e.Name, out var loaded)) return;

        // A zero base means the overlay has no declared region, which is how
        // "main" is registered. It genuinely coexists with everything else, so
        // leave it alone.
        if (loaded.Base == 0) return;

        // Snapshot: Dispatcher.Unload rebuilds internal state, so do not iterate
        // the live collection.
        foreach (var name in Dispatcher.ActiveNames)
        {
            if (string.Equals(name, e.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Dispatcher.Overlays.TryGetValue(name, out var other)) continue;
            if (other.Base != loaded.Base) continue;

            Dispatcher.Unload(name);
            Evictions++;

            Console.WriteLine(
                $"[OverlayPolicy] unloaded '{name}' - shares base 0x{loaded.Base:X8} with '{e.Name}'");
        }
    }
}
