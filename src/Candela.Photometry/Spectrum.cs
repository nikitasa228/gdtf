using System.Diagnostics.CodeAnalysis;

namespace Candela.Photometry;

/// <summary>
/// An immutable spectral distribution sampled on a regular wavelength grid: <see cref="StartNm"/>,
/// <see cref="StepNm"/> and one value per sample. The meaning and unit of the values depend on use — relative
/// spectral power (CIE illuminants, 100 at 560 nm), spectral power in W/nm (emitters), spectral irradiance in
/// W·m⁻²·nm⁻¹, or a dimensionless transmission factor 0..1 (filters).
/// </summary>
/// <remarks>
/// <para>Between samples the distribution is linearly interpolated. Outside its range a spectrum is zero
/// (<see cref="Sample"/>), except when it is used as a <em>factor</em> in <see cref="Multiply"/>, where it is
/// extended with its edge values so that a filter tabulated over 380–780 nm does not cut off the tails of a source.</para>
/// <para>Integrals use the CIE summation rule Σ S(λᵢ)·W(λᵢ)·Δλ on the finer of the two grids involved
/// (CIE 015:2004 §7.1).</para>
/// <para>Spectra are created at load time or for analysis; per-frame code uses precomputed models such as
/// <see cref="SubtractiveFilterModel"/> instead of allocating spectra.</para>
/// </remarks>
public sealed class Spectrum
{
    /// <summary>First wavelength of the default visible grid (the CIE 1931 table), nm.</summary>
    public const float DefaultStartNm = 360f;

    /// <summary>Step of the default visible grid, nm.</summary>
    public const float DefaultStepNm = 5f;

    /// <summary>Number of samples of the default visible grid (360–830 nm at 5 nm).</summary>
    public const int DefaultCount = 95;

    /// <summary>Largest number of samples a spectrum may have (guards against absurd grids, e.g. a 0.0001 nm step).</summary>
    public const int MaxCount = 1_000_000;

    private static readonly Lazy<Spectrum> _d65 = new(() =>
        new Spectrum(CieTables.IlluminantStartNm, CieTables.StepNm, CieTables.D65));

    private static readonly Lazy<Spectrum> _illuminantA = new(CreateIlluminantA);

    private readonly float[] _values;

    /// <summary>
    /// Creates a spectrum from a copy of <paramref name="values"/>. Non-finite values are replaced by 0 so that one
    /// corrupt sample cannot poison every integral derived from the spectrum.
    /// </summary>
    /// <param name="startNm">Wavelength of the first sample, nm.</param>
    /// <param name="stepNm">Distance between samples, nm (must be &gt; 0).</param>
    /// <param name="values">At least one sample value.</param>
    /// <exception cref="ArgumentOutOfRangeException">The grid is invalid (non-finite start, step ≤ 0, no values or more than <see cref="MaxCount"/>).</exception>
    public Spectrum(float startNm, float stepNm, ReadOnlySpan<float> values)
        : this(startNm, stepNm, Sanitize(values))
    {
    }

    /// <summary>Takes ownership of <paramref name="values"/> (no copy). Callers guarantee finite values.</summary>
    private Spectrum(float startNm, float stepNm, float[] values)
    {
        ValidateGrid(startNm, stepNm, values.Length);
        StartNm = startNm;
        StepNm = stepNm;
        _values = values;
    }

    /// <summary>Wavelength of the first sample, nm.</summary>
    public float StartNm { get; }

    /// <summary>Distance between samples, nm.</summary>
    public float StepNm { get; }

    /// <summary>Number of samples.</summary>
    public int Count => _values.Length;

    /// <summary>Wavelength of the last sample, nm.</summary>
    public float EndNm => StartNm + StepNm * (_values.Length - 1);

    /// <summary>The sample values in grid order.</summary>
    public ReadOnlySpan<float> Values => _values;

    /// <summary>The value of sample <paramref name="index"/>.</summary>
    public float this[int index] => _values[index];

    /// <summary>
    /// CIE standard illuminant D65 (average daylight, CCT ≈ 6504 K), tabulated 300–780 nm at 5 nm, relative
    /// (100 at 560 nm). Source: CIE 015:2004 Table T.1 / ISO 11664-2.
    /// </summary>
    public static Spectrum IlluminantD65 => _d65.Value;

    /// <summary>
    /// CIE standard illuminant A (tungsten-filament light, CCT ≈ 2856 K), 300–830 nm at 5 nm, relative (100 at 560 nm),
    /// computed from its defining equation (ISO 11664-2:2007, eq. 1):
    /// S_A(λ) = 100·(560/λ)⁵·(e^(1.435×10⁷/(2848·560)) − 1)/(e^(1.435×10⁷/(2848·λ)) − 1), λ in nm.
    /// </summary>
    public static Spectrum IlluminantA => _illuminantA.Value;

    /// <summary>Wavelength of sample <paramref name="index"/>, nm.</summary>
    public float WavelengthAt(int index) => StartNm + StepNm * index;

    /// <summary>Linearly interpolated value at <paramref name="wavelengthNm"/>; 0 outside the tabulated range.</summary>
    public float Sample(float wavelengthNm)
    {
        double f = (wavelengthNm - (double)StartNm) / StepNm;
        int last = _values.Length - 1;
        if (!(f >= -1e-6) || f > last + 1e-6)
            return 0f;
        return Interpolate(f);
    }

    /// <summary>Linearly interpolated value at <paramref name="wavelengthNm"/>; outside the range the edge value is returned.</summary>
    public float SampleClamped(float wavelengthNm)
    {
        double f = (wavelengthNm - (double)StartNm) / StepNm;
        if (double.IsNaN(f))
            return 0f;
        return Interpolate(f);
    }

    /// <summary>Point-samples this spectrum (linear interpolation, 0 outside) onto a new regular grid.</summary>
    /// <param name="startNm">First wavelength of the new grid, nm.</param>
    /// <param name="stepNm">Step of the new grid, nm.</param>
    /// <param name="count">Number of samples of the new grid.</param>
    public Spectrum Resample(float startNm, float stepNm, int count)
    {
        ValidateGrid(startNm, stepNm, count);
        var values = new float[count];
        for (int i = 0; i < values.Length; i++)
            values[i] = Sample(startNm + stepNm * i);
        return new Spectrum(startNm, stepNm, values);
    }

    /// <summary>
    /// Product S(λ)·F(λ) on this spectrum's grid — e.g. a source filtered by a transmission curve. The factor is
    /// extended with its edge values outside its own range.
    /// </summary>
    public Spectrum Multiply(Spectrum factor)
    {
        ArgumentNullException.ThrowIfNull(factor);
        var values = new float[_values.Length];
        if (SameGrid(factor))
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = _values[i] * factor._values[i];
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = _values[i] * factor.SampleClamped(WavelengthAt(i));
        }

        return new Spectrum(StartNm, StepNm, values);
    }

    /// <summary>Every value multiplied by <paramref name="factor"/> (non-finite factors give an all-zero spectrum).</summary>
    public Spectrum Scale(float factor)
    {
        if (!float.IsFinite(factor))
            factor = 0f;
        var values = new float[_values.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = _values[i] * factor;
        return new Spectrum(StartNm, StepNm, values);
    }

    /// <summary>
    /// Sum S(λ)+T(λ). On identical grids the samples are added directly; otherwise the result covers the union of
    /// both ranges on the finer step, each operand being zero outside its own range.
    /// </summary>
    public Spectrum Add(Spectrum other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (SameGrid(other))
        {
            var sum = new float[_values.Length];
            for (int i = 0; i < sum.Length; i++)
                sum[i] = _values[i] + other._values[i];
            return new Spectrum(StartNm, StepNm, sum);
        }

        float start = MathF.Min(StartNm, other.StartNm);
        float end = MathF.Max(EndNm, other.EndNm);
        float step = MathF.Min(StepNm, other.StepNm);
        int count = GridCount(start, end, step);
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            float wavelength = start + step * i;
            values[i] = Sample(wavelength) + other.Sample(wavelength);
        }

        return new Spectrum(start, step, values);
    }

    /// <summary>∫ S(λ) dλ by the summation rule Σ S(λᵢ)·Δλ (unit: value unit × nm).</summary>
    public double Integrate()
    {
        double sum = 0;
        foreach (float v in _values)
            sum += v;
        return sum * StepNm;
    }

    /// <summary>
    /// ∫ S(λ)·W(λ) dλ by the summation rule on the finer of the two grids, over the range both spectra cover
    /// (unit: value unit × weighting unit × nm).
    /// </summary>
    public double Integrate(Spectrum weighting)
    {
        ArgumentNullException.ThrowIfNull(weighting);
        if (SameGrid(weighting))
        {
            double direct = 0;
            for (int i = 0; i < _values.Length; i++)
                direct += (double)_values[i] * weighting._values[i];
            return direct * StepNm;
        }

        var (grid, other) = weighting.StepNm < StepNm ? (weighting, this) : (this, weighting);
        double lo = Math.Max(StartNm, weighting.StartNm), hi = Math.Min(EndNm, weighting.EndNm);
        if (hi < lo)
            return 0;
        int first = Math.Max(0, (int)Math.Ceiling((lo - grid.StartNm) / grid.StepNm - 1e-6));
        int last = Math.Min(grid.Count - 1, (int)Math.Floor((hi - grid.StartNm) / grid.StepNm + 1e-6));
        double sum = 0;
        for (int i = first; i <= last; i++)
            sum += (double)grid._values[i] * other.Sample(grid.WavelengthAt(i));
        return sum * grid.StepNm;
    }

    /// <summary>Largest sample value.</summary>
    public float MaxValue()
    {
        float max = float.NegativeInfinity;
        foreach (float v in _values)
            max = MathF.Max(max, v);
        return max;
    }

    /// <summary>
    /// The spectrum scaled so that its value at <paramref name="wavelengthNm"/> equals <paramref name="value"/>
    /// (CIE illuminants use 100 at 560 nm). Returned unchanged when the value there is zero.
    /// </summary>
    public Spectrum NormalizedAt(float wavelengthNm, float value = 1f)
    {
        float current = Sample(wavelengthNm);
        return current != 0f && float.IsFinite(current) ? Scale(value / current) : this;
    }

    /// <summary>The spectrum scaled so that its largest value is 1. Returned unchanged when the maximum is ≤ 0.</summary>
    public Spectrum NormalizedToPeak()
    {
        float max = MaxValue();
        return max > 0f ? Scale(1f / max) : this;
    }

    /// <summary>A copy of the sample values.</summary>
    public float[] ToArray() => (float[])_values.Clone();

    /// <inheritdoc/>
    public override string ToString() => $"Spectrum {StartNm}–{EndNm} nm, step {StepNm} nm, {Count} samples";

    /// <summary>Filtering: <c>source * transmission</c>, see <see cref="Multiply"/>.</summary>
    public static Spectrum operator *(Spectrum spectrum, Spectrum factor)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        return spectrum.Multiply(factor);
    }

    /// <summary>Scaling, see <see cref="Scale"/>.</summary>
    public static Spectrum operator *(Spectrum spectrum, float factor)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        return spectrum.Scale(factor);
    }

    /// <summary>Scaling, see <see cref="Scale"/>.</summary>
    public static Spectrum operator *(float factor, Spectrum spectrum)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        return spectrum.Scale(factor);
    }

    /// <summary>Additive mixing, see <see cref="Add"/>.</summary>
    public static Spectrum operator +(Spectrum left, Spectrum right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Add(right);
    }

    /// <summary>Tabulates <paramref name="function"/> (wavelength nm → value) on a regular grid.</summary>
    public static Spectrum FromFunction(Func<float, float> function, float startNm = DefaultStartNm, float stepNm = DefaultStepNm,
        int count = DefaultCount)
    {
        ArgumentNullException.ThrowIfNull(function);
        ValidateGrid(startNm, stepNm, count);
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = function(startNm + stepNm * i);
            values[i] = float.IsFinite(v) ? v : 0f;
        }

        return new Spectrum(startNm, stepNm, values);
    }

    /// <summary>
    /// Builds a spectrum from irregular (wavelength, value) samples such as GDTF emitter measurement points: pairs with
    /// a non-finite or non-positive wavelength or a non-finite value are skipped, negative values (measurement noise)
    /// are clamped to 0, duplicate wavelengths are averaged, and the data is linearly interpolated onto a regular grid
    /// from the shortest to the longest wavelength.
    /// </summary>
    /// <param name="wavelengthsNm">Sample wavelengths, nm, in any order.</param>
    /// <param name="values">Sample values (same length as <paramref name="wavelengthsNm"/>; extra entries are ignored).</param>
    /// <param name="stepNm">Step of the resulting grid, nm (non-positive or non-finite → 1 nm).</param>
    /// <param name="spectrum">The resampled spectrum, or null.</param>
    /// <returns>False when fewer than two distinct valid wavelengths remain.</returns>
    public static bool TryFromSamples(ReadOnlySpan<float> wavelengthsNm, ReadOnlySpan<float> values, float stepNm,
        [NotNullWhen(true)] out Spectrum? spectrum)
    {
        spectrum = null;
        if (!(stepNm > 0f) || !float.IsFinite(stepNm))
            stepNm = 1f;
        int n = Math.Min(wavelengthsNm.Length, values.Length);
        var keys = new List<float>(n);
        var items = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            float w = wavelengthsNm[i], v = values[i];
            if (!float.IsFinite(w) || w <= 0f || !float.IsFinite(v))
                continue;
            keys.Add(w);
            items.Add(MathF.Max(v, 0f));
        }

        if (keys.Count < 2)
            return false;
        var k = keys.ToArray();
        var it = items.ToArray();
        Array.Sort(k, it);

        // Collapse duplicates (average).
        int m = 0;
        for (int i = 0; i < k.Length;)
        {
            int j = i;
            double acc = 0;
            while (j < k.Length && k[j] == k[i])
                acc += it[j++];
            k[m] = k[i];
            it[m] = (float)(acc / (j - i));
            m++;
            i = j;
        }

        if (m < 2)
            return false;
        float start = k[0], end = k[m - 1];
        if ((end - start) / stepNm >= MaxCount)
            stepNm = (end - start) / (MaxCount - 1);
        int count = GridCount(start, end, stepNm);
        var grid = new float[count];
        int seg = 0;
        for (int i = 0; i < count; i++)
        {
            float w = start + stepNm * i;
            while (seg < m - 2 && w > k[seg + 1])
                seg++;
            float w0 = k[seg], w1 = k[seg + 1];
            float t = Math.Clamp((w - w0) / (w1 - w0), 0f, 1f);
            grid[i] = it[seg] + (it[seg + 1] - it[seg]) * t;
        }

        spectrum = new Spectrum(start, stepNm, grid);
        return true;
    }

    /// <summary>A constant spectrum, e.g. the CIE equal-energy illuminant E (<paramref name="value"/> = 100).</summary>
    public static Spectrum Constant(float value, float startNm = DefaultStartNm, float stepNm = DefaultStepNm, int count = DefaultCount)
    {
        ValidateGrid(startNm, stepNm, count);
        var values = new float[count];
        Array.Fill(values, float.IsFinite(value) ? value : 0f);
        return new Spectrum(startNm, stepNm, values);
    }

    /// <summary>
    /// Relative spectral distribution of a Planckian (black-body) radiator by Planck's law, normalised to 1 at 560 nm.
    /// The temperature is clamped to [<see cref="BlackbodyRadiator.MinKelvin"/>, <see cref="BlackbodyRadiator.MaxKelvin"/>]
    /// (NaN → 6500 K). For absolute values use <see cref="BlackbodyRadiator.SpectralRadiance"/>.
    /// </summary>
    /// <param name="kelvin">Temperature, K.</param>
    /// <param name="startNm">First wavelength, nm.</param>
    /// <param name="stepNm">Step, nm.</param>
    /// <param name="count">Number of samples.</param>
    public static Spectrum Blackbody(float kelvin, float startNm = DefaultStartNm, float stepNm = DefaultStepNm, int count = DefaultCount)
    {
        ValidateGrid(startNm, stepNm, count);
        double t = BlackbodyRadiator.ClampKelvin(kelvin);
        double reference = BlackbodyRadiator.SpectralRadiance(560e-9, t);
        var values = new float[count];
        for (int i = 0; i < count; i++)
            values[i] = (float)(BlackbodyRadiator.SpectralRadiance((startNm + stepNm * i) * 1e-9, t) / reference);
        return new Spectrum(startNm, stepNm, values);
    }

    /// <summary>
    /// CIE daylight illuminant D(T) of correlated colour temperature <paramref name="kelvin"/> (CIE 015:2004 §3.1),
    /// 300–830 nm at 5 nm, relative (100 at 560 nm): S(λ) = S0(λ) + M1·S1(λ) + M2·S2(λ), with the chromaticity
    /// x_D(T) of the daylight locus and M1, M2 rounded to three decimals as the CIE prescribes. Valid for
    /// 4000–25000 K; the temperature is clamped to that range (NaN → 6504 K). The CCTs of the named illuminants
    /// use the 1968 radiation constant: D65 is D(6504 K), D50 is D(5003 K), D75 is D(7504 K).
    /// </summary>
    public static Spectrum Daylight(float kelvin)
    {
        double t = float.IsNaN(kelvin) ? 6504.0 : Math.Clamp(kelvin, 4000f, 25000f);
        double xd = t <= 7000.0
            ? -4.6070e9 / (t * t * t) + 2.9678e6 / (t * t) + 0.09911e3 / t + 0.244063
            : -2.0064e9 / (t * t * t) + 1.9018e6 / (t * t) + 0.24748e3 / t + 0.237040;
        double yd = -3.000 * xd * xd + 2.870 * xd - 0.275;
        double m = 0.0241 + 0.2562 * xd - 0.7341 * yd;
        double m1 = Math.Round((-1.3515 - 1.7703 * xd + 5.9114 * yd) / m, 3, MidpointRounding.AwayFromZero);
        double m2 = Math.Round((0.0300 - 31.4424 * xd + 30.0717 * yd) / m, 3, MidpointRounding.AwayFromZero);
        var values = new float[CieTables.S0.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = (float)(CieTables.S0[i] + m1 * CieTables.S1[i] + m2 * CieTables.S2[i]);
        return new Spectrum(CieTables.IlluminantStartNm, CieTables.StepNm, values);
    }

    /// <summary>
    /// The CIE 13.3 reference illuminant for a white source of correlated colour temperature <paramref name="kelvin"/>:
    /// a Planckian radiator below 5000 K, the daylight illuminant D(T) from 5000 K up; 300–830 nm at 5 nm, 100 at
    /// 560 nm. Used as the spectrum of a white source whose real spectrum is unknown (discharge lamp, white LED engine).
    /// </summary>
    public static Spectrum ReferenceIlluminant(float kelvin)
    {
        float t = float.IsNaN(kelvin) ? 6504f : Math.Clamp(kelvin, Cct.MinKelvin, 25000f);
        return t < 5000f
            ? Blackbody(t, CieTables.IlluminantStartNm, CieTables.StepNm, CieTables.S0.Length).Scale(100f)
            : Daylight(t);
    }

    /// <summary>
    /// A Gaussian band with peak 1 at <paramref name="peakNm"/> and full width at half maximum
    /// <paramref name="fwhmNm"/> — a simple model of a narrow-band LED or a band-pass filter.
    /// </summary>
    public static Spectrum Gaussian(float peakNm, float fwhmNm, float startNm = DefaultStartNm, float stepNm = 1f, int count = 471)
    {
        ValidateGrid(startNm, stepNm, count);
        double width = float.IsFinite(fwhmNm) && fwhmNm > 1e-3f ? fwhmNm : 1e-3;
        double k = 4.0 * Math.Log(2.0) / (width * width);
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            double d = startNm + stepNm * i - peakNm;
            values[i] = (float)Math.Exp(-k * d * d);
        }

        return new Spectrum(startNm, stepNm, values);
    }

    internal bool SameGrid(Spectrum other) =>
        other._values.Length == _values.Length && other.StartNm == StartNm && other.StepNm == StepNm;

    private float Interpolate(double f)
    {
        int last = _values.Length - 1;
        if (f <= 0 || last == 0)
            return _values[0];
        if (f >= last)
            return _values[last];
        int i = (int)f;
        float t = (float)(f - i);
        return _values[i] + (_values[i + 1] - _values[i]) * t;
    }

    private static int GridCount(float start, float end, float step)
    {
        double n = Math.Floor((end - (double)start) / step + 1e-6) + 1;
        if (n > MaxCount)
            throw new ArgumentOutOfRangeException(nameof(step), $"A {step} nm grid over {start}–{end} nm needs more than {MaxCount} samples.");
        return Math.Max(1, (int)n);
    }

    private static void ValidateGrid(float startNm, float stepNm, int count)
    {
        if (!float.IsFinite(startNm))
            throw new ArgumentOutOfRangeException(nameof(startNm), startNm, "Start wavelength must be finite.");
        if (!(stepNm > 0f) || !float.IsFinite(stepNm))
            throw new ArgumentOutOfRangeException(nameof(stepNm), stepNm, "Wavelength step must be positive.");
        if (count < 1 || count > MaxCount)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"A spectrum needs 1..{MaxCount} samples.");
    }

    private static float[] Sanitize(ReadOnlySpan<float> values)
    {
        var copy = values.ToArray();
        for (int i = 0; i < copy.Length; i++)
        {
            if (!float.IsFinite(copy[i]))
                copy[i] = 0f;
        }

        return copy;
    }

    private static Spectrum CreateIlluminantA()
    {
        const double c = 1.435e7, t = 2848.0;
        double norm = Math.Exp(c / (t * 560.0)) - 1.0;
        var values = new float[107];
        for (int i = 0; i < values.Length; i++)
        {
            double lambda = 300.0 + 5.0 * i;
            values[i] = (float)(100.0 * Math.Pow(560.0 / lambda, 5) * norm / (Math.Exp(c / (t * lambda)) - 1.0));
        }

        return new Spectrum(300f, 5f, values);
    }
}
