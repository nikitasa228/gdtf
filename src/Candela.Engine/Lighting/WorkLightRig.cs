using System.Numerics;
using Candela.Core.Math;
using Candela.Rendering;

namespace Candela.Engine;

/// <summary>When the render binding adds the work light (see <see cref="RenderSync.WorkLight"/>).</summary>
public enum WorkLightMode
{
    /// <summary>Never: only the stage light is drawn (final renders, photometric checks).</summary>
    Off,

    /// <summary>
    /// When the scene would otherwise be black: no light of the frame reaches <see cref="WorkLightRig.MinStageCandela"/>
    /// (no fixture emits, every dimmer closed). The editor's preview uses this by default.
    /// </summary>
    Auto,

    /// <summary>Always, on top of the stage light.</summary>
    On,
}

/// <summary>
/// The work light: a dim, neutral (D65) light rig that lets the user see the set in <see cref="ViewShading.Full"/>
/// shading when no fixture emits — one key light from the audience side (front-left, above; casts shadows) and a soft
/// ambient made of six very wide lights from ±X, ±Y and ±Z (no shadows), all aimed at the centre of the drawn content.
/// It is not stage light: the engine's <see cref="StageEngine.Lights"/>, the light meter and the light plot never include
/// it. The rig depends only on the bounds it is given, so it is deterministic.
/// </summary>
/// <remarks>
/// Levels: <see cref="KeyIlluminance"/> on a surface facing the key at the centre, <see cref="AmbientIlluminance"/> on a
/// surface facing any axis — a dim room light, about 1/10 of a lit stage, so a scene still reads as "dark" and the first
/// fixture switched on dominates. The lights are placed at 2.5 × the bounds' radius from the centre (at least 5 m), so the
/// illuminance varies little over the content. Directions follow the project's convention: right-handed, Z up, the
/// audience on the −Y side.
/// </remarks>
public static class WorkLightRig
{
    /// <summary>Number of lights the rig adds (one key, six ambient).</summary>
    public const int LightCount = 7;

    /// <summary>Illuminance of the key light at the centre of the content on a surface facing it, lx.</summary>
    public const float KeyIlluminance = 40f;

    /// <summary>Illuminance of each ambient light at the centre on a surface facing it, lx.</summary>
    public const float AmbientIlluminance = 8f;

    /// <summary>
    /// In <see cref="WorkLightMode.Auto"/>, the frame counts as lit when one of its lights has at least this peak
    /// intensity, cd (a fixture at 1 % of a small LED already exceeds it).
    /// </summary>
    public const float MinStageCandela = 1f;

    /// <summary>Full beam (50 %) and field (10 %) angles of the ambient lights, rad.</summary>
    public const float AmbientBeamAngle = 120f * MathF.PI / 180f;

    /// <summary>Full field (10 %) angle of the ambient lights, rad (the renderer's widest spot cone).</summary>
    public const float AmbientFieldAngle = 170f * MathF.PI / 180f;

    /// <summary>Direction the key light travels: from the audience side (−Y), stage left (−X) and above (+Z).</summary>
    public static readonly Vector3 KeyDirection = Vector3.Normalize(new Vector3(0.45f, 1f, -1.1f));

    /// <summary>Content smaller than this radius is lit as if it had this radius, m.</summary>
    public const float MinRadius = 2f;

    /// <summary>
    /// True when <paramref name="mode"/> wants the work light for a frame with <paramref name="stageLights"/>: always for
    /// <see cref="WorkLightMode.On"/>, for <see cref="WorkLightMode.Auto"/> only when no light reaches
    /// <see cref="MinStageCandela"/>.
    /// </summary>
    public static bool IsNeeded(WorkLightMode mode, ReadOnlySpan<SpotLight> stageLights)
    {
        if (mode == WorkLightMode.On) return true;
        if (mode != WorkLightMode.Auto) return false;
        foreach (ref readonly var light in stageLights)
            if (light.PeakCandela >= MinStageCandela) return false;
        return true;
    }

    /// <summary>
    /// Writes the <see cref="LightCount"/> lights of the rig for content within <paramref name="bounds"/> (world space,
    /// m; empty or non-finite: a 5 m stage around the origin) into <paramref name="destination"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="LightCount"/>.</exception>
    public static void Write(Aabb bounds, Span<SpotLight> destination)
    {
        if (destination.Length < LightCount) throw new ArgumentException($"At least {LightCount} lights are needed.", nameof(destination));
        Vector3 centre;
        float radius;
        if (bounds.IsEmpty || !IsFinite(bounds.Min) || !IsFinite(bounds.Max))
        {
            centre = new Vector3(0f, 0f, 1.5f);
            radius = 5f;
        }
        else
        {
            centre = bounds.Center;
            radius = MathF.Max(0.5f * bounds.Size.Length(), MinRadius);
        }
        float distance = MathF.Max(2.5f * radius, 5f);
        float throwSquared = distance * distance;

        // key: covers the content with its beam (50 %) cone
        float halfCover = MathF.Atan(1.3f * radius / distance);
        float keyField = Math.Clamp(2f * halfCover / 0.75f, 0.2f, AmbientFieldAngle);
        destination[0] = Light(centre - KeyDirection * distance, KeyDirection, KeyIlluminance * throwSquared,
            beam: 0.75f * keyField, field: keyField, sourceRadius: 0.5f, castShadows: true);

        Span<Vector3> axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        for (int i = 0; i < axes.Length; i++)
        {
            // the light on the +axis side travels along −axis and lights the faces that look towards +axis
            destination[1 + i] = Light(centre + axes[i] * distance, -axes[i], AmbientIlluminance * throwSquared,
                AmbientBeamAngle, AmbientFieldAngle, sourceRadius: 1f, castShadows: false);
        }
    }

    private static SpotLight Light(Vector3 position, Vector3 direction, float candela, float beam, float field, float sourceRadius, bool castShadows)
    {
        var reference = MathF.Abs(direction.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var up = Vector3.Normalize(reference - direction * Vector3.Dot(reference, direction));
        return new SpotLight
        {
            Position = position,
            Direction = direction,
            Up = up,
            Color = Vector3.One, // D65 white in linear Rec.2020 has luminance Y = 1
            PeakCandela = candela,
            BeamAngle = beam,
            FieldAngle = field,
            SourceRadius = sourceRadius,
            Iris = 1f,
            CastShadows = castShadows,
            BeamType = Candela.Photometry.BeamType.Wash,
            FixtureId = Guid.Empty,
        };
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
