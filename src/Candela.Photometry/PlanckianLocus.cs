namespace Candela.Photometry;

/// <summary>
/// The Planckian locus in the CIE 1960 UCS (u, v) diagram, computed by integrating Planck's law against the
/// CIE 1931 colour-matching functions (5 nm summation over 360–830 nm, c₂ = h·c/k), plus a cache of the locus on a
/// logarithmic temperature grid with 0.25 % steps covering <see cref="Cct.MinKelvin"/>–<see cref="Cct.MaxKelvin"/>
/// with two extra nodes on each side (≈ 1300 nodes, built once in about a millisecond).
/// </summary>
internal static class PlanckianLocus
{
    /// <summary>Ratio between neighbouring table temperatures (0.25 % steps).</summary>
    internal const double TableRatio = 1.0025;

    // Per-wavelength constants of Planck's law on the CMF grid, wavelength in micrometres (relative units suffice
    // for chromaticity): λ⁻⁵ and c₂/λ (c₂ in µm·K).
    private static readonly double[] _lambdaPowMinus5 = new double[Cie1931.Count];
    private static readonly double[] _c2OverLambda = new double[Cie1931.Count];

    private static readonly double _logT0;
    private static readonly double _logRatio = Math.Log(TableRatio);
    private static readonly double[] _t;
    private static readonly double[] _u;
    private static readonly double[] _v;

    static PlanckianLocus()
    {
        double c2Micrometres = PhysicalConstants.SecondRadiationConstant * 1e6;
        for (int i = 0; i < Cie1931.Count; i++)
        {
            double lambda = Cie1931.WavelengthAt(i) * 1e-3;
            _lambdaPowMinus5[i] = 1.0 / (lambda * lambda * lambda * lambda * lambda);
            _c2OverLambda[i] = c2Micrometres / lambda;
        }

        double t0 = Cct.MinKelvin / (TableRatio * TableRatio);
        double t1 = Cct.MaxKelvin * TableRatio * TableRatio;
        int n = (int)Math.Ceiling(Math.Log(t1 / t0) / _logRatio) + 1;
        _logT0 = Math.Log(t0);
        _t = new double[n];
        _u = new double[n];
        _v = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = Math.Exp(_logT0 + i * _logRatio);
            ComputeUv(t, out double u, out double v);
            _t[i] = t;
            _u[i] = u;
            _v[i] = v;
        }
    }

    /// <summary>Number of table nodes.</summary>
    internal static int TableLength => _t.Length;

    /// <summary>Table node <paramref name="index"/>: temperature (K) and CIE 1960 (u, v).</summary>
    internal static void Node(int index, out double kelvin, out double u, out double v)
    {
        kelvin = _t[index];
        u = _u[index];
        v = _v[index];
    }

    /// <summary>Relative tristimulus values of a black body at <paramref name="kelvin"/> (any temperature &gt; 0).</summary>
    internal static void ComputeXyz(double kelvin, out double x, out double y, out double z)
    {
        x = y = z = 0;
        double invT = 1.0 / kelvin;
        for (int i = 0; i < Cie1931.Count; i++)
        {
            double e = _c2OverLambda[i] * invT;
            if (e > 700.0)
                continue;
            double m = _lambdaPowMinus5[i] / double.ExpM1(e);
            x += m * CieTables.XBar[i];
            y += m * CieTables.YBar[i];
            z += m * CieTables.ZBar[i];
        }
    }

    /// <summary>Exact CIE 1960 (u, v) of a black body at <paramref name="kelvin"/>.</summary>
    internal static void ComputeUv(double kelvin, out double u, out double v)
    {
        ComputeXyz(kelvin, out double x, out double y, out double z);
        double d = x + 15.0 * y + 3.0 * z;
        u = 4.0 * x / d;
        v = 6.0 * y / d;
    }

    /// <summary>Exact (u, v) and its derivative with respect to temperature (per kelvin).</summary>
    internal static void ComputeUvWithDerivative(double kelvin, out double u, out double v, out double du, out double dv)
    {
        double x = 0, y = 0, z = 0, dx = 0, dy = 0, dz = 0;
        double invT = 1.0 / kelvin;
        for (int i = 0; i < Cie1931.Count; i++)
        {
            double e = _c2OverLambda[i] * invT;
            if (e > 700.0)
                continue;
            double em1 = double.ExpM1(e);
            double m = _lambdaPowMinus5[i] / em1;

            // ∂/∂T [λ⁻⁵/(eˣ−1)] with x = c₂/(λT): λ⁻⁵·x·eˣ / (T·(eˣ−1)²)
            double dm = m * e * (em1 + 1.0) / (em1 * kelvin);
            x += m * CieTables.XBar[i];
            y += m * CieTables.YBar[i];
            z += m * CieTables.ZBar[i];
            dx += dm * CieTables.XBar[i];
            dy += dm * CieTables.YBar[i];
            dz += dm * CieTables.ZBar[i];
        }

        double d = x + 15.0 * y + 3.0 * z;
        double dd = dx + 15.0 * dy + 3.0 * dz;
        u = 4.0 * x / d;
        v = 6.0 * y / d;
        du = 4.0 * (dx * d - x * dd) / (d * d);
        dv = 6.0 * (dy * d - y * dd) / (d * d);
    }

    /// <summary>
    /// (u, v) at <paramref name="kelvin"/> from the cached table (linear interpolation in log T; error &lt; 10⁻⁷), or
    /// by direct integration outside the table range.
    /// </summary>
    internal static void InterpolateUv(double kelvin, out double u, out double v)
    {
        double f = (Math.Log(kelvin) - _logT0) / _logRatio;
        if (!(f >= 0) || f > _t.Length - 1)
        {
            ComputeUv(kelvin, out u, out v);
            return;
        }

        int i = Math.Min((int)f, _t.Length - 2);
        double s = f - i;
        u = _u[i] + (_u[i + 1] - _u[i]) * s;
        v = _v[i] + (_v[i + 1] - _v[i]) * s;
    }
}
