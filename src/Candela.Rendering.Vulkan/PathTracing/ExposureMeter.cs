using Candela.Core.Math;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// The auto-exposure metering of the real-time renderer (exposure.comp) on the CPU, for final renders: the mean log2
/// luminance of the pixels between two percentiles of a log2-luminance histogram and the exposure it gives.
/// </summary>
internal static class ExposureMeter
{
    /// <summary>
    /// Mean log2 luminance (cd/m²) of the histogram's pixels between the 85th and 99th percentile
    /// (<see cref="ExposureMetering.Stage"/>) or the 50th and 95th (<see cref="ExposureMetering.Average"/>); the bins
    /// span <see cref="ShaderConstants.HistogramMinLog2"/> … <see cref="ShaderConstants.HistogramMaxLog2"/>. Null for an
    /// empty histogram.
    /// </summary>
    public static float? AverageLog2Luminance(ReadOnlySpan<uint> histogram, ExposureMetering metering)
    {
        double total = 0;
        foreach (uint n in histogram)
            total += n;
        if (total <= 0 || histogram.Length == 0)
            return null;
        bool stage = metering == ExposureMetering.Stage;
        double low = total * (stage ? 0.85 : 0.5);
        double high = total * (stage ? 0.99 : 0.95);
        double binWidth = (ShaderConstants.HistogramMaxLog2 - ShaderConstants.HistogramMinLog2) / histogram.Length;
        double below = 0, weight = 0, sum = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            double n = histogram[i];
            double inside = Math.Max(Math.Min(below + n, high) - Math.Max(below, low), 0);
            below += n;
            sum += inside * (ShaderConstants.HistogramMinLog2 + (i + 0.5) * binWidth);
            weight += inside;
        }

        return weight > 0 ? (float)(sum / weight) : ShaderConstants.HistogramMinLog2;
    }

    /// <summary>
    /// The exposure (HDR value per cd/m²) for a metered mean log2 luminance: EV100 of that luminance, plus
    /// <see cref="VulkanRenderer.StageMeteringStops"/> with stage metering, minus the compensation in stops.
    /// </summary>
    public static float ExposureFor(float averageLog2Luminance, ExposureMetering metering, float compensation)
    {
        float metered = metering == ExposureMetering.Stage ? VulkanRenderer.StageMeteringStops : 0f;
        float ev = Units.Ev100FromLuminance(MathF.Pow(2f, averageLog2Luminance)) + metered - (float.IsFinite(compensation) ? compensation : 0f);
        return Units.ExposureFromEv100(ev);
    }
}
