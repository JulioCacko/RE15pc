using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using Silk.NET.OpenGL;

namespace RE15pc.Diagnostics;

/// <summary>
/// Reads the pixels of the render target that draws are being classified into.
/// </summary>
/// <remarks>
/// This is the one reading that splits the remaining space in two. Draws are classified into render
/// targets, those targets are flushed more often than they are re-seeded, and the room still never
/// reaches video memory. Either the room **is** in the target and the writeback is what fails, or the
/// target is empty as well and the draw itself produces nothing. Every earlier reading came from VRAM
/// rather than from the surface a draw actually goes into, so those two had never been separated.
///
/// It lives in its own file for a reason. `ReadPixels` resolves to the span overload only when the
/// `PixelFormat` and `PixelType` enums are in scope the ordinary way - the same way
/// `Gl21Vram.ReadRect` uses them. Importing the whole `Silk.NET.OpenGL` namespace is what makes that
/// work, but the namespace also declares a `Buffer` type that collides with `System.Buffer`, so a file
/// that imports it must not use the bare name `Buffer`. Keeping this read isolated satisfies both
/// constraints; the earlier attempt in `VramDump` satisfied neither and did not compile.
/// </remarks>
internal static class TargetProbe
{
    /// <summary>
    /// Reads the target the last draw was classified into and counts non-black pixels.
    /// </summary>
    /// <remarks>
    /// GL work must happen on the thread owning the context, so the read is handed to the GPU job
    /// queue the presentation loop drains, with a bounded wait - that queue has no timeout and stops
    /// being drained once the guest ends.
    /// </remarks>
    public static (int Lit, int Total, string Note) Read()
    {
        if (GpuGlAccess.Gl is null || !GpuGlAccess.Available)
            return (0, 0, "no GL target available");

        if (!GpuJobs.Claimed)
            return (0, 0, "GPU job queue not claimed");

        var width = GpuGlAccess.TargetWidth;
        var height = GpuGlAccess.TargetHeight;
        var fbo = GpuGlAccess.TargetFbo;

        if (width <= 0 || height <= 0 || fbo == 0)
            return (0, 0, "target has no dimensions");

        var pixels = new byte[width * height * 4];
        var lit = 0;
        var note = "ok";

        var done = new ManualResetEventSlim(false);

        var reader = new Thread(() =>
        {
            try
            {
                GpuJobs.Run(() =>
                {
                    var gl = GpuGlAccess.Gl!;
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
                    gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                    gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba,
                        PixelType.UnsignedByte, pixels.AsSpan());
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                });

                for (var i = 0; i + 3 < pixels.Length; i += 4)
                {
                    // Colour only. The target's alpha carries the PlayStation mask bit, and a cleared
                    // target can legitimately have alpha set with no colour.
                    if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) lit++;
                }

                WriteArtifacts(pixels, width, height);
            }
            catch (Exception ex)
            {
                note = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "target-probe"
        };

        reader.Start();

        if (!done.Wait(TimeSpan.FromSeconds(3))) return (0, 0, "target read timed out");

        return (lit, width * height, note);
    }
    /// <summary>
    /// Writes the target surface to disk, so it can be looked at rather than only counted.
    /// </summary>
    /// <remarks>
    /// A count says the room's drawn output is missing from the target; only looking says whether it
    /// is absent or merely somewhere else in the surface, which are different defects.
    ///
    /// Written on every sample rather than once. Writing once captures the first sample, which is the
    /// title screen in both configurations and therefore tells you nothing about the difference; the
    /// capture that matters is the last one, after the room should have been drawn.
    /// </remarks>
    private static void WriteArtifacts(byte[] pixels, int width, int height)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "out", "diagnostics");
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);

            File.WriteAllBytes(Path.Combine(dir, "target-surface.rgba.bin"), pixels);

            // A luminance render small enough to read in a terminal. Downsampled by point sampling,
            // which is enough to show where content is.
            const int cols = 100;
            const int rows = 38;
            var text = new System.Text.StringBuilder();

            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var x = c * width / cols;
                    var y = r * height / rows;
                    var o = (y * width + x) * 4;

                    var lum = (pixels[o] * 299 + pixels[o + 1] * 587 + pixels[o + 2] * 114) / 1000;
                    text.Append(lum switch
                    {
                        < 8 => ' ',
                        < 32 => '.',
                        < 64 => ':',
                        < 96 => '-',
                        < 128 => '=',
                        < 160 => '+',
                        < 192 => '*',
                        < 224 => '#',
                        _ => '@'
                    });
                }

                text.Append('\n');
            }

            File.WriteAllText(Path.Combine(dir, "target-surface-ascii.txt"), text.ToString());
        }
        catch
        {
            // A diagnostic that cannot write its artifact must not take down the run it describes.
        }
    }
}
