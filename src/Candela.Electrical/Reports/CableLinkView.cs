namespace Candela.Electrical;

/// <summary>A device on the strip of the cable link window.</summary>
public sealed record CableLinkDevice
{
    /// <summary>Position on the strip (0 = the source: console or power source).</summary>
    public required int Index { get; init; }

    /// <summary>Device entity (click → select it).</summary>
    public required Guid EntityId { get; init; }

    /// <summary>Device name.</summary>
    public required string Name { get; init; }

    /// <summary>Label for display ("#12 'Spot 12'").</summary>
    public required string Label { get; init; }

    /// <summary>Kind of device (null when the entity is not a device).</summary>
    public DeviceKind? Kind { get; init; }

    /// <summary>Port the line enters through (null for the source).</summary>
    public string? InPort { get; init; }

    /// <summary>Distance from the source in devices (0 = source).</summary>
    public required int Depth { get; init; }

    /// <summary>True when the selected cable is plugged into this device.</summary>
    public required bool OnSelectedCable { get; init; }
}

/// <summary>A cable between two devices of the strip.</summary>
public sealed record CableLinkConnection
{
    /// <summary>Index of the upstream device.</summary>
    public required int FromIndex { get; init; }

    /// <summary>Index of the downstream device.</summary>
    public required int ToIndex { get; init; }

    /// <summary>Port on the upstream device.</summary>
    public required string FromPort { get; init; }

    /// <summary>Port on the downstream device.</summary>
    public required string ToPort { get; init; }

    /// <summary>Cable entity.</summary>
    public required Guid CableEntityId { get; init; }

    /// <summary>Cable label.</summary>
    public required string CableLabel { get; init; }

    /// <summary>Length from the upstream port to the downstream port in metres (with slack).</summary>
    public required float LengthM { get; init; }

    /// <summary>True for the cable the window was opened for.</summary>
    public required bool IsSelected { get; init; }
}

/// <summary>
/// View model of the cable link window: the strip of every device along the line a cable belongs to, in order from
/// the source — the console output's daisy chain for a data cable, the outlet circuit's links for a power cable — with
/// the selected cable highlighted. A cable that is not part of a fed line shows just the devices plugged into it.
/// </summary>
public sealed class CableLinkView
{
    private CableLinkView(CableInfo cable, string? source, IReadOnlyList<CableLinkDevice> devices, IReadOnlyList<CableLinkConnection> connections)
    {
        CableEntityId = cable.EntityId;
        Label = cable.DisplayLabel;
        TypeId = cable.TypeId;
        TypeName = cable.Type?.DisplayName ?? cable.TypeId;
        Signal = cable.Type?.Signal;
        ColorHex = cable.Type?.ColorHex ?? "#FFFFFF";
        LengthM = cable.LengthM;
        SourceDescription = source;
        Devices = devices;
        Connections = connections;
    }

    /// <summary>Cable entity.</summary>
    public Guid CableEntityId { get; }

    /// <summary>Cable label.</summary>
    public string Label { get; }

    /// <summary>Cable type id.</summary>
    public string TypeId { get; }

    /// <summary>Cable type name.</summary>
    public string TypeName { get; }

    /// <summary>Signal (null for unknown types).</summary>
    public SignalKind? Signal { get; }

    /// <summary>Legend colour.</summary>
    public string ColorHex { get; }

    /// <summary>Cable length in metres.</summary>
    public float LengthM { get; }

    /// <summary>"console 'Desk' DMX A (universe 1)" / "'Distro' Outlet 1", or null for an unfed cable.</summary>
    public string? SourceDescription { get; }

    /// <summary>Devices along the line, depth first.</summary>
    public IReadOnlyList<CableLinkDevice> Devices { get; }

    /// <summary>Cables between the devices.</summary>
    public IReadOnlyList<CableLinkConnection> Connections { get; }

    /// <summary>Builds the view for a cable, or null when the cable is not in the solution.</summary>
    public static CableLinkView? Build(NetworkSolution solution, Guid cableEntityId)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var cable = solution.FindCable(cableEntityId);
        if (cable is null) return null;
        var onCable = cable.Endpoints.Select(e => e.EntityId).ToHashSet();

        if (cable.Type is { IsData: true })
        {
            foreach (var line in solution.DataLines)
            {
                if (!line.Feeds.Any(f => f.CableEntityId == cableEntityId)) continue;
                var builder = new StripBuilder(solution, cable, onCable);
                builder.AddRoot(line.ConsoleEntityId);
                foreach (var f in line.Feeds)
                    builder.Add(f.DeviceEntityId, f.InputPort, f.UpstreamEntityId ?? line.ConsoleEntityId, f.UpstreamPort, f.CableEntityId, f.ChainIndex);
                string universes = line.UniverseCount <= 1 ? $"universe {line.FirstUniverse}" : $"universes {line.FirstUniverse}–{line.FirstUniverse + line.UniverseCount - 1}";
                return builder.Build($"console '{line.ConsoleName}' {line.Output.Name} ({universes})");
            }
        }
        else if (cable.Type is { IsPower: true })
        {
            foreach (var circuit in solution.PowerCircuits)
            {
                if (!circuit.Feeds.Any(f => f.CableEntityId == cableEntityId)) continue;
                var builder = new StripBuilder(solution, cable, onCable);
                builder.AddRoot(circuit.SourceEntityId);
                foreach (var f in circuit.Feeds)
                    builder.Add(f.DeviceEntityId, f.InputPort, f.UpstreamEntityId ?? circuit.SourceEntityId, f.UpstreamPort, f.CableEntityId, f.Depth + 1);
                string where = circuit.Outlet.Circuits > 1 ? $"{circuit.Outlet.Name} circuit {circuit.Circuit}" : circuit.Outlet.Name;
                return builder.Build($"'{circuit.SourceName}' {where}");
            }
        }

        // Not part of a fed line: the plugged devices in endpoint order, linked from the first.
        var plain = new StripBuilder(solution, cable, onCable);
        CableEndpointInfo? first = null;
        foreach (var e in cable.Endpoints)
        {
            if (first is null)
            {
                first = e;
                plain.AddRoot(e.EntityId, e.EntityName);
                continue;
            }
            plain.Add(e.EntityId, e.PortName, first.EntityId, first.PortName, cable.EntityId, 1, e.EntityName);
        }
        return plain.Build(null);
    }

    private sealed class StripBuilder(NetworkSolution solution, CableInfo selected, HashSet<Guid> onCable)
    {
        private readonly List<CableLinkDevice> _devices = new();
        private readonly List<CableLinkConnection> _connections = new();
        private readonly Dictionary<Guid, int> _index = new();

        public void AddRoot(Guid entityId, string? fallbackName = null)
        {
            _index[entityId] = _devices.Count;
            _devices.Add(Device(entityId, null, 0, fallbackName));
        }

        public void Add(Guid entityId, string inPort, Guid upstreamId, string upstreamPort, Guid cableId, int depth, string? fallbackName = null)
        {
            if (_index.ContainsKey(entityId)) return;
            int to = _devices.Count;
            _index[entityId] = to;
            _devices.Add(Device(entityId, inPort, depth, fallbackName));
            if (!_index.TryGetValue(upstreamId, out int from)) return;
            var cable = solution.FindCable(cableId);
            float length = cable?.LengthM ?? 0f;
            if (cable is not null)
            {
                var a = cable.Endpoints.FirstOrDefault(e => e.EntityId == upstreamId && string.Equals(e.PortName, upstreamPort, StringComparison.OrdinalIgnoreCase));
                var b = cable.Endpoints.FirstOrDefault(e => e.EntityId == entityId && string.Equals(e.PortName, inPort, StringComparison.OrdinalIgnoreCase));
                if (a is not null && b is not null && cable.Geometry.PathLengthM(a.NodeId, b.NodeId) is { } l) length = l;
            }
            _connections.Add(new CableLinkConnection
            {
                FromIndex = from,
                ToIndex = to,
                FromPort = upstreamPort,
                ToPort = inPort,
                CableEntityId = cableId,
                CableLabel = cable?.DisplayLabel ?? "",
                LengthM = length,
                IsSelected = cableId == selected.EntityId,
            });
        }

        public CableLinkView Build(string? source) => new(selected, source, _devices, _connections);

        private CableLinkDevice Device(Guid id, string? inPort, int depth, string? fallbackName)
        {
            solution.Devices.TryGetValue(id, out var d);
            string name = d?.Name ?? fallbackName ?? "";
            return new CableLinkDevice
            {
                Index = _devices.Count,
                EntityId = id,
                Name = name,
                Label = d?.Label ?? $"'{name}'",
                Kind = d?.Kind,
                InPort = inPort,
                Depth = depth,
                OnSelectedCable = onCable.Contains(id),
            };
        }
    }
}
