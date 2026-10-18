using System.Numerics;
using Candela.Core.Math;
using Candela.Photometry;

namespace Candela.Simulation;

/// <summary>
/// A point light standing in for one section of a flame: the renderer adds it as an isotropic light of
/// <see cref="IntensityCandela"/> in <see cref="Color"/> (chromaticity normalised to luminance 1, linear Rec.2020).
/// </summary>
/// <param name="FlameId">Device the sample belongs to.</param>
/// <param name="Position">Light-weighted centre of the section, m.</param>
/// <param name="Color">Black-body colour of the section's mean radiation temperature.</param>
/// <param name="IntensityCandela">Luminous intensity, cd (= <paramref name="LuminousFlux"/>/4π).</param>
/// <param name="LuminousFlux">Luminous flux of the section, lm.</param>
/// <param name="Temperature">Flux-weighted radiation temperature of the section, K.</param>
/// <param name="Radius">Radius of the section's flame, m (size of the light source, for soft shadows).</param>
public readonly record struct FlameLightSample(Guid FlameId, Vector3 Position, LightColor Color, float IntensityCandela,
    float LuminousFlux, float Temperature, float Radius);

/// <summary>Diagnostics of the last <see cref="FireSimulation.Step"/> call.</summary>
/// <param name="SubSteps">Fixed sub-steps run.</param>
/// <param name="ActiveFlames">Devices with burning fuel or an open valve.</param>
/// <param name="Particles">Burning fuel parcels.</param>
/// <param name="IgnoredEmitters">Descriptors skipped (non-finite position, duplicate id).</param>
/// <param name="DroppedTime">Time discarded because the sub-step cap was reached, s.</param>
public readonly record struct FireStepStatistics(int SubSteps, int ActiveFlames, int Particles, int IgnoredEmitters,
    double DroppedTime);

/// <summary>
/// The temperature field of one flame on a small axis-aligned grid around it (x fastest, then y, then z — the layout of
/// the renderer's <c>VolumeGrid</c>): soot radiation temperature in K where the flame is, fading to 0 (= no flame) at
/// its edge. Owned and reused by the <see cref="FireSimulation"/>; valid until its next step.
/// </summary>
public sealed class FlameVolume
{
    private float[] _temperature = Array.Empty<float>();

    internal FlameVolume(Guid flameId) => FlameId = flameId;

    /// <summary>Device the volume belongs to.</summary>
    public Guid FlameId { get; }

    /// <summary>World-space box covered by the grid, m.</summary>
    public Aabb Bounds { get; private set; }

    /// <summary>Cells along X.</summary>
    public int SizeX { get; private set; }

    /// <summary>Cells along Y.</summary>
    public int SizeY { get; private set; }

    /// <summary>Cells along Z.</summary>
    public int SizeZ { get; private set; }

    /// <summary>Cell edge length, m.</summary>
    public float CellSize { get; private set; }

    /// <summary>Temperature per cell, K (0 = no flame).</summary>
    public ReadOnlySpan<float> Temperature => _temperature.AsSpan(0, SizeX * SizeY * SizeZ);

    /// <summary>Highest temperature in the volume, K.</summary>
    public float PeakTemperature { get; private set; }

    /// <summary>
    /// Volumetric absorption (= emission) coefficient κ, 1/m, that makes the optically thin black-body emission of this
    /// volume, Σ 4π·κ·L_bb(T)·V over the cells, equal the flame's luminous flux — so a volumetric renderer that emits
    /// κ·L_bb(T) per metre agrees with the point-light samples.
    /// </summary>
    public float EmissionCoefficient { get; private set; }

    /// <summary>Luminous flux of the flame, lm.</summary>
    public float LuminousFlux { get; private set; }

    /// <summary>Temperature at a world point by trilinear interpolation, K (0 outside the volume).</summary>
    public float TemperatureAt(Vector3 point)
    {
        if (SizeX == 0 || !Bounds.Contains(point))
            return 0f;
        var g = (point - Bounds.Min) / CellSize - new Vector3(0.5f);
        float x = Math.Clamp(g.X, 0f, SizeX - 1), y = Math.Clamp(g.Y, 0f, SizeY - 1), z = Math.Clamp(g.Z, 0f, SizeZ - 1);
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
            return 0f;
        int i0 = (int)x, j0 = (int)y, k0 = (int)z;
        int i1 = Math.Min(i0 + 1, SizeX - 1), j1 = Math.Min(j0 + 1, SizeY - 1), k1 = Math.Min(k0 + 1, SizeZ - 1);
        float fx = x - i0, fy = y - j0, fz = z - k0;
        float T(int i, int j, int k) => _temperature[i + SizeX * (j + SizeY * k)];
        float c00 = T(i0, j0, k0) + (T(i1, j0, k0) - T(i0, j0, k0)) * fx;
        float c10 = T(i0, j1, k0) + (T(i1, j1, k0) - T(i0, j1, k0)) * fx;
        float c01 = T(i0, j0, k1) + (T(i1, j0, k1) - T(i0, j0, k1)) * fx;
        float c11 = T(i0, j1, k1) + (T(i1, j1, k1) - T(i0, j1, k1)) * fx;
        float c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
        return c0 + (c1 - c0) * fz;
    }

    /// <summary>Resizes the grid (reusing the buffer when it is big enough) and clears it.</summary>
    internal Span<float> Prepare(Aabb bounds, int sx, int sy, int sz, float cellSize, Span<float> weights)
    {
        int count = sx * sy * sz;
        if (_temperature.Length < count)
            _temperature = new float[Math.Max(count, _temperature.Length * 2)];
        Bounds = bounds;
        (SizeX, SizeY, SizeZ, CellSize) = (sx, sy, sz, cellSize);
        _temperature.AsSpan(0, count).Clear();
        weights[..count].Clear();
        return _temperature.AsSpan(0, count);
    }

    internal void Finish(float peak, float emissionCoefficient, float flux)
    {
        PeakTemperature = peak;
        EmissionCoefficient = emissionCoefficient;
        LuminousFlux = flux;
    }

    internal void Clear()
    {
        SizeX = SizeY = SizeZ = 0;
        Bounds = default;
        PeakTemperature = EmissionCoefficient = LuminousFlux = 0f;
    }

    internal FlameVolume Clone()
    {
        var copy = new FlameVolume(FlameId)
        {
            Bounds = Bounds,
            SizeX = SizeX,
            SizeY = SizeY,
            SizeZ = SizeZ,
            CellSize = CellSize,
            PeakTemperature = PeakTemperature,
            EmissionCoefficient = EmissionCoefficient,
            LuminousFlux = LuminousFlux,
        };
        copy._temperature = (float[])_temperature.Clone();
        return copy;
    }

    internal void CopyFrom(FlameVolume other)
    {
        if (_temperature.Length < other._temperature.Length)
            _temperature = new float[other._temperature.Length];
        other._temperature.AsSpan().CopyTo(_temperature);
        Bounds = other.Bounds;
        (SizeX, SizeY, SizeZ, CellSize) = (other.SizeX, other.SizeY, other.SizeZ, other.CellSize);
        PeakTemperature = other.PeakTemperature;
        EmissionCoefficient = other.EmissionCoefficient;
        LuminousFlux = other.LuminousFlux;
    }
}
