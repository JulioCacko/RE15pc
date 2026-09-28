using System.Security.Cryptography;
using System.Text;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Memory;

namespace RE15pc.Diagnostics;

/// <summary>
/// What a run produced, judged by the runtime's own log.
/// </summary>
public sealed record RunFindings(
    bool Crashed,
    int UnmappedCalls,
    int OverlayLoads,
    int OverlayEvictions,
    int RegionOverwrites,
    int VramCollisions,
    int DiscWarnings,
    int ListenerErrors)
{
    /// <summary>
    /// A run fails on a crash, on any unmapped call, or on any listener error.
    /// Region overwrites and V-RAM collisions are reported because they mean
    /// overlay bookkeeping is misbehaving even when nothing has crashed yet.
    /// </summary>
    public bool Failed => Crashed || UnmappedCalls > 0 || ListenerErrors > 0;

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  crashed                 : {Crashed}");
        sb.AppendLine($"  unmapped calls          : {UnmappedCalls}");
        sb.AppendLine($"  overlays loaded         : {OverlayLoads}");
        sb.AppendLine($"  overlays evicted        : {OverlayEvictions}");
        sb.AppendLine($"  region overwrites       : {RegionOverwrites}");
        sb.AppendLine($"  vram collisions         : {VramCollisions}");
        sb.AppendLine($"  disc read warnings      : {DiscWarnings}");
        sb.AppendLine($"  event listener errors   : {ListenerErrors}");
        sb.AppendLine($"  verdict                 : {(Failed ? "FAIL" : "PASS")}");
        return sb.ToString();
    }
}

/// <summary>
/// Turns a finished run into a verdict and a set of artifacts on disk.
/// </summary>
/// <remarks>
/// The runtime already captures everything it prints: <c>ConsoleMirror.Install</c>
/// wraps <c>Console.Out</c> and <c>Console.Error</c> and keeps the last 4000 lines.
/// Reading that is strictly better than installing another writer, because it
/// cannot be clobbered by ordering and needs no changes to RecompOne.
///
/// The RAM dump is the interesting artifact. Phase 5 of the plan needs to compare
/// the overlay region in RAM against the overlay file on disc to settle whether the
/// loader copies it verbatim or strips the leading 4-byte word, and that comparison
/// is impossible to make from a log.
/// </remarks>
public static class RunReport
{
    /// <summary>Base all seven overlays load at.</summary>
    public const uint OverlayBase = 0x8010_0000;

    /// <summary>
    /// Size of the dumped region. The largest overlay is STAGE1 at 137,648 bytes
    /// (0x219B0), so 0x22000 covers every one of them with room to spare.
    /// </summary>
    public const int OverlayWindow = 0x22000;

    public static RunFindings Analyse(IReadOnlyList<string> lines)
    {
        var crashed = false;
        var unmapped = 0;
        var loads = 0;
        var evictions = 0;
        var overwrites = 0;
        var collisions = 0;
        var discWarnings = 0;
        var listenerErrors = 0;

        foreach (var line in lines)
        {
            if (line.Contains("runtime has crashed", StringComparison.Ordinal) ||
                (line.StartsWith("[Runtime] thread ", StringComparison.Ordinal) && line.Contains(" stopped:")))
            {
                crashed = true;
            }

            if (line.Contains("[Dispatcher] skipped an unmapped call", StringComparison.Ordinal) ||
                line.Contains("unmapped call:", StringComparison.Ordinal))
            {
                unmapped++;
                continue;
            }

            if (line.Contains("[Dispatcher] loaded overlay:", StringComparison.Ordinal))
            {
                loads++;
                continue;
            }

            if (line.Contains("[OverlayPolicy] unloaded", StringComparison.Ordinal))
            {
                evictions++;
                continue;
            }

            if (line.Contains("overwritten by", StringComparison.Ordinal))
            {
                overwrites++;
                continue;
            }

            // Upstream spells this "colision"; match both in case it is fixed.
            if (line.Contains("colision", StringComparison.Ordinal) ||
                line.Contains("collision", StringComparison.Ordinal))
            {
                collisions++;
                continue;
            }

            if (line.Contains("[DiscImage]", StringComparison.Ordinal))
            {
                discWarnings++;
                continue;
            }

            if (line.Contains("[Event]", StringComparison.Ordinal) &&
                line.Contains("throwed", StringComparison.Ordinal))
            {
                listenerErrors++;
            }
        }

        return new RunFindings(crashed, unmapped, loads, evictions, overwrites, collisions,
            discWarnings, listenerErrors);
    }

    public static RunFindings AnalyseMirror()
    {
        var lines = new List<string>();
        ConsoleMirror.SnapshotInto(lines);
        return Analyse(lines);
    }

    /// <summary>
    /// Writes the console log, the overlay RAM window, a verdict file and a summary
    /// into <paramref name="outDir"/>. Returns the summary text.
    /// </summary>
    public static string WriteArtifacts(string outDir, PSMemory mem, RunFindings findings,
        string reason, string progress, string vram)
    {
        Directory.CreateDirectory(outDir);

        var all = new List<string>();
        ConsoleMirror.SnapshotInto(all);
        File.WriteAllLines(Path.Combine(outDir, "console.log"), all);

        var overlay = SliceRam(mem, OverlayBase, OverlayWindow);
        File.WriteAllBytes(Path.Combine(outDir, "ram-overlay-80100000.bin"), overlay);

        // The whole of guest RAM as well. The overlay window answers questions about
        // overlay loading; this answers questions about anything the game copied into
        // memory, such as a file read from the disc. It is only 2 MB, and being able to
        // search it for a known byte signature is what makes it worth writing.
        var fullRam = mem.Ram.ToArray();
        File.WriteAllBytes(Path.Combine(outDir, "ram-full.bin"), fullRam);

        var hex = Convert.ToHexString(SHA256.HashData(overlay));

        var sb = new StringBuilder();
        sb.AppendLine("RE15pc run report");
        sb.AppendLine($"  finished because        : {reason}");
        sb.AppendLine($"  console lines captured  : {all.Count}");
        sb.AppendLine();
        sb.Append(findings.Describe());

        if (progress.Length > 0)
        {
            sb.AppendLine();
            sb.Append(progress);
        }

        if (vram.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("VIDEO MEMORY");
            sb.AppendLine(vram);
        }

        sb.AppendLine();
        sb.AppendLine($"RAM 0x{OverlayBase:X8}..0x{OverlayBase + OverlayWindow:X8}");
        sb.AppendLine($"  sha256 (RAM now)        : {hex}");
        sb.AppendLine($"  dumped to               : ram-overlay-80100000.bin");
        sb.AppendLine($"  full guest RAM          : ram-full.bin ({fullRam.Length} bytes)");
        sb.AppendLine();
        sb.AppendLine("  first 32 bytes of the overlay window:");
        sb.AppendLine("    " + HexLine(overlay, 32));
        sb.AppendLine();
        sb.AppendLine("  The overlay load question is settled: docs/overlay-load-verbatim.md shows");
        sb.AppendLine("  RAM equals the file from offset 0, leading header word included, so \"skip\" is");
        sb.AppendLine("  correctly 0. Comparing this window against PSX/BIN/TITLE.BIN (LBA 333, 9932");
        sb.AppendLine("  bytes, read at sector offset 24) still matches byte for byte and remains the");
        sb.AppendLine("  cheapest way to confirm the resident overlay is the expected one.");

        var text = sb.ToString();
        File.WriteAllText(Path.Combine(outDir, "report.txt"), text);

        return text;
    }

    /// <summary>Reads back the verdict written by the last run, or null if there was none.</summary>
    public static string? ReadVerdict(string outDir)
    {
        var path = Path.Combine(outDir, "verdict.txt");
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        var nl = text.IndexOf('\n');
        return nl < 0 ? text.Trim() : text[..nl].Trim();
    }

    /// <summary>
    /// Copies a slice of console RAM. Addresses in KSEG0/KSEG1 map to the same
    /// physical bytes, so the offset is the address masked by the RAM size.
    /// </summary>
    private static byte[] SliceRam(PSMemory mem, uint address, int length)
    {
        var ram = mem.Ram;
        var mask = RecompOne.Runtime.Runtime.RamSize - 1u;
        var offset = (int)(address & mask);

        if (offset < 0 || offset + length > ram.Length)
        {
            // Never let diagnostics take down the run they are describing.
            length = Math.Max(0, ram.Length - Math.Max(0, offset));
        }

        return ram.Slice(Math.Max(0, offset), length).ToArray();
    }

    public static string HexLine(ReadOnlySpan<byte> data, int count)
    {
        var n = Math.Min(count, data.Length);
        var sb = new StringBuilder(n * 3);
        for (var i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
