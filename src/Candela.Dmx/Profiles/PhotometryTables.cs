using System.Collections.Concurrent;
using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx;

/// <summary>
/// Effective solid angle Ω_eff (sr) of one beam as a function of its zoom (full beam angle), with the field/beam ratio
/// of the fixture's optics kept constant. Built once per beam definition from <see cref="BeamDistribution"/> (≈ 3 µs
/// per sample) so that per-frame peak intensities I₀ = Φ/Ω_eff cost a table lookup instead of a quadrature.
/// </summary>
public sealed class BeamSolidAngleTable
{
    private const int Samples = 48;
    private readonly float[] _omega;
    private readonly float _logMin;
    private readonly float _logStep;

    /// <summary>Builds the table for beam angles <paramref name="minBeamAngle"/> … <paramref name="maxBeamAngle"/> (rad).</summary>
    /// <param name="type">Optical beam type.</param>
    /// <param name="minBeamAngle">Narrowest full beam angle, rad.</param>
    /// <param name="maxBeamAngle">Widest full beam angle, rad.</param>
    /// <param name="fieldRatio">Field angle / beam angle (≥ 1).</param>
    public BeamSolidAngleTable(Photometry.BeamType type, float minBeamAngle, float maxBeamAngle, float fieldRatio)
    {
        Type = type;
        FieldRatio = float.IsFinite(fieldRatio) && fieldRatio >= 1f ? fieldRatio : 1.3f;
        float lo = SanitizeAngle(MathF.Min(minBeamAngle, maxBeamAngle));
        float hi = SanitizeAngle(MathF.Max(minBeamAngle, maxBeamAngle));
        MinBeamAngle = lo;
        MaxBeamAngle = hi;
        bool lambertian = type is Photometry.BeamType.Glow or Photometry.BeamType.None;
        int n = lambertian || hi / lo < 1.0005f ? 1 : Samples;
        _omega = new float[n];
        _logMin = MathF.Log(lo);
        _logStep = n > 1 ? (MathF.Log(hi) - _logMin) / (n - 1) : 1f;
        for (int i = 0; i < n; i++)
        {
            float beam = MathF.Exp(_logMin + i * _logStep);
            var d = BeamDistribution.Create(type, beam, beam * FieldRatio);
            _omega[i] = (float)Math.Max(d.SolidAngleIntegral(), 1e-9);
        }
    }

    /// <summary>Optical beam type.</summary>
    public Photometry.BeamType Type { get; }

    /// <summary>Field angle / beam angle used for every sample.</summary>
    public float FieldRatio { get; }

    /// <summary>Narrowest tabulated full beam angle, rad.</summary>
    public float MinBeamAngle { get; }

    /// <summary>Widest tabulated full beam angle, rad.</summary>
    public float MaxBeamAngle { get; }

    /// <summary>Ω_eff (sr) at full beam angle <paramref name="beamAngle"/> (rad; clamped to the table range).</summary>
    public float SolidAngle(float beamAngle)
    {
        if (_omega.Length == 1) return _omega[0];
        float t = float.IsFinite(beamAngle) && beamAngle > 0f ? (MathF.Log(beamAngle) - _logMin) / _logStep : 0f;
        if (!(t > 0f)) return _omega[0];
        if (t >= _omega.Length - 1) return _omega[^1];
        int i = (int)t;
        float f = t - i;
        return _omega[i] + (_omega[i + 1] - _omega[i]) * f;
    }

    private static float SanitizeAngle(float a) =>
        float.IsFinite(a) ? Math.Clamp(a, 2f * BeamDistribution.MinHalfAngle, 2f * BeamDistribution.MaxBeamHalfAngle / 1.0001f) : 0.4363f;
}

/// <summary>
/// Spectral CMY flags plus an ideal colour-temperature conversion filter (CTO/CTB/CTC) in front of one white source,
/// evaluated in about 30 multiply-adds per call. Same physical model as <see cref="SubtractiveFilterModel"/> (the CMY
/// absorption bands of <see cref="Filters"/> and the Planck-ratio conversion filter), but expanded algebraically: the
/// transmission (1 − c·C(λ))(1 − m·M(λ))(1 − y·Y(λ)) is a polynomial in c, m, y whose eight coefficient spectra are
/// integrated against source × CMFs once, for conversion targets on a 5-mired grid (linear interpolation between).
/// </summary>
public sealed class SubtractiveColorTable
{
    private const double MiredStep = 5.0;
    private const double MinMired = 1e6 / 25000.0;
    private const double MaxMired = 1e6 / 1000.0;
    private static readonly ConcurrentDictionary<int, SubtractiveColorTable> _cache = new();

    // [entry * 8 + term]: XYZ integrals of the eight CMY monomials (1, c, m, y, cm, cy, my, cmy) with the conversion
    // filter of that entry; entry 0 is "no conversion filter", entries 1… the mired grid.
    private readonly Vector3[] _terms;
    private readonly int _gridCount;
    private readonly float _incidentY;

    private SubtractiveColorTable(float sourceKelvin)
    {
        SourceKelvin = Math.Clamp(sourceKelvin, 1000f, 25000f);
        var source = Spectrum.ReferenceIlluminant(SourceKelvin);
        int n = Cie1931.Count;
        var sx = new double[n];
        var sy = new double[n];
        var sz = new double[n];
        var cy = new double[n];
        var mg = new double[n];
        var ye = new double[n];
        var c2l = new double[n];
        double incident = 0;
        var xbar = Cie1931.XBar;
        var ybar = Cie1931.YBar;
        var zbar = Cie1931.ZBar;
        for (int i = 0; i < n; i++)
        {
            float w = Cie1931.WavelengthAt(i);
            double s = source.Sample(w);
            sx[i] = s * xbar[i];
            sy[i] = s * ybar[i];
            sz[i] = s * zbar[i];
            incident += sy[i];
            cy[i] = Filters.PeakAbsorption * Filters.CyanBand(w);
            mg[i] = Filters.PeakAbsorption * Filters.MagentaBand(w);
            ye[i] = Filters.PeakAbsorption * Filters.YellowBand(w);
            c2l[i] = PhysicalConstants.SecondRadiationConstant / (w * 1e-9);
        }
        _incidentY = (float)incident;
        _gridCount = (int)Math.Ceiling((MaxMired - MinMired) / MiredStep) + 1;
        _terms = new Vector3[(_gridCount + 1) * 8];
        double ts = SourceKelvin;
        Span<double> acc = stackalloc double[24];
        for (int e = 0; e <= _gridCount; e++)
        {
            acc.Clear();
            double tt = e == 0 ? ts : 1e6 / (MinMired + (e - 1) * MiredStep);
            bool convert = e > 0;
            double norm = convert ? ConversionNormalization(ts, tt) : 1.0;
            for (int i = 0; i < n; i++)
            {
                double conv = 1.0;
                if (convert)
                {
                    double ratio = PlanckRatio(c2l[i], ts, tt);
                    conv = Math.Min(1.0, ratio / norm);
                }
                double c = cy[i], m = mg[i], y = ye[i];
                // monomial coefficient spectra of (1 − cC)(1 − mM)(1 − yY): 1, −C, −M, −Y, +CM, +CY, +MY, −CMY
                Span<double> k = [1.0, -c, -m, -y, c * m, c * y, m * y, -c * m * y];
                for (int t = 0; t < 8; t++)
                {
                    double f = conv * k[t];
                    acc[t * 3] += f * sx[i];
                    acc[t * 3 + 1] += f * sy[i];
                    acc[t * 3 + 2] += f * sz[i];
                }
            }
            for (int t = 0; t < 8; t++)
                _terms[e * 8 + t] = new Vector3((float)(acc[t * 3] / incident), (float)(acc[t * 3 + 1] / incident), (float)(acc[t * 3 + 2] / incident));
        }
        SourceXyz = _terms[0];
        SourceColor = LightColor.FromXyz(SourceXyz);
        SourceRgb = ColorSpaces.XyzToRec2020(SourceXyz);
    }

    /// <summary>CCT of the white source, K.</summary>
    public float SourceKelvin { get; }

    /// <summary>XYZ of the unfiltered source normalised to Y = 1.</summary>
    public Vector3 SourceXyz { get; }

    /// <summary>Colour of the unfiltered source.</summary>
    public LightColor SourceColor { get; }

    /// <summary>Linear Rec.2020 RGB of the unfiltered source (luminance 1).</summary>
    public Vector3 SourceRgb { get; }

    /// <summary>Shared table for a white source of <paramref name="kelvin"/> (rounded to 10 K; CIE reference illuminant spectrum).</summary>
    public static SubtractiveColorTable ForKelvin(float kelvin)
    {
        if (!float.IsFinite(kelvin) || kelvin <= 0f) kelvin = 6500f;
        int key = (int)MathF.Round(Math.Clamp(kelvin, 1000f, 25000f) / 10f);
        return _cache.GetOrAdd(key, static k => new SubtractiveColorTable(k * 10f));
    }

    /// <summary>
    /// XYZ of the filtered light relative to the unfiltered source (source Y = 1, so the result's Y is the luminous
    /// transmission). Levels are clamped to 0 … 1 (NaN = 0); <paramref name="targetKelvin"/> NaN or ≤ 0 means no
    /// conversion filter. Allocation-free.
    /// </summary>
    public Vector3 Evaluate(float cyan, float magenta, float yellow, float targetKelvin)
    {
        float c = Level(cyan), m = Level(magenta), y = Level(yellow);
        if (!(targetKelvin > 0f) || !float.IsFinite(targetKelvin) || MathF.Abs(targetKelvin - SourceKelvin) < 0.5f)
            return Combine(0, c, m, y);
        double mired = 1e6 / Math.Clamp(targetKelvin, 1000f, 25000f);
        double pos = (mired - MinMired) / MiredStep;
        int i = Math.Clamp((int)Math.Floor(pos), 0, _gridCount - 2);
        float f = (float)Math.Clamp(pos - i, 0.0, 1.0);
        var a = Combine(i + 1, c, m, y);
        var b = Combine(i + 2, c, m, y);
        return a + (b - a) * f;
    }

    /// <summary>
    /// Per-channel (linear Rec.2020) transmission of the filters for this source: the filtered RGB divided by the source
    /// RGB. Multiplying any light of about this source's colour by it applies the filters (von Kries-like for other colours).
    /// </summary>
    public Vector3 RgbTransmission(float cyan, float magenta, float yellow, float targetKelvin)
    {
        var rgb = ColorSpaces.XyzToRec2020(Evaluate(cyan, magenta, yellow, targetKelvin));
        var s = SourceRgb;
        return new Vector3(Ratio(rgb.X, s.X), Ratio(rgb.Y, s.Y), Ratio(rgb.Z, s.Z));
    }

    private static float Ratio(float a, float b) => b > 1e-4f ? Math.Clamp(a / b, 0f, 1f) : 0f;

    private Vector3 Combine(int entry, float c, float m, float y)
    {
        int b = entry * 8;
        var t = _terms;
        return t[b] + t[b + 1] * c + t[b + 2] * m + t[b + 3] * y + t[b + 4] * (c * m) + t[b + 5] * (c * y) + t[b + 6] * (m * y)
            + t[b + 7] * (c * m * y);
    }

    private static float Level(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 1f);

    private static double PlanckRatio(double c2OverLambda, double sourceKelvin, double targetKelvin)
    {
        double xs = c2OverLambda / sourceKelvin, xt = c2OverLambda / targetKelvin;
        if (xt > 700.0) return 0.0;
        return double.ExpM1(Math.Min(xs, 700.0)) / double.ExpM1(xt);
    }

    private static double ConversionNormalization(double sourceKelvin, double targetKelvin)
    {
        double c380 = PhysicalConstants.SecondRadiationConstant / 380e-9, c780 = PhysicalConstants.SecondRadiationConstant / 780e-9;
        double norm = Math.Max(PlanckRatio(c380, sourceKelvin, targetKelvin), PlanckRatio(c780, sourceKelvin, targetKelvin));
        return norm > 0 && double.IsFinite(norm) ? norm : 1.0;
    }
}
