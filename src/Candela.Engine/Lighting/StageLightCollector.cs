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
    /// Widest beam and field angle passed to the renderer, rad (170°): its spot cone stops short of a hemisphere.
    /// Candela.Dmx already keeps field angles at 179° and below and recognises files that write the zoom range into the
    /// Beam geometry. Wide washes (LED panels and battens, strobes, tubes: about 7 % of the library's beams) reach this
    /// sanity clamp with their field — the faint 10 % edge, trimmed by a few degrees — which is not reported; a beam
    /// whose 50 % cone itself is wider (bulbs radiating in every direction) is drawn narrower than it is: those fixtures
    /// are listed in <see cref="ClampedFieldFixtures"/> and reported as <c>engine.field-clamped</c>.
    /// </summary>
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

    /// <summary>Fixtures (entity ids) with a beam angle wider than <see cref="MaxFieldAngle"/> in the last collection.</summary>
    public IReadOnlyCollection<Guid> ClampedFieldFixtures => _clamped;

    private readonly HashSet<Guid> _clamped = new();

    /// <summary>
    /// Rebuilds the light list from the fixture poses and the flame light samples. <paramref name="gobos"/> supplies
    /// gobo textures (null: lights without gobo images, e.g. for the light meter).
    /// </summary>
    public void Collect(FixturePoses poses, ReadOnlySpan<FlameLightSample> flames, GoboResolver? gobos = null)
    {
        ArgumentNullException.ThrowIfNull(poses);
        _count = 0;
        if (_clamped.Count > 0) _clamped.Clear();
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
                float beamAngle = beam.BeamAngle;
                if (beamAngle > MaxFieldAngle)
                {
                    beamAngle = MaxFieldAngle;
                    _clamped.Add(pose.Entity.Id);
                }
                light.BeamAngle = beamAngle;
                light.FieldAngle = PlausibleField(beamAngle, beam.FieldAngle);
                light.BeamType = beam.BeamType;
                light.SourceRadius = SourceRadius(beam.Beam.Geometry.BeamRadius);
                light.Iris = float.IsFinite(beam.Iris) ? Math.Clamp(beam.Iris, 0f, 1f) : 1f;
                light.Frost = float.IsFinite(beam.Frost) ? Math.Clamp(beam.Frost, 0f, 1f) : 0f;
                light.CastShadows = pose.Fixture.CastShadow;
                light.FixtureId = pose.Entity.Id;
                ApplyGobo(ref light, state.Profile, beam, gobos);
                ApplyBlades(ref light, beam);
                ApplyPrism(ref light, state.Profile, beam);
                ApplyShaperStretch(ref light, beam);
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

    /// <summary>
    /// Largest elongation drawn for an anamorphic beam shaper: two half-intensity copies of the round beam side by side
    /// stay one continuous oval up to about 2.2 : 1; beyond that a dip would open between them.
    /// </summary>
    public const float MaxDrawnShaperStretch = 2.2f;

    /// <summary>
    /// An anamorphic beam shaper (<see cref="BeamState.ShaperStretch"/> &gt; 1) drawn with the renderer's prism path: two
    /// copies of the round beam, each with half of the unstretched peak, offset along the long axis by (stretch − 1) ×
    /// half the beam angle, which together form an oval stretch times as long as wide with the beam's flux. A beam that
    /// also passes a real prism keeps the prism (the renderer has one facet pattern per light).
    /// </summary>
    private static void ApplyShaperStretch(ref SpotLight light, BeamState beam)
    {
        float stretch = beam.ShaperStretch;
        if (!(stretch > 1.01f) || !float.IsFinite(stretch) || light.PrismFacets > 1) return;
        float drawn = MathF.Min(stretch, MaxDrawnShaperStretch);
        light.PeakCandela *= stretch; // the beam's peak is the stretched one; each copy carries half the round beam's peak
        light.PrismFacets = 2;
        light.PrismRotation = float.IsFinite(beam.ShaperStretchRotation) ? beam.ShaperStretchRotation : 0f;
        float beamAngle = float.IsFinite(light.BeamAngle) && light.BeamAngle > 0f ? light.BeamAngle : 0f;
        light.PrismSpread = (drawn - 1f) * 0.5f * beamAngle;
    }

    /// <summary>The field angle kept between the beam angle and <see cref="MaxFieldAngle"/> (sanity clamp), rad.</summary>
    public static float PlausibleField(float beamAngle, float fieldAngle)
    {
        if (!float.IsFinite(fieldAngle) || !(beamAngle > 0f) || !float.IsFinite(beamAngle)) return fieldAngle;
        return MathF.Max(MathF.Min(beamAngle, MaxFieldAngle), MathF.Min(fieldAngle, MaxFieldAngle));
    }

    private static float SourceRadius(float radius) => float.IsFinite(radius) && radius > 0f ? Math.Clamp(radius, 0.001f, 2f) : 0.05f;

    private static float Finite01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private ref SpotLight Next()
    {
        if (_count == _lights.Length) Array.Resize(ref _lights, _lights.Length * 2);
        return ref _lights[_count++];
    }
}
