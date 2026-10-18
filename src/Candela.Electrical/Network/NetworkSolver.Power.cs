using Candela.Core.Diagnostics;

namespace Candela.Electrical;

public sealed partial class NetworkSolver
{
    /// <summary>Active power plus apparent power per phase (W, VA).</summary>
    private readonly record struct PhaseLoad(double W, double Va1, double Va2, double Va3)
    {
        public static PhaseLoad Single(int phase, double w, double va) => phase switch
        {
            2 => new(w, 0, va, 0),
            3 => new(w, 0, 0, va),
            _ => new(w, va, 0, 0),
        };

        public static PhaseLoad operator +(PhaseLoad a, PhaseLoad b) => new(a.W + b.W, a.Va1 + b.Va1, a.Va2 + b.Va2, a.Va3 + b.Va3);

        public PhaseLoad Scale(double k) => new(W * k, Va1 * k, Va2 * k, Va3 * k);

        public PhaseLoad Collapse(int phase) => Single(phase, W, VaTotal);

        public double VaTotal => Va1 + Va2 + Va3;

        public double MaxVa => Math.Max(Va1, Math.Max(Va2, Va3));

        public double[] Currents(float voltage) => voltage > 0 ? [Va1 / voltage, Va2 / voltage, Va3 / voltage] : [0, 0, 0];
    }

    /// <summary>One cable connection from a power output to a power input.</summary>
    private sealed record PowerEdge(CableState Cable, CableEndpointInfo Up, CableEndpointInfo Down, int Circuit);

    /// <summary>What a device is supplied with.</summary>
    private sealed record Supply(SourceState Source, Port Outlet, CircuitState Circuit, int Phase, bool ThreePhase, float VoltageV, float FrequencyHz,
        bool IsDc, bool Dimmed);

    private sealed class CircuitState(SourceState source, Port outlet, int number, int phase, float voltage, float frequency)
    {
        public SourceState Source { get; } = source;
        public Port Outlet { get; } = outlet;
        public int Number { get; } = number;
        public int Phase { get; } = phase;
        public float VoltageV { get; } = voltage;
        public float FrequencyHz { get; } = frequency;
        public List<FeedNode> TopLevel { get; } = new();
        public List<FeedNode> All { get; } = new();

        public PhaseLoad Load()
        {
            var sum = default(PhaseLoad);
            foreach (var n in TopLevel) sum += n.Load;
            return sum;
        }
    }

    private sealed class SourceState(Device device)
    {
        public Device Device { get; } = device;
        public float VoltageV { get; set; }
        public float FrequencyHz { get; set; }
        public int Phases { get; set; } = 1;
        public bool Valid { get; set; } = true;
        public bool IsFed { get; set; }
        public bool Powered { get; set; }
        public bool Expanded { get; set; }
        public List<CircuitState> Circuits { get; } = new();
        public bool IsDc => FrequencyHz <= 0;
    }

    private sealed class FeedNode(Device device, Port input, PowerEdge edge, Supply supply, FeedNode? parent, Port via, int depth)
    {
        public Device Device { get; } = device;
        public Port Input { get; } = input;
        public PowerEdge Edge { get; } = edge;
        public Supply Supply { get; } = supply;
        public FeedNode? Parent { get; } = parent;
        public Port Via { get; } = via;
        public int Depth { get; } = depth;
        public List<FeedNode> Children { get; } = new();
        public PhaseLoad Load { get; set; }
        public double OwnW { get; set; }
        public double InputCurrentA { get; set; }
    }

    private readonly record struct PowerWork(PowerEdge Edge, Supply Supply, FeedNode? Parent, Port Via, int Depth);

    private sealed record PowerResult(IReadOnlyList<PowerSourceLoad> Sources, IReadOnlyDictionary<Guid, PowerFeed> Feeds, IReadOnlyList<Guid> Unpowered);

    /// <summary>
    /// Line-to-neutral voltage of a source. A three-phase source entered with its line-to-line voltage (300 V or more,
    /// e.g. 400 V or 415 V) supplies its single-phase loads with V / √3 (231 V, 240 V); values below 300 V are taken as
    /// line-to-neutral already.
    /// </summary>
    internal static float PhaseVoltage(float voltageV, int phases) =>
        phases == 3 && float.IsFinite(voltageV) && voltageV >= 300f ? voltageV / MathF.Sqrt(3f) : voltageV;

    private sealed partial class SolveRun
    {
        private readonly Dictionary<PortRef, List<PowerEdge>> _powerOut = new(PortRef.Comparer);
        private readonly HashSet<PortRef> _powerIn = new(PortRef.Comparer);
        private readonly Dictionary<Guid, FeedNode> _fed = new();
        private readonly List<FeedNode> _preorder = new();
        private readonly Dictionary<Guid, SourceState> _sources = new();
        private readonly Stack<PowerWork> _work = new();

        private PowerResult SolvePower()
        {
            BuildPowerEdges();

            var sourceOrder = new List<SourceState>();
            foreach (var d in _deviceOrder)
            {
                if (d.Source is not { } src) continue;
                int phases = Math.Clamp(src.Phases, 1, 3);
                var s = new SourceState(d)
                {
                    VoltageV = PhaseVoltage(src.VoltageV, phases),
                    FrequencyHz = float.IsFinite(src.FrequencyHz) ? Math.Max(0f, src.FrequencyHz) : 0f,
                    Phases = phases,
                };
                if (!(float.IsFinite(src.VoltageV) && src.VoltageV > 0))
                {
                    s.Valid = false;
                    Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerSourceInvalid,
                        $"Power source '{d.Info.Name}' has an invalid voltage ({src.VoltageV} V); nothing it feeds is analysed.", d.Id);
                }
                // A distro whose Mains In has a power cable is not a source of its own, even when that cable does
                // not lead to a powered output (it is then reported unpowered instead of silently feeding its loads).
                var mainsIn = new PortRef(d.Id, PortResolver.MainsInPortName);
                s.IsFed = _powerIn.Contains(mainsIn) || (_plugged.TryGetValue(mainsIn, out var plug) && plug.Cable.Type is { IsPower: true });
                _sources[d.Id] = s;
                sourceOrder.Add(s);
            }

            foreach (var s in sourceOrder)
            {
                if (s.IsFed || !s.Valid) continue;
                s.Powered = true;
                ExpandSource(s, queueLoads: true);
                Drain();
            }

            // Distros whose feed has no power (and invalid sources) still list their outlets, with no load.
            foreach (var s in sourceOrder) ExpandSource(s, queueLoads: false);

            ComputeLoads();
            CheckCircuitsAndCables(sourceOrder);
            var unpowered = ReportUnpowered(sourceOrder);
            return BuildPowerResult(sourceOrder, unpowered);
        }

        private void BuildPowerEdges()
        {
            foreach (var cable in _cables)
            {
                if (cable.Type is not { IsPower: true } type) continue;
                var usable = cable.Info.Endpoints.Where(e => e.IsUsable && e.Port!.Signal.CarriesPower()).ToList();
                var ups = usable.Where(e => e.Port!.IsPowerOutput).ToList();
                var downs = usable.Where(e => e.Port!.Role == PortRole.PowerIn).ToList();
                if (ups.Count > 1)
                {
                    Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerBackfeed,
                        $"Power cable '{cable.Label}' connects {ups.Count} power outputs ({Fmt.List(ups.Select(DescribeEnd))}): power would be fed back into an output. The cable is ignored.",
                        [cable.Entity.Id, .. ups.Select(u => u.EntityId)]);
                    continue;
                }
                if (ups.Count == 0 || downs.Count == 0) continue;
                var up = ups[0];
                cable.Info.UpstreamEndpointIndex = up.Index;
                bool multicore = type.Signal == SignalKind.Multicore;
                int circuits = multicore ? Math.Max(1, type.Circuits) : 1;
                if (multicore && downs.Count > circuits)
                    Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerMulticoreCircuits,
                        $"{type.DisplayName} '{cable.Label}' has {downs.Count} tails but only {circuits} circuits; tails beyond {circuits} share circuits.",
                        cable.Entity.Id);
                var key = new PortRef(up.EntityId, up.PortName);
                if (!_powerOut.TryGetValue(key, out var list)) _powerOut[key] = list = new List<PowerEdge>();
                int k = 0;
                foreach (var d in downs)
                {
                    var edge = new PowerEdge(cable, up, d, multicore ? (k % circuits) + 1 : 0);
                    k++;
                    list.Add(edge);
                    cable.PowerEdges.Add(edge);
                    _powerIn.Add(new PortRef(d.EntityId, d.PortName));
                }
            }
        }

        /// <summary>Creates the circuits of a source's outlets and (when powered) queues what is plugged into them.</summary>
        private void ExpandSource(SourceState s, bool queueLoads)
        {
            if (s.Expanded) return;
            s.Expanded = true;
            var items = new List<PowerWork>();
            foreach (var outlet in s.Device.Info.Ports)
            {
                if (outlet.Role != PortRole.PowerOutlet) continue;
                bool threePhaseOutlet = outlet.Connector.Phases == 3 && s.Phases == 3;
                int circuitCount = outlet.Circuits;
                var circuits = new CircuitState[circuitCount];
                for (int c = 1; c <= circuitCount; c++)
                {
                    int phase = OutletPhase(s, outlet, c);
                    circuits[c - 1] = new CircuitState(s, outlet, c, threePhaseOutlet ? 0 : phase, s.VoltageV, s.FrequencyHz);
                    s.Circuits.Add(circuits[c - 1]);
                }
                if (!queueLoads || !_powerOut.TryGetValue(new PortRef(s.Device.Id, outlet.Name), out var edges)) continue;
                foreach (var e in edges)
                {
                    int c = Math.Clamp(e.Circuit, 1, circuitCount);
                    var circuit = circuits[c - 1];
                    int phase = OutletPhase(s, outlet, c);
                    // Only a three-phase cable carries all phases on (a single-phase lead on a CEE 5-pin outlet is one phase).
                    bool threePhase = threePhaseOutlet && e.Cable.Type is { Phases: 3 };
                    var supply = new Supply(s, outlet, circuit, phase, threePhase, s.VoltageV, s.FrequencyHz, s.IsDc, false);
                    items.Add(new PowerWork(e, supply, null, outlet, 0));
                }
            }
            for (int i = items.Count - 1; i >= 0; i--) _work.Push(items[i]);
        }

        private static int OutletPhase(SourceState s, Port outlet, int circuit)
        {
            int basePhase = Math.Clamp(outlet.Phase, 1, s.Phases);
            if (s.Phases == 1) return 1;
            return outlet.Circuits > 1 ? ((basePhase - 1 + circuit - 1) % s.Phases) + 1 : basePhase;
        }

        /// <summary>Depth-first walk from queued outlets through links, distribution outputs and fed distros.</summary>
        private void Drain()
        {
            var items = new List<PowerWork>();
            while (_work.Count > 0)
            {
                var w = _work.Pop();
                var target = _devices[w.Edge.Down.EntityId];
                var input = w.Edge.Down.Port!;
                if (_fed.TryGetValue(target.Id, out var existing))
                {
                    if (!ReferenceEquals(existing.Edge, w.Edge))
                        Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerMultipleFeeds,
                            $"{target.Info.Label} is fed twice: through '{existing.Input.Name}' from {FeedOrigin(existing.Parent, existing.Supply, existing.Via)} and through '{input.Name}' from {FeedOrigin(w.Parent, w.Supply, w.Via)}. Only the first feed is counted.",
                            target.Id, existing.Edge.Cable.Entity.Id, w.Edge.Cable.Entity.Id);
                    continue;
                }
                var node = new FeedNode(target, input, w.Edge, w.Supply, w.Parent, w.Via, w.Depth);
                _fed[target.Id] = node;
                _preorder.Add(node);
                (w.Parent?.Children ?? w.Supply.Circuit.TopLevel).Add(node);
                w.Supply.Circuit.All.Add(node);

                if (target.Info.Profile is { } profile && !profile.AcceptsSupply(w.Supply.VoltageV, w.Supply.FrequencyHz, w.Supply.IsDc, out var accepted))
                    Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerVoltageMismatch,
                        $"{target.LongLabel} is supplied with {Fmt.Supply(w.Supply.VoltageV, w.Supply.FrequencyHz, w.Supply.IsDc)} from {FeedOrigin(w.Parent, w.Supply, w.Via)} but accepts only {accepted}.",
                        target.Id, w.Edge.Cable.Entity.Id);

                if (target.Source is not null && _sources.TryGetValue(target.Id, out var distro))
                {
                    distro.Powered = true;
                    if (distro.Valid)
                    {
                        bool dcMismatch = distro.IsDc != w.Supply.IsDc;
                        if (dcMismatch || MathF.Abs(distro.VoltageV - w.Supply.VoltageV) > PowerNetworkSpec.VoltageTolerance * w.Supply.VoltageV ||
                            (!distro.IsDc && MathF.Abs(distro.FrequencyHz - w.Supply.FrequencyHz) > 1f))
                            Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerVoltageMismatch,
                                $"Distro '{target.Info.Name}' is set to {Fmt.Supply(distro.VoltageV, distro.FrequencyHz, distro.IsDc)} but is fed with {Fmt.Supply(w.Supply.VoltageV, w.Supply.FrequencyHz, w.Supply.IsDc)} from {FeedOrigin(w.Parent, w.Supply, w.Via)}.",
                                target.Id, w.Edge.Cable.Entity.Id);
                        if (distro.Phases == 3 && !w.Supply.ThreePhase)
                            Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerPhaseMismatch,
                                $"Distro '{target.Info.Name}' is three-phase but is fed from single-phase {FeedOrigin(w.Parent, w.Supply, w.Via)}: its outlets on the other phases have no supply, and everything it feeds is counted on L{w.Supply.Phase}.",
                                target.Id, w.Edge.Cable.Entity.Id);
                        ExpandSource(distro, queueLoads: true);
                    }
                    continue;
                }
                if (target.Fixture is null) continue;

                var dist = target.Info.Profile?.Distribution;
                items.Clear();
                foreach (var p in target.Info.Ports)
                {
                    if (p.Role is not (PortRole.PowerLink or PortRole.PowerDistribution)) continue;
                    if (!_powerOut.TryGetValue(new PortRef(target.Id, p.Name), out var edges)) continue;
                    var supply = w.Supply;
                    if (p.Role == PortRole.PowerDistribution && dist is not null)
                        supply = supply with
                        {
                            VoltageV = dist.OutputVoltageDcV ?? supply.VoltageV,
                            FrequencyHz = dist.OutputIsDc ? 0f : supply.FrequencyHz,
                            IsDc = dist.OutputIsDc || supply.IsDc,
                            Dimmed = dist.OutputIsDimmed,
                            ThreePhase = false,
                        };
                    else if (supply.ThreePhase)
                        supply = supply with { ThreePhase = false };
                    foreach (var e in edges) items.Add(new PowerWork(e, supply, node, p, w.Depth + 1));
                }
                for (int i = items.Count - 1; i >= 0; i--) _work.Push(items[i]);
            }
        }

        /// <summary>Post-order load accumulation: own draw + links + distributed loads (× losses), then port checks.</summary>
        private void ComputeLoads()
        {
            var groups = new Dictionary<Port, PhaseLoad>(ReferenceEqualityComparer.Instance);
            for (int i = _preorder.Count - 1; i >= 0; i--)
            {
                var node = _preorder[i];
                var device = node.Device;
                var supply = node.Supply;

                if (device.Source is not null && _sources.TryGetValue(device.Id, out var distro))
                {
                    var total = default(PhaseLoad);
                    foreach (var c in distro.Circuits) total += c.Load();
                    if (!(supply.ThreePhase && distro.Phases == 3)) total = total.Collapse(supply.Phase);
                    node.Load = total;
                    node.InputCurrentA = Current(total, supply.VoltageV);
                    continue;
                }

                var profile = device.Info.Profile;
                double own = profile?.PowerW ?? 0;
                float pf = profile?.PowerFactor is > 0f and <= 1f ? profile.PowerFactor : FixtureProfile.DefaultPowerFactor;
                var load = PhaseLoad.Single(supply.Phase, own, supply.IsDc ? own : own / pf);

                groups.Clear();
                foreach (var child in node.Children)
                    groups[child.Via] = groups.GetValueOrDefault(child.Via) + child.Load;

                double distW = 0, distVa = 0, distCurrent = 0;
                bool dcOut = false;
                var dist = profile?.Distribution;
                foreach (var (port, sum) in groups)
                {
                    if (port.Role == PortRole.PowerLink)
                    {
                        load += sum;
                        double amps = Current(sum, supply.VoltageV);
                        float? limit = port.MaxThroughCurrentA ?? port.Connector.RatedCurrentA;
                        if (port.MaxPayloadW is { } payload && supply.VoltageV > 0) limit = Math.Min(limit ?? float.MaxValue, payload / supply.VoltageV);
                        if (limit is { } lim && amps > lim + 1e-6)
                        {
                            var behind = node.Children.Where(c => ReferenceEquals(c.Via, port)).SelectMany(Subtree).ToList();
                            Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerLinkOverload,
                                $"Link output '{port.Name}' ({port.Connector.DisplayName}) of {device.Info.Label} carries {Fmt.A(amps, lim)} ({Fmt.W(sum.W)} for {Fmt.Count(behind.Count, "linked device", "linked devices")}: {Fmt.List(behind.Select(n => n.Device.Info.Label))}) but is rated {Fmt.A(lim)}.",
                                [device.Id, .. behind.Select(n => n.Device.Id)]);
                        }
                    }
                    else
                    {
                        var childSupply = node.Children.First(c => ReferenceEquals(c.Via, port)).Supply;
                        dcOut |= childSupply.IsDc;
                        double amps = Current(sum, childSupply.VoltageV);
                        distW += sum.W;
                        distVa += sum.VaTotal;
                        distCurrent += amps;
                        if (port.MaxThroughCurrentA is { } chLimit && amps > chLimit + 1e-6)
                            Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerChannelOverload,
                                $"Output '{port.Name}' of {device.LongLabel} carries {Fmt.A(amps, chLimit)} ({Fmt.W(sum.W)}) but each channel is rated {Fmt.A(chLimit)}.",
                                [device.Id, .. node.Children.Where(c => ReferenceEquals(c.Via, port)).Select(c => c.Device.Id)]);
                    }
                }

                if (distW > 0 || distVa > 0)
                {
                    double loss = dist?.LossFactor ?? 1.0;
                    double inW = distW * loss;
                    double inVa = dcOut ? inW / pf : distVa * loss;
                    load += PhaseLoad.Single(supply.Phase, inW, inVa);
                    if (dist?.MaxLoadW is { } maxW && distW > maxW + 1e-6)
                        Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerDistributionOverload,
                            $"{device.LongLabel} delivers {Fmt.W(distW, maxW)} to its loads but is rated {Fmt.W(maxW)}.", device.Id);
                    if (dist?.MaxLoadCurrentA is { } maxA && distCurrent > maxA + 1e-6)
                        Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerDistributionOverload,
                            $"The outputs of {device.LongLabel} carry {Fmt.A(distCurrent, maxA)} in total but are rated {Fmt.A(maxA)}.", device.Id);
                }

                node.OwnW = own;
                node.Load = load;
                node.InputCurrentA = Current(load, supply.VoltageV);

                float? inletLimit = node.Input.MaxThroughCurrentA ?? node.Input.Connector.RatedCurrentA;
                if (inletLimit is { } inLim && node.InputCurrentA > inLim + 1e-6)
                    Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerInletOverload,
                        $"Power inlet '{node.Input.Name}' ({node.Input.Connector.DisplayName}) of {device.Info.Label} carries {Fmt.A(node.InputCurrentA, inLim)} ({Fmt.W(load.W)} including {Fmt.Count(Subtree(node).Count() - 1, "device", "devices")} behind it) but is rated {Fmt.A(inLim)}.",
                        device.Id);

                if (profile is not null && _owner.Options.ReportDimmedElectronicLoads && supply.Dimmed && own > 0 && !IsResistive(profile.LightSourceType))
                    Add(IssueSeverity.Note, IssueCategory.Power, IssueCodes.PowerDimmedElectronicLoad,
                        $"{device.LongLabel} has an electronic power supply but hangs on dimmed output '{node.Via.Name}' of {node.Parent?.Device.Info.Label}; set that channel to switch (non-dim) mode.",
                        device.Id);
                if (profile is not null && _owner.Options.ReportEstimatedLoads)
                {
                    if (profile.PowerSource == PowerDataSource.GdtfBeams)
                        Add(IssueSeverity.Note, IssueCategory.Power, IssueCodes.PowerEstimatedLoad,
                            $"The power draw of {device.LongLabel} ({Fmt.W(own)}) is estimated from its GDTF beam power.", device.Id);
                    else if (profile.PowerW is null && profile.NeedsPower)
                        Add(IssueSeverity.Note, IssueCategory.Power, IssueCodes.PowerEstimatedLoad,
                            $"The power draw of {device.LongLabel} is unknown and counted as 0 W.", device.Id);
                }
            }
        }

        private void CheckCircuitsAndCables(List<SourceState> sources)
        {
            foreach (var s in sources)
            {
                foreach (var c in s.Circuits)
                {
                    var load = c.Load();
                    double amps = Current(load, c.VoltageV);
                    if (c.Outlet.BreakerA is { } breaker && amps > breaker + 1e-6)
                    {
                        string where = c.Outlet.Circuits > 1 ? $"Circuit {c.Number} of outlet '{c.Outlet.Name}'" : $"Outlet '{c.Outlet.Name}'";
                        Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerBreakerOverload,
                            $"{where} of '{s.Device.Info.Name}' is overloaded: {Fmt.Count(c.All.Count, "device draws", "devices draw")} {Fmt.W(load.W)} = {Fmt.A(amps, breaker)} on a {Fmt.A(breaker)} breaker ({Fmt.Pct(100 * amps / breaker)}): {Fmt.List(c.All.Select(n => n.Device.Info.Label))}.",
                            [s.Device.Id, .. c.All.Select(n => n.Device.Id)]);
                    }
                }
                if (s.Phases == 3 && s.Powered)
                {
                    var total = default(PhaseLoad);
                    foreach (var c in s.Circuits) total += c.Load();
                    var currents = total.Currents(s.VoltageV);
                    double max = currents.Max(), min = currents.Min();
                    if (max >= _owner.Options.PhaseImbalanceMinCurrentA && (max - min) / max > _owner.Options.PhaseImbalanceRatio)
                        Add(IssueSeverity.Note, IssueCategory.Power, IssueCodes.PowerPhaseImbalance,
                            $"The phases of '{s.Device.Info.Name}' are unevenly loaded: L1 {Fmt.A(currents[0])}, L2 {Fmt.A(currents[1])}, L3 {Fmt.A(currents[2])}.",
                            s.Device.Id);
                }
            }

            foreach (var cable in _cables)
            {
                if (cable.PowerEdges.Count == 0 || cable.Type is null) continue;
                double peak = 0, watts = 0;
                bool any = false;
                foreach (var group in cable.PowerEdges.GroupBy(e => e.Circuit))
                {
                    var sum = default(PhaseLoad);
                    float voltage = 0;
                    foreach (var e in group)
                    {
                        if (!_fed.TryGetValue(e.Down.EntityId, out var node) || !ReferenceEquals(node.Edge, e)) continue;
                        sum += node.Load;
                        voltage = node.Supply.VoltageV;
                        any = true;
                    }
                    peak = Math.Max(peak, Current(sum, voltage));
                    watts += sum.W;
                }
                if (!any) continue;
                cable.Info.PeakCurrentA = peak;
                cable.Info.LoadW = watts;
                if (cable.Type.RatedCurrentA is { } rated && peak > rated + 1e-6)
                {
                    var behind = cable.PowerEdges
                        .Where(e => _fed.TryGetValue(e.Down.EntityId, out var n) && ReferenceEquals(n.Edge, e))
                        .SelectMany(e => Subtree(_fed[e.Down.EntityId]))
                        .Select(n => n.Device)
                        .ToList();
                    Add(IssueSeverity.Error, IssueCategory.Power, IssueCodes.PowerCableOverload,
                        $"{cable.Type.DisplayName} '{cable.Label}' carries {Fmt.A(peak, rated)} ({Fmt.W(watts)}) but is rated {Fmt.A(rated)}; it feeds {Fmt.List(behind.Select(d => d.Info.Label))}.",
                        [cable.Entity.Id, .. behind.Select(d => d.Id)]);
                }
            }
        }

        private List<Guid> ReportUnpowered(List<SourceState> sources)
        {
            var unpowered = new List<Guid>();
            foreach (var d in _deviceOrder)
            {
                if (_fed.ContainsKey(d.Id)) continue;
                if (d.Fixture is not null)
                {
                    if (d.Info.Profile is not { NeedsPower: true } profile) continue;
                    unpowered.Add(d.Id);
                    var inlets = d.Info.Ports.Where(p => p.Role == PortRole.PowerIn).ToList();
                    var plugged = inlets.FirstOrDefault(p => _plugged.ContainsKey(new PortRef(d.Id, p.Name)));
                    string why = inlets.Count == 0 ? "no power inlet is known for it"
                        : plugged is null ? $"nothing is plugged into '{inlets[0].Name}'"
                        : $"the cable on '{plugged.Name}' does not lead back to a power source";
                    string draw = profile.PowerW is { } w ? $", {Fmt.W(w)}" : "";
                    Add(IssueSeverity.Warning, IssueCategory.Power, IssueCodes.PowerUnpowered,
                        $"{d.Info.Label} ({profile.DisplayName}{draw}) has no power: {why}.", d.Id);
                }
                else if (_sources.TryGetValue(d.Id, out var s) && s.IsFed && !s.Powered)
                {
                    unpowered.Add(d.Id);
                    Add(IssueSeverity.Warning, IssueCategory.Power, IssueCodes.PowerUnpowered,
                        $"Distro '{d.Info.Name}' is fed through '{PortResolver.MainsInPortName}' but that feed does not lead back to a powered source.", d.Id);
                }
            }
            return unpowered;
        }

        private PowerResult BuildPowerResult(List<SourceState> sources, List<Guid> unpowered)
        {
            var feeds = new Dictionary<Guid, PowerFeed>(_fed.Count);
            var feedByNode = new Dictionary<FeedNode, PowerFeed>(ReferenceEqualityComparer.Instance);
            foreach (var n in _preorder)
            {
                var f = new PowerFeed
                {
                    DeviceEntityId = n.Device.Id,
                    InputPort = n.Input.Name,
                    SourceEntityId = n.Supply.Source.Device.Id,
                    OutletPort = n.Supply.Outlet.Name,
                    Circuit = n.Supply.Circuit.Number,
                    Phase = n.Supply.Phase,
                    VoltageV = n.Supply.VoltageV,
                    FrequencyHz = n.Supply.IsDc ? 0f : n.Supply.FrequencyHz,
                    IsDc = n.Supply.IsDc,
                    Dimmed = n.Supply.Dimmed,
                    UpstreamEntityId = n.Parent?.Device.Id,
                    UpstreamPort = n.Via.Name,
                    CableEntityId = n.Edge.Cable.Entity.Id,
                    Depth = n.Depth,
                    OwnW = n.OwnW,
                    TotalW = n.Load.W,
                    InputCurrentA = n.InputCurrentA,
                };
                feeds[n.Device.Id] = f;
                feedByNode[n] = f;
            }

            var result = new List<PowerSourceLoad>(sources.Count);
            foreach (var s in sources)
            {
                var circuits = new List<PowerCircuit>(s.Circuits.Count);
                var total = default(PhaseLoad);
                foreach (var c in s.Circuits)
                {
                    var load = c.Load();
                    total += load;
                    circuits.Add(new PowerCircuit
                    {
                        SourceEntityId = s.Device.Id,
                        SourceName = s.Device.Info.Name,
                        Outlet = c.Outlet,
                        Circuit = c.Number,
                        Phase = c.Phase,
                        VoltageV = c.VoltageV,
                        FrequencyHz = c.FrequencyHz,
                        LoadW = load.W,
                        ApparentPowerVA = load.VaTotal,
                        PhaseCurrentsA = load.Currents(c.VoltageV),
                        Feeds = c.All.Select(n => feedByNode[n]).ToList(),
                    });
                }
                result.Add(new PowerSourceLoad
                {
                    SourceEntityId = s.Device.Id,
                    Name = s.Device.Info.Name,
                    VoltageV = s.VoltageV,
                    FrequencyHz = s.FrequencyHz,
                    Phases = s.Phases,
                    IsFed = s.IsFed,
                    IsPowered = s.Powered,
                    TotalW = total.W,
                    PhaseCurrentsA = total.Currents(s.VoltageV),
                    Circuits = circuits,
                });
            }
            return new PowerResult(result, feeds, unpowered);
        }

        private string FeedOrigin(FeedNode? parent, Supply supply, Port via) =>
            parent is null ? $"'{supply.Source.Device.Info.Name}' {via.Name}" : $"{parent.Device.Info.Label} {via.Name}";

        private static IEnumerable<FeedNode> Subtree(FeedNode root)
        {
            var stack = new Stack<FeedNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }

        private static double Current(PhaseLoad load, float voltage) => voltage > 0 ? load.MaxVa / voltage : 0;

        private static bool IsResistive(string? lightSource) =>
            lightSource is not null && (lightSource.Contains("Tungsten", StringComparison.OrdinalIgnoreCase) ||
                                        lightSource.Contains("Halogen", StringComparison.OrdinalIgnoreCase));
    }
}
