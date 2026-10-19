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
    /// Like <see cref="InScattering"/>, but from the light as the renderer packs it (<see cref="LightPacker"/>): the
    /// profile of the GPU light including the iris, every prism facet with its own axis and lens near field, and the
    /// modulation per facet (point, unit direction of the light there, facet index). Luminance of the light's colour.
    /// </summary>
    public static double InScatteringPacked(SpotLight light, Vector3 origin, Vector3 direction, double sigma, double g, double tMax = 60,
        Func<Vector3, Vector3, int, double>? modulation = null, int steps = 100000)
    {
        LightPacker.Pack(light, 0, out var l);
        int facets = Math.Max((int)l.PrismFacets, 1);
        bool prism = l.PrismFacets > 0;
        double spread = prism ? Math.Atan2(l.PrismSinSpread, l.PrismCosSpread) : 0;
        double halfAngle = Math.Clamp(l.BoundingAngle - spread, 1e-4, 1.5);
        double lens = l.SourceRadius;
        double apex = lens / Math.Max(Math.Tan(halfAngle), 1e-4);
        double nearField = lens > 0 ? 3 * apex : 0;
        double intensity = 0.2627 * l.Intensity.X + 0.6780 * l.Intensity.Y + 0.0593 * l.Intensity.Z;
        var axes = new Vector3[facets];
        for (int k = 0; k < facets; k++)
        {
            double phi = l.PrismRotation + 2 * Math.PI * k / facets;
            axes[k] = prism
                ? Vector3.Normalize(l.Right * (float)(l.PrismSinSpread * Math.Cos(phi)) + l.Up * (float)(l.PrismSinSpread * Math.Sin(phi)) +
                                    l.Direction * l.PrismCosSpread)
                : l.Direction;
        }

        double dt = tMax / steps, sum = 0;
        for (int i = 0; i < steps; i++)
        {
            double t = (i + 0.5) * dt;
            var x = origin + direction * (float)t;
            var rel = x - l.Position;
            double d = rel.Length();
            for (int k = 0; k < facets; k++)
            {
                var axis = axes[k];
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

                double f = PackedProfile(l, Math.Atan2(s * rho, z));
                if (f <= 0)
                    continue;
                double r2 = z * z + s * s * rho * rho;
                var omega = Vector3.Normalize(axis * (float)z + radial * (float)s);
                double cos = Vector3.Dot(omega, -direction);
                double phase = (1 - g * g) / (4 * Math.PI * Math.Pow(1 + g * g - 2 * g * cos, 1.5));
                double m = modulation?.Invoke(x, omega, k) ?? 1.0;
                sum += sigma * phase * intensity * f * s * s / r2 * Math.Exp(-sigma * d) * Math.Exp(-sigma * t) * m * dt;
            }
        }

        return sum;
    }

    /// <summary>Gate coordinates (the gobo image spans −1 … 1) of a unit direction leaving a light without a prism.</summary>
    public static Vector2 Gate(in SpotLight light, Vector3 omega)
    {
        LightPacker.Pack(light, 0, out var l);
        float z = Vector3.Dot(omega, l.Direction);
        return new Vector2(Vector3.Dot(omega, l.Right), Vector3.Dot(omega, l.Up)) * (l.GateScale / z);
    }

    /// <summary>The GPU light's relative intensity at <paramref name="theta"/> (beamProfile in photometry.glsl).</summary>
    private static double PackedProfile(in GpuLight l, double theta)
    {
        double value;
        if (l.Shape == ShaderConstants.ShapeUniformCone)
        {
            value = theta * l.InvHalfBeam <= 1 ? 1 : 0;
        }
        else
        {
            if (theta >= l.Cutoff)
                return 0;
            double r = theta * l.InvHalfBeam;
            value = r > 0 ? Math.Exp(-Math.Log(2) * Math.Pow(r, l.Exponent)) : 1;
            if (theta > l.HalfField)
            {
                double s = (theta - l.HalfField) * l.InvWindow;
                value *= 1 - s * s * (3 - 2 * s);
            }
        }

        if (theta > l.IrisSoftStart)
        {
            double x = Math.Clamp((theta - l.IrisSoftStart) / (l.IrisAngle - l.IrisSoftStart), 0, 1);
            value *= 1 - x * x * (3 - 2 * x);
        }

        return value;
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
