using System.Numerics;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// A dense rig like Фокина's (91 fixtures, 1295 cells): many spot lights hung 3.5 … 8 m above the floor around the
/// origin, every one aimed within half a metre of it and wide enough to light it, so all of them overlap in the clusters
/// around the origin — more than twice the 512 lights the cluster lists used to be truncated to. Deterministic (fixed
/// seed); the CPU references are <see cref="LightMeter"/> and the photometric floor luminance.
/// </summary>
internal sealed class OverlappingBeams
{
    private static readonly BeamType[] Types = { BeamType.Wash, BeamType.Spot, BeamType.Fresnel, BeamType.Wash, BeamType.PC };

    public OverlappingBeams(int count, int seed = 4242)
    {
        var random = new Random(seed);
        Lights = new SpotLight[count];
        Profiles = new BeamDistribution[count];
        Reference = new Photometry.SpotLight[count];
        for (int i = 0; i < count; i++)
        {
            float angle = random.NextSingle() * 2f * MathF.PI;
            float radius = 1f + 5f * random.NextSingle();
            var position = new Vector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle) * 0.7f, 3.5f + 4.5f * random.NextSingle());
            var aim = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, 0f);
            float beam = 22f + 25f * random.NextSingle();
            float field = beam * (1.25f + 0.5f * random.NextSingle());
            var color = LightColor.FromRec709(new Vector3(0.2f + random.NextSingle(), 0.2f + random.NextSingle(), 0.2f + random.NextSingle()));
            var type = Types[i % Types.Length];
            Lights[i] = StageScene.Spot(position, aim, 300f + 2700f * random.NextSingle(), beam, field, color, shadows: false, type: type);
            Profiles[i] = BeamDistribution.Create(type, Lights[i].BeamAngle, Lights[i].FieldAngle);
            Reference[i] = new Photometry.SpotLight(Lights[i].Position, Lights[i].Direction, Lights[i].PeakCandela, Profiles[i],
                LightColor.FromRec2020(Lights[i].Color));
        }
    }

    /// <summary>The lights for the renderer.</summary>
    public SpotLight[] Lights { get; }

    /// <summary>Beam profile of every light (as the renderer derives it).</summary>
    public BeamDistribution[] Profiles { get; }

    /// <summary>The same lights for <see cref="LightMeter"/>.</summary>
    public Photometry.SpotLight[] Reference { get; }

    /// <summary>Lights whose cut-off cone contains <paramref name="point"/> (they all reach the cluster around it).</summary>
    public int LightsReaching(Vector3 point)
    {
        int n = 0;
        foreach (ref readonly var light in Lights.AsSpan())
        {
            var to = Vector3.Normalize(point - light.Position);
            var profile = BeamDistribution.Create(light.BeamType, light.BeamAngle, light.FieldAngle);
            if (MathF.Acos(Math.Clamp(Vector3.Dot(to, light.Direction), -1f, 1f)) < profile.CutoffAngle)
                n++;
        }

        return n;
    }

    /// <summary>Luminance of a white Lambertian floor (ρ = 1) at <paramref name="point"/> summed over every light, cd/m².</summary>
    public float FloorLuminance(Vector3 point)
    {
        float sum = 0f;
        for (int i = 0; i < Lights.Length; i++)
            sum += PhotometricTests.FloorLuminance(Lights[i], Profiles[i].Relative, point);
        return sum;
    }
}
