using System.Text.Json;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Dispatch;

namespace RE15pc.Diagnostics;

public sealed record DiscFile(string Path, int Lba, int Size);
public sealed record FileReadCoverage(string Path, int Lba, int Size, int TotalSectors,
    int TouchedSectors, int CompleteSectors)
{
    public bool ExtentRead => TotalSectors > 0 && CompleteSectors == TotalSectors;
}

public static class ContentCoverage
{
    public static FileReadCoverage Measure(DiscFile file, IReadOnlyDictionary<int, int> reads)
    {
        var total = (file.Size + 2047) / 2048;
        var touched = 0;
        var complete = 0;
        for (var i = 0; i < total; i++)
        {
            var bytes = reads.GetValueOrDefault(file.Lba + i);
            if (bytes > 0) touched++;
            if (bytes >= Math.Min(2048, file.Size - i * 2048)) complete++;
        }
        return new(file.Path, file.Lba, file.Size, total, touched, complete);
    }

    public static CheckResult Write(string manifestPath, string output)
    {
        var snapshot = ExecutionCoverage.Snapshot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var reads = snapshot.Sectors.ToDictionary(s => s.Lba, s => s.Bytes);
        var files = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Where(f => f.GetProperty("kind").GetString() == "file")
            .Select(f => Measure(new(f.GetProperty("path").GetString()!,
                f.GetProperty("lba").GetInt32(), f.GetProperty("size").GetInt32()), reads)).ToArray();
        var functions = Dispatcher.Overlays.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o =>
        {
            var visited = snapshot.Functions.Where(f => f.Overlay == o.Key).Select(f => f.Address).ToHashSet();
            var registered = o.Value.Functions.Keys.Order().ToArray();
            return new { overlay = o.Key, registered = registered.Length,
                entered = registered.Where(visited.Contains).Select(a => $"0x{a:X8}").ToArray(),
                unentered = registered.Where(a => !visited.Contains(a)).Select(a => $"0x{a:X8}").ToArray() };
        }).ToArray();
        var unknown = snapshot.Functions.Where(f => !Dispatcher.Overlays.TryGetValue(f.Overlay, out var table) ||
            !table.Functions.ContainsKey(f.Address)).ToArray();
        var data = new
        {
            schemaVersion = 1,
            scope = "generated function entry and disc sector access; not branch, decoded asset, or playability coverage",
            branches = "not instrumented", containerMembers = "not enumerated",
            functions, unregisteredEntries = unknown, files, sectors = snapshot.Sectors
        };
        File.WriteAllText(Path.Combine(output, "coverage.json"), JsonSerializer.Serialize(data,
            new JsonSerializerOptions { WriteIndented = true }));
        var observed = files.Count(f => f.TouchedSectors > 0);
        return new("coverage-capture", snapshot.Functions.Any(f => f.Overlay == "main") && observed > 0 && unknown.Length == 0,
            $"{snapshot.Functions.Length} function entries, {observed}/{files.Length} file extents touched, " +
            $"{unknown.Length} unregistered entries; coverage capture is not a completion verdict");
    }
}
