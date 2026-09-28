using System.Text;
using RecompOne.Runtime.Dispatch;

namespace RE15pc.Diagnostics;

/// <summary>
/// Exercises overlay dispatch for every registered overlay and reports the result.
/// </summary>
/// <remarks>
/// The objective requires all seven overlays to dispatch correctly, and only three - main, title and
/// stage1 - have ever actually been loaded by playing the game, because reaching the later stages needs
/// gameplay that the rendering defect currently prevents. This tests the dispatch machinery itself,
/// which does not depend on gameplay at all.
///
/// The mechanism has two halves and both are exercised. <c>Dispatcher.Register</c> maps an overlay's
/// start LBA to its name, and an overlay with a non-zero base does not load on request: <c>LoadByLba</c>
/// only parks it in <c>_pending</c>, and <c>NotifyWrite</c> promotes it to a real load once the guest
/// writes inside the first 0x800 bytes of the overlay's base. That write is how the runtime detects
/// that the guest has finished copying an overlay into RAM, so testing the request without the write
/// would only test half of it.
///
/// Runs at the end of a run, after the guest has stopped, because it resets the dispatcher.
/// </remarks>
public static class OverlayVerification
{
    public static string Verify()
    {
        var sb = new StringBuilder();
        var overlays = Dispatcher.Overlays;

        sb.AppendLine($"  overlays registered     : {overlays.Count}");

        Dispatcher.Reset();

        var dispatched = 0;
        var deferred = 0;
        var entryPoint = 0;
        var failed = 0;

        foreach (var (name, overlay) in overlays.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var functions = overlay.Functions?.Count ?? 0;
            var baseAddress = overlay.Base;
            var lba = overlay.LbaStart;
            var how = "";

            if (lba < 0)
            {
                // No LBA means this is not loaded by a disc read: it is the executable's own code,
                // which Entry.cs loads explicitly. Testing it through LoadByLba would fail for a
                // reason that has nothing to do with dispatch.
                Dispatcher.Load(name);
                entryPoint++;
                how = "loaded by the entry point (no LBA)";
            }
            else
            {
                // Request the overlay. With a non-zero base this only parks it, matching what the
                // guest's CD read does; only a write into the base region turns it into a load.
                Dispatcher.LoadByLba(lba);

                var parkedOnly = !Dispatcher.ActiveNames.Contains(name);
                if (parkedOnly)
                {
                    deferred++;
                    // The write the guest would make to the overlay's base once it has copied it in.
                    Dispatcher.NotifyWrite(baseAddress & 0x1FFFFFFFu);
                }

                how = parkedOnly ? "dispatched via write to base" : "dispatched on request";
            }

            var active = Dispatcher.ActiveNames.Contains(name);
            if (active) dispatched++; else { failed++; how = "FAILED"; }

            sb.AppendLine($"    {name,-8} base=0x{baseAddress:X8}  lba={lba,6}  " +
                          $"functions={functions,5}  {how}");
        }

        sb.AppendLine($"  overlays dispatched     : {dispatched} of {overlays.Count}" +
                      $"  ({deferred} deferred until a write to base, {entryPoint} from the entry point, {failed} failed)");

        return sb.ToString().TrimEnd();
    }
}
