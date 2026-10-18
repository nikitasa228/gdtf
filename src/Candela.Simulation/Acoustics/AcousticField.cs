using System.Numerics;
using Candela.Core.Math;
using Candela.Photometry;

namespace Candela.Simulation;

/// <summary>Options of an <see cref="AcousticField"/>.</summary>
public sealed record AcousticFieldOptions
{
    /// <summary>Add one specular reflection from the floor plane (an image source below it).</summary>
    public bool FloorReflection { get; init; }

    /// <summary>Height of the floor plane, m (world Z).</summary>
    public float FloorHeight { get; init; }

    /// <summary>
    /// Energy absorption coefficient α of the floor, 0..1: the image source radiates (1 − α) of the source's power. A
    /// hard stage floor (wood on joists, concrete, vinyl) absorbs 0.05–0.15 across the A-weighted band; 0.1 is the
    /// default.
    /// </summary>
    public float FloorAbsorption { get; init; } = 0.1f;

    /// <summary>Levels below this are written as this value in grids and surface samples, dB(A) (silence becomes it too).</summary>
    public float MinimumLevel { get; init; }

    /// <summary>Threads for grid evaluation (≤ 0 = all cores). Results do not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;
}

/// <summary>
/// A sound-pressure-level grid: dB(A) at the cell centres of a box (x fastest, then y, then z — the layout of the
/// renderer's <c>VolumeGrid</c>; the engine wraps <see cref="Values"/> for the noise overlay's volumetric fog).
/// </summary>
public sealed class AcousticGrid
{
    internal AcousticGrid(Aabb bounds, int sizeX, int sizeY, int sizeZ)
    {
        Bounds = bounds;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        Values = new float[sizeX * sizeY * sizeZ];
    }

    /// <summary>World-space box, m.</summary>
    public Aabb Bounds { get; }

    /// <summary>Cells along X.</summary>
    public int SizeX { get; }

    /// <summary>Cells along Y.</summary>
    public int SizeY { get; }

    /// <summary>Cells along Z.</summary>
    public int SizeZ { get; }

    /// <summary>SPL per cell centre, dB(A).</summary>
    public float[] Values { get; }

    /// <summary>World position of the centre of cell (i, j, k), m.</summary>
    public Vector3 CellCentre(int i, int j, int k)
    {
        var size = Bounds.Size;
        return Bounds.Min + new Vector3((i + 0.5f) * size.X / SizeX, (j + 0.5f) * size.Y / SizeY, (k + 0.5f) * size.Z / SizeZ);
    }
}

/// <summary>
/// Sound pressure level of the noise sources in a scene (fixture fans, hazers, fog machines — dB(A) at 1 m from the
/// <c>_specs</c> <c>noiseLevelDb</c>) for the noise overlay: at points, on surfaces and on volume grids. The free field
/// follows <see cref="Acoustics"/> — spherical spreading L = L₁ − 20·log₁₀ d with d clamped to
/// <see cref="Acoustics.MinDistance"/>, sources added energetically — optionally with one floor reflection as an
/// image source mirrored below the floor plane radiating (1 − α) of the power, also added energetically (broadband
/// noise from independent sources: no interference). No directivity, air absorption, screening or room reverberation.
/// </summary>
/// <remarks>
/// Levels are summed as intensities relative to the loudest source, Σ 10^((Lᵢ − L_max)/10)/dᵢ², so any range of input
/// levels is safe and each source costs a few multiplications per receiver. Queries may run concurrently with each
/// other but not with <see cref="SetSources"/>; grid evaluation runs over Z slices in parallel and is deterministic.
/// </remarks>
public sealed class AcousticField
{
    private readonly AcousticFieldOptions _options;
    private readonly ParallelOptions _parallel;
    private Vector3[] _positions = Array.Empty<Vector3>();
    private double[] _power = Array.Empty<double>();
    private int _directCount;
    private int _count;
    private float _reference = float.NegativeInfinity;

    /// <summary>Creates a field with no sources.</summary>
    public AcousticField(AcousticFieldOptions? options = null)
    {
        var o = options ?? new AcousticFieldOptions();
        _options = o with
        {
            FloorHeight = float.IsFinite(o.FloorHeight) ? o.FloorHeight : 0f,
            FloorAbsorption = float.IsNaN(o.FloorAbsorption) ? 0.1f : Math.Clamp(o.FloorAbsorption, 0f, 1f),
            MinimumLevel = float.IsFinite(o.MinimumLevel) ? o.MinimumLevel : 0f,
        };
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism > 0 ? _options.MaxDegreeOfParallelism : -1 };
    }

    /// <summary>The options in use (NaN values replaced).</summary>
    public AcousticFieldOptions Options => _options;

    /// <summary>Sources in use (valid ones from the last <see cref="SetSources"/>).</summary>
    public int SourceCount => _directCount;

    /// <summary>Sources skipped by the last <see cref="SetSources"/> (non-finite position or level).</summary>
    public int IgnoredSources { get; private set; }

    /// <summary>
    /// Replaces the noise sources. Sources with a non-finite position or level are skipped (and counted in
    /// <see cref="IgnoredSources"/>); a 0 dB source (fanless fixture) is kept but contributes nothing audible.
    /// </summary>
    public void SetSources(ReadOnlySpan<SoundSource> sources)
    {
        int capacity = sources.Length * (_options.FloorReflection ? 2 : 1);
        if (_positions.Length < capacity)
        {
            _positions = new Vector3[capacity];
            _power = new double[capacity];
        }

        float reference = float.NegativeInfinity;
        int ignored = 0;
        foreach (var s in sources)
        {
            if (Valid(s))
                reference = MathF.Max(reference, s.LevelAt1MetreDb);
            else
                ignored++;
        }

        int n = 0;
        foreach (var s in sources)
        {
            if (!Valid(s))
                continue;
            _positions[n] = s.Position;
            _power[n] = Math.Pow(10.0, (s.LevelAt1MetreDb - reference) / 10.0);
            n++;
        }

        _directCount = n;
        if (_options.FloorReflection && _options.FloorAbsorption < 1f)
        {
            float floor = _options.FloorHeight;
            double reflection = 1.0 - _options.FloorAbsorption;
            for (int m = 0; m < _directCount; m++)
            {
                var p = _positions[m];
                if (p.Z < floor)
                    continue; // a source under the floor has no image above it
                _positions[n] = new Vector3(p.X, p.Y, 2f * floor - p.Z);
                _power[n] = _power[m] * reflection;
                n++;
            }
        }

        _count = n;
        _reference = reference;
        IgnoredSources = ignored;

        static bool Valid(in SoundSource s) => float.IsFinite(s.LevelAt1MetreDb)
            && float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z);
    }

    /// <summary>
    /// Sound pressure level at <paramref name="point"/>, dB(A); <see cref="Acoustics.Silence"/> (−∞) without sources.
    /// With the floor reflection on, receivers below the floor hear only the direct sound.
    /// </summary>
    public float LevelAt(Vector3 point)
    {
        if (_directCount == 0 || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
            return Acoustics.Silence;
        double sum = Intensity(point);
        return sum > 0 ? (float)(_reference + 10.0 * Math.Log10(sum)) : Acoustics.Silence;
    }

    /// <summary>
    /// SPL at each of <paramref name="points"/> (e.g. the vertices or texels of a wall) into <paramref name="levels"/>,
    /// dB(A), clamped below at <see cref="AcousticFieldOptions.MinimumLevel"/>.
    /// </summary>
    public void SampleSurface(ReadOnlySpan<Vector3> points, Span<float> levels)
    {
        if (levels.Length < points.Length)
            throw new ArgumentException($"Levels hold {levels.Length} values, {points.Length} needed.", nameof(levels));
        for (int n = 0; n < points.Length; n++)
            levels[n] = Clamp(LevelAt(points[n]));
    }

    /// <summary>
    /// SPL on a rectangle (wall, floor, poster): <paramref name="countU"/> × <paramref name="countV"/> samples at the cell
    /// centres of the parallelogram <paramref name="origin"/> + u·<paramref name="edgeU"/> + v·<paramref name="edgeV"/>
    /// (u, v in 0..1), u fastest, dB(A) clamped below at <see cref="AcousticFieldOptions.MinimumLevel"/>.
    /// </summary>
    public void SampleRectangle(Vector3 origin, Vector3 edgeU, Vector3 edgeV, int countU, int countV, Span<float> levels)
    {
        if (countU <= 0 || countV <= 0)
            throw new ArgumentOutOfRangeException(nameof(countU), "Sample counts must be positive.");
        if (levels.Length < countU * countV)
            throw new ArgumentException($"Levels hold {levels.Length} values, {countU * countV} needed.", nameof(levels));
        for (int v = 0; v < countV; v++)
        {
            for (int u = 0; u < countU; u++)
            {
                var p = origin + edgeU * ((u + 0.5f) / countU) + edgeV * ((v + 0.5f) / countV);
                levels[u + countU * v] = Clamp(LevelAt(p));
            }
        }
    }

    /// <summary>
    /// SPL at the cell centres of a <paramref name="sizeX"/> × <paramref name="sizeY"/> × <paramref name="sizeZ"/> grid
    /// over <paramref name="bounds"/> into <paramref name="destination"/> (x fastest), dB(A) clamped below at
    /// <see cref="AcousticFieldOptions.MinimumLevel"/>. Cost: cells × sources; slices run in parallel. Reusing the
    /// destination array, a call allocates only its small parallel-loop closure.
    /// </summary>
    public void ComputeGrid(Aabb bounds, int sizeX, int sizeY, int sizeZ, float[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeX), "Grid sizes must be positive.");
        long count = (long)sizeX * sizeY * sizeZ;
        if (destination.Length < count)
            throw new ArgumentException($"Destination holds {destination.Length} values, {count} needed.", nameof(destination));
        if (bounds.IsEmpty)
            throw new ArgumentException("The grid box is empty.", nameof(bounds));
        var size = bounds.Size;
        var cell = new Vector3(size.X / sizeX, size.Y / sizeY, size.Z / sizeZ);
        var min = bounds.Min;
        Parallel.For(0, sizeZ, _parallel, k =>
        {
            for (int j = 0; j < sizeY; j++)
            {
                for (int i = 0; i < sizeX; i++)
                {
                    var p = min + new Vector3(i + 0.5f, j + 0.5f, k + 0.5f) * cell;
                    destination[i + sizeX * (j + sizeY * k)] = Clamp(LevelAt(p));
                }
            }
        });
    }

    /// <summary>
    /// A grid over <paramref name="bounds"/> with cells of about <paramref name="cellSize"/> metres (coarsened so no
    /// axis exceeds <paramref name="maxCellsPerAxis"/>), filled with <see cref="ComputeGrid(Aabb, int, int, int, float[])"/>.
    /// </summary>
    public AcousticGrid ComputeGrid(Aabb bounds, float cellSize, int maxCellsPerAxis = 128)
    {
        if (bounds.IsEmpty || !float.IsFinite(bounds.Min.X + bounds.Min.Y + bounds.Min.Z + bounds.Max.X + bounds.Max.Y + bounds.Max.Z))
            throw new ArgumentException("The grid box is empty or not finite.", nameof(bounds));
        if (!(cellSize > 0f) || !float.IsFinite(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "The cell size must be positive.");
        int max = Math.Max(1, maxCellsPerAxis);
        var size = bounds.Size;
        int Cells(float extent) => Math.Clamp((int)MathF.Ceiling(extent / cellSize - 1e-4f), 1, max);
        var grid = new AcousticGrid(bounds, Cells(size.X), Cells(size.Y), Cells(size.Z));
        ComputeGrid(bounds, grid.SizeX, grid.SizeY, grid.SizeZ, grid.Values);
        return grid;
    }

    /// <summary>Σ pᵢ/dᵢ² over the sources (relative to the loudest one); images only for receivers above the floor.</summary>
    private double Intensity(Vector3 point)
    {
        const double minD2 = Acoustics.MinDistance * Acoustics.MinDistance;
        int count = _options.FloorReflection && point.Z < _options.FloorHeight ? _directCount : _count;
        double sum = 0;
        for (int n = 0; n < count; n++)
        {
            double d2 = Vector3.DistanceSquared(point, _positions[n]);
            sum += _power[n] / Math.Max(d2, minD2);
        }

        return sum;
    }

    private float Clamp(float level) => level > _options.MinimumLevel ? level : _options.MinimumLevel;
}
