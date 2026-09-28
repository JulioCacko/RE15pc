using RecompOne.Runtime;
using RecompOne.Runtime.Assets;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;


namespace RE15pc.Diagnostics;

/// <summary>
/// Dumps video memory to disk, from every source that can provide it, and in a form that can
/// be read without an image viewer.
/// </summary>
/// <remarks>
/// There are two views of VRAM and this project has now been misled by each of them once.
///
/// - **The software shadow**, <c>Gpu.Vram</c>. Authoritative for anything the CPU uploaded,
///   and for VRAM-to-VRAM copies, which the command decoder performs itself. Whether it also
///   receives *rasterised* output depends on the backend.
/// - **The backend**, read through <c>IGpuBackend.ReadVram</c> on the GL thread.
///
/// Each was separately believed to be the truth and each produced a different black frame. The
/// lesson is not to pick one, so this writes both and reports the statistics for both. A run in
/// which the two disagree is itself the finding.
///
/// Each source also gets a luminance text render, because the PNG needs an image-capable reader
/// to interpret and the whole point of the artifact is that it can be checked from a log.
/// </remarks>
public static class VramDump
{
    private const int AsciiCols = 100;
    private const int AsciiRows = 38;

    /// <summary>Dark to bright. Index 0 is a space so empty areas read as blank.</summary>
    private const string AsciiRamp = " .:-=+*#%@";

    /// <summary>Dumps VRAM from every available source and returns a readable summary.</summary>
    public static string Dump(Gpu? gpu, string outDir)
    {
        if (gpu is null) return "  VRAM                    : unavailable (Runtime.Gpu is null)";

        const int width = Gpu.VramWidth;
        const int height = Gpu.VramHeight;

        Directory.CreateDirectory(outDir);

        var sources = new List<(string Label, ushort[] Data)>
        {
            ("shadow", gpu.Vram)
        };

        var (glData, glNote) = TryReadBackend(width, height);
        if (glData != null) sources.Add(("gl", glData));

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"  vram sources            : {string.Join(", ", sources.Select(s => s.Label))}" +
                           (glNote.Length > 0 ? $"  ({glNote})" : ""));
        summary.AppendLine($"  VRAM size               : {width}x{height}");
        summary.AppendLine($"  display enabled         : {gpu.DisplayEnabled}");

        var dx = gpu.DisplayX;
        var dy = gpu.DisplayY;
        var dw = Math.Clamp(gpu.DisplayWidth, 0, width);
        var dh = Math.Clamp(gpu.DisplayHeight, 0, height);

        summary.AppendLine($"  display area            : {dw}x{dh} at VRAM ({dx},{dy})" +
                           (gpu.Pal ? ", PAL" : ", NTSC") +
                           (gpu.Display24Bit ? ", 24-bit" : ""));

        if (dw <= 0 || dh <= 0)
        {
            summary.AppendLine("  framebuffer             : not dumped, display area is empty");
            return summary.ToString().TrimEnd();
        }

        foreach (var (label, data) in sources)
        {
            if (data.Length < width * height)
            {
                summary.AppendLine($"  [{label}] unexpected size {data.Length}, wanted {width * height}");
                continue;
            }

            try
            {
                var full = ToRgba(data, width, height, 0, 0, width, height);
                PngWriter.WriteRgba(Path.Combine(outDir, $"vram-{label}.png"), full, width, height);

                var raw = new byte[data.Length * 2];
                Buffer.BlockCopy(data, 0, raw, 0, raw.Length);
                File.WriteAllBytes(Path.Combine(outDir, $"vram-{label}.rgb555.bin"), raw);

                var crop = ToRgba(data, width, height, dx, dy, dw, dh);
                PngWriter.WriteRgba(Path.Combine(outDir, $"framebuffer-{label}.png"), crop, dw, dh);

                var lit = 0;
                var colors = new HashSet<int>();
                for (var i = 0; i < crop.Length; i += 4)
                {
                    if (crop[i] != 0 || crop[i + 1] != 0 || crop[i + 2] != 0) lit++;
                    colors.Add((crop[i] << 16) | (crop[i + 1] << 8) | crop[i + 2]);
                }

                var total = dw * dh;
                summary.AppendLine($"  [{label}] display crop    : {lit}/{total} non-black " +
                                   $"({100.0 * lit / total:0.0}%), {colors.Count} distinct colours");

                var ascii = RenderAscii(crop, dw, dh, AsciiCols, AsciiRows);
                File.WriteAllText(Path.Combine(outDir, $"framebuffer-{label}-ascii.txt"), ascii);

                // The GL source is the one that reflects what a rasteriser produced, so prefer it
                // for the canonical artifact when it exists.
                if (label == "gl" || sources.Count == 1)
                    File.WriteAllText(Path.Combine(outDir, "framebuffer-ascii.txt"), ascii);
            }
            catch (Exception ex)
            {
                summary.AppendLine($"  [{label}] dump failed    : {ex.Message}");
            }
        }

        // Render whichever source was written as canonical, so the report is self-contained.
        var primary = sources.FirstOrDefault(s => s.Label == "gl");
        if (primary.Data == null) primary = sources[0];

        if (primary.Data.Length >= width * height)
        {
            summary.AppendLine();
            summary.AppendLine($"  display area from '{primary.Label}' as luminance text " +
                               $"({AsciiCols}x{AsciiRows}, '{AsciiRamp[0]}' = dark, '{AsciiRamp[^1]}' = bright):");
            summary.Append(RenderAscii(ToRgba(primary.Data, width, height, dx, dy, dw, dh), dw, dh,
                AsciiCols, AsciiRows).TrimEnd());
        }

        return summary.ToString().TrimEnd();
    }

    /// <summary>
    /// Reads VRAM through the GPU backend, on the thread that owns the GL context.
    /// </summary>
    /// <remarks>
    /// GL work has to happen on the owning thread, so this hands the read to the GPU job queue
    /// the presentation loop drains. That queue has no timeout and stops being drained once the
    /// guest thread ends, so the wait is bounded here and the read is skipped rather than
    /// hanging the run it is diagnosing.
    /// </remarks>
    /// <summary>Reads the whole of the backend's VRAM.</summary>
    internal static (ushort[]? Data, string Note) TryReadBackend(int width, int height)
    {
        if (GpuHle.Backend is not { Ready: true } backend)
            return (null, "no ready backend");

        if (!GpuJobs.Claimed)
            return (null, "GPU job queue not claimed");

        var buffer = new ushort[width * height];
        if (GpuJobs.IsOwner)
        {
            backend.ReadVram(0, 0, width, height, buffer);
            return (buffer, "");
        }
        Exception? failure = null;
        var done = new ManualResetEventSlim(false);

        var reader = new Thread(() =>
        {
            try
            {
                GpuJobs.Run(() => backend.ReadVram(0, 0, width, height, buffer));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "vram-read"
        };

        reader.Start();

        return done.Wait(TimeSpan.FromSeconds(5))
            ? (failure is null ? (buffer, "") : (null, $"backend read failed: {failure.Message}"))
            : (null, "backend read timed out");
    }

    /// <summary>
    /// Downsamples an RGBA image onto a grid of characters by average luminance.
    /// </summary>
    private static string RenderAscii(byte[] rgba, int w, int h, int cols, int rows)
    {
        var sb = new System.Text.StringBuilder((cols + 1) * rows);

        for (var ry = 0; ry < rows; ry++)
        {
            var y0 = ry * h / rows;
            var y1 = Math.Max(y0 + 1, (ry + 1) * h / rows);

            for (var rx = 0; rx < cols; rx++)
            {
                var x0 = rx * w / cols;
                var x1 = Math.Max(x0 + 1, (rx + 1) * w / cols);

                long sum = 0;
                var n = 0;

                for (var y = y0; y < y1 && y < h; y++)
                {
                    for (var x = x0; x < x1 && x < w; x++)
                    {
                        var d = (y * w + x) * 4;
                        sum += (rgba[d] * 299 + rgba[d + 1] * 587 + rgba[d + 2] * 114) / 1000;
                        n++;
                    }
                }

                var lum = n == 0 ? 0 : (int)(sum / n);
                var idx = Math.Clamp(lum * (AsciiRamp.Length - 1) / 255, 0, AsciiRamp.Length - 1);
                sb.Append(AsciiRamp[idx]);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Expands PS1 16-bit pixels (bit 15 mask, then 5 bits each of B, G, R, low to high) into
    /// RGBA8. The 5-to-8 bit expansion replicates the high bits into the low ones so that full
    /// scale stays full scale.
    /// </summary>
    private static byte[] ToRgba(ushort[] vram, int vramWidth, int vramHeight,
        int x0, int y0, int w, int h)
    {
        var rgba = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            var sy = (y0 + y) & (vramHeight - 1);

            for (var x = 0; x < w; x++)
            {
                var sx = (x0 + x) & (vramWidth - 1);
                var px = vram[sy * vramWidth + sx];

                var r5 = px & 0x1F;
                var g5 = (px >> 5) & 0x1F;
                var b5 = (px >> 10) & 0x1F;

                var d = (y * w + x) * 4;
                rgba[d] = (byte)((r5 << 3) | (r5 >> 2));
                rgba[d + 1] = (byte)((g5 << 3) | (g5 >> 2));
                rgba[d + 2] = (byte)((b5 << 3) | (b5 >> 2));
                rgba[d + 3] = 0xFF;
            }
        }

        return rgba;
    }
}
