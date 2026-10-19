using System.Numerics;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Rendering.Vulkan.Tests;

public sealed class BeamTwinProbe
{
    private readonly ITestOutputHelper _output;

    public BeamTwinProbe(ITestOutputHelper output) => _output = output;

    private static double Shape(in GpuLight l, double theta)
    {
        if (theta >= l.Cutoff)
            return 0;
        double r = theta * l.InvHalfBeam;
        double g = r > 0 ? Math.Exp(-Math.Log(2) * Math.Pow(r, l.Exponent)) : 1;
        if (theta > l.HalfField)
        {
            double s = (theta - l.HalfField) * l.InvWindow;
            g *= 1 - s * s * (3 - 2 * s);
        }

        return g;
    }

    [Fact]
    public void FarFieldDisc()
    {
        var light = StageScene.Spot(new Vector3(0, 0, 4), Vector3.Zero, 100000f, 6f, 9f, shadows: false, type: BeamType.Spot);
        light.SourceRadius = 0;
        LightPacker.Pack(light, 0, out var l);
        _output.WriteLine($"layer {l.BeamLayer} exp {l.Exponent} cutoff {l.Cutoff} halfField {l.HalfField} bounding {l.BoundingAngle} gate {l.GateScale}");
        double thetaB = 1.0 / l.InvHalfBeam;
        double W = l.Cutoff * l.InvHalfBeam;
        double scale = 2 * Math.Sin(0.5 * thetaB) * W;
        double profileScale = thetaB * W;
        double v = 0.025;
        // Ray at D = 2 through the axis: R = 1, yScale = 1/scale; y in [-1, 1].
        double yScale = 1 / scale;
        foreach (double radius in new[] { 0.25, 0.45, 0.7, 2.0 })
        {
            double gateScale = l.GateScale;
            bool Open(double y)
            {
                double u = 2 * Math.Asin(Math.Clamp(y / (2 * yScale), -1, 1));
                double p = Math.Tan(u) * gateScale;
                return Math.Abs(p) < radius;
            }

            // Truth: integral over y of f(true angle) * open / cos(u/2) dy.
            double truth = 0;
            int n = 200000;
            for (int i = 0; i < n; i++)
            {
                double y = -1 + (i + 0.5) * 2.0 / n;
                double u = 2 * Math.Asin(Math.Clamp(y / (2 * yScale), -1, 1));
                double f = Shape(l, Math.Abs(u));
                truth += f * (Open(y) ? 1 : 0) / Math.Cos(0.5 * u) * 2.0 / n;
            }

            // Twin: K = 2 segments [-1, 0], [0, 1]; N samples per segment, inverted from the table.
            int K = 2, N = 14, trials = 4000;
            var rng = new Random(1);
            double est = 0, tableTotal = 0;
            long bad = 0, total = 0;
            for (int trial = 0; trial < trials; trial++)
            {
                double acc = 0;
                double dy = 2.0 / K;
                for (int seg = 0; seg < K; seg++)
                {
                    double y0 = -1 + seg * dy, y1 = y0 + dy;
                    double h0 = BeamScatteringTable.Lookup(l.BeamLayer, v, y0), h1 = BeamScatteringTable.Lookup(l.BeamLayer, v, y1);
                    double dH = h1 - h0;
                    double f0 = Shape(l, profileScale * Math.Abs(y0)), f1 = Shape(l, profileScale * Math.Abs(y1)),
                        fm = Shape(l, profileScale * Math.Abs(0.5 * (y0 + y1)));
                    double m0 = 1 / Math.Cos(Math.Asin(Math.Clamp(y0 / (2 * yScale), -1, 1)));
                    double m1 = 1 / Math.Cos(Math.Asin(Math.Clamp(y1 / (2 * yScale), -1, 1)));
                    double offset = rng.NextDouble();
                    double sum = 0;
                    for (int s = 0; s < N; s++)
                    {
                        double xi = (offset + (double)s / N) % 1.0;
                        double yj = Invert(l, v, profileScale, y0, y1, h0, h1, f0, fm, f1, h0 + xi * dH);
                        if (Math.Abs(BeamScatteringTable.Lookup(l.BeamLayer, v, yj) - (h0 + xi * dH)) > 0.01 * dH)
                            bad++;
                        total++;
                        double uj = 2 * Math.Asin(Math.Clamp(yj / (2 * yScale), -1, 1));
                        double exact = Shape(l, Math.Abs(uj));
                        double approx = Shape(l, profileScale * Math.Abs(yj));
                        double ratio = approx > 1e-4 ? Math.Clamp(exact / approx, 0, 4) : 1;
                        double mj = m0 + (m1 - m0) * Math.Clamp((yj - y0) / dy, 0, 1);
                        sum += (Open(yj) ? 1 : 0) * ratio * mj;
                    }

                    acc += dH * sum / N;
                    if (trial == 0)
                        tableTotal += dH;
                }

                est += acc / trials;
            }

            _output.WriteLine($"radius {radius}: truth {truth:0.000000} twin {est:0.000000} error {(est - truth) / truth:P2} (table total {tableTotal:0.0000}) bad {bad / (double)total:P2}");
        }
    }

    private static double Invert(in GpuLight l, double v, double profileScale, double y0, double y1, double h0, double h1, double f0, double fm,
        double f1, double target)
    {
        double dy = y1 - y0;
        double q = h1 > h0 ? Math.Clamp((target - h0) / (h1 - h0), 0, 1) : 0.5;
        double left = 0.5 * (f0 + fm), right = 0.5 * (fm + f1);
        double y = y0 + q * dy;
        if (left + right > 1e-6)
        {
            double mass = q * (left + right);
            bool first = mass <= left;
            double fa = first ? f0 : fm, fb = first ? fm : f1, c = first ? mass : mass - left;
            double a2 = 0.5 * (fb - fa);
            double root = Math.Sqrt(Math.Max(fa * fa + 4 * a2 * c, 0));
            double s = fa + root > 1e-9 ? 2 * c / (fa + root) : 0.5;
            y = (first ? y0 : y0 + 0.5 * dy) + 0.5 * dy * Math.Clamp(s, 0, 1);
        }

        double lo = y0, hi = y1;
        for (int it = 0; it < 3; it++)
        {
            double err = BeamScatteringTable.Lookup(l.BeamLayer, v, y) - target;
            if (err > 0)
                hi = y;
            else
                lo = y;
            double f = Shape(l, profileScale * Math.Sqrt(v * v + y * y));
            double next = f > 1e-6 ? y - err / f : 0.5 * (lo + hi);
            y = next > lo && next < hi ? next : 0.5 * (lo + hi);
        }

        return y;
    }
}

public sealed class BeamTwinProbe2
{
    private readonly ITestOutputHelper _output;

    public BeamTwinProbe2(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TableRatio()
    {
        var light = StageScene.Spot(new Vector3(0, 0, 4), Vector3.Zero, 100000f, 6f, 9f, shadows: false, type: BeamType.Spot);
        LightPacker.Pack(light, 0, out var l);
        _output.WriteLine($"cpu ratio {BeamScatteringTable.Lookup(l.BeamLayer, 0, 0.2) / BeamScatteringTable.Lookup(l.BeamLayer, 0, 1.0):0.0000}");
        _output.WriteLine($"cpu ratio 0.7 {BeamScatteringTable.Lookup(l.BeamLayer, 0, 0.7) / BeamScatteringTable.Lookup(l.BeamLayer, 0, 1.0):0.0000}");
        _output.WriteLine($"exact ratio {BeamScatteringTable.Integral(0, l.Exponent, 0, 0.2) / BeamScatteringTable.Integral(0, l.Exponent, 0, 1.0):0.0000}");
    }
}

public sealed class BeamTwinIris
{
    private readonly ITestOutputHelper _output;

    public BeamTwinIris(ITestOutputHelper output) => _output = output;

    private static double Shape(in GpuLight l, double theta)
    {
        if (theta >= l.Cutoff)
            return 0;
        double r = theta * l.InvHalfBeam;
        double g = r > 0 ? Math.Exp(-Math.Log(2) * Math.Pow(r, l.Exponent)) : 1;
        if (theta > l.HalfField)
        {
            double s = (theta - l.HalfField) * l.InvWindow;
            g *= 1 - s * s * (3 - 2 * s);
        }

        return g;
    }

    private static double Iris(in GpuLight l, double theta)
    {
        if (theta <= l.IrisSoftStart)
            return 1;
        double x = Math.Clamp((theta - l.IrisSoftStart) / (l.IrisAngle - l.IrisSoftStart), 0, 1);
        return 1 - x * x * (3 - 2 * x);
    }

    [Fact]
    public void Band()
    {
        var light = StageScene.Spot(new Vector3(0, 0, 4), Vector3.Zero, 30000f, 20f, 30f, shadows: false, type: BeamType.Spot);
        light.Iris = 0.5f;
        LightPacker.Pack(light, 0, out var l);
        _output.WriteLine($"iris {l.IrisAngle} soft {l.IrisSoftStart} cutoff {l.Cutoff} bounding {l.BoundingAngle}");
        double thetaB = 1.0 / l.InvHalfBeam, W = l.Cutoff * l.InvHalfBeam;
        double scale = 2 * Math.Sin(0.5 * thetaB) * W, profileScale = thetaB * W;
        foreach (double x in new[] { 0.0, 0.2, 0.235, 0.25, 0.26 })
        {
            double thetaM = Math.Atan2(x, 2);
            // Ray perpendicular to the axis at distance 2, offset x: R = cos(thetaM), chord of the closest point.
            double R = Math.Cos(thetaM);
            double v = 2 * Math.Sin(0.5 * thetaM) / scale;
            double yScale = Math.Sqrt(R) / scale;
            double outer = Math.Min(1, 2 * Math.Sin(0.5 * l.IrisAngle) / scale);
            double inner = Math.Min(outer, 2 * Math.Sin(0.5 * l.IrisSoftStart) / scale);
            if (v >= outer)
            {
                _output.WriteLine($"x {x}: outside");
                continue;
            }

            double yOuter = Math.Sqrt(outer * outer - v * v);
            double yInner = v < inner ? Math.Sqrt(inner * inner - v * v) : 0;
            double Angle(double y) => 2 * Math.Asin(Math.Min(0.5 * scale * Math.Sqrt(v * v + y * y), 1));
            double M(double y) => 1 / Math.Cos(Math.Asin(Math.Clamp(y / (2 * yScale), -1, 1)));
            double F(double y) => Shape(l, profileScale * Math.Sqrt(v * v + y * y));
            double truth = 0;
            int n = 100000;
            for (int i = 0; i < n; i++)
            {
                double y = -yOuter + (i + 0.5) * 2 * yOuter / n;
                double u = 2 * Math.Asin(Math.Clamp(y / (2 * yScale), -1, 1));
                double theta = Math.Acos(Math.Cos(thetaM) * Math.Cos(u));
                truth += Shape(l, theta) * Iris(l, theta) * M(y) * 2 * yOuter / n;
            }

            double est = 0;
            int K = 4;
            for (int part = 0; part < 3; part++)
            {
                double lo = part == 0 ? -yOuter : Math.Max(-yOuter, part == 1 ? -yInner : yInner);
                double hi = part == 0 ? Math.Min(yOuter, -yInner) : part == 1 ? Math.Min(yOuter, yInner) : yOuter;
                if (!(hi > lo))
                    continue;
                bool band = part != 1;
                int segs = band ? 2 : K;
                double dy = (hi - lo) / segs;
                for (int i = 0; i < segs; i++)
                {
                    double y0 = lo + i * dy, y1 = y0 + dy, ym = y0 + 0.5 * dy;
                    double dH = BeamScatteringTable.Lookup(l.BeamLayer, v, y1) - BeamScatteringTable.Lookup(l.BeamLayer, v, y0);
                    double f0 = F(y0), fm = F(ym), f1 = F(y1);
                    double i0 = band ? Iris(l, Angle(y0)) : 1, im = band ? Iris(l, Angle(ym)) : 1, i1 = band ? Iris(l, Angle(y1)) : 1;
                    double m0 = M(y0), m1 = M(y1);
                    double simpson = f0 + 4 * fm + f1;
                    double average = simpson > 1e-6 ? (f0 * i0 * m0 + 2 * fm * im * (m0 + m1) + f1 * i1 * m1) / simpson : 0.25 * (i0 * m0 + i1 * m1) + 0.25 * im * (m0 + m1);
                    est += dH * average;
                }
            }

            _output.WriteLine($"x {x}: v {v:0.0000} inner {inner:0.000} outer {outer:0.000} truth {truth:0.00000} est {est:0.00000} error {(est - truth) / truth:P2}");
        }
    }
}
