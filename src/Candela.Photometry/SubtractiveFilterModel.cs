using System.Numerics;

namespace Candela.Photometry;

/// <summary>
/// Fast spectral evaluation of subtractive colour (CMY flags and a colour-temperature conversion filter) in front of
/// one white source — built once per fixture, evaluated every frame without allocations (≈ 95 multiply-adds, plus 95
/// exponentials when a conversion filter is active).
/// </summary>
/// <remarks>
/// The source spectrum is pre-multiplied with the CIE 1931 colour-matching functions on their 5 nm grid; each
/// evaluation multiplies in the filter transmissions of <see cref="Filters"/> (CMY model and ideal Planckian
/// conversion filter) and sums. Filters in series multiply spectrally, so CMY + CTO combine correctly, which a product
/// of chromaticities would not.
/// </remarks>
public sealed class SubtractiveFilterModel
{
    private readonly float[] _sx = new float[Cie1931.Count];
    private readonly float[] _sy = new float[Cie1931.Count];
    private readonly float[] _sz = new float[Cie1931.Count];
    private readonly float[] _cyan = new float[Cie1931.Count];
    private readonly float[] _magenta = new float[Cie1931.Count];
    private readonly float[] _yellow = new float[Cie1931.Count];

    // Conversion filter: c₂/λ per wavelength and the source's Planck denominator e^(c₂/λT₁) − 1, so that each
    // evaluation needs one exponential per wavelength for the target temperature only.
    private readonly double[] _c2OverLambda = new double[Cie1931.Count];
    private readonly double[] _sourceExpm1 = new double[Cie1931.Count];
    private readonly double _incidentY;

    /// <summary>Creates the model for a source spectrum.</summary>
    /// <param name="source">Spectral power distribution of the source (any scale).</param>
    /// <param name="sourceKelvin">
    /// CCT the conversion filter starts from; NaN (default) computes it from the spectrum (6500 K if that fails).
    /// </param>
    public SubtractiveFilterModel(Spectrum source, float sourceKelvin = float.NaN)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        double incident = 0;
        for (int i = 0; i < Cie1931.Count; i++)
        {
            float w = Cie1931.WavelengthAt(i);
            float s = source.Sample(w);
            _sx[i] = s * CieTables.XBar[i];
            _sy[i] = s * CieTables.YBar[i];
            _sz[i] = s * CieTables.ZBar[i];
            incident += _sy[i];
            _cyan[i] = Filters.PeakAbsorption * Filters.CyanBand(w);
            _magenta[i] = Filters.PeakAbsorption * Filters.MagentaBand(w);
            _yellow[i] = Filters.PeakAbsorption * Filters.YellowBand(w);
        }

        _incidentY = incident;
        SourceColor = LightColor.FromSpectrum(source);
        if (float.IsFinite(sourceKelvin) && sourceKelvin > 0f)
        {
            SourceKelvin = (float)Cct.ClampKelvin(sourceKelvin);
        }
        else
        {
            var cct = SourceColor.ToCct();
            SourceKelvin = cct.IsValid ? cct.Kelvin : 6500f;
        }

        for (int i = 0; i < Cie1931.Count; i++)
        {
            _c2OverLambda[i] = PhysicalConstants.SecondRadiationConstant / (Cie1931.WavelengthAt(i) * 1e-9);
            _sourceExpm1[i] = double.ExpM1(Math.Min(_c2OverLambda[i] / SourceKelvin, 700.0));
        }
    }

    /// <summary>The source spectrum.</summary>
    public Spectrum Source { get; }

    /// <summary>Colour of the unfiltered source.</summary>
    public LightColor SourceColor { get; }

    /// <summary>CCT the conversion filter converts from, K.</summary>
    public float SourceKelvin { get; }

    /// <summary>
    /// A model for a white source known only by its CCT, using the CIE reference illuminant of that CCT
    /// (<see cref="Spectrum.ReferenceIlluminant"/>): Planckian below 5000 K, daylight above — a reasonable stand-in for
    /// tungsten, discharge lamps and white LED engines.
    /// </summary>
    public static SubtractiveFilterModel ForCct(float kelvin) => new(Spectrum.ReferenceIlluminant(kelvin), kelvin);

    /// <summary>CMY flags at the given levels (0..1; NaN = 0).</summary>
    public FilterResult Evaluate(float cyan, float magenta, float yellow) => Evaluate(cyan, magenta, yellow, float.NaN);

    /// <summary>
    /// CMY flags plus a conversion filter to <paramref name="conversionTargetKelvin"/> (NaN or ≤ 0 = no conversion
    /// filter; clamped to 1000–25000 K).
    /// </summary>
    public FilterResult Evaluate(float cyan, float magenta, float yellow, float conversionTargetKelvin)
    {
        if (!(_incidentY > 0))
            return new FilterResult(SourceColor, 0f);
        float c = Filters.Level(cyan), m = Filters.Level(magenta), y = Filters.Level(yellow);
        bool convert = float.IsFinite(conversionTargetKelvin) && conversionTargetKelvin > 0f;
        double ts = SourceKelvin, tt = convert ? Cct.ClampKelvin(conversionTargetKelvin) : ts;
        convert &= tt != ts;
        double norm = convert ? Filters.ConversionNormalization(ts, tt) : 1.0;

        double x = 0, yy = 0, z = 0;
        for (int i = 0; i < Cie1931.Count; i++)
        {
            double t = (1f - c * _cyan[i]) * (1f - m * _magenta[i]) * (1f - y * _yellow[i]);
            if (convert)
            {
                // B(λ,T₂)/B(λ,T₁) = (e^(c₂/λT₁) − 1)/(e^(c₂/λT₂) − 1), normalised to a peak of 1.
                double xt = _c2OverLambda[i] / tt;
                t *= xt > 700.0 ? 0.0 : Math.Min(1.0, _sourceExpm1[i] / double.ExpM1(xt) / norm);
            }
            x += t * _sx[i];
            yy += t * _sy[i];
            z += t * _sz[i];
        }

        float factor = (float)Math.Clamp(yy / _incidentY, 0.0, 1.0);
        var xyz = new Vector3((float)x, (float)yy, (float)z);
        return new FilterResult(yy > 0 ? LightColor.FromXyz(xyz) : SourceColor, factor);
    }
}
