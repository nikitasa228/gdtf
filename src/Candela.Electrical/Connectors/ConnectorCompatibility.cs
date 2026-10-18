namespace Candela.Electrical;

/// <summary>Result of plugging a cable end into a port, from best to worst.</summary>
public enum ConnectorMatch
{
    /// <summary>The connectors mate (same family, TRUE1 ↔ TRUE1 TOP, RJ45 ↔ etherCON, a multicore breakout tail, …).</summary>
    Compatible,

    /// <summary>One side is not known well enough to judge; nothing is reported.</summary>
    Unknown,

    /// <summary>Same kind of signal but different connectors: works with an adapter (warning).</summary>
    NeedsAdapter,

    /// <summary>The cable cannot work on this port: wrong signal or physically non-mating keyed connectors (error).</summary>
    Incompatible,
}

/// <summary>Outcome of a connector check with a short explanation for messages and the UI.</summary>
/// <param name="Match">Verdict.</param>
/// <param name="Reason">Why, in a few words ("XLR 3-pin to 5-pin adapter needed").</param>
public readonly record struct ConnectorCheck(ConnectorMatch Match, string Reason)
{
    /// <summary>True for <see cref="ConnectorMatch.Compatible"/> and <see cref="ConnectorMatch.Unknown"/>.</summary>
    public bool IsAcceptable => Match is ConnectorMatch.Compatible or ConnectorMatch.Unknown;
}

/// <summary>
/// Connector compatibility rules. Signal first: a cable must carry what the port expects (power into power, DMX
/// into DMX, a hybrid port takes either, a multicore fans out into single power circuits) — otherwise the cable is
/// wrong (error). Then the connector: same family mates (TRUE1 ↔ TRUE1 TOP, RJ45 ↔ etherCON); legacy powerCON blue
/// (in) never mates powerCON grey (out) (error); XLR 3-pin ↔ 5-pin, Schuko ↔ CEE, legacy powerCON ↔ TRUE1 and every
/// other pair of the same signal need an adapter (warning).
/// </summary>
public static class ConnectorCompatibility
{
    /// <summary>Checks a cable connector against a port connector (signals are assumed compatible).</summary>
    public static ConnectorCheck CheckConnectors(ConnectorInfo cableEnd, ConnectorInfo port)
    {
        ArgumentNullException.ThrowIfNull(cableEnd);
        ArgumentNullException.ThrowIfNull(port);
        if (!cableEnd.IsKnown || !port.IsKnown)
            return new ConnectorCheck(ConnectorMatch.Unknown, $"connector {(port.IsKnown ? cableEnd : port).DisplayName} is not in the catalogue");
        if (cableEnd.IsWildcard || port.IsWildcard)
            return new ConnectorCheck(ConnectorMatch.Compatible, "unspecified termination accepts the cable");

        if (cableEnd.Family == port.Family)
        {
            if (cableEnd.Keying != PowerconKeying.None && port.Keying != PowerconKeying.None && cableEnd.Keying != port.Keying)
                return new ConnectorCheck(ConnectorMatch.Incompatible,
                    $"{cableEnd.DisplayName} does not mate {port.DisplayName} (powerCON colours are keyed)");
            return new ConnectorCheck(ConnectorMatch.Compatible,
                cableEnd.Id == port.Id ? "same connector" : $"{cableEnd.DisplayName} mates {port.DisplayName}");
        }

        // A multicore cable that ends on a single-circuit power port is a breakout tail.
        if (cableEnd.Domain == ConnectorDomain.Multicore && port.Domain == ConnectorDomain.Power)
            return new ConnectorCheck(ConnectorMatch.Compatible, $"{cableEnd.DisplayName} breakout tail");
        if (cableEnd.Domain == ConnectorDomain.Power && port.Domain == ConnectorDomain.Multicore)
            return Adapter($"{port.DisplayName} outlet needs a breakout to {cableEnd.DisplayName}");

        if (IsPair(cableEnd, port, "XLR3", "XLR5"))
            return Adapter("XLR 3-pin to 5-pin adapter needed");
        if (IsPair(cableEnd, port, "powerCON", "powerCON TRUE1"))
            return Adapter("legacy powerCON (blue/grey) does not mate powerCON TRUE1; an adapter cable is needed");
        if (IsSchukoCee(cableEnd, port))
            return Adapter("Schuko to CEE adapter needed");

        bool cablePower = IsPowerish(cableEnd.Domain), portPower = IsPowerish(port.Domain);
        bool cableData = IsDataish(cableEnd.Domain), portData = IsDataish(port.Domain);
        if ((cablePower && portPower) || (cableData && portData))
            return Adapter($"adapter from {cableEnd.DisplayName} to {port.DisplayName} needed");
        if (cableEnd.Domain == ConnectorDomain.Other || port.Domain == ConnectorDomain.Other)
            return new ConnectorCheck(ConnectorMatch.Incompatible, $"{port.DisplayName} is not a lighting power or data connector");
        return new ConnectorCheck(ConnectorMatch.Incompatible, $"{cableEnd.DisplayName} cannot plug into {port.DisplayName}");
    }

    /// <summary>
    /// Checks a cable type plugged into a port: the signals first, then the cable end that meets the port — the
    /// source end (<see cref="CableType.ConnectorA"/>) on an output, the load end (<see cref="CableType.ConnectorB"/>)
    /// on an input, the better of the two on a bidirectional port — against the port connector and its alternatives.
    /// </summary>
    public static ConnectorCheck Check(CableType cable, Port port)
    {
        ArgumentNullException.ThrowIfNull(cable);
        ArgumentNullException.ThrowIfNull(port);
        if (!SignalKinds.IsCompatible(cable.Signal, port.Signal))
            return new ConnectorCheck(ConnectorMatch.Incompatible,
                $"a {cable.Signal.DisplayName()} cable cannot be plugged into a {port.Signal.DisplayName()} port");

        ConnectorCheck best = new(ConnectorMatch.Incompatible, "");
        bool first = true;
        foreach (var end in EndsFor(cable, port.Direction))
        {
            foreach (var pc in port.AllConnectors())
            {
                var c = CheckConnectors(end, pc);
                if (first || c.Match < best.Match)
                {
                    best = c;
                    first = false;
                }
                if (best.Match == ConnectorMatch.Compatible) return best;
            }
        }
        return best;
    }

    /// <summary>The cable end(s) that meet a port of the given direction.</summary>
    public static IEnumerable<ConnectorInfo> EndsFor(CableType cable, PortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(cable);
        if (direction != PortDirection.Input) yield return cable.ConnectorA;
        if (direction != PortDirection.Output && (direction == PortDirection.Input || cable.ConnectorB.Id != cable.ConnectorA.Id))
            yield return cable.ConnectorB;
    }

    private static ConnectorCheck Adapter(string reason) => new(ConnectorMatch.NeedsAdapter, reason);

    private static bool IsPair(ConnectorInfo a, ConnectorInfo b, string familyA, string familyB) =>
        (a.Family == familyA && b.Family == familyB) || (a.Family == familyB && b.Family == familyA);

    private static bool IsSchukoCee(ConnectorInfo a, ConnectorInfo b)
    {
        static bool Cee(ConnectorInfo c) => c.Family.StartsWith("CEE", StringComparison.Ordinal);
        return (a.Family == "Schuko" && Cee(b)) || (b.Family == "Schuko" && Cee(a));
    }

    private static bool IsPowerish(ConnectorDomain d) => d is ConnectorDomain.Power or ConnectorDomain.Multicore or ConnectorDomain.Hybrid;

    private static bool IsDataish(ConnectorDomain d) => d is ConnectorDomain.Dmx or ConnectorDomain.Ethernet or ConnectorDomain.Hybrid;
}
