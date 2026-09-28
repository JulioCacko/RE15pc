using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using Silk.NET.OpenGL;

namespace RE15pc.Diagnostics;

/// <summary>
/// Samples the actual OpenGL state the GPU backend is leaving behind, rather than reasoning about
/// what the runtime intends to set.
/// </summary>
/// <remarks>
/// Three mechanisms in this project's notes were written up as findings and later retracted, and all
/// three failed the same way: they were derived by following the code's intent rather than reading
/// what the code does with the values it actually has. This exists to stop doing that.
///
/// <c>GpuGlAccess</c> is public and exposes the live <c>GL</c> object along with the current render
/// target and its VRAM origin, so the blend factors, the blend equation, the enabled state and the
/// bound draw framebuffer are all directly queryable. Sampling them repeatedly builds a
/// distribution of states actually seen during a run, which can be compared between a run that
/// renders correctly and one that does not.
///
/// GL calls must happen on the thread that owns the context, so each sample is handed to the GPU job
/// queue the presentation loop drains, with a bounded wait. The queue has no timeout and stops being
/// drained when the guest thread ends, so a sample that cannot be taken is skipped rather than
/// allowed to hang the run it is measuring.
/// </remarks>
public static class GlStateSampler
{
    private static readonly Dictionary<string, int> States = [];
    private static readonly object Gate = new();

    private static bool _attached;

    /// <summary>Samples taken, and how many produced a reading.</summary>
    private static int _attempts, _readings;

    public static void Attach() => _attached = true;

    public static void Sample()
    {
        if (!_attached) return;
        if (GpuGlAccess.Gl is null || !GpuGlAccess.Available) return;
        if (!GpuJobs.Claimed) return;

        _attempts++;

        string? description = null;
        var done = new ManualResetEventSlim(false);

        var reader = new Thread(() =>
        {
            try
            {
                GpuJobs.Run(() => description = Read());
            }
            catch
            {
                // A failed sample is skipped; the counters below make that visible.
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "glstate-read"
        };

        reader.Start();

        if (!done.Wait(TimeSpan.FromSeconds(2)) || description is null) return;

        _readings++;

        lock (Gate)
        {
            States[description] = States.TryGetValue(description, out var c) ? c + 1 : 1;
        }
    }

    /// <summary>
    /// Reads the blend and framebuffer state. Enum values are printed numerically and named where
    /// they matter, so an unexpected value is still readable rather than being silently mapped.
    /// </summary>
    private static string Read()
    {
        var gl = GpuGlAccess.Gl!;

        var blend = gl.IsEnabled(EnableCap.Blend);

        return $"blend={blend,-5} eqRgb={Name(gl.GetInteger(GLEnum.BlendEquationRgb))} " +
               $"srcRgb={Factor(gl.GetInteger(GLEnum.BlendSrcRgb))} dstRgb={Factor(gl.GetInteger(GLEnum.BlendDstRgb))} " +
               $"srcA={Factor(gl.GetInteger(GLEnum.BlendSrcAlpha))} dstA={Factor(gl.GetInteger(GLEnum.BlendDstAlpha))} " +
               $"fbo={gl.GetInteger(GLEnum.DrawFramebufferBinding)} " +
               $"origin=({GpuGlAccess.TargetOriginX},{GpuGlAccess.TargetOriginY}) margin={GpuGlAccess.TargetMargin} " +
               $"target={GpuGlAccess.TargetWidth}x{GpuGlAccess.TargetHeight}";
    }

    private static string Name(int equation)
    {
        return equation switch
        {
            0x8006 => "FUNC_ADD",
            0x800A => "FUNC_SUBTRACT",
            0x800B => "FUNC_REVERSE_SUBTRACT",
            0x8007 => "MIN",
            0x8008 => "MAX",
            _ => $"0x{equation:X4}"
        };
    }

    private static string Factor(int factor)
    {
        return factor switch
        {
            0 => "ZERO",
            1 => "ONE",
            0x0300 => "SRC_COLOR",
            0x0301 => "ONE_MINUS_SRC_COLOR",
            0x0302 => "SRC_ALPHA",
            0x0303 => "ONE_MINUS_SRC_ALPHA",
            0x0304 => "DST_ALPHA",
            0x0305 => "ONE_MINUS_DST_ALPHA",
            0x0306 => "DST_COLOR",
            0x0307 => "ONE_MINUS_DST_COLOR",
            0x0308 => "SRC_ALPHA_SATURATE",
            0x8589 => "SRC1_ALPHA",
            0x858A => "ONE_MINUS_SRC1_ALPHA",
            0x88F9 => "SRC1_COLOR",
            0x88FA => "ONE_MINUS_SRC1_COLOR",
            _ => $"0x{factor:X4}"
        };
    }

    public static string Describe()
    {
        if (!_attached) return "";

        lock (Gate)
        {
            if (States.Count == 0)
                return $"  GL state samples       : none taken ({_attempts} attempted, {_readings} read)";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"  GL state samples       : {_readings}/{_attempts} read, {States.Count} distinct state(s)");

            foreach (var (state, count) in States.OrderByDescending(k => k.Value).Take(6))
                sb.AppendLine($"    x{count,-5} {state}");

            return sb.ToString().TrimEnd();
        }
    }
}
