using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical;

public sealed partial class NetworkSolver
{
    /// <summary>Immutable list of the cables walked so far (shared tails, so branching costs nothing).</summary>
    private sealed record CablePathNode(Guid Cable, CablePathNode? Previous)
    {
        public IReadOnlyList<Guid> ToList()
        {
            int n = 0;
            for (var p = this; p is not null; p = p.Previous) n++;
            var list = new Guid[n];
            for (var p = this; p is not null; p = p.Previous) list[--n] = p.Cable;
            return list;
        }
    }

    /// <summary>A data output waiting to be followed.</summary>
    private readonly record struct DataFrame(
        Device Device, Port Output, int Depth, int SegmentCount, float SegmentRunM, float SegmentMaxRunM, (Guid, string) SegmentKey,
        CablePathNode? Path);

    private sealed record DataResult(
        IReadOnlyList<DataLine> Lines, IReadOnlyDictionary<Guid, IReadOnlyList<DataFeed>> FeedsByDevice, HashSet<Guid> ConsoleDriven);

    private sealed partial class SolveRun
    {
        private DataResult SolveData()
        {
            var lines = new List<DataLine>();
            var feeds = new Dictionary<Guid, List<DataFeed>>();
            var reportedCables = new HashSet<Guid>();
            var reportedSegments = new HashSet<(Guid, string, int)>();
            int maxPerSegment = Math.Max(1, _owner.Options.MaxDmxDevicesPerSegment);

            foreach (var console in _deviceOrder)
            {
                if (console.Console is null) continue;
                foreach (var output in console.Info.Ports)
                {
                    if (output.Role != PortRole.ConsoleOutput) continue;
                    var lineFeeds = new List<DataFeed>();
                    var line = new DataLine
                    {
                        ConsoleEntityId = console.Id,
                        ConsoleName = console.Info.Name,
                        Output = output,
                        Feeds = lineFeeds,
                    };
                    bool dmx = output.Signal == SignalKind.Dmx512;
                    string lineName = $"console '{console.Info.Name}' {output.Name}";
                    var visited = new HashSet<Guid> { console.Id };
                    var stack = new Stack<DataFrame>();
                    stack.Push(new DataFrame(console, output, 0, 0, 0f, float.PositiveInfinity, (console.Id, output.Name), null));
                    var next = new List<DataFrame>();

                    while (stack.Count > 0)
                    {
                        var f = stack.Pop();
                        if (!_plugged.TryGetValue(new PortRef(f.Device.Id, f.Output.Name), out var hit)) continue;
                        var cable = hit.Cable;
                        if (cable.Type is not { IsData: true } type) continue;
                        var from = cable.Info.Endpoints[hit.Position];
                        if (!from.IsUsable) continue;
                        float[]? dist = null;
                        next.Clear();

                        foreach (var ep in cable.Info.Endpoints)
                        {
                            if (ReferenceEquals(ep, from) || !ep.IsUsable) continue;
                            var target = _devices[ep.EntityId];
                            var input = ep.Port!;
                            if (!input.Signal.CarriesData() || !SameDataFamily(input.Signal, output.Signal)) continue;
                            if (input.Direction == PortDirection.Output)
                            {
                                if (reportedCables.Add(cable.Entity.Id))
                                    Add(IssueSeverity.Error, IssueCategory.Data, IssueCodes.DataOutputConflict,
                                        $"{type.DisplayName} '{cable.Label}' connects two outputs: '{f.Output.Name}' of {f.Device.Info.Label} and '{input.Name}' of {target.Info.Label}.",
                                        cable.Entity.Id, f.Device.Id, target.Id);
                                continue;
                            }
                            if (!visited.Add(target.Id))
                            {
                                if (reportedCables.Add(cable.Entity.Id))
                                    Add(IssueSeverity.Error, IssueCategory.Data, IssueCodes.DataLoop,
                                        $"{target.Info.Label} receives data from {lineName} a second time through cable '{cable.Label}': the data cabling forms a loop.",
                                        target.Id, cable.Entity.Id);
                                continue;
                            }

                            dist ??= cable.Geometry.DistancesFrom(from.NodeId);
                            float link = LinkLength(cable, dist, ep);
                            int depth = f.Depth + 1;
                            int segCount = f.SegmentCount + 1;
                            float segRun = f.SegmentRunM + link;
                            float segMax = MathF.Min(f.SegmentMaxRunM, type.MaxRunM ?? float.PositiveInfinity);
                            var path = new CablePathNode(cable.Entity.Id, f.Path);
                            var feed = new DataFeed
                            {
                                DeviceEntityId = target.Id,
                                InputPort = input.Name,
                                ConsoleEntityId = console.Id,
                                ConsolePort = output.Name,
                                Signal = output.Signal,
                                FirstUniverse = line.FirstUniverse,
                                UniverseCount = line.UniverseCount,
                                ChainIndex = depth,
                                UpstreamEntityId = f.Device.Id == console.Id ? null : f.Device.Id,
                                UpstreamPort = f.Output.Name,
                                CableEntityId = cable.Entity.Id,
                                CablePath = path.ToList(),
                                RunLengthM = dmx ? segRun : link,
                            };
                            lineFeeds.Add(feed);
                            if (!feeds.TryGetValue(target.Id, out var list)) feeds[target.Id] = list = new List<DataFeed>(1);
                            list.Add(feed);

                            if (dmx && segCount > maxPerSegment && reportedSegments.Add((f.SegmentKey.Item1, f.SegmentKey.Item2, 0)))
                                Add(IssueSeverity.Warning, IssueCategory.Data, IssueCodes.DataChainTooLong,
                                    $"The DMX line from {lineName} has more than {maxPerSegment} devices without a splitter: {target.Info.Label} is device {segCount} of the segment.",
                                    target.Id, cable.Entity.Id);
                            if (dmx && segRun > segMax && reportedSegments.Add((f.SegmentKey.Item1, f.SegmentKey.Item2, 1)))
                                Add(IssueSeverity.Warning, IssueCategory.Data, IssueCodes.DataRunTooLong,
                                    $"The DMX line from {lineName} reaches {Fmt.M(segRun)} of cable at {target.Info.Label} (cable '{cable.Label}'); a DMX line should not exceed {Fmt.M(segMax)}.",
                                    target.Id, cable.Entity.Id);

                            if (target.Fixture is null) continue; // consoles and power sources do not pass data on
                            int dmxOutputs = 0;
                            foreach (var p in target.Info.Ports)
                                if (p.Direction == PortDirection.Output && p.Signal == SignalKind.Dmx512) dmxOutputs++;
                            bool splitter = dmx && dmxOutputs >= 2;
                            foreach (var o in target.Info.Ports)
                            {
                                if (ReferenceEquals(o, input) || o.Direction == PortDirection.Input || !o.Signal.CarriesData() ||
                                    !SameDataFamily(o.Signal, output.Signal) || o.Role is PortRole.Other or PortRole.PowerIn)
                                    continue;
                                next.Add(splitter
                                    ? new DataFrame(target, o, depth, 0, 0f, float.PositiveInfinity, (target.Id, o.Name), path)
                                    : new DataFrame(target, o, depth, segCount, segRun, segMax, f.SegmentKey, path));
                            }
                        }
                        for (int i = next.Count - 1; i >= 0; i--) stack.Push(next[i]);
                    }
                    lines.Add(line);
                }
            }

            var consoleDriven = new HashSet<Guid>();
            foreach (var device in _deviceOrder)
            {
                if (device.Fixture is not { } fixture) continue;
                feeds.TryGetValue(device.Id, out var list);
                if (list is { Count: > 0 })
                {
                    consoleDriven.Add(device.Id);
                    CheckUniverses(device, fixture, list);
                }
                else if (fixture.Addresses.Count > 0 && device.Info.Ports.Any(p => p.Signal.CarriesData() && p.Direction != PortDirection.Output))
                {
                    var plugged = device.Info.Ports.FirstOrDefault(p => p.Signal.CarriesData() && p.Direction != PortDirection.Output &&
                                                                         _plugged.ContainsKey(new PortRef(device.Id, p.Name)));
                    string why = plugged is null
                        ? "no data cable is plugged into its inputs"
                        : $"the cable on '{plugged.Name}' does not lead back to a console output";
                    Add(IssueSeverity.Warning, IssueCategory.Data, IssueCodes.DataNoSignal,
                        $"{device.Info.Label} is patched to {fixture.Addresses[0]} but receives no data: {why}.", device.Id);
                }
            }

            var readOnly = new Dictionary<Guid, IReadOnlyList<DataFeed>>(feeds.Count);
            foreach (var (k, v) in feeds) readOnly[k] = v;
            return new DataResult(lines, readOnly, consoleDriven);
        }

        private void CheckUniverses(Device device, FixtureComponent fixture, List<DataFeed> feeds)
        {
            var sources = feeds.Select(f => (f.ConsoleEntityId, f.ConsolePort)).Distinct().ToList();
            if (sources.Count > 1)
                Add(IssueSeverity.Warning, IssueCategory.Data, IssueCodes.DataMultipleSources,
                    $"{device.Info.Label} receives data from {sources.Count} console outputs ({Fmt.List(feeds.Select(f => $"'{_devices[f.ConsoleEntityId].Info.Name}' {f.ConsolePort}").Distinct())}).",
                    [device.Id, .. feeds.Select(f => f.ConsoleEntityId)]);
            if (fixture.Addresses.Count == 0) return;

            foreach (var feed in feeds.DistinctBy(f => f.InputPort, StringComparer.OrdinalIgnoreCase))
            {
                var console = _devices[feed.ConsoleEntityId].Info.Name;
                if (feed.Signal == SignalKind.Dmx512)
                {
                    int dmxBreak = device.Info.FindPort(feed.InputPort)?.DmxBreak ?? 1;
                    var address = fixture.Addresses.FirstOrDefault(a => a.Break == dmxBreak);
                    if (address.Universe == 0) address = fixture.Addresses[0];
                    if (address.Universe != feed.FirstUniverse)
                        Add(IssueSeverity.Error, IssueCategory.Data, IssueCodes.DataUniverseMismatch,
                            $"{device.Info.Label} is patched to universe {address.Universe} ({address}) but its input '{feed.InputPort}' carries universe {feed.FirstUniverse} from console '{console}' {feed.ConsolePort} (device {feed.ChainIndex} of the chain).",
                            device.Id, feed.ConsoleEntityId, feed.CableEntityId);
                }
                else
                {
                    var outside = fixture.Addresses.Where(a => !feed.Carries(a.Universe)).ToList();
                    if (outside.Count > 0)
                    {
                        string range = feed.UniverseCount <= 1 ? $"universe {feed.FirstUniverse}" : $"universes {feed.FirstUniverse}–{feed.FirstUniverse + feed.UniverseCount - 1}";
                        Add(IssueSeverity.Error, IssueCategory.Data, IssueCodes.DataUniverseMismatch,
                            $"{device.Info.Label} is patched to {Fmt.List(outside.Select(a => $"universe {a.Universe} ({a})"))} but its input '{feed.InputPort}' receives {range} from console '{console}' {feed.ConsolePort}.",
                            device.Id, feed.ConsoleEntityId, feed.CableEntityId);
                    }
                }
            }
        }

        private static bool SameDataFamily(SignalKind a, SignalKind b) => a == b || a == SignalKind.Hybrid || b == SignalKind.Hybrid;
    }
}
