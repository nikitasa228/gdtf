using System.Numerics;
using System.Text.Json;
using Candela.Core.Math;
using Candela.Photometry;
using Xunit.Abstractions;

namespace Candela.Simulation.Tests;

/// <summary>Sound pressure levels at points, on grids and on surfaces, with and without the floor reflection.</summary>
public sealed class AcousticFieldTests(ITestOutputHelper output)
{
    private static readonly Aabb Room = new(new Vector3(-5f, -4f, 0f), new Vector3(5f, 4f, 4f));

    [Fact]
    public void SingleSource_GridFollowsTheInverseSquareLaw()
    {
        var field = new AcousticField();
        var source = new Vector3(0.3f, -0.2f, 1.7f);
        field.SetSources([new SoundSource(source, 47f)]);
        var grid = field.ComputeGrid(Room, 0.5f);
        Assert.Equal((20, 16, 8), (grid.SizeX, grid.SizeY, grid.SizeZ));
        for (int k = 0; k < grid.SizeZ; k++)
        {
            for (int j = 0; j < grid.SizeY; j++)
            {
                for (int i = 0; i < grid.SizeX; i++)
                {
                    float d = MathF.Max(Vector3.Distance(grid.CellCentre(i, j, k), source), Acoustics.MinDistance);
                    Assert.Equal(47f - 20f * MathF.Log10(d), grid.Values[i + grid.SizeX * (j + grid.SizeY * k)], 2e-3f);
                }
            }
        }

        Assert.Equal(47f, field.LevelAt(source + new Vector3(1f, 0f, 0f)), 2e-4f);
        Assert.Equal(41f, field.LevelAt(source + new Vector3(0f, 2f, 0f)), 0.05f);   // −6 dB per doubling of distance
        Assert.Equal(47f + 20f, field.LevelAt(source), 2e-4f);                          // clamped at 0.1 m
    }

    [Fact]
    public void TwoSources_AddEnergetically()
    {
        var a = new SoundSource(new Vector3(-2f, 0f, 1f), 50f);
        var b = new SoundSource(new Vector3(3f, 1f, 2.5f), 44f);
        var field = new AcousticField();
        field.SetSources([a, b]);
        var grid = new float[12 * 10 * 6];
        field.ComputeGrid(Room, 12, 10, 6, grid);
        var size = Room.Size;
        for (int k = 0; k < 6; k++)
        {
            for (int j = 0; j < 10; j++)
            {
                for (int i = 0; i < 12; i++)
                {
                    var p = Room.Min + new Vector3((i + 0.5f) * size.X / 12, (j + 0.5f) * size.Y / 10, (k + 0.5f) * size.Z / 6);
                    float la = 50f - 20f * MathF.Log10(Vector3.Distance(p, a.Position));
                    float lb = 44f - 20f * MathF.Log10(Vector3.Distance(p, b.Position));
                    float expected = 10f * MathF.Log10(MathF.Pow(10f, la / 10f) + MathF.Pow(10f, lb / 10f));
                    float actual = grid[i + 12 * (j + 10 * k)];
                    Assert.Equal(expected, actual, 2e-3f);
                    // Same as Candela.Photometry's reference implementation.
                    Assert.Equal(Acoustics.SplAt(p, [a, b]), actual, 2e-3f);
                }
            }
        }

        // Two equal sources in one place: +3.01 dB.
        field.SetSources([a, a]);
        Assert.Equal(50f + 3.0103f, field.LevelAt(a.Position + Vector3.UnitX), 2e-3f);
    }

    [Fact]
    public void FloorReflection_AddsTheImageSource()
    {
        var source = new SoundSource(new Vector3(0f, 0f, 2f), 60f);
        var hard = new AcousticField(new AcousticFieldOptions { FloorReflection = true, FloorAbsorption = 0f });
        var soft = new AcousticField(new AcousticFieldOptions { FloorReflection = true, FloorAbsorption = 0.5f });
        var free = new AcousticField();
        hard.SetSources([source]);
        soft.SetSources([source]);
        free.SetSources([source]);

        // On a hard floor directly below the source, direct and reflected paths are equally long: +3.01 dB.
        var onFloor = new Vector3(0f, 0f, 0f);
        Assert.Equal(free.LevelAt(onFloor) + 3.0103f, hard.LevelAt(onFloor), 2e-3f);
        // Elsewhere: 10·log10(1/d² + (1 − α)/d'²) with d' the distance to the image at z = −2.
        var p = new Vector3(3f, 1f, 1.2f);
        float d = Vector3.Distance(p, source.Position), dImage = Vector3.Distance(p, new Vector3(0f, 0f, -2f));
        Assert.Equal(60f + 10f * MathF.Log10(1f / (d * d) + 0.5f / (dImage * dImage)), soft.LevelAt(p), 2e-3f);
        Assert.True(hard.LevelAt(p) > soft.LevelAt(p) && soft.LevelAt(p) > free.LevelAt(p));
        // Below the floor only the direct sound counts; a fully absorbing floor reflects nothing.
        Assert.Equal(free.LevelAt(new Vector3(1f, 0f, -0.5f)), hard.LevelAt(new Vector3(1f, 0f, -0.5f)), 2e-4f);
        var absorbing = new AcousticField(new AcousticFieldOptions { FloorReflection = true, FloorAbsorption = 1f });
        absorbing.SetSources([source]);
        Assert.Equal(free.LevelAt(p), absorbing.LevelAt(p), 2e-4f);
        // A raised floor (stage deck at 1.2 m).
        var deck = new AcousticField(new AcousticFieldOptions { FloorReflection = true, FloorHeight = 1.2f, FloorAbsorption = 0f });
        deck.SetSources([source]);
        Assert.Equal(free.LevelAt(new Vector3(0f, 0f, 1.2f)) + 3.0103f, deck.LevelAt(new Vector3(0f, 0f, 1.2f)), 2e-3f);
    }

    [Fact]
    public void RealNoiseLevelsFromSpecs_GiveTheExpectedField()
    {
        // Atmospheric machines from _specs/fixtures_index.json: their noiseLevelDb is the SPL at 1 m.
        var specs = SpecNoiseLevels().Take(6).ToArray();
        Assert.NotEmpty(specs);
        var sources = specs.Select((s, n) => new SoundSource(new Vector3(-4f + 1.6f * n, 3f, 0.4f), s.Level)).ToArray();
        foreach (var s in specs)
            output.WriteLine($"{s.File}: {s.Level} dB(A) at 1 m");
        var field = new AcousticField(new AcousticFieldOptions { FloorReflection = true, FloorAbsorption = 0.1f });
        field.SetSources(sources);
        Assert.Equal(sources.Length, field.SourceCount);

        // Grid for the noise overlay: 0.25 m cells over the room.
        var grid = field.ComputeGrid(Room, 0.25f);
        Assert.Equal(40 * 32 * 16, grid.Values.Length);
        float loudest = sources.Max(s => s.LevelAt1MetreDb);
        Assert.All(grid.Values, v => Assert.InRange(v, 0f, loudest + 20f + 3.02f + 10f * MathF.Log10(sources.Length)));
        // Far from all sources the level drops: the far corner is quieter than the cell next to a machine.
        float near = field.LevelAt(sources[0].Position + new Vector3(0f, -1f, 0f));
        float far = field.LevelAt(new Vector3(4.5f, -3.5f, 3.5f));
        output.WriteLine($"1 m from the first machine: {near:F1} dB(A); far corner: {far:F1} dB(A)");
        Assert.True(near > far + 6f);
        Assert.True(near >= sources[0].LevelAt1MetreDb);
    }

    [Fact]
    public void Surfaces_AreSampledAtPointsAndOnRectangles()
    {
        var field = new AcousticField(new AcousticFieldOptions { MinimumLevel = 20f });
        field.SetSources([new SoundSource(new Vector3(0f, 0f, 1f), 50f)]);

        // A 4 × 2 m wall at y = 3, sampled 8 × 4.
        var levels = new float[32];
        field.SampleRectangle(new Vector3(-2f, 3f, 0f), new Vector3(4f, 0f, 0f), new Vector3(0f, 0f, 2f), 8, 4, levels);
        var centreOfFirstCell = new Vector3(-2f + 0.25f, 3f, 0.25f);
        Assert.Equal(field.LevelAt(centreOfFirstCell), levels[0], 2e-4f);
        Assert.Equal(field.LevelAt(new Vector3(-0.25f, 3f, 0.75f)), levels[3 + 8 * 1], 2e-4f);
        // The middle of the wall (closest to the source) is the loudest.
        Assert.Equal(levels.Max(), MathF.Max(levels[3 + 8 * 1], levels[4 + 8 * 1]));

        var points = new[] { new Vector3(0f, 1f, 1f), new Vector3(0f, 400f, 1f) };
        var sampled = new float[2];
        field.SampleSurface(points, sampled);
        Assert.Equal(50f, sampled[0], 2e-4f);
        Assert.Equal(20f, sampled[1]);   // 50 − 52 dB → clamped to the 20 dB floor of the overlay
        Assert.Throws<ArgumentException>(() => field.SampleSurface(points, new float[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => field.SampleRectangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 0, 2, levels));
    }

    [Fact]
    public void BadSources_AreSkipped_AndSilenceIsMinusInfinity()
    {
        var field = new AcousticField();
        Assert.Equal(Acoustics.Silence, field.LevelAt(Vector3.Zero));
        field.SetSources([
            new SoundSource(new Vector3(float.NaN, 0f, 0f), 50f),
            new SoundSource(Vector3.Zero, float.NaN),
            new SoundSource(Vector3.Zero, float.PositiveInfinity),
            new SoundSource(new Vector3(1f, 0f, 0f), 0f),     // fanless fixture: kept, inaudible
        ]);
        Assert.Equal(3, field.IgnoredSources);
        Assert.Equal(1, field.SourceCount);
        Assert.Equal(0f, field.LevelAt(new Vector3(2f, 0f, 0f)), 2e-4f);
        Assert.Equal(Acoustics.Silence, field.LevelAt(new Vector3(float.NaN, 0f, 0f)));

        // Extreme ranges do not overflow: a 200 dB source next to a 0 dB one.
        field.SetSources([new SoundSource(Vector3.Zero, 200f), new SoundSource(Vector3.One, 0f)]);
        Assert.Equal(200f - 20f * MathF.Log10(3f), field.LevelAt(new Vector3(3f, 0f, 0f)), 2e-3f);
        Assert.Throws<ArgumentException>(() => field.ComputeGrid(Aabb.Empty, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => field.ComputeGrid(Room, 0f));
    }

    [Fact]
    public void Grid_IsDeterministicWhateverTheThreadCount()
    {
        var sources = Enumerable.Range(0, 40)
            .Select(n => new SoundSource(new Vector3(-4f + 0.2f * n, MathF.Sin(n) * 3f, 3.5f), 30f + n % 17))
            .ToArray();
        var serial = new AcousticField(new AcousticFieldOptions { FloorReflection = true, MaxDegreeOfParallelism = 1 });
        var parallel = new AcousticField(new AcousticFieldOptions { FloorReflection = true });
        serial.SetSources(sources);
        parallel.SetSources(sources);
        var a = serial.ComputeGrid(Room, 0.2f);
        var b = parallel.ComputeGrid(Room, 0.2f);
        Assert.True(a.Values.AsSpan().SequenceEqual(b.Values));
    }

    /// <summary>HAZE/FOG/FAN entries of the _specs index with a noise level.</summary>
    private static IEnumerable<(string File, float Level)> SpecNoiseLevels()
    {
        var path = Path.Combine(RepoRoot(), "_specs", "fixtures_index.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var result = new List<(string, float)>();
        foreach (var fixture in document.RootElement.EnumerateArray())
        {
            if (!fixture.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String)
                continue;
            if (category.GetString() is not ("HAZE" or "FOG" or "FAN"))
                continue;
            if (fixture.TryGetProperty("noiseLevelDb", out var noise) && noise.ValueKind == JsonValueKind.Number)
                result.Add((fixture.GetProperty("file").GetString() ?? "?", noise.GetSingle()));
        }

        return result;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "_specs")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Repository root with _specs/ not found above " + AppContext.BaseDirectory);
    }
}
