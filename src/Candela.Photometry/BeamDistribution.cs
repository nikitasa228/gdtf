using System.Globalization;

namespace Candela.Photometry;

/// <summary>Shape family of a <see cref="BeamDistribution"/>.</summary>
public enum BeamShape
{
    /// <summary>Diffuse emitter, I(θ) = cos θ for θ &lt; 90°. The value of <c>default(BeamDistribution)</c>.</summary>
    Lambertian,

    /// <summary>Generalised Gaussian I(θ) = exp(−a·θᵖ) through the beam and field angles, with a smooth cut-off.</summary>
    GeneralizedGaussian,

    /// <summary>Hard-edged uniform cone: I(θ) = 1 inside the half-angle, 0 outside.</summary>
    UniformCone,
}

/// <summary>
/// Relative luminous intensity distribution I(θ)/I(0) of a rotationally symmetric fixture beam, θ being the angle
/// from the beam axis in radians. Combined with a peak intensity in candela it gives the intensity in any direction.
/// </summary>
/// <remarks>
/// <para><b>Model.</b> From the GDTF beam angle (full angle at 50 % of peak) and field angle (full angle at 10 %) a
/// generalised Gaussian I(θ) = exp(−ln 2·(θ/θ_b)ᵖ) is solved exactly from the two conditions:
/// p = ln(ln 10 / ln 2) / ln(θ_f/θ_b), with θ_b, θ_f the half-angles. So I(θ_b) = 0.5 and I(θ_f) = 0.1 exactly. Beyond
/// the field angle the tail is multiplied by a smoothstep window that reaches zero at <see cref="CutoffAngle"/> =
/// θ_f + k·(θ_f − θ_b) (at least 0.1·θ_f further, at most π), so the distribution has finite support (needed for light
/// culling) and stays C¹-continuous. k depends on the beam type: Spot and Rectangle 0.35 (gate-limited, sharp),
/// Wash 0.6, PC 0.8, Fresnel 1.0 (long soft spill).</para>
/// <para><b>Sanitising.</b> Fixture files are messy, so angles are repaired instead of rejected: half-angles are
/// clamped to [10⁻⁴ rad, 0.49π] (beam) and 0.75π (field); a missing, non-finite or not-larger-than-beam field angle
/// (GDTF files often repeat the beam angle) is estimated as beam × 1.30 (Spot, Rectangle), 1.82 (Wash — a true
/// Gaussian), 1.60 (PC) or 2.0 (Fresnel) and <see cref="FieldAngleEstimated"/> is set; the exponent p is kept within
/// [0.5, 64] (field/beam ratio between 1.019 and 11.0).</para>
/// <para><b>Types.</b> Glow and None are diffuse Lambertian emitters (I = cos θ, beam 120°, field 168.5°).
/// <see cref="UniformCone"/> gives a hard-edged cone (reference for tests and gobo-like cut-offs).</para>
/// <para><b>Cost.</b> The struct holds a handful of precomputed floats; creating one costs one logarithm and no
/// allocation, so DMX evaluation can rebuild it every frame when zoom changes. <see cref="Relative"/> costs one
/// logarithm and two exponentials; <see cref="SolidAngleIntegral"/> 72 evaluations (≈ 2 µs).</para>
/// </remarks>
public readonly struct BeamDistribution : IEquatable<BeamDistribution>
{
    /// <summary>Smallest beam half-angle, rad.</summary>
    public const float MinHalfAngle = 1e-4f;

    /// <summary>Largest beam half-angle, rad (88.2°).</summary>
    public const float MaxBeamHalfAngle = 0.49f * MathF.PI;

    /// <summary>Largest field half-angle, rad (135°).</summary>
    public const float MaxFieldHalfAngle = 0.75f * MathF.PI;

    /// <summary>Smallest exponent of the generalised Gaussian (cusp-like centre).</summary>
    public const float MinExponent = 0.5f;

    /// <summary>Largest exponent of the generalised Gaussian (nearly a hard edge).</summary>
    public const float MaxExponent = 64f;

    private const float Ln2 = 0.693147180559945f;
    private static readonly float _lnRatio = MathF.Log(MathF.Log(10f) / Ln2);
    private static readonly float _minFieldRatio = MathF.Exp(_lnRatio / MaxExponent);
    private static readonly float _maxFieldRatio = MathF.Exp(_lnRatio / MinExponent);
    private static readonly float _lambertFieldHalfAngle = MathF.Acos(0.1f);

    private readonly float _halfBeam;
    private readonly float _halfField;
    private readonly float _cutoff;
    private readonly float _exponent;
    private readonly float _invHalfBeam;
    private readonly float _invWindow;
    private readonly BeamType _type;
    private readonly BeamShape _shape;
    private readonly bool _fieldEstimated;

    private BeamDistribution(BeamType type, BeamShape shape, float halfBeam, float halfField, float cutoff, float exponent, bool fieldEstimated)
    {
        _type = type;
        _shape = shape;
        _halfBeam = halfBeam;
        _halfField = halfField;
        _cutoff = cutoff;
        _exponent = exponent;
        _invHalfBeam = halfBeam > 0f ? 1f / halfBeam : 0f;
        _invWindow = cutoff > halfField ? 1f / (cutoff - halfField) : 0f;
        _fieldEstimated = fieldEstimated;
    }

    /// <summary>GDTF beam type this distribution was made for.</summary>
    public BeamType Type => _type;

    /// <summary>Shape family.</summary>
    public BeamShape Shape => _shape;

    /// <summary>Full angle at 50 % of peak intensity, rad (after sanitising).</summary>
    public float BeamAngle => _shape switch
    {
        BeamShape.Lambertian => 2f * MathF.PI / 3f,
        _ => 2f * _halfBeam,
    };

    /// <summary>Full angle at 10 % of peak intensity, rad (after sanitising; equals the beam angle for a uniform cone).</summary>
    public float FieldAngle => _shape switch
    {
        BeamShape.Lambertian => 2f * _lambertFieldHalfAngle,
        _ => 2f * _halfField,
    };

    /// <summary>Half-angle from the axis beyond which the intensity is exactly zero, rad.</summary>
    public float CutoffAngle => _shape switch
    {
        BeamShape.Lambertian => MathF.PI / 2f,
        _ => _cutoff,
    };

    /// <summary>Exponent p of the generalised Gaussian (0 for other shapes).</summary>
    public float Exponent => _shape == BeamShape.GeneralizedGaussian ? _exponent : 0f;

    /// <summary>True when the field angle was missing or invalid and has been estimated from the beam angle.</summary>
    public bool FieldAngleEstimated => _fieldEstimated;

    /// <summary>Diffuse (cosine) distribution of a glowing surface.</summary>
    public static BeamDistribution Lambertian(BeamType type = BeamType.Glow) =>
        new(type, BeamShape.Lambertian, MathF.PI / 3f, _lambertFieldHalfAngle, MathF.PI / 2f, 0f, false);

    /// <summary>Hard-edged uniform cone of half-angle <paramref name="halfAngleRad"/> (clamped to [10⁻⁴, π]).</summary>
    public static BeamDistribution UniformCone(float halfAngleRad)
    {
        float a = float.IsNaN(halfAngleRad) ? MinHalfAngle : Math.Clamp(halfAngleRad, MinHalfAngle, MathF.PI);
        return new BeamDistribution(BeamType.Spot, BeamShape.UniformCone, a, a, a, 0f, false);
    }

    /// <summary>Distribution for a GDTF beam with full beam and field angles in degrees (as stored in GDTF files).</summary>
    public static BeamDistribution FromDegrees(BeamType type, float beamAngleDeg, float fieldAngleDeg) =>
        Create(type, beamAngleDeg * (MathF.PI / 180f), fieldAngleDeg * (MathF.PI / 180f));

    /// <summary>
    /// Distribution for a beam of the given type with full beam angle (50 %) and full field angle (10 %) in radians.
    /// Angles are sanitised as described on <see cref="BeamDistribution"/>; Glow and None give a Lambertian emitter.
    /// </summary>
    public static BeamDistribution Create(BeamType type, float beamAngleRad, float fieldAngleRad)
    {
        if (type is BeamType.Glow or BeamType.None)
            return Lambertian(type);
        if (!Enum.IsDefined(type))
            type = BeamType.Wash;

        float halfBeam = float.IsFinite(beamAngleRad) && beamAngleRad > 0f ? 0.5f * beamAngleRad : MinHalfAngle;
        halfBeam = Math.Clamp(halfBeam, MinHalfAngle, MathF.Min(MaxBeamHalfAngle, MaxFieldHalfAngle / _minFieldRatio));
        float halfField = float.IsFinite(fieldAngleRad) ? 0.5f * fieldAngleRad : 0f;
        bool estimated = false;
        if (!(halfField > halfBeam * _minFieldRatio))
        {
            halfField = halfBeam * DefaultFieldRatio(type);
            estimated = true;
        }

        // halfBeam ≤ 0.49π keeps the lower bound below the upper one.
        halfField = Math.Clamp(halfField, halfBeam * _minFieldRatio, MathF.Min(halfBeam * _maxFieldRatio, MaxFieldHalfAngle));
        float exponent = Math.Clamp(_lnRatio / MathF.Log(halfField / halfBeam), MinExponent, MaxExponent);
        float extension = MathF.Max(WindowFactor(type) * (halfField - halfBeam), 0.1f * halfField);
        float cutoff = MathF.Min(halfField + extension, MathF.PI);
        return new BeamDistribution(type, BeamShape.GeneralizedGaussian, halfBeam, halfField, cutoff, exponent, estimated);
    }

    /// <summary>The same beam type with new full beam and field angles (zoom), rad.</summary>
    public BeamDistribution WithAngles(float beamAngleRad, float fieldAngleRad) => _shape switch
    {
        BeamShape.Lambertian => this,
        BeamShape.UniformCone => UniformCone(0.5f * beamAngleRad),
        _ => Create(_type, beamAngleRad, fieldAngleRad),
    };

    /// <summary>Zoomed to a new full beam angle (rad), keeping the field/beam ratio and therefore the profile shape.</summary>
    public BeamDistribution Zoomed(float beamAngleRad) => _shape switch
    {
        BeamShape.Lambertian => this,
        BeamShape.UniformCone => UniformCone(0.5f * beamAngleRad),
        _ => Create(_type, beamAngleRad, beamAngleRad * (_halfField / _halfBeam)),
    };

    /// <summary>
    /// Relative intensity I(θ)/I(0) at <paramref name="thetaRad"/> from the axis (sign ignored): 1 on the axis,
    /// 0.5 at half the beam angle, 0.1 at half the field angle, 0 beyond <see cref="CutoffAngle"/>. NaN gives 0.
    /// </summary>
    public float Relative(float thetaRad)
    {
        float t = MathF.Abs(thetaRad);
        switch (_shape)
        {
            case BeamShape.Lambertian:
                return t < MathF.PI / 2f ? MathF.Cos(t) : 0f;
            case BeamShape.UniformCone:
                return t <= _halfBeam ? 1f : 0f;
            default:
                if (!(t < _cutoff))
                    return 0f;
                float r = t * _invHalfBeam;
                float g = r > 0f ? MathF.Exp(-Ln2 * MathF.Exp(_exponent * MathF.Log(r))) : 1f;
                if (t > _halfField)
                {
                    float s = (t - _halfField) * _invWindow;
                    g *= 1f - s * s * (3f - 2f * s);
                }

                return g;
        }
    }

    /// <summary>
    /// Ω_eff = 2π·∫₀^π I_rel(θ)·sin θ dθ, sr — the solid angle a uniform cone of the peak intensity would need to carry
    /// the same flux. Computed by piecewise Gauss–Legendre quadrature (segments split at the beam, field and cut-off
    /// angles), relative accuracy better than 10⁻⁶; allocation-free.
    /// </summary>
    public double SolidAngleIntegral()
    {
        double integral = _shape switch
        {
            BeamShape.Lambertian => Integrate(0.0, Math.PI / 2.0),
            BeamShape.UniformCone => Integrate(0.0, _halfBeam),
            _ => Integrate(0.0, _halfBeam) + Integrate(_halfBeam, _halfField) + Integrate(_halfField, _cutoff),
        };
        return 2.0 * Math.PI * integral;
    }

    /// <summary>Peak (on-axis) intensity I(0) = Φ/Ω_eff, cd, of a beam carrying luminous flux <paramref name="fluxLm"/> (lm).</summary>
    public float PeakIntensityFromFlux(float fluxLm)
    {
        if (!(fluxLm > 0f) || !float.IsFinite(fluxLm))
            return 0f;
        double omega = SolidAngleIntegral();
        return omega > 0 ? (float)(fluxLm / omega) : 0f;
    }

    /// <summary>Luminous flux Φ = I(0)·Ω_eff, lm, of a beam with peak intensity <paramref name="peakCandela"/> (cd).</summary>
    public float FluxFromPeakIntensity(float peakCandela)
    {
        if (!(peakCandela > 0f) || !float.IsFinite(peakCandela))
            return 0f;
        return (float)(peakCandela * SolidAngleIntegral());
    }

    /// <summary>
    /// Tabulates <see cref="Relative"/> at <paramref name="samples"/> angles evenly spaced over [0, <paramref name="maxThetaRad"/>]
    /// (inclusive) — e.g. a 1D texture for the GPU, sampled with linear filtering at u = θ/maxθ.
    /// </summary>
    /// <param name="samples">Number of entries (≥ 2).</param>
    /// <param name="maxThetaRad">Angle of the last entry, rad; non-positive or non-finite → <see cref="CutoffAngle"/>.</param>
    public float[] BakeLut(int samples, float maxThetaRad)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);
        var lut = new float[samples];
        BakeLut(lut, maxThetaRad);
        return lut;
    }

    /// <summary>Allocation-free <see cref="BakeLut(int, float)"/> into <paramref name="destination"/> (length ≥ 2).</summary>
    public void BakeLut(Span<float> destination, float maxThetaRad)
    {
        if (destination.Length < 2)
            throw new ArgumentException("A beam LUT needs at least 2 entries.", nameof(destination));
        float max = float.IsFinite(maxThetaRad) && maxThetaRad > 0f ? maxThetaRad : CutoffAngle;
        float step = max / (destination.Length - 1);
        for (int i = 0; i < destination.Length; i++)
            destination[i] = Relative(i * step);
    }

    /// <inheritdoc/>
    public bool Equals(BeamDistribution other) =>
        _type == other._type && _shape == other._shape && _halfBeam.Equals(other._halfBeam) &&
        _halfField.Equals(other._halfField) && _cutoff.Equals(other._cutoff) && _exponent.Equals(other._exponent);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BeamDistribution other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_type, _shape, _halfBeam, _halfField, _cutoff, _exponent);

    /// <summary>Equality of type, shape and parameters.</summary>
    public static bool operator ==(BeamDistribution left, BeamDistribution right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(BeamDistribution left, BeamDistribution right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{_type} {_shape}: beam {BeamAngle * 180f / MathF.PI:F2}°, field {FieldAngle * 180f / MathF.PI:F2}°, p = {Exponent:F3}");

    private double Integrate(double from, double to) =>
        GaussLegendre.Integrate(from, to, this, static (beam, theta) => beam.Relative((float)theta) * Math.Sin(theta));

    private static float DefaultFieldRatio(BeamType type) => type switch
    {
        BeamType.Spot or BeamType.Rectangle => 1.30f,
        BeamType.PC => 1.60f,
        BeamType.Fresnel => 2.0f,
        _ => 1.8226f, // sqrt(ln 10 / ln 2): a true Gaussian
    };

    private static float WindowFactor(BeamType type) => type switch
    {
        BeamType.Spot or BeamType.Rectangle => 0.35f,
        BeamType.PC => 0.8f,
        BeamType.Fresnel => 1.0f,
        _ => 0.6f,
    };
}
