using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// CPU reference of the single scattering of one volumetric beam along a view ray, integrated numerically in double
/// precision with the midpoint rule:
/// L = ∫ σ · p_HG(cos θ_s) · I(x) · m(x) · e^(−σ·d) · e^(−σ·t) dt, where I(x) is the beam's illuminance at x
/// (the profile of <see cref="BeamDistribution"/> over the squared distance, with the lens near field of
/// beam_scatter.comp: within 3·R/tan θ_c of the lens the shaft has the radius (R⁴ + (z·tan θ_c)⁴)^¼, flux conserved),
/// d the distance from the lens, t the distance along the ray and m an optional modulation (gobo, shadow).
/// </summary>
internal static class BeamReference
{
    /// <summary>Modulation of the light at a point: the point and the unit direction the light travels there.</summary>
    public delegate double Modulation(Vector3 point, Vector3 direction);

    /// <summary>In-scattered luminance (cd/m², photopic) along <paramref name="origin"/> + t·<paramref name="direction"/>, t in [0, tMax].</summary>
    public static double InScattering(SpotLight light, Vector3 origin, Vector3 direction, double sigma, double g, double tMax = 60,
        Modulation? modulation = null, int steps = 200000)
    {
        var beam = BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle);
        var axis = Vector3.Normalize(light.Direction);
        double halfAngle = Math.Min(beam.CutoffAngle, 1.5);
        double lens = Math.Max(light.SourceRadius, 0f);
        double apexOffset = lens / Math.Max(Math.Tan(halfAngle), 1e-4);
        double nearField = lens > 0 ? 3 * apexOffset : 0;
        double dt = tMax / steps;
        double sum = 0;
        for (int i = 0; i < steps; i++)
        {
            double t = (i + 0.5) * dt;
            var x = origin + direction * (float)t;
            var rel = x - light.Position;
            double z = Vector3.Dot(rel, axis);
            if (z <= 1e-6)
                continue;
            var radial = rel - axis * (float)z;
            double rho = radial.Length();
            double s = 1;
            if (z < nearField)
            {
                double zeta = z * Math.Tan(halfAngle);
                s = zeta / Math.Pow(Math.Pow(lens, 4) + Math.Pow(zeta, 4), 0.25);
            }

            double theta = Math.Atan2(s * rho, z);
            double f = beam.Relative((float)theta);
            if (f <= 0)
                continue;
            double r2 = z * z + s * s * rho * rho;
            var scaled = axis * (float)z + radial * (float)s;
            var omega = Vector3.Normalize(scaled);
            double cos = Vector3.Dot(omega, -direction);
            double phase = (1 - g * g) / (4 * Math.PI * Math.Pow(1 + g * g - 2 * g * cos, 1.5));
            double d = rel.Length();
            double m = modulation?.Invoke(x, omega) ?? 1.0;
            sum += sigma * phase * light.PeakCandela * f * s * s / r2 * Math.Exp(-sigma * d) * Math.Exp(-sigma * t) * m * dt;
        }

        return sum;
    }

    /// <summary>
    /// World-space view ray through the centre of pixel (<paramref name="x"/>, <paramref name="y"/>) of a perspective
    /// camera rendered at <paramref name="width"/> × <paramref name="height"/> (origin top-left, as the renderer projects it).
    /// </summary>
    public static (Vector3 Origin, Vector3 Direction) PixelRay(RenderCamera camera, int width, int height, float x, float y)
    {
        Matrix4x4.Invert(camera.View, out var inverse);
        float tan = MathF.Tan(0.5f * camera.FovY);
        float aspect = width / (float)height;
        float nx = (x + 0.5f) / width * 2f - 1f, ny = (y + 0.5f) / height * 2f - 1f;
        var view = Vector3.Normalize(new Vector3(nx * tan * aspect, -ny * tan, -1f));
        return (inverse.Translation, Vector3.Normalize(Vector3.TransformNormal(view, inverse)));
    }
}
