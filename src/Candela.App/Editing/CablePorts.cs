using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Engine;

namespace Candela.App.Editing;

/// <summary>Which ends a cable may still get on a device, judged against the cable's other end.</summary>
internal enum PortSide
{
    /// <summary>The cable's first end: any compatible port, outputs preferred (a cable usually starts at its source).</summary>
    Start,

    /// <summary>The far end of a cable that starts at an output: inputs and bidirectional ports.</summary>
    AfterOutput,

    /// <summary>The far end of a cable that starts at an input: outputs and bidirectional ports.</summary>
    AfterInput,

    /// <summary>The far end of a cable that starts unplugged (a free end or a branch): any port, inputs preferred.</summary>
    AfterFree,
}

/// <summary>One port of a device as the cable tool's port picker shows it.</summary>
/// <param name="Port">The port.</param>
/// <param name="Index">Its position in the device's port list (spreads ports without a known position).</param>
/// <param name="Check">How the cable's connector meets the port.</param>
/// <param name="Occupant">The cable already plugged in, or null.</param>
/// <param name="WrongDirection">True when the port has the same direction as the cable's other end (output to output).</param>
internal sealed record PortCandidate(Port Port, int Index, ConnectorCheck Check, CableInfo? Occupant, bool WrongDirection)
{
    /// <summary>True when the cable can be plugged in here (free, mating or with an adapter, right direction).</summary>
    public bool Usable => Occupant is null && !WrongDirection && Check.Match != ConnectorMatch.Incompatible;
}

/// <summary>
/// The ports of scene devices for the cable tool: which ports a cable type can be plugged into (with the reason for the
/// others: wrong signal, keyed connector, port in use, output to output), and where each port sits — the GDTF
/// WiringObject position when the file gives one, otherwise a spot on the device's body (along the rear edge of a
/// console desk, the front face of a distro, under the base of a fixture), so cable ends are anchored to the device and
/// follow it.
/// </summary>
internal static class CablePorts
{
    /// <summary>
    /// Every port of <paramref name="device"/> judged for a cable of <paramref name="type"/>: usable ports first (mating
    /// connectors before adapters, the preferred direction of <paramref name="side"/> first, then port order), then the
    /// others with their reason. <paramref name="exclude"/> is a port the cable being drawn already uses.
    /// </summary>
    public static IReadOnlyList<PortCandidate> Candidates(StageEngine engine, Entity device, CableType type, PortSide side, PortRef? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(type);
        var ports = engine.Network.Ports.GetPorts(device);
        NetworkSolution? solution = null;
        try
        {
            solution = engine.NetworkSolution;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // An unreadable GDTF must not stop the tool: ports are then judged without their occupants.
        }

        var list = new List<PortCandidate>(ports.Count);
        for (int i = 0; i < ports.Count; i++)
        {
            var port = ports[i];
            var check = ConnectorCompatibility.Check(type, port);
            CableInfo? occupant = solution?.CableAt(device.Id, port.Name)?.Cable;
            // The port the cable being drawn already starts from is never offered for its other end.
            bool sameAsStart = exclude is { } own && own.EntityId == device.Id && string.Equals(own.PortName, port.Name, StringComparison.OrdinalIgnoreCase);
            bool wrong = sameAsStart || side switch
            {
                PortSide.AfterOutput => port.Direction == PortDirection.Output,
                PortSide.AfterInput => port.Direction == PortDirection.Input,
                _ => false,
            };
            list.Add(new PortCandidate(port, i, check, occupant, wrong));
        }

        list.Sort((a, b) =>
        {
            int c = b.Usable.CompareTo(a.Usable);
            if (c != 0) return c;
            c = Rank(a.Check.Match).CompareTo(Rank(b.Check.Match));
            if (c != 0) return c;
            c = DirectionRank(a.Port, side).CompareTo(DirectionRank(b.Port, side));
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return list;
    }

    /// <summary>True when the device has at least one port (fixtures with a resolvable profile, consoles, power sources).</summary>
    public static bool IsDevice(StageEngine engine, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.Has<FixtureComponent>() && !entity.Has<ConsoleComponent>() && !entity.Has<PowerSourceComponent>())
            return false;
        return engine.Network.Ports.GetPorts(entity).Count > 0;
    }

    /// <summary>The side a cable's far end is on, given the port its first end is plugged into (null: a free end).</summary>
    public static PortSide FarSide(Port? start) => start?.Direction switch
    {
        PortDirection.Output => PortSide.AfterOutput,
        PortDirection.Input => PortSide.AfterInput,
        _ => PortSide.AfterFree,
    };

    /// <summary>
    /// Where a port sits in its device's local space (metres): the GDTF position when known; otherwise spread along
    /// the rear edge of a console, the front of a power source, or under a fixture's base, by <paramref name="index"/>
    /// of <paramref name="count"/>.
    /// </summary>
    public static Vector3 LocalPosition(Entity device, Port port, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(port);
        if (port.LocalPosition is { } known && float.IsFinite(known.X) && float.IsFinite(known.Y) && float.IsFinite(known.Z))
            return known;
        count = Math.Max(1, count);
        index = Math.Clamp(index, 0, count - 1);
        float t = count == 1 ? 0f : index / (float)(count - 1) - 0.5f; // −0.5 … 0.5
        if (device.Has<ConsoleComponent>())
        {
            var s = DeviceCatalog.ConsoleSize;
            return new Vector3(t * s.X * 0.85f, s.Y * 0.5f, s.Z * 0.75f);
        }

        if (device.Has<PowerSourceComponent>())
        {
            var s = DeviceCatalog.PowerSourceSize;
            // Outlets in a row across the front face, the mains input low on the side.
            if (port.Role == PortRole.PowerIn)
                return new Vector3(s.X * 0.5f, 0f, s.Z * 0.15f);
            return new Vector3(t * s.X * 0.8f, -s.Y * 0.5f, s.Z * 0.65f);
        }

        // A fixture hangs below its origin (GDTF convention): just under the base, spread across it.
        return new Vector3(t * 0.12f, 0f, -0.06f);
    }

    /// <summary>World position of a port (its device's world matrix × <see cref="LocalPosition"/>).</summary>
    public static Vector3 WorldPosition(Entity device, Port port, int index, int count) =>
        Vector3.Transform(LocalPosition(device, port, index, count), device.WorldMatrix);

    /// <summary>The index and count of <paramref name="portName"/> in the device's port list (for <see cref="LocalPosition"/>).</summary>
    public static (Port? Port, int Index, int Count) Find(StageEngine engine, Entity device, string portName)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(device);
        var ports = engine.Network.Ports.GetPorts(device);
        for (int i = 0; i < ports.Count; i++)
        {
            if (string.Equals(ports[i].Name, portName, StringComparison.OrdinalIgnoreCase))
                return (ports[i], i, ports.Count);
        }

        return (null, 0, ports.Count);
    }

    /// <summary>
    /// The port a cable continues from after being plugged into <paramref name="justUsed"/> (a daisy chain): the
    /// device's free output of the cable's signal with the same connector family, else its only free output; null when
    /// there is none or the choice is ambiguous (a splitter's outputs — the user picks one).
    /// </summary>
    public static PortCandidate? Continuation(StageEngine engine, Entity device, CableType type, Port justUsed)
    {
        ArgumentNullException.ThrowIfNull(justUsed);
        if (justUsed.Direction == PortDirection.Output)
            return null;
        var usable = Candidates(engine, device, type, PortSide.AfterInput, new PortRef(device.Id, justUsed.Name))
            .Where(c => c.Usable && c.Port.Direction == PortDirection.Output && c.Check.IsAcceptable)
            .ToList();
        if (usable.Count == 1)
            return usable[0];
        var sameFamily = usable.Where(c => c.Port.Connector.Family == justUsed.Connector.Family).ToList();
        return sameFamily.Count == 1 ? sameFamily[0] : null;
    }

    private static int Rank(ConnectorMatch match) => match switch
    {
        ConnectorMatch.Compatible => 0,
        ConnectorMatch.Unknown => 1,
        ConnectorMatch.NeedsAdapter => 2,
        _ => 3,
    };

    private static int DirectionRank(Port port, PortSide side) => side switch
    {
        PortSide.Start => port.Direction == PortDirection.Output ? 0 : port.Direction == PortDirection.Bidirectional ? 1 : 2,
        PortSide.AfterFree => port.Direction == PortDirection.Input ? 0 : port.Direction == PortDirection.Bidirectional ? 1 : 2,
        PortSide.AfterOutput => port.Direction == PortDirection.Input ? 0 : 1,
        _ => port.Direction == PortDirection.Output ? 0 : 1,
    };
}
