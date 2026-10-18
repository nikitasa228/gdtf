using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical;

/// <summary>One device of a data chain.</summary>
public sealed record DataReportEntry
{
    /// <summary>1-based position from the console.</summary>
    public required int ChainIndex { get; init; }

    /// <summary>Device entity.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>Device name.</summary>
    public required string Name { get; init; }

    /// <summary>Fixture id (0 when none).</summary>
    public required int FixtureId { get; init; }

    /// <summary>Input port the data arrives at.</summary>
    public required string InputPort { get; init; }

    /// <summary>Patched address of the break carried by the input ("2.101"), or null when unpatched.</summary>
    public DmxAddress? Patch { get; init; }

    /// <summary>True when the patch is on a universe the line carries (also true when unpatched).</summary>
    public required bool PatchMatches { get; init; }

    /// <summary>Device the data comes from (null = the console).</summary>
    public Guid? UpstreamEntityId { get; init; }

    /// <summary>Cable into this device.</summary>
    public required Guid CableEntityId { get; init; }

    /// <summary>Label of that cable.</summary>
    public required string CableLabel { get; init; }

    /// <summary>DMX: cable metres since the segment start; Ethernet: length of the last link.</summary>
    public required float RunLengthM { get; init; }
}

/// <summary>One console output and the chain of fixtures it drives.</summary>
public sealed record DataReportOutput
{
    /// <summary>Console entity.</summary>
    public required Guid ConsoleEntityId { get; init; }

    /// <summary>Console name.</summary>
    public required string ConsoleName { get; init; }

    /// <summary>Output port.</summary>
    public required string Port { get; init; }

    /// <summary>DMX512 or Ethernet.</summary>
    public required SignalKind Signal { get; init; }

    /// <summary>First universe.</summary>
    public required int FirstUniverse { get; init; }

    /// <summary>Universe count.</summary>
    public required int UniverseCount { get; init; }

    /// <summary>Devices in cabling order.</summary>
    public required IReadOnlyList<DataReportEntry> Chain { get; init; }
}

/// <summary>Data table for the UI: per console output, its universe(s) and the chain of fixtures in order.</summary>
public sealed class DataReport
{
    private DataReport(IReadOnlyList<DataReportOutput> outputs, IReadOnlyList<Guid> withoutData)
    {
        Outputs = outputs;
        PatchedFixturesWithoutData = withoutData;
    }

    /// <summary>Console outputs in scene and port order.</summary>
    public IReadOnlyList<DataReportOutput> Outputs { get; }

    /// <summary>Patched fixtures that no console output reaches.</summary>
    public IReadOnlyList<Guid> PatchedFixturesWithoutData { get; }

    /// <summary>Builds the table from a solution.</summary>
    public static DataReport Build(NetworkSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var outputs = new List<DataReportOutput>(solution.DataLines.Count);
        foreach (var line in solution.DataLines)
        {
            var chain = new List<DataReportEntry>(line.Feeds.Count);
            foreach (var f in line.Feeds)
            {
                solution.Devices.TryGetValue(f.DeviceEntityId, out var device);
                DmxAddress? patch = null;
                bool matches = true;
                if (device is { Addresses.Count: > 0 })
                {
                    int dmxBreak = device.FindPort(f.InputPort)?.DmxBreak ?? 1;
                    var a = device.Addresses.FirstOrDefault(x => x.Break == dmxBreak);
                    patch = a.Universe == 0 ? device.Addresses[0] : a;
                    matches = f.Signal == SignalKind.Dmx512 ? patch.Value.Universe == f.FirstUniverse : device.Addresses.All(x => f.Carries(x.Universe));
                }
                chain.Add(new DataReportEntry
                {
                    ChainIndex = f.ChainIndex,
                    EntityId = f.DeviceEntityId,
                    Name = device?.Name ?? "",
                    FixtureId = device?.FixtureId ?? 0,
                    InputPort = f.InputPort,
                    Patch = patch,
                    PatchMatches = matches,
                    UpstreamEntityId = f.UpstreamEntityId,
                    CableEntityId = f.CableEntityId,
                    CableLabel = solution.FindCable(f.CableEntityId)?.DisplayLabel ?? "",
                    RunLengthM = f.RunLengthM,
                });
            }
            outputs.Add(new DataReportOutput
            {
                ConsoleEntityId = line.ConsoleEntityId,
                ConsoleName = line.ConsoleName,
                Port = line.Output.Name,
                Signal = line.Signal,
                FirstUniverse = line.FirstUniverse,
                UniverseCount = line.UniverseCount,
                Chain = chain,
            });
        }
        var withoutData = solution.Devices.Values
            .Where(d => d.Kind == DeviceKind.Fixture && d.Addresses.Count > 0 && !solution.DataFeedsByDevice.ContainsKey(d.EntityId))
            .Select(d => d.EntityId).ToList();
        return new DataReport(outputs, withoutData);
    }
}
