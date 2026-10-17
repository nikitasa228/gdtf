namespace Candela.Photometry;

/// <summary>
/// Gauss–Legendre quadrature nodes and weights on [-1, 1], computed once at start-up by Newton iteration on the
/// Legendre polynomial (accurate to double precision). Used for the beam solid-angle integrals, which must be fast
/// (no allocations, a few dozen function evaluations) and accurate (better than 10⁻⁶ relative for smooth profiles).
/// </summary>
internal static class GaussLegendre
{
    /// <summary>Number of nodes per integration segment.</summary>
    internal const int Order = 24;

    private static readonly (double[] Nodes, double[] Weights) _rule = BuildRule(Order);

    /// <summary>∫ f(x) dx over [a, b] with <see cref="Order"/> nodes. <paramref name="state"/> avoids closures.</summary>
    internal static double Integrate<TState>(double a, double b, in TState state, Func<TState, double, double> f)
    {
        if (!(b > a))
            return 0.0;
        var (nodes, weights) = _rule;
        double half = 0.5 * (b - a), mid = 0.5 * (a + b), sum = 0.0;
        for (int i = 0; i < nodes.Length; i++)
            sum += weights[i] * f(state, mid + half * nodes[i]);
        return sum * half;
    }

    private static (double[] Nodes, double[] Weights) BuildRule(int n)
    {
        var nodes = new double[n];
        var weights = new double[n];
        for (int i = 0; i < (n + 1) / 2; i++)
        {
            // Tricomi initial guess for the i-th root, then Newton iterations on P_n.
            double x = System.Math.Cos(System.Math.PI * (i + 0.75) / (n + 0.5));
            double derivative = 1.0;
            for (int iteration = 0; iteration < 100; iteration++)
            {
                double p0 = 1.0, p1 = x;
                for (int k = 2; k <= n; k++)
                {
                    double p2 = ((2 * k - 1) * x * p1 - (k - 1) * p0) / k;
                    p0 = p1;
                    p1 = p2;
                }

                // p1 = P_n(x), p0 = P_(n-1)(x)
                derivative = n * (x * p1 - p0) / (x * x - 1.0);
                double step = p1 / derivative;
                x -= step;
                if (System.Math.Abs(step) < 1e-16)
                    break;
            }

            double w = 2.0 / ((1.0 - x * x) * derivative * derivative);
            nodes[i] = -x;
            nodes[n - 1 - i] = x;
            weights[i] = w;
            weights[n - 1 - i] = w;
        }

        return (nodes, weights);
    }
}
