using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical;

/// <summary>Tunable limits of the network checks.</summary>
public sealed record NetworkSolverOptions
{
    /// <summary>Devices allowed on one DMX segment (console output or splitter output) before a warning (EIA-485: 32 unit loads).</summary>
    public int MaxDmxDevicesPerSegment { get; init; } = 32;

    /// <summary>Phase imbalance reported when (max − min) / max of the phase currents exceeds this fraction.</summary>
    public double PhaseImbalanceRatio { get; init; } = 0.2;

    /// <summary>Phase imbalance is only reported when the heaviest phase carries at least this current (A).</summary>
    public double PhaseImbalanceMinCurrentA { get; init; } = 2.0;

    /// <summary>Report notes for loads estimated from GDTF beam power or unknown.</summary>
    public bool ReportEstimatedLoads { get; init; } = true;

    /// <summary>Report notes for LED/discharge fixtures on dimmed channels.</summary>
    public bool ReportDimmedElectronicLoads { get; init; } = true;
}

/// <summary>
/// Solves the scene's cabling: resolves every <see cref="CableComponent"/> against device ports, follows data from
/// each console output through cables and daisy chains (which fixtures a console drives, in which order, on which
/// universe), follows power from each source outlet through links, dimmers, PSUs and fed distros, sums loads without
/// double counting and checks breakers, ports, cables, voltages, connectors and DMX topology. Linear in the number of
/// devices, ports and cable endpoints. The scene is only read; call from the thread that owns the scene.
/// </summary>
public sealed partial class NetworkSolver
{
    private NetworkSolution? _last;

    /// <summary>Creates a solver.</summary>
    /// <param name="fixtures">Fixture data (ports, power, weight); null leaves fixtures without ports.</param>
    /// <param name="catalog">Cable types; null = <see cref="CableCatalog.Default"/>.</param>
    /// <param name="options">Check limits; null = defaults.</param>
    public NetworkSolver(IFixtureProfileProvider? fixtures, CableCatalog? catalog = null, NetworkSolverOptions? options = null)
    {
        Ports = new PortResolver(fixtures);
        Catalog = catalog ?? CableCatalog.Default;
        Options = options ?? new NetworkSolverOptions();
    }

    /// <summary>Port resolver used for devices.</summary>
    public PortResolver Ports { get; }

    /// <summary>Cable catalogue.</summary>
    public CableCatalog Catalog { get; }

    /// <summary>Check limits.</summary>
    public NetworkSolverOptions Options { get; }

    /// <summary>Solves the scene now.</summary>
    public NetworkSolution Solve(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var solution = new SolveRun(this, scene).Run();
        Volatile.Write(ref _last, solution);
        return solution;
    }

    /// <summary>
    /// The last solution when it was computed for this scene at its current <c>Scene.Version</c>, otherwise a new one.
    /// Cheap to call every frame (e.g. to lock the controls of console-driven fixtures); edits made without
    /// <c>Component.NotifyChanged()</c> do not change the version and are not seen until <see cref="Solve"/> is called.
    /// </summary>
    public NetworkSolution GetOrSolve(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var last = Volatile.Read(ref _last);
        return last is not null && last.SceneId == scene.Id && last.SceneVersion == scene.Version ? last : Solve(scene);
    }

    /// <summary>
    /// <paramref name="solution"/> with the geometry of some cables computed again: their nodes moved with the entities
    /// they are anchored to, or the cable entity itself moved, while nothing else about the cabling changed (no cable,
    /// device, port or patch was added, removed or edited, no entity was reparented). Only those cables' geometry —
    /// node positions, segment, routed and cut lengths, weight — and what depends on it are recomputed: their geometry
    /// problems, the Ethernet link length checks and, when one of them is a data cable, the data lines (run lengths
    /// and <c>data.run-too-long</c>). Devices, ports, plugs and the power network are taken over unchanged. The result
    /// equals a fresh <see cref="Solve"/> of the scene as it is now (same cables, feeds and issues in the same order)
    /// and carries the current <c>Scene.Version</c>; the old solution is not changed.
    /// </summary>
    /// <remarks>
    /// Falls back to a full <see cref="Solve"/> when the solution was not made by this solver, belongs to another
    /// scene, or a named cable is no longer the one it solved (its entity or component was replaced or added since).
    /// Ids of entities that are not cables are ignored. Linear in the named cables' nodes and segments, plus the data
    /// network when a data cable is among them.
    /// </remarks>
    /// <param name="solution">A solution of <paramref name="scene"/> by this solver.</param>
    /// <param name="scene">The scene, after the move.</param>
    /// <param name="cableEntityIds">Entities carrying the cables whose geometry changed.</param>
    public NetworkSolution UpdateGeometry(NetworkSolution solution, Scene scene, IEnumerable<Guid> cableEntityIds)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(cableEntityIds);
        if (solution.State is not SolveState state || !ReferenceEquals(state.Owner, this) || solution.SceneId != scene.Id) return Solve(scene);

        var indices = new SortedSet<int>();
        foreach (var id in cableEntityIds)
        {
            var entity = scene.Find(id);
            if (state.CableIndex.TryGetValue(id, out int first))
            {
                if (entity is null) return Solve(scene);
                // the same entity with the same cable components (those of one entity are consecutive), still in the
                // scene: only their geometry changed
                int count = 0;
                foreach (var c in entity.Components)
                {
                    if (c is not CableComponent component) continue;
                    int i = first + count++;
                    if (i >= state.Cables.Count || !ReferenceEquals(state.Cables[i].Entity, entity) || !ReferenceEquals(state.Cables[i].Component, component))
                        return Solve(scene);
                    indices.Add(i);
                }
                if (count == 0 || (first + count < state.Cables.Count && ReferenceEquals(state.Cables[first + count].Entity, entity))) return Solve(scene);
            }
            else if (entity?.Get<CableComponent>() is not null)
            {
                return Solve(scene); // a cable the solution does not know
            }
        }
        if (indices.Count == 0) return solution;

        var updated = new SolveRun(this, scene, state).UpdateGeometry(indices);
        Volatile.Write(ref _last, updated);
        return updated;
    }

    /// <summary>
    /// What a solve keeps for <see cref="UpdateGeometry"/>: the devices, the cables with their plugs and the results and
    /// issues of every phase, so a geometry change recomputes only what depends on cable geometry. Never changed after
    /// the solve; an update builds a new state that shares the unchanged parts.
    /// </summary>
    private sealed class SolveState
    {
        public required NetworkSolver Owner { get; init; }
        public required Dictionary<Guid, Device> Devices { get; init; }
        public required List<Device> DeviceOrder { get; init; }
        public required List<Issue> DeviceIssues { get; init; }
        public required List<CableState> Cables { get; init; }
        public required Dictionary<Guid, int> CableIndex { get; init; }
        public required Dictionary<PortRef, (CableState Cable, int Position)> Plugged { get; init; }
        public required DataResult Data { get; init; }
        public required List<Issue> DataIssues { get; init; }
        public required PowerResult Power { get; init; }
        public required List<Issue> PowerIssues { get; init; }
    }

    /// <summary>Per-solve state.</summary>
    private sealed partial class SolveRun
    {
        private readonly NetworkSolver _owner;
        private readonly Scene _scene;
        private readonly Dictionary<Guid, Device> _devices;
        private readonly List<Device> _deviceOrder;
        private readonly List<(Entity Entity, CableComponent Component)> _cableComponents = new();
        private readonly List<CableState> _cables;
        private readonly Dictionary<PortRef, (CableState Cable, int Position)> _plugged;
        private readonly SolveState? _previous;
        private readonly List<Issue> _deviceIssues;
        private List<Issue> _dataIssues = new();
        private List<Issue> _powerIssues = new();

        /// <summary>Where <see cref="Add"/> writes: the list of the phase (or cable) being solved.</summary>
        private List<Issue> _sink;

        public SolveRun(NetworkSolver owner, Scene scene)
        {
            _owner = owner;
            _scene = scene;
            _devices = new Dictionary<Guid, Device>();
            _deviceOrder = new List<Device>();
            _cables = new List<CableState>();
            _plugged = new Dictionary<PortRef, (CableState, int)>(PortRef.Comparer);
            _deviceIssues = new List<Issue>();
            _sink = _deviceIssues;
        }

        /// <summary>A run that continues <paramref name="previous"/> (see <see cref="UpdateGeometry"/>).</summary>
        public SolveRun(NetworkSolver owner, Scene scene, SolveState previous)
        {
            _owner = owner;
            _scene = scene;
            _previous = previous;
            _devices = previous.Devices;
            _deviceOrder = previous.DeviceOrder;
            _deviceIssues = previous.DeviceIssues;
            _cables = new List<CableState>(previous.Cables);
            _plugged = previous.Plugged;
            _sink = _deviceIssues;
        }

        public NetworkSolution Run()
        {
            _sink = _deviceIssues;
            CollectDevices();
            ResolveCables();
            _sink = _dataIssues;
            var data = SolveData(_plugged);
            _sink = _powerIssues;
            var power = SolvePower();
            return Build(data, power, _plugged);
        }

        /// <summary>
        /// Recomputes the geometry of the cables at <paramref name="indices"/> (positions in the cable list) and what
        /// depends on it; everything else is taken over from the previous solve.
        /// </summary>
        public NetworkSolution UpdateGeometry(IEnumerable<int> indices)
        {
            var previous = _previous!;
            bool data = false;
            var replaced = new Dictionary<CableState, CableState>(ReferenceEqualityComparer.Instance);
            foreach (int i in indices)
            {
                var old = _cables[i];
                var state = new CableState(old.Entity, old.Component, old.Type, CableGeometry.Compute(_scene, old.Entity, old.Component), old.Label, old.Category)
                {
                    HeadIssues = old.HeadIssues,
                    TailIssues = old.TailIssues,
                };
                _sink = state.GeometryIssues;
                ReportGeometry(state);
                state.Info = new CableInfo(old.Info.EntityId, old.Info.Name, old.Info.Label, old.Info.TypeId, old.Type, state.Geometry, old.Info.Endpoints)
                {
                    UpstreamEndpointIndex = old.Info.UpstreamEndpointIndex,
                    PeakCurrentA = old.Info.PeakCurrentA,
                    LoadW = old.Info.LoadW,
                };
                _sink = state.LengthIssues;
                ReportLength(state);
                _cables[i] = state;
                replaced[old] = state;
                data |= old.Type is { IsData: true };
            }

            var plugged = new Dictionary<PortRef, (CableState Cable, int Position)>(previous.Plugged.Count, PortRef.Comparer);
            foreach (var (key, value) in previous.Plugged)
                plugged[key] = replaced.TryGetValue(value.Cable, out var now) ? (now, value.Position) : value;

            var dataResult = previous.Data;
            _dataIssues = previous.DataIssues;
            if (data)
            {
                // run lengths add up along the lines: the data walk runs again over the new geometry (cheap: no port or
                // profile is resolved again)
                _dataIssues = new List<Issue>();
                _sink = _dataIssues;
                dataResult = SolveData(plugged);
            }
            _powerIssues = previous.PowerIssues;
            return Build(dataResult, previous.Power, plugged);
        }

        private NetworkSolution Build(DataResult data, PowerResult power, Dictionary<PortRef, (CableState Cable, int Position)> pluggedStates)
        {
            var devices = new Dictionary<Guid, DeviceInfo>(_devices.Count);
            foreach (var d in _deviceOrder) devices[d.Id] = d.Info;
            var plugged = new Dictionary<PortRef, (CableInfo, int)>(pluggedStates.Count, PortRef.Comparer);
            foreach (var (key, value) in pluggedStates) plugged[key] = (value.Cable.Info, value.Cable.Info.Endpoints[value.Position].Index);

            int count = _deviceIssues.Count + _dataIssues.Count + _powerIssues.Count;
            foreach (var c in _cables) count += c.HeadIssues.Count + c.GeometryIssues.Count + c.TailIssues.Count + c.LengthIssues.Count;
            var issues = new List<Issue>(count);
            issues.AddRange(_deviceIssues);
            foreach (var c in _cables)
            {
                issues.AddRange(c.HeadIssues);
                issues.AddRange(c.GeometryIssues);
                issues.AddRange(c.TailIssues);
                issues.AddRange(c.LengthIssues);
            }
            issues.AddRange(_dataIssues);
            issues.AddRange(_powerIssues);

            var cableIndex = new Dictionary<Guid, int>(_cables.Count);
            for (int i = 0; i < _cables.Count; i++) cableIndex.TryAdd(_cables[i].Entity.Id, i); // the first cable component of an entity wins
            var state = new SolveState
            {
                Owner = _owner,
                Devices = _devices,
                DeviceOrder = _deviceOrder,
                DeviceIssues = _deviceIssues,
                Cables = _cables,
                CableIndex = cableIndex,
                Plugged = pluggedStates,
                Data = data,
                DataIssues = _dataIssues,
                Power = power,
                PowerIssues = _powerIssues,
            };
            return new NetworkSolution(_scene.Id, _scene.Version, devices, _cables.Select(c => c.Info).ToList(), plugged,
                data.Lines, data.FeedsByDevice, data.ConsoleDriven, power.Sources, power.Feeds, power.Unpowered, issues)
            {
                State = state,
            };
        }

        // ------------------------------------------------------------------ devices

        private void CollectDevices()
        {
            foreach (var e in _scene.All())
            {
                FixtureComponent? fixture = null;
                ConsoleComponent? console = null;
                PowerSourceComponent? source = null;
                foreach (var c in e.Components)
                {
                    switch (c)
                    {
                        case FixtureComponent f: fixture ??= f; break;
                        case ConsoleComponent k: console ??= k; break;
                        case PowerSourceComponent p: source ??= p; break;
                        case CableComponent cable: _cableComponents.Add((e, cable)); break;
                    }
                }
                if (fixture is null && console is null && source is null) continue;

                FixtureProfile? profile = null;
                if (fixture is not null)
                {
                    profile = _owner.Ports.GetProfile(fixture);
                    if (profile is null && _owner.Ports.Fixtures is not null && !string.IsNullOrWhiteSpace(fixture.GdtfSpec))
                        Add(IssueSeverity.Note, IssueCategory.Fixture, IssueCodes.FixtureUnknownType,
                            $"{Label(e, fixture)} uses GDTF '{fixture.GdtfSpec}', which is not in the fixture library: its ports, power draw and weight are unknown.",
                            e.Id);
                    else if (profile is not null)
                        foreach (var note in profile.Notes)
                            Add(IssueSeverity.Note, IssueCategory.Fixture, IssueCodes.FixtureDataIncomplete, $"{Label(e, fixture)}: {note}", e.Id);
                }
                var kind = fixture is not null ? DeviceKind.Fixture : console is not null ? DeviceKind.Console : DeviceKind.PowerSource;
                var info = new DeviceInfo
                {
                    EntityId = e.Id,
                    Name = e.Name,
                    Kind = kind,
                    FixtureId = fixture?.FixtureId ?? 0,
                    Profile = profile,
                    Ports = _owner.Ports.GetPorts(e),
                    Addresses = fixture is null ? Array.Empty<DmxAddress>() : fixture.Addresses.ToArray(),
                };
                var device = new Device(e, info, fixture, console, source);
                _devices[e.Id] = device;
                _deviceOrder.Add(device);
            }
        }

        // ------------------------------------------------------------------ cables

        private void ResolveCables()
        {
            foreach (var (entity, component) in _cableComponents)
            {
                var type = _owner.Catalog.Find(component.CableTypeId);
                var geometry = CableGeometry.Compute(_scene, entity, component);
                string label = string.IsNullOrWhiteSpace(component.Label) ? entity.Name : component.Label!;
                string category = type is null ? IssueCategory.Scene : type.IsPower ? IssueCategory.Power : IssueCategory.Data;
                var state = new CableState(entity, component, type, geometry, label, category);
                _sink = state.HeadIssues;
                if (type is null)
                    Add(IssueSeverity.Warning, IssueCategory.Scene, IssueCodes.CableUnknownType,
                        $"Cable '{label}' has the unknown type '{component.CableTypeId}'; it is left out of the power and data checks.", entity.Id);
                _sink = state.GeometryIssues;
                ReportGeometry(state);

                _sink = state.TailIssues;
                var endpoints = new List<CableEndpointInfo>(component.Endpoints.Count);
                for (int i = 0; i < component.Endpoints.Count; i++)
                {
                    var ep = component.Endpoints[i];
                    if (ep is null) continue;
                    var info = ResolveEndpoint(state, ep, i, category);
                    if (info.Port is { } port)
                    {
                        var key = new PortRef(info.EntityId, port.Name);
                        if (_plugged.TryGetValue(key, out var other))
                        {
                            var otherLabel = other.Cable.Label;
                            Add(IssueSeverity.Error, category, IssueCodes.CablePortOccupied,
                                $"Port '{port.Name}' of {_devices[info.EntityId].Info.Label} has two plugs: cable '{otherLabel}' and cable '{label}'.",
                                info.EntityId, entity.Id, other.Cable.Entity.Id);
                            info = info with { Check = new ConnectorCheck(ConnectorMatch.Incompatible, "port already occupied") };
                        }
                        else
                        {
                            _plugged[key] = (state, endpoints.Count);
                        }
                    }
                    endpoints.Add(info);
                }
                state.Info = new CableInfo(entity.Id, entity.Name, component.Label, component.CableTypeId, type, geometry, endpoints);
                _cables.Add(state);

                if (type is { IsData: true })
                {
                    var usable = endpoints.Where(e => e.IsUsable).ToList();
                    if (usable.Count > 2)
                    {
                        string what = type.Signal == SignalKind.Ethernet
                            ? "Ethernet cannot be split passively; use a switch"
                            : "every DMX branch needs its own opto-splitter output";
                        Add(IssueSeverity.Warning, IssueCategory.Data, IssueCodes.DataYSplit,
                            $"{type.DisplayName} '{label}' branches to {usable.Count} devices ({Fmt.List(usable.Select(DescribeEnd))}) without a splitter; {what}.",
                            [entity.Id, .. usable.Select(e => e.EntityId).Distinct()]);
                    }
                }
                _sink = state.LengthIssues;
                ReportLength(state);
            }
        }

        /// <summary>Problems of a cable's node and segment data (anchor missing, invalid positions, loops).</summary>
        private void ReportGeometry(CableState cable)
        {
            foreach (var p in cable.Geometry.Problems)
                Add(IssueSeverity.Warning, cable.Category,
                    p.Kind == CableGeometryProblemKind.AnchorMissing ? IssueCodes.CableAnchorMissing : IssueCodes.CableInvalidGeometry,
                    $"Cable '{cable.Label}': {p.Message}.", cable.Entity.Id);
        }

        /// <summary>Checks of a single cable's length (an Ethernet link is limited to its type's maximum run).</summary>
        private void ReportLength(CableState cable)
        {
            if (cable.Type is { IsData: true, Signal: SignalKind.Ethernet, MaxRunM: { } max } && cable.Geometry.LengthM > max)
                Add(IssueSeverity.Warning, IssueCategory.Data, IssueCodes.DataRunTooLong,
                    $"Ethernet cable '{cable.Label}' is {Fmt.M(cable.Geometry.LengthM)} long; an Ethernet link is limited to {Fmt.M(max)}.", cable.Entity.Id);
        }

        private CableEndpointInfo ResolveEndpoint(CableState cable, CableEndpoint ep, int index, string category)
        {
            bool nodeExists = cable.Geometry.HasNode(ep.NodeId);
            var entity = _scene.Find(ep.EntityId);
            var device = _devices.GetValueOrDefault(ep.EntityId);
            var port = device?.Info.FindPort(ep.PortName);
            ConnectorCheck? check = cable.Type is not null && port is not null ? ConnectorCompatibility.Check(cable.Type, port) : null;
            var cableId = cable.Entity.Id;
            string portName = ep.PortName ?? "";

            if (entity is null)
                Add(IssueSeverity.Warning, category, IssueCodes.CableEndpointMissing,
                    $"Cable '{cable.Label}' end {index + 1} is plugged into an object that no longer exists.", cableId);
            else if (port is null)
            {
                string available = device is null || device.Info.Ports.Count == 0
                    ? "it has no ports"
                    : "its ports are " + Fmt.List(device.Info.Ports.Select(p => $"'{p.Name}'"), 8);
                Add(IssueSeverity.Warning, category, IssueCodes.CableUnknownPort,
                    $"Cable '{cable.Label}' is plugged into port '{portName}' of '{entity.Name}', which has no such port ({available}).",
                    cableId, entity.Id);
            }
            if (!nodeExists)
                Add(IssueSeverity.Warning, category, IssueCodes.CableEndpointNodeMissing,
                    $"Cable '{cable.Label}' end {index + 1} sits on a node the cable does not have.", cableId);
            if (check is { } c && device is not null && port is not null && cable.Type is not null)
            {
                if (c.Match == ConnectorMatch.Incompatible)
                    Add(IssueSeverity.Error, category, IssueCodes.CableWrongConnector,
                        $"{cable.Type.DisplayName} '{cable.Label}' cannot be plugged into '{port.Name}' ({port.Connector.DisplayName}) of {device.Info.Label}: {c.Reason}.",
                        cableId, device.Id);
                else if (c.Match == ConnectorMatch.NeedsAdapter)
                    Add(IssueSeverity.Warning, category, IssueCodes.CableAdapterNeeded,
                        $"{cable.Type.DisplayName} '{cable.Label}' on '{port.Name}' ({port.Connector.DisplayName}) of {device.Info.Label}: {c.Reason}.",
                        cableId, device.Id);
            }
            return new CableEndpointInfo
            {
                Index = index,
                NodeId = ep.NodeId,
                EntityId = ep.EntityId,
                PortName = port?.Name ?? portName,
                EntityName = entity?.Name,
                Port = port,
                Check = check,
                NodeExists = nodeExists,
            };
        }

        private string DescribeEnd(CableEndpointInfo e) =>
            _devices.TryGetValue(e.EntityId, out var d) ? $"{d.Info.Label} '{e.PortName}'" : $"'{e.EntityName}' '{e.PortName}'";

        /// <summary>Length in metres (with slack) from one endpoint's node to another's along the cable.</summary>
        private static float LinkLength(CableState cable, float[]? distancesFromSource, CableEndpointInfo to)
        {
            if (distancesFromSource is null) return cable.Geometry.LengthM;
            int i = cable.Geometry.IndexOf(to.NodeId);
            if (i < 0 || !float.IsFinite(distancesFromSource[i])) return cable.Geometry.LengthM;
            return distancesFromSource[i] * cable.Geometry.SlackFactor;
        }

        // ------------------------------------------------------------------ helpers

        private void Add(IssueSeverity severity, string category, string code, string message, params Guid[] entities) =>
            _sink.Add(new Issue(severity, category, code, message, entities.Distinct().ToArray()));

        private static string Label(Entity e, FixtureComponent f) => f.FixtureId > 0 ? $"#{f.FixtureId} '{e.Name}'" : $"'{e.Name}'";
    }

    /// <summary>A device during a solve.</summary>
    private sealed class Device(Entity entity, DeviceInfo info, FixtureComponent? fixture, ConsoleComponent? console, PowerSourceComponent? source)
    {
        public Entity Entity { get; } = entity;
        public DeviceInfo Info { get; } = info;
        public FixtureComponent? Fixture { get; } = fixture;
        public ConsoleComponent? Console { get; } = console;
        public PowerSourceComponent? Source { get; } = source;
        public Guid Id => Entity.Id;

        /// <summary>Long label with the fixture type: "#3 'Spot 3' (Robe Robin T1 Profile)".</summary>
        public string LongLabel => Info.Profile is { } p ? $"{Info.Label} ({p.DisplayName})" : Info.Label;
    }

    /// <summary>A cable during a solve, with its issues in report order (kept apart so a geometry update replaces only its own).</summary>
    private sealed class CableState(Entity entity, CableComponent component, CableType? type, CableGeometry geometry, string label, string category)
    {
        public Entity Entity { get; } = entity;
        public CableComponent Component { get; } = component;
        public CableType? Type { get; } = type;
        public CableGeometry Geometry { get; } = geometry;
        public string Label { get; } = label;

        /// <summary>Issue category of the cable's own problems (power, data, or scene for an unknown type).</summary>
        public string Category { get; } = category;

        public CableInfo Info { get; set; } = null!;
        public List<PowerEdge> PowerEdges { get; } = new();

        /// <summary>Unknown type.</summary>
        public List<Issue> HeadIssues { get; init; } = new();

        /// <summary>Geometry problems (depend on node positions).</summary>
        public List<Issue> GeometryIssues { get; } = new();

        /// <summary>Plugs: endpoints, ports, connectors, Y-splits.</summary>
        public List<Issue> TailIssues { get; init; } = new();

        /// <summary>Length checks of the cable itself (depend on node positions).</summary>
        public List<Issue> LengthIssues { get; } = new();
    }
}
