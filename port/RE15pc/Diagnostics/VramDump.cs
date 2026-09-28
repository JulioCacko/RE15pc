using RecompOne.Runtime;
using RecompOne.Runtime.Assets;

namespace RE15pc.Diagnostics;

/// <summary>
/// Writes the PlayStation's video memory to PNG and to a text luminance render, so a
/// run can be inspected without a human watching the window.
/// </summary>
/// <remarks>
/// This reads <c>Gpu.Vram</c>, the software shadow copy, rather than going through the
/// OpenGL backend. Two reasons, both load-bearing: GL reads can only happen on the
/// thread that owns the context and this runs on a diagnostics thread; and the shadow
/// is the authoritative bytes the guest actually wrote, whereas the GL side is the
/// presentation copy.
///
/// The full 1024x512 VRAM image is dumped alongside a crop of the current display area,
/// because on this hardware textures, framebuffers and CLUTs all share VRAM. The full
/// image shows what was uploaded; the crop shows what would be on screen.
///
/// The same crop is also rendered as text. A PNG needs an image-capable reader to
/// interpret, and the whole point of the artifact is that it can be checked from a log.
///
/// A snapshot taken while the guest is writing can tear. That is acceptable for
/// diagnosis and is noted here rather than papered over.
/// </remarks>
public static class VramDump
{
    private const int AsciiCols = 100;
    private const int AsciiRows = 38;

    /// <summary>Dark to bright. Index 0 is a space so empty areas read as blank.</summary>
    private const string AsciiRamp = " .:-=+*#%@";

    /// <summary>Dumps VRAM and returns a short human-readable summary, render included.</summary>
    public static string Dump(Gpu? gpu, string outDir)
    {
        if (gpu is null) return "  VRAM                    : unavailable (Runtime.Gpu is null)";

        var vram = gpu.Vram;
        const int width = Gpu.VramWidth;
        const int height = Gpu.VramHeight;

        if (vram.Length < width * height)
            return $"  VRAM                    : unexpected size {vram.Length}, wanted {width * height}";

        Directory.CreateDirectory(outDir);

        try
        {
            var full = ToRgba(vram, width, height, 0, 0, width, height);
            PngWriter.WriteRgba(Path.Combine(outDir, "vram-full.png"), full, width, height);

            // Raw 16-bit pixels as well as the PNG. The PNG has already been converted
            // 15-bit to 8-bit, which throws away the low bits needed for an exact
            // comparison against a TIM texture from the disc; this keeps them.
            var raw = new byte[vram.Length * 2];
            Buffer.BlockCopy(vram, 0, raw, 0, raw.Length);
            File.WriteAllBytes(Path.Combine(outDir, "vram.rgb555.bin"), raw);
        }
        catch (Exception ex)
        {
            return $"  VRAM                    : full dump failed: {ex.Message}";
        }

        var dx = gpu.DisplayX;
        var dy = gpu.DisplayY;
        var dw = Math.Clamp(gpu.DisplayWidth, 0, width);
        var dh = Math.Clamp(gpu.DisplayHeight, 0, height);

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"  display enabled         : {gpu.DisplayEnabled}");
        summary.AppendLine($"  display area            : {dw}x{dh} at VRAM ({dx},{dy})" +
                           (gpu.Pal ? ", PAL" : ", NTSC") +
                           (gpu.Display24Bit ? ", 24-bit" : ""));
        summary.AppendLine($"  vram-full.png           : {width}x{height}, whole VRAM");
        summary.AppendLine($"  vram.rgb555.bin         : raw 16-bit pixels, 2 bytes each, {width} per row");

        if (dw <= 0 || dh <= 0)
        {
            summary.AppendLine("  framebuffer             : not dumped, display area is empty");
            return summary.ToString().TrimEnd();
        }

        try
        {
            var crop = ToRgba(vram, width, height, dx, dy, dw, dh);
            PngWriter.WriteRgba(Path.Combine(outDir, "framebuffer.png"), crop, dw, dh);

            // A blank framebuffer is the signature of a run that reached the code but
            // never drew anything, which is worth distinguishing from a run that drew.
            var lit = 0;
            for (var i = 0; i < crop.Length; i += 4)
                if (crop[i] != 0 || crop[i + 1] != 0 || crop[i + 2] != 0)
                    lit++;

            var total = dw * dh;
            var percent = total == 0 ? 0.0 : 100.0 * lit / total;
            summary.AppendLine($"  non-black pixels        : {lit}/{total} ({percent:0.0}%)");

            // Distinct colour count separates "the guest drew something" from "the
            // framebuffer is noise": a real title screen has a small palette, garbage
            // has thousands of colours.
            var colors = new HashSet<int>();
            for (var i = 0; i < crop.Length; i += 4)
                colors.Add((crop[i] << 16) | (crop[i + 1] << 8) | crop[i + 2]);
            summary.AppendLine($"  distinct colours        : {colors.Count}");

            var ascii = RenderAscii(crop, dw, dh, AsciiCols, AsciiRows);
            File.WriteAllText(Path.Combine(outDir, "framebuffer-ascii.txt"), ascii);

            summary.AppendLine($"  framebuffer.png         : {dw}x{dh} crop of the display area");
            summary.AppendLine($"  framebuffer-ascii.txt   : {AsciiCols}x{AsciiRows} luminance render");
            summary.AppendLine();
            summary.AppendLine($"  display area as luminance text ({AsciiCols}x{AsciiRows}, " +
                               $"'{AsciiRamp[0]}' = dark, '{AsciiRamp[^1]}' = bright):");
            summary.Append(ascii.TrimEnd());
        }
        catch (Exception ex)
        {
            summary.AppendLine($"  framebuffer             : dump failed: {ex.Message}");
        }

        return summary.ToString().TrimEnd();
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
    /// Expands PS1 16-bit pixels (bit 15 mask, then 5 bits each of B, G, R, low to high)
    /// into RGBA8. The 5-to-8 bit expansion replicates the high bits into the low ones so
    /// that full scale stays full scale.
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
