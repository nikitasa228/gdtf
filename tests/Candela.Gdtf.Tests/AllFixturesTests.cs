using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Candela.Gdtf.Tests;

/// <summary>What the all-files tests need from one parsed fixture (the fixture types themselves are not kept).</summary>
public sealed record ModeSummary(string Name, int DeclaredChannelCount, int Footprint, int ChannelCount, int InstanceCount, bool HasReferencedInstances);

/// <summary>Summary of one library file after a full parse.</summary>
public sealed record FixtureSummary(string FileName, string? Error, int WarningCount, IReadOnlyList<ModeSummary> Modes);

/// <summary>Parses every <c>.gdtf</c> in the repository root once (in parallel) for the tests of this collection.</summary>
public sealed class AllFixturesFixture
{
    public AllFixturesFixture()
    {
        var results = new ConcurrentBag<FixtureSummary>();
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(TestData.GdtfFiles, file =>
        {
            string name = Path.GetFileName(file);
            try
            {
                var ft = GdtfReader.ReadFile(file);
                var modes = ft.DmxModes.Select(m => new ModeSummary(
                    m.Name, m.DeclaredChannelCount, m.Footprint, m.Channels.Count, m.ChannelInstances.Count,
                    m.ChannelInstances.Any(i => i.References.Count > 0 && i.Offsets.Length > 0))).ToList();
                results.Add(new FixtureSummary(name, null, ft.Warnings.Count, modes));
            }
            catch (Exception e)
            {
                results.Add(new FixtureSummary(name, $"{e.GetType().Name}: {e.Message}", 0, Array.Empty<ModeSummary>()));
            }
        });
        Elapsed = sw.Elapsed;
        CpuTime = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        Summaries = results.OrderBy(r => r.FileName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Wall-clock time of the parallel full parse.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// Processor time the test process used meanwhile (the parse, plus whatever other tests ran in parallel): the work
    /// itself, which unlike the wall-clock time does not grow when other processes load the machine.
    /// </summary>
    public TimeSpan CpuTime { get; }

    /// <summary>
    /// The wall-clock time the parse takes on an otherwise idle machine, estimated from <see cref="CpuTime"/> spread
    /// over the cores.
    /// </summary>
    public TimeSpan IdleMachineEstimate => CpuTime / Environment.ProcessorCount;

    /// <summary>One summary per file, sorted by file name.</summary>
    public IReadOnlyList<FixtureSummary> Summaries { get; }
}

[CollectionDefinition(Name)]
public sealed class AllFixturesCollection : ICollectionFixture<AllFixturesFixture>
{
    public const string Name = "All library fixtures";
}

[Collection(AllFixturesCollection.Name)]
public sealed class AllFixturesTests
{
    private readonly AllFixturesFixture _all;
    private readonly ITestOutputHelper _output;

    public AllFixturesTests(AllFixturesFixture all, ITestOutputHelper output)
    {
        _all = all;
        _output = output;
    }

    [Fact]
    public void Every_library_file_parses_without_an_exception_well_within_two_minutes()
    {
        Assert.Equal(TestData.LibraryFileCount, _all.Summaries.Count);
        var failures = _all.Summaries.Where(s => s.Error is not null).Select(s => $"{s.FileName}: {s.Error}").ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        int withWarnings = _all.Summaries.Count(s => s.WarningCount > 0);
        _output.WriteLine($"Parsed {_all.Summaries.Count} files in {_all.Elapsed.TotalSeconds:F1} s (parallel, {Environment.ProcessorCount} cores, " +
            $"{_all.CpuTime.TotalSeconds:F1} s processor time).");
        _output.WriteLine($"{withWarnings} files have warnings ({_all.Summaries.Sum(s => s.WarningCount)} in total).");
        foreach (var s in _all.Summaries.OrderByDescending(s => s.WarningCount).Take(5))
            _output.WriteLine($"  {s.WarningCount,4}  {s.FileName}");
        // Wall-clock time alone fails on a machine busy with other work (131 s once under a load average of 37); the
        // processor time measures the parse itself. Only both being slow is a real regression.
        Assert.True(_all.Elapsed < TimeSpan.FromSeconds(90) || _all.IdleMachineEstimate < TimeSpan.FromSeconds(60),
            $"Full parse took {_all.Elapsed} ({_all.CpuTime} processor time on {Environment.ProcessorCount} cores).");
        // Only accessories (two Elation brackets) come without DMX modes; mode counts are checked against the specs below.
        Assert.True(_all.Summaries.Count(s => s.Modes.Count == 0) <= 2);
    }

    /// <summary>
    /// The specs extractor records, per mode, the highest offset written on the DMX channels of each break (summed
    /// over breaks). <see cref="DmxMode.DeclaredChannelCount"/> computes exactly that and must agree for every mode.
    /// </summary>
    [Fact]
    public void Declared_channel_count_of_every_mode_equals_the_specs_index()
    {
        var mismatches = new List<string>();
        int modes = 0;
        foreach (var s in _all.Summaries)
        {
            var record = TestData.Specs.Find(s.FileName);
            if (record is null)
            {
                mismatches.Add($"{s.FileName}: no specs record");
                continue;
            }
            if (record.DmxModes.Count != s.Modes.Count)
            {
                mismatches.Add($"{s.FileName}: {s.Modes.Count} modes, specs list {record.DmxModes.Count}");
                continue;
            }
            for (int i = 0; i < s.Modes.Count; i++)
            {
                modes++;
                var mode = s.Modes[i];
                var spec = record.DmxModes[i];
                if (FixtureNames.Key(mode.Name) != FixtureNames.Key(spec.Name))
                    mismatches.Add($"{s.FileName}: mode {i} is '{mode.Name}', specs say '{spec.Name}'");
                if (mode.DeclaredChannelCount != spec.Channels)
                    mismatches.Add($"{s.FileName} '{mode.Name}': {mode.DeclaredChannelCount} channels, specs say {spec.Channels}");
            }
        }
        _output.WriteLine($"Compared {modes} modes of {_all.Summaries.Count} fixtures with _specs/fixtures_index.json.");
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(50)));
        Assert.True(modes > 7000);
    }

    /// <summary>
    /// The real DMX footprint differs from the specs count only where GDTF geometry references instance channels
    /// (pixel bars, multi-head fixtures): the specs extractor reads the offsets as written on the template channels
    /// (relative to each reference's Break offset), the reader adds every instance at its absolute address.
    /// </summary>
    [Fact]
    public void Footprint_differs_from_the_specs_count_only_for_modes_with_geometry_reference_instances()
    {
        var unexplained = new List<string>();
        int differing = 0;
        foreach (var s in _all.Summaries)
            foreach (var m in s.Modes)
            {
                if (m.Footprint == m.DeclaredChannelCount) continue;
                differing++;
                if (!m.HasReferencedInstances) unexplained.Add($"{s.FileName} '{m.Name}': footprint {m.Footprint}, declared {m.DeclaredChannelCount}");
            }
        _output.WriteLine($"{differing} modes address more (or other) slots than written on their channels because of geometry references.");
        Assert.True(unexplained.Count == 0, string.Join(Environment.NewLine, unexplained.Take(30)));
    }
}
