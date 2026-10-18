using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Electrical;

/// <summary>
/// Gives the ports of scene entities. Fixtures: GDTF 1.2 WiringObjects (connector type, input/output, signal, fuse
/// and payload ratings, position) or, in GDTF files without them, the legacy <c>Connectors</c>; whatever the GDTF does
/// not describe (no power inlet, no data input, blank connector names, dimmer/PSU outputs) is completed from
/// <c>_specs</c> (<c>powerConnectorIn/Out</c>, <c>powerLinkable</c>, <c>dataConnectors</c>, <c>controlSignal</c>,
/// <c>outputChannels</c>) with the names "Power In", "Power Out", "DMX In", "DMX Out", "Ethernet", "Output n" /
/// "Dimmer n". Consoles: one port per <c>ConsoleOutput</c>. Power sources: one port per outlet plus a "Mains In"
/// input through which a distro can itself be fed.
/// </summary>
public sealed class PortResolver
{
    /// <summary>Name of the synthesised input of a <see cref="PowerSourceComponent"/> (a distro fed from upstream).</summary>
    public const string MainsInPortName = "Mains In";

    private static readonly char[] NameSeparators = [' ', '-', '_', '/', '(', ')', '.', ',', ':'];

    private readonly IFixtureProfileProvider? _fixtures;

    /// <summary>Creates a resolver; without a profile provider fixtures have no ports.</summary>
    public PortResolver(IFixtureProfileProvider? fixtures)
    {
        _fixtures = fixtures;
    }

    /// <summary>The fixture profile provider used for fixtures (may be null).</summary>
    public IFixtureProfileProvider? Fixtures => _fixtures;

    /// <summary>
    /// Ports of an entity: fixture ports first, then console outputs, then power source outlets (an entity may carry
    /// several of these components). Empty for entities without ports.
    /// </summary>
    public IReadOnlyList<Port> GetPorts(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        IReadOnlyList<Port>? first = null;
        List<Port>? merged = null;
        foreach (var c in entity.Components)
        {
            IReadOnlyList<Port> ports = c switch
            {
                FixtureComponent f => GetProfile(f)?.Ports ?? Array.Empty<Port>(),
                ConsoleComponent console => FromConsole(console),
                PowerSourceComponent source => FromPowerSource(source),
                _ => Array.Empty<Port>(),
            };
            if (ports.Count == 0) continue;
            if (first is null)
            {
                first = ports;
                continue;
            }
            merged ??= new List<Port>(first);
            foreach (var p in ports)
                if (!merged.Any(r => string.Equals(r.Name, p.Name, StringComparison.OrdinalIgnoreCase))) merged.Add(p);
        }
        return merged ?? first ?? Array.Empty<Port>();
    }

    /// <summary>Port of an entity by name (case-insensitive), or null.</summary>
    public Port? FindPort(Entity entity, string? portName)
    {
        if (string.IsNullOrEmpty(portName)) return null;
        foreach (var p in GetPorts(entity))
            if (string.Equals(p.Name, portName, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>
    /// Ports of an entity that a cable of <paramref name="cableType"/> may be plugged into, best match first
    /// (compatible, then unknown, then adapter-needed; incompatible ports are left out). Used by the port picker.
    /// </summary>
    public IReadOnlyList<(Port Port, ConnectorCheck Check)> CompatiblePorts(Entity entity, CableType cableType)
    {
        ArgumentNullException.ThrowIfNull(cableType);
        var list = new List<(Port, ConnectorCheck)>();
        foreach (var p in GetPorts(entity))
        {
            var check = ConnectorCompatibility.Check(cableType, p);
            if (check.Match != ConnectorMatch.Incompatible) list.Add((p, check));
        }
        return list.OrderBy(x => x.Item2.Match).ToList();
    }

    /// <summary>Fixture profile of a fixture component, or null without a provider / unknown GDTF.</summary>
    public FixtureProfile? GetProfile(FixtureComponent fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (_fixtures is null || string.IsNullOrWhiteSpace(fixture.GdtfSpec)) return null;
        return _fixtures.GetProfile(fixture.GdtfSpec, fixture.GdtfMode);
    }

    // ------------------------------------------------------------------ consoles and sources

    /// <summary>Console outputs as ports: "DMX512" outputs use XLR 5-pin, "Ethernet" outputs etherCON.</summary>
    public static IReadOnlyList<Port> FromConsole(ConsoleComponent console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var ports = new List<Port>(console.Outputs.Count);
        int i = 0;
        foreach (var o in console.Outputs)
        {
            i++;
            if (o is null) continue;
            bool ethernet = o.Signal?.Contains("ether", StringComparison.OrdinalIgnoreCase) == true ||
                            o.Signal?.Contains("net", StringComparison.OrdinalIgnoreCase) == true;
            string name = string.IsNullOrWhiteSpace(o.PortName) ? $"Output {i}" : o.PortName.Trim();
            AddUnique(ports, new Port
            {
                Name = name,
                Direction = PortDirection.Output,
                Signal = ethernet ? SignalKind.Ethernet : SignalKind.Dmx512,
                Connector = ConnectorCatalog.Resolve(ethernet ? "etherCON" : "XLR5"),
                Role = PortRole.ConsoleOutput,
                Origin = PortOrigin.Console,
                Universe = Math.Max(1, o.FirstUniverse),
                UniverseCount = ethernet ? Math.Max(1, o.UniverseCount) : 1,
            });
        }
        return ports;
    }

    /// <summary>
    /// Outlets of a power source as ports (breaker, phase, connector; multicore outlets carry six circuits with the
    /// breaker rating per circuit), plus the <see cref="MainsInPortName"/> input.
    /// </summary>
    public static IReadOnlyList<Port> FromPowerSource(PowerSourceComponent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ports = new List<Port>(source.Outlets.Count + 1);
        int i = 0;
        foreach (var o in source.Outlets)
        {
            i++;
            if (o is null) continue;
            var connector = ConnectorCatalog.Resolve(o.Connector);
            float? breaker = o.BreakerA > 0 && float.IsFinite(o.BreakerA) ? o.BreakerA : null;
            AddUnique(ports, new Port
            {
                Name = string.IsNullOrWhiteSpace(o.PortName) ? $"Outlet {i}" : o.PortName.Trim(),
                Direction = PortDirection.Output,
                Signal = connector.Domain == ConnectorDomain.Multicore ? SignalKind.Multicore : SignalKind.Power,
                Connector = connector,
                ConnectorText = o.Connector,
                Role = PortRole.PowerOutlet,
                Origin = PortOrigin.PowerSource,
                BreakerA = breaker,
                MaxThroughCurrentA = breaker ?? connector.RatedCurrentA,
                Phase = Math.Clamp(o.Phase, 1, 3),
            });
        }
        AddUnique(ports, new Port
        {
            Name = MainsInPortName,
            Direction = PortDirection.Input,
            Signal = SignalKind.Power,
            Connector = ConnectorCatalog.AnyTermination,
            Role = PortRole.PowerIn,
            Origin = PortOrigin.PowerSource,
        });
        return ports;
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// Ports of a fixture: from the GDTF (WiringObjects of the mode's geometry tree, else legacy connectors), completed
    /// from the specs record. Either argument may be null; with neither, the fixture has no ports.
    /// </summary>
    public static IReadOnlyList<Port> FromFixture(FixtureType? type, string? mode, FixtureSpecs? specs)
    {
        var distribution = DistributionInfo.FromSpecs(specs);
        var ports = new List<Port>();
        if (type is not null)
        {
            ports.AddRange(FromWiringObjects(type, mode));
            if (ports.Count == 0) ports.AddRange(FromLegacyConnectors(type));
        }
        if (specs is not null) CompleteFromSpecs(ports, specs, distribution);
        if (distribution is not null) AssignDistributionRoles(ports, distribution, specs);
        return ports;
    }

    /// <summary>Ports described only by a specs record (what a GDTF without wiring data gets).</summary>
    public static IReadOnlyList<Port> FromSpecs(FixtureSpecs specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        return FromFixture(null, null, specs);
    }

    /// <summary>Ports from the GDTF 1.2 WiringObjects of a mode (fuses are internal and skipped).</summary>
    public static IReadOnlyList<Port> FromWiringObjects(FixtureType type, string? mode)
    {
        ArgumentNullException.ThrowIfNull(type);
        var ports = new List<Port>();
        foreach (var w in FixtureProfile.WiringObjects(type, mode))
        {
            if (w.ComponentType == WiringComponentType.Fuse) continue;
            var connector = ConnectorCatalog.Resolve(w.ConnectorType);
            if (connector.IsInternal) continue;
            var signal = SignalFromGdtf(w, connector);
            var direction = w.ComponentType switch
            {
                WiringComponentType.Input or WiringComponentType.Consumer or WiringComponentType.NetworkInput => PortDirection.Input,
                WiringComponentType.NetworkInOut => PortDirection.Bidirectional,
                _ => PortDirection.Output,
            };
            if (signal == SignalKind.Ethernet && direction != PortDirection.Bidirectional && w.ComponentType is not
                    (WiringComponentType.NetworkInput or WiringComponentType.NetworkOutput or WiringComponentType.Input or WiringComponentType.Output))
                direction = PortDirection.Bidirectional;

            float? maxA = null;
            float? payload = w.MaxPayLoad > 0 && float.IsFinite(w.MaxPayLoad) ? w.MaxPayLoad : null;
            if (signal.CarriesPower())
            {
                if (w.FuseCurrent > 0 && float.IsFinite(w.FuseCurrent)) maxA = w.FuseCurrent;
                else if (payload is { } pl && w.Voltage > 0 && float.IsFinite(w.Voltage)) maxA = pl / w.Voltage;
            }
            string name = string.IsNullOrWhiteSpace(w.Name) ? $"{signal.DisplayName()} {direction}" : w.Name.Trim();
            AddUnique(ports, new Port
            {
                Name = name,
                Direction = direction,
                Signal = signal,
                Connector = connector,
                ConnectorText = w.ConnectorType,
                Role = RoleFor(signal, direction),
                Origin = PortOrigin.GdtfWiringObject,
                MaxThroughCurrentA = maxA,
                MaxPayloadW = payload,
                LocalPosition = SafeTranslation(w),
            });
        }
        return ports;
    }

    /// <summary>Ports from GDTF 1.0/1.1 <c>Connectors</c>; direction from the name ("In", "Out", "Thru", …), else the gender.</summary>
    public static IReadOnlyList<Port> FromLegacyConnectors(FixtureType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var ports = new List<Port>();
        foreach (var c in type.PhysicalDescriptions.Connectors)
        {
            if (c is null) continue;
            var connector = ConnectorCatalog.Resolve(c.Type);
            if (connector.IsInternal) continue;
            var signal = SignalFromConnector(connector, c.Name);
            var direction = DirectionFromName(c.Name) ?? signal switch
            {
                SignalKind.Ethernet => PortDirection.Bidirectional,
                _ => c.Gender > 0 ? PortDirection.Output : PortDirection.Input,
            };
            string name = string.IsNullOrWhiteSpace(c.Name) ? $"{connector.DisplayName} {direction}" : c.Name.Trim();
            AddUnique(ports, new Port
            {
                Name = name,
                Direction = direction,
                Signal = signal,
                Connector = connector,
                ConnectorText = c.Type,
                Role = RoleFor(signal, direction),
                Origin = PortOrigin.GdtfConnector,
                DmxBreak = c.DmxBreak > 0 ? c.DmxBreak : 1,
            });
        }
        return ports;
    }

    // ------------------------------------------------------------------ specs completion

    private static void CompleteFromSpecs(List<Port> ports, FixtureSpecs specs, DistributionInfo? distribution)
    {
        var powerIn = ConnectorCatalog.ResolveAll(specs.PowerConnectorIn).Where(c => !c.IsInternal).ToList();
        var powerOut = ConnectorCatalog.ResolveAll(specs.PowerConnectorOut).Where(c => !c.IsInternal).ToList();

        // Blank connector names in the GDTF take the specs connector of the same role.
        for (int i = 0; i < ports.Count; i++)
        {
            var p = ports[i];
            if (p.Connector.IsKnown) continue;
            IReadOnlyList<ConnectorInfo> fill = p.Role switch
            {
                PortRole.PowerIn => powerIn,
                PortRole.PowerLink or PortRole.PowerDistribution => powerOut.Count > 0 ? powerOut : powerIn,
                _ when p.Signal == SignalKind.Dmx512 => DmxConnectors(specs) is { Count: > 0 } dmx ? dmx : [ConnectorCatalog.GenericXlr],
                _ when p.Signal == SignalKind.Ethernet => EthernetConnectors(specs),
                _ => Array.Empty<ConnectorInfo>(),
            };
            if (fill.Count > 0 && p.Connector.Id.Length == 0)
                ports[i] = p with { Connector = fill[0], AlternativeConnectors = fill.Skip(1).ToList() };
        }

        bool battery = specs.BatteryPowered && powerIn.Count == 0;
        if (!battery && powerIn.Count > 0 && !ports.Any(p => p.Role == PortRole.PowerIn))
        {
            if (ConnectorCatalog.ListsSeveralPresent(specs.PowerConnectorIn))
                for (int i = 0; i < powerIn.Count; i++)
                    AddUnique(ports, PowerPort(i == 0 ? "Power In" : $"Power In {i + 1}", PortDirection.Input, PortRole.PowerIn, [powerIn[i]]));
            else
                AddUnique(ports, PowerPort("Power In", PortDirection.Input, PortRole.PowerIn, powerIn));
        }

        bool hasPowerOut = ports.Any(p => p.Role is PortRole.PowerLink or PortRole.PowerDistribution);
        if (!hasPowerOut)
        {
            if (distribution is not null)
            {
                var connectors = powerOut.Count > 0 ? powerOut : powerIn;
                string stem = distribution.OutputIsDimmed ? "Dimmer" : "Output";
                for (int ch = 1; ch <= distribution.OutputChannels; ch++)
                {
                    var port = PowerPort(distribution.OutputChannels == 1 ? $"{stem} Out" : $"{stem} {ch}", PortDirection.Output,
                        PortRole.PowerDistribution, connectors);
                    AddUnique(ports, port with
                    {
                        Channel = ch,
                        MaxThroughCurrentA = distribution.MaxLoadPerChannelA is { } perCh ? (float)perCh : port.MaxThroughCurrentA,
                    });
                }
            }
            else if (specs.PowerLinkable && (powerOut.Count > 0 || powerIn.Count > 0))
            {
                AddUnique(ports, PowerPort("Power Out", PortDirection.Output, PortRole.PowerLink, powerOut.Count > 0 ? powerOut : powerIn));
            }
        }

        bool hasDmx = ports.Any(p => p.Signal is SignalKind.Dmx512 or SignalKind.Hybrid && p.Direction != PortDirection.Output);
        bool hasEthernet = ports.Any(p => p.Signal == SignalKind.Ethernet);
        bool dmxSignal = specs.ControlSignal.Any(s => s.Contains("DMX512", StringComparison.OrdinalIgnoreCase) && !s.Contains("Wireless", StringComparison.OrdinalIgnoreCase));
        bool ethernetSignal = specs.ControlSignal.Any(s => s.StartsWith("Ethernet", StringComparison.OrdinalIgnoreCase));
        if (!hasDmx)
        {
            var dmx = DmxConnectors(specs);
            if (dmx.Count == 0 && dmxSignal) dmx = [ConnectorCatalog.GenericXlr];
            for (int i = 0; i < dmx.Count; i++)
            {
                string suffix = i == 0 ? "" : $" ({dmx[i].Id})";
                AddUnique(ports, DataPort($"DMX In{suffix}", PortDirection.Input, SignalKind.Dmx512, dmx[i]));
                AddUnique(ports, DataPort($"DMX Out{suffix}", PortDirection.Output, SignalKind.Dmx512, dmx[i]));
            }
        }
        if (!hasEthernet)
        {
            var eth = EthernetConnectors(specs);
            if (eth.Count > 0)
                AddUnique(ports, DataPort("Ethernet", PortDirection.Bidirectional, SignalKind.Ethernet, eth[0]) with
                {
                    AlternativeConnectors = eth.Skip(1).ToList(),
                });
            else if (ethernetSignal)
                AddUnique(ports, DataPort("Ethernet", PortDirection.Bidirectional, SignalKind.Ethernet, ConnectorCatalog.Resolve("RJ45")));
        }
    }

    /// <summary>
    /// On a distribution device, power outputs feed loads that count as their own devices: every power output becomes
    /// a distribution output with a channel number — except a mains link that uses the inlet's connector next to DC
    /// outputs (e.g. the TRUE1 thru of a P3 PowerPort).
    /// </summary>
    private static void AssignDistributionRoles(List<Port> ports, DistributionInfo distribution, FixtureSpecs? specs)
    {
        var inletFamily = ports.FirstOrDefault(p => p.Role == PortRole.PowerIn)?.Connector.Family;
        int channel = 0;
        for (int i = 0; i < ports.Count; i++)
        {
            var p = ports[i];
            if (p.Role is not (PortRole.PowerLink or PortRole.PowerDistribution)) continue;
            bool mainsLink = distribution.OutputIsDc && inletFamily is not null && p.Connector.Family == inletFamily &&
                             p.Signal == SignalKind.Power && specs?.PowerLinkable == true;
            if (mainsLink)
            {
                ports[i] = p with { Role = PortRole.PowerLink, Channel = 0 };
                continue;
            }
            channel++;
            ports[i] = p with
            {
                Role = PortRole.PowerDistribution,
                Channel = p.Channel > 0 ? p.Channel : channel,
                MaxThroughCurrentA = distribution.MaxLoadPerChannelA is { } perCh ? (float)perCh : p.MaxThroughCurrentA,
            };
        }
    }

    private static List<ConnectorInfo> DmxConnectors(FixtureSpecs specs) =>
        specs.DataConnectors.Select(ConnectorCatalog.Resolve).Where(c => c.Domain == ConnectorDomain.Dmx).DistinctBy(c => c.Id).ToList();

    private static List<ConnectorInfo> EthernetConnectors(FixtureSpecs specs) =>
        specs.DataConnectors.Select(ConnectorCatalog.Resolve).Where(c => c.Domain == ConnectorDomain.Ethernet).DistinctBy(c => c.Id).ToList();

    private static Port PowerPort(string name, PortDirection direction, PortRole role, IReadOnlyList<ConnectorInfo> connectors) => new()
    {
        Name = name,
        Direction = direction,
        Signal = connectors.Count > 0 && connectors[0].Domain == ConnectorDomain.Hybrid ? SignalKind.Hybrid : SignalKind.Power,
        Connector = connectors.Count > 0 ? connectors[0] : ConnectorCatalog.Unspecified,
        AlternativeConnectors = connectors.Skip(1).ToList(),
        Role = role,
        Origin = PortOrigin.Specs,
        MaxThroughCurrentA = connectors.Count > 0 ? connectors[0].RatedCurrentA : null,
    };

    private static Port DataPort(string name, PortDirection direction, SignalKind signal, ConnectorInfo connector) => new()
    {
        Name = name,
        Direction = direction,
        Signal = signal,
        Connector = connector,
        Role = RoleFor(signal, direction),
        Origin = PortOrigin.Specs,
    };

    // ------------------------------------------------------------------ classification helpers

    /// <summary>Role of a port from its signal and direction (fixtures; distribution roles are assigned later).</summary>
    internal static PortRole RoleFor(SignalKind signal, PortDirection direction) => signal switch
    {
        SignalKind.Power or SignalKind.Multicore or SignalKind.Hybrid => direction == PortDirection.Output ? PortRole.PowerLink : PortRole.PowerIn,
        SignalKind.Dmx512 or SignalKind.Ethernet => direction switch
        {
            PortDirection.Input => PortRole.DataIn,
            PortDirection.Output => PortRole.DataOut,
            _ => PortRole.DataInOut,
        },
        _ => PortRole.Other,
    };

    /// <summary>Signal of a WiringObject from its SignalType text, component type and connector.</summary>
    internal static SignalKind SignalFromGdtf(WiringObjectGeometry w, ConnectorInfo connector)
    {
        string s = (w.SignalType ?? "").Trim();
        if (s.Length > 0)
        {
            bool power = s.Contains("power", StringComparison.OrdinalIgnoreCase) || s.Contains("battery", StringComparison.OrdinalIgnoreCase) ||
                         s.StartsWith("DC", StringComparison.OrdinalIgnoreCase) || s.StartsWith("AC", StringComparison.OrdinalIgnoreCase);
            bool dmx = s.Contains("DMX", StringComparison.OrdinalIgnoreCase) || s.Contains("data", StringComparison.OrdinalIgnoreCase);
            bool net = s.Contains("ether", StringComparison.OrdinalIgnoreCase) || s.Contains("artnet", StringComparison.OrdinalIgnoreCase) ||
                       s.Contains("sacn", StringComparison.OrdinalIgnoreCase) || s.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                       s.Contains("protocol", StringComparison.OrdinalIgnoreCase);
            if (power && (dmx || net)) return SignalKind.Hybrid;
            if (power) return connector.Domain == ConnectorDomain.Multicore ? SignalKind.Multicore : SignalKind.Power;
            if (dmx) return connector.Domain == ConnectorDomain.Ethernet && net ? SignalKind.Ethernet : SignalKind.Dmx512;
            if (net) return SignalKind.Ethernet;
            if (s.Contains("video", StringComparison.OrdinalIgnoreCase) || s.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("clock", StringComparison.OrdinalIgnoreCase))
                return connector.Domain == ConnectorDomain.Hybrid ? SignalKind.Hybrid : SignalKind.Other;
        }
        if (w.ComponentType is WiringComponentType.Consumer or WiringComponentType.PowerSource)
            return connector.Domain switch
            {
                ConnectorDomain.Hybrid => SignalKind.Hybrid,
                ConnectorDomain.Multicore => SignalKind.Multicore,
                _ => SignalKind.Power,
            };
        var fromConnector = SignalFromConnector(connector, w.Name);
        if (fromConnector == SignalKind.Other && w.ComponentType is WiringComponentType.NetworkInput or WiringComponentType.NetworkOutput
                or WiringComponentType.NetworkInOut or WiringComponentType.NetworkProvider)
            return SignalKind.Ethernet;
        return fromConnector;
    }

    /// <summary>Signal implied by a connector (and, for unknown connectors, words in the port name).</summary>
    internal static SignalKind SignalFromConnector(ConnectorInfo connector, string? portName)
    {
        switch (connector.Domain)
        {
            case ConnectorDomain.Power: return SignalKind.Power;
            case ConnectorDomain.Multicore: return SignalKind.Multicore;
            case ConnectorDomain.Dmx: return SignalKind.Dmx512;
            case ConnectorDomain.Ethernet: return SignalKind.Ethernet;
            case ConnectorDomain.Hybrid: return SignalKind.Hybrid;
            case ConnectorDomain.Other: return SignalKind.Other;
        }
        string n = portName ?? "";
        if (n.Contains("dmx", StringComparison.OrdinalIgnoreCase)) return SignalKind.Dmx512;
        if (n.Contains("power", StringComparison.OrdinalIgnoreCase) || n.Contains("mains", StringComparison.OrdinalIgnoreCase)) return SignalKind.Power;
        if (n.Contains("ether", StringComparison.OrdinalIgnoreCase) || n.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("artnet", StringComparison.OrdinalIgnoreCase))
            return SignalKind.Ethernet;
        return connector.Domain == ConnectorDomain.Any ? SignalKind.Power : SignalKind.Other;
    }

    /// <summary>Direction from words in a connector name ("DMX-In 5pin", "PowerCon True1-Out", "Thru", "Mains"), or null.</summary>
    internal static PortDirection? DirectionFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        bool input = false, output = false;
        foreach (var raw in name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = raw.ToLowerInvariant();
            if (t is "in" or "input" or "inlet" or "mains" or "supply" or "feed" or "eingang") input = true;
            else if (t is "out" or "output" or "outlet" or "thru" or "through" or "link" or "ausgang") output = true;
            else if (t.EndsWith("in", StringComparison.Ordinal) && t.Length <= 6 && (t.StartsWith("dmx", StringComparison.Ordinal) || t.StartsWith("pwr", StringComparison.Ordinal))) input = true;
            else if (t.EndsWith("out", StringComparison.Ordinal) && (t.StartsWith("dmx", StringComparison.Ordinal) || t.StartsWith("pwr", StringComparison.Ordinal))) output = true;
        }
        if (input == output) return null;
        return input ? PortDirection.Input : PortDirection.Output;
    }

    private static System.Numerics.Vector3? SafeTranslation(GdtfGeometry g)
    {
        var t = g.TransformToTopLevel().Translation;
        return float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z) ? t : null;
    }

    /// <summary>Adds a port, renaming it "Name (2)", "Name (3)" … when the name is already taken (case-insensitive).</summary>
    private static void AddUnique(List<Port> ports, Port port)
    {
        string name = port.Name;
        for (int n = 2; ports.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{port.Name} ({n})";
        ports.Add(name == port.Name ? port : port with { Name = name });
    }
}
