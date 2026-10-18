using System.Numerics;
using Candela.Dmx;
using Candela.Rendering;
using Candela.Simulation;
using PhotometryBeamType = Candela.Photometry.BeamType;

namespace Candela.Engine;

/// <summary>
/// Resolves the projected image of a wheel slot (gobo, animation disc) to a renderer texture. RenderSync implements it
/// with a per-slot cache; returning an invalid handle draws the beam without that image.
/// </summary>
/// <param name="profile">The fixture's profile.</param>
/// <param name="wheelIndex">Index into <see cref="FixtureProfile.Wheels"/>.</param>
/// <param name="slot">1-based slot of that wheel.</param>
public delegate TextureHandle GoboResolver(FixtureProfile profile, int wheelIndex, int slot);

/// <summary>
/// Builds the frame's photometric light list: one <see cref="SpotLight"/> per lit beam of every fixture (position,
/// axis and gobo orientation from the beam geometry's world matrix; candela, colour, angles and beam type from the
/// DMX state; gobo, iris, framing blades, prism and frost for the renderer's cookie) plus the flame light samples of
/// the fire simulation. Beams with zero intensity are skipped. The buffer is reused, so steady-state frames do not
/// allocate.
/// </summary>
public sealed class StageLightCollector
{
    /// <summary>Full beam angle of the two hemispherical lights that stand in for an isotropic flame sample, rad (176°).</summary>
    public const float FlameHemisphereBeamAngle = 176f * MathF.PI / 180f;

    /// <summary>Full field angle of the flame hemispheres, rad (270°): with 176° beams two opposed lights are isotropic within 5 %.</summary>
    public const float FlameHemisphereFieldAngle = 270f * MathF.PI / 180f;

    /// <summary>
    /// Largest field angle (10 %) relative to the beam angle (50 %) passed to the renderer. Real optics stay well below
    /// it (profiles ≈ 1.2, washes ≈ 1.6–2, PARs ≈ 2.5), but some GDTF files write the zoom range into the Beam geometry
    /// (Ayrton Rivale Wash: BeamAngle 4°, FieldAngle 57°), and a ratio of 14 applied at a wide zoom would spread the
    /// light over more than a hemisphere.
    /// </summary>
    public const float MaxFieldToBeamRatio = 3f;

    /// <summary>Widest field angle passed to the renderer, rad (170°).</summary>
    public const float MaxFieldAngle = 170f * MathF.PI / 180f;

    /// <summary>Prism facet offset (fraction of the field radius) assumed when a prism slot gives no facet translation.</summary>
    public const float DefaultPrismOffset = 0.6f;

    private SpotLight[] _lights = new SpotLight[64];
    private int _count;

    /// <summary>The lights of the last <see cref="Collect"/> call (valid until the next one).</summary>
    public ReadOnlySpan<SpotLight> Lights => _lights.AsSpan(0, _count);

    /// <summary>Number of lights from fixture beams in the last collection.</summary>
    public int FixtureLightCount { get; private set; }

    /// <summary>Number of lights from flames in the last collection (two per flame light sample).</summary>
    public int FlameLightCount { get; private set; }

    /// <summary>
    /// Rebuilds the light list from the fixture poses and the flame light samples. <paramref name="gobos"/> supplies
    /// gobo textures (null: lights without gobo images, e.g. for the light meter).
    /// </summary>
    public void Collect(FixturePoses poses, ReadOnlySpan<FlameLightSample> flames, GoboResolver? gobos = null)
    {
        ArgumentNullException.ThrowIfNull(poses);
        _count = 0;
        foreach (var pose in poses.All)
        {
            var state = pose.State;
            var beams = state.Beams;
            for (int b = 0; b < beams.Count; b++)
            {
                var beam = beams[b];
                float candela = beam.PeakCandela;
                if (!(candela > 0f) || !float.IsFinite(candela)) continue;
                if (!pose.TryGetBeamFrame(beam.PartIndex, out var position, out var direction, out var up)) continue;
                ref var light = ref Next();
                light = default;
                light.Position = position;
                light.Direction = direction;
                light.Up = up;
                light.Color = beam.Color.Rgb;
                light.PeakCandela = candela;
                light.BeamAngle = beam.BeamAngle;
                light.FieldAngle = PlausibleField(beam.BeamAngle, beam.FieldAngle);
                light.BeamType = beam.BeamType;
                light.SourceRadius = SourceRadius(beam.Beam.Geometry.BeamRadius);
                light.Iris = float.IsFinite(beam.Iris) ? Math.Clamp(beam.Iris, 0f, 1f) : 1f;
                light.Frost = float.IsFinite(beam.Frost) ? Math.Clamp(beam.Frost, 0f, 1f) : 0f;
                light.CastShadows = pose.Fixture.CastShadow;
                light.FixtureId = pose.Entity.Id;
                ApplyGobo(ref light, state.Profile, beam, gobos);
                ApplyBlades(ref light, beam);
                ApplyPrism(ref light, state.Profile, beam);
            }
        }
        FixtureLightCount = _count;

        foreach (ref readonly var sample in flames)
        {
            if (!(sample.IntensityCandela > 0f) || !float.IsFinite(sample.IntensityCandela)) continue;
            var p = sample.Position;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
            AddFlameHemisphere(sample, Vector3.UnitZ);
            AddFlameHemisphere(sample, -Vector3.UnitZ);
        }
        FlameLightCount = _count - FixtureLightCount;
    }

    /// <summary>
    /// Blur of the projected gobo, 0 (sharp) … 1: the engine assumes a gobo is in focus at the middle of the fixture's
    /// focus range (where typical throws are focused) and goes soft towards either end; frost softens it further.
    /// </summary>
    public static float GoboBlur(float focus, float frost)
    {
        float defocus = float.IsFinite(focus) ? Math.Clamp(MathF.Abs(2f * focus - 1f), 0f, 1f) : 0f;
        float f = float.IsFinite(frost) ? Math.Clamp(frost, 0f, 1f) : 0f;
        return Math.Clamp(1f - (1f - 0.6f * defocus) * (1f - f), 0f, 1f);
    }

    private void AddFlameHemisphere(in FlameLightSample sample, Vector3 axis)
    {
        ref var light = ref Next();
        light = default;
        light.Position = sample.Position;
        light.Direction = axis;
        light.Up = Vector3.UnitY;
        light.Color = sample.Color.Rgb;
        light.PeakCandela = sample.IntensityCandela;
        light.BeamAngle = FlameHemisphereBeamAngle;
        light.FieldAngle = FlameHemisphereFieldAngle;
        light.BeamType = PhotometryBeamType.Wash;
        light.SourceRadius = SourceRadius(sample.Radius);
        light.Iris = 1f;
        light.CastShadows = false;
        light.FixtureId = sample.FlameId;
    }

    private static void ApplyGobo(ref SpotLight light, FixtureProfile profile, BeamState beam, GoboResolver? gobos)
    {
        light.GoboBlur = GoboBlur(beam.Focus, beam.Frost);
        if (gobos is null) return;
        foreach (ref readonly var wheel in beam.Gobos.AsSpan())
        {
            if (wheel.Wheel < 0 || wheel.IsOpen || wheel.Slot <= 0) continue;
            int slot = wheel.Blend > 0.5f && wheel.NextSlot > 0 ? wheel.NextSlot : wheel.Slot;
            var texture = gobos(profile, wheel.Wheel, slot);
            if (!texture.IsValid) continue;
            light.Gobo = texture;
            light.GoboRotation = float.IsFinite(wheel.Rotation) ? wheel.Rotation : 0f;
            // a wheel caught between two slots projects a split, blurred image
            float between = wheel.Blend > 0f && wheel.Blend < 1f ? 1f - MathF.Abs(2f * wheel.Blend - 1f) : 0f;
            light.GoboBlur = Math.Max(light.GoboBlur, between);
            return;
        }
        foreach (ref readonly var wheel in beam.AnimationWheels.AsSpan())
        {
            if (wheel.Wheel < 0 || wheel.Slot <= 0 || !(wheel.Insertion > 0.5f)) continue;
            var texture = gobos(profile, wheel.Wheel, wheel.Slot);
            if (!texture.IsValid) continue;
            light.Gobo = texture;
            light.GoboRotation = float.IsFinite(wheel.WheelAngle) ? wheel.WheelAngle : 0f;
            return;
        }
    }

    private static void ApplyBlades(ref SpotLight light, BeamState beam)
    {
        var blades = beam.Blades;
        int n = Math.Min(blades.Length, 4);
        for (int i = 0; i < n; i++)
        {
            ref readonly var blade = ref blades[i];
            float a = Finite01(blade.A), b = Finite01(blade.B);
            float insertion = 0.5f * (a + b);
            // the two ends of a blade inserted by different amounts tilt it: Δ over the beam diameter (2 radii)
            float angle = (float.IsFinite(blade.Rotation) ? blade.Rotation : 0f) + MathF.Atan(0.5f * (b - a));
            switch (i)
            {
                case 0: light.BladeInsertion.X = insertion; light.BladeAngle.X = angle; break;
                case 1: light.BladeInsertion.Y = insertion; light.BladeAngle.Y = angle; break;
                case 2: light.BladeInsertion.Z = insertion; light.BladeAngle.Z = angle; break;
                default: light.BladeInsertion.W = insertion; light.BladeAngle.W = angle; break;
            }
        }
        light.BladeRotation = float.IsFinite(beam.ShaperRotation) ? beam.ShaperRotation : 0f;
    }

    private static void ApplyPrism(ref SpotLight light, FixtureProfile profile, BeamState beam)
    {
        int facets = beam.PrismFacets;
        if (facets <= 1) return;
        light.PrismFacets = facets;
        float offset = DefaultPrismOffset;
        foreach (ref readonly var prism in beam.Prisms.AsSpan())
        {
            if (prism.Facets <= 0 || prism.Wheel < 0) continue;
            light.PrismRotation = float.IsFinite(prism.Rotation) ? prism.Rotation : 0f;
            if ((uint)prism.Wheel < (uint)profile.Wheels.Count && profile.Wheels[prism.Wheel].Source.GetSlot(prism.Slot) is { } slot && slot.Facets.Count > 0)
            {
                float sum = 0f;
                foreach (var f in slot.Facets)
                {
                    var t = f.Transform.Translation;
                    float len = t.Length();
                    if (float.IsFinite(len)) sum += len;
                }
                float mean = sum / slot.Facets.Count;
                if (mean > 1e-3f) offset = Math.Clamp(mean, 0.05f, 2f);
            }
            break;
        }
        float field = float.IsFinite(light.FieldAngle) && light.FieldAngle > 0f ? light.FieldAngle : light.BeamAngle;
        light.PrismSpread = float.IsFinite(field) ? offset * 0.5f * field : 0f;
    }

    /// <summary>The field angle limited to <see cref="MaxFieldToBeamRatio"/> × the beam angle and <see cref="MaxFieldAngle"/>.</summary>
    public static float PlausibleField(float beamAngle, float fieldAngle)
    {
        if (!float.IsFinite(fieldAngle) || !(beamAngle > 0f) || !float.IsFinite(beamAngle)) return fieldAngle;
        return MathF.Max(beamAngle, MathF.Min(fieldAngle, MathF.Min(beamAngle * MaxFieldToBeamRatio, MaxFieldAngle)));
    }

    private static float SourceRadius(float radius) => float.IsFinite(radius) && radius > 0f ? Math.Clamp(radius, 0.001f, 2f) : 0.05f;

    private static float Finite01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private ref SpotLight Next()
    {
        if (_count == _lights.Length) Array.Resize(ref _lights, _lights.Length * 2);
        return ref _lights[_count++];
    }
}
