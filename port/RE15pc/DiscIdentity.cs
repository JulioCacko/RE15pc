using System.Security.Cryptography;
using RecompOne.Runtime.Cdrom;

namespace RE15pc;

/// <summary>
/// Refuses to boot against a disc that is not the prototype this port was built
/// for.
/// </summary>
/// <remarks>
/// Without this, a wrong image produces a crash deep inside recompiled code with
/// no indication that the input was wrong. That failure mode wastes more time
/// than the check costs.
///
/// Validation runs through <see cref="DiscFs"/>, the same reader the game itself
/// uses, so a disc that passes is a disc the runtime can actually read. Success is
/// cached by path because <c>Runtime.WaitForValidDisc</c> calls the validator in a
/// loop and re-reading a 124 MB image every frame would be absurd.
/// </remarks>
public static class DiscIdentity
{
    /// <summary>Byte length of the November 6, 1996 prototype image.</summary>
    public const long ExpectedLength = 124_300_848;

    /// <summary>SHA-256 recorded in disc-manifest.json.</summary>
    public const string ExpectedSha256 =
        "B5C26B6A5EC21FC93B16F904642DAACD527207A7460FFB8349077AA6426BC30D";

    /// <summary>
    /// Results are cached by path. <c>Runtime.WaitForValidDisc</c> calls the
    /// validator in a tight loop, and re-reading a 124 MB image every iteration
    /// would turn a rejection into an I/O storm. Rejections are cached too, and
    /// reported exactly once: without that, a rejected disc produces a window that
    /// simply hangs with no explanation, because the wait loop discards the reason
    /// string it is handed.
    /// </summary>
    private static readonly Dictionary<string, string?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RejectionsReported =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AcceptancesReported =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>
    /// Builds the validator to hand to <c>Runtime.DiscValidator</c>. Returns
    /// <c>null</c> when the disc is acceptable, or a human-readable reason when it
    /// is not.
    /// </summary>
    public static Func<string, string?> CreateValidator(bool fullHash)
    {
        return path =>
        {
            try
            {
                var full = Path.GetFullPath(path);

                string? reason;
                lock (Gate)
                {
                    if (!Cache.TryGetValue(full, out reason))
                    {
                        reason = Inspect(full, fullHash);
                        Cache[full] = reason;
                    }
                }

                if (reason != null)
                {
                    lock (Gate)
                    {
                        if (RejectionsReported.Add(full))
                        {
                            Console.Error.WriteLine($"[DiscIdentity] REJECTED {full}");
                            Console.Error.WriteLine($"[DiscIdentity] reason: {reason}");
                            Console.Error.WriteLine(
                                "[DiscIdentity] the port will keep waiting for a valid disc; " +
                                "pass --cue or fix the image.");
                        }
                    }
                    return reason;
                }

                lock (Gate)
                {
                    if (AcceptancesReported.Add(full))
                        Console.WriteLine($"[DiscIdentity] accepted {full}");
                }
                return null;
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the wait loop.
                return ex.Message;
            }
        };
    }

    private static string? Inspect(string path, bool fullHash)
    {
        if (!File.Exists(path)) return $"disc image not found: {path}";

        // The path handed in is the cue sheet: a few dozen bytes of text. The file
        // whose size and hash matter is the binary that cue references, so resolve
        // it first. Comparing the cue's own length against the expected image
        // length rejects every valid disc, which is exactly the bug this comment
        // exists to prevent returning.
        var (imagePath, cueError) = ResolveImage(path);
        if (imagePath == null) return cueError;

        if (!File.Exists(imagePath))
            return $"cue sheet references '{Path.GetFileName(imagePath)}', which does not exist.";

        var length = new FileInfo(imagePath).Length;
        if (length != ExpectedLength)
        {
            return $"wrong disc image: {Path.GetFileName(imagePath)} is {length} bytes, " +
                   $"expected {ExpectedLength}. This port requires the Biohazard 2 " +
                   "(November 6, 1996) prototype.";
        }

        if (fullHash)
        {
            using var stream = File.OpenRead(imagePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return $"disc sha256 {hash} does not match the recorded {ExpectedSha256}.";
        }

        using var fs = DiscFs.Open(path);

        // The boot executable must carry a PS-X EXE header at the offset the
        // recompiler configured. Read through DiscFs so this exercises the same
        // ISO9660 path the game will.
        byte[] exe;
        try
        {
            exe = fs.ReadFile("PSX.EXE");
        }
        catch (Exception ex)
        {
            return $"disc has no readable PSX.EXE: {ex.Message}";
        }

        if (exe.Length < 8 || !exe.AsSpan(0, 8).SequenceEqual("PS-X EXE"u8))
        {
            return "disc PSX.EXE does not carry a PS-X EXE header; wrong image.";
        }

        // The seven overlays the configuration declares must all be present, or
        // the recompiled build would register dispatch tables for files that can
        // never load.
        string[] overlays =
        [
            "PSX/BIN/STAGE1.BIN", "PSX/BIN/STAGE2.BIN", "PSX/BIN/STAGE3.BIN",
            "PSX/BIN/STAGE4.BIN", "PSX/BIN/STAGE5.BIN", "PSX/BIN/STAGE6.BIN",
            "PSX/BIN/TITLE.BIN"
        ];

        foreach (var overlay in overlays)
            if (!fs.Exists(overlay))
                return $"disc is missing required overlay {overlay}; wrong image.";

        return null;
    }

    /// <summary>
    /// Finds the binary a cue sheet points at, by reading its <c>FILE</c> line the
    /// way the runtime's own cue parser does.
    /// </summary>
    private static (string? ImagePath, string? Error) ResolveImage(string cuePath)
    {
        // Anything that is not a cue sheet is passed straight through and left for
        // DiscFs to accept or reject on its own terms.
        if (!cuePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            return (cuePath, null);

        var dir = Path.GetDirectoryName(cuePath) ?? string.Empty;

        foreach (var raw in File.ReadLines(cuePath))
        {
            var line = raw.Trim();
            if (!line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;

            var open = line.IndexOf('"');
            var close = line.LastIndexOf('"');
            if (open < 0 || close <= open) continue;

            return (Path.GetFullPath(Path.Combine(dir, line[(open + 1)..close])), null);
        }

        return (null, $"cue sheet {Path.GetFileName(cuePath)} has no FILE entry, " +
                      "so the disc image it describes cannot be identified.");
    }
}
