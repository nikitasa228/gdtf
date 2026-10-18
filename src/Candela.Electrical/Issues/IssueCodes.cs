namespace Candela.Electrical;

/// <summary>
/// Stable <c>Issue.Code</c> values reported by this module (the issues panel filters and silences by code).
/// Severities are given in brackets.
/// </summary>
public static class IssueCodes
{
    // ---- cables (category Power or Data, after the cable's signal)

    /// <summary>[Warning] The cable type id is not in the catalogue.</summary>
    public const string CableUnknownType = "cable.unknown-type";

    /// <summary>[Warning] An endpoint refers to an entity that no longer exists.</summary>
    public const string CableEndpointMissing = "cable.endpoint-missing";

    /// <summary>[Warning] An endpoint refers to a port the device does not have.</summary>
    public const string CableUnknownPort = "cable.unknown-port";

    /// <summary>[Warning] An endpoint refers to a node the cable does not have.</summary>
    public const string CableEndpointNodeMissing = "cable.endpoint-node-missing";

    /// <summary>[Error] Wrong signal or physically non-mating connector.</summary>
    public const string CableWrongConnector = "cable.wrong-connector";

    /// <summary>[Warning] The connectors differ: an adapter is needed.</summary>
    public const string CableAdapterNeeded = "cable.adapter-needed";

    /// <summary>[Error] Two cables are plugged into one port.</summary>
    public const string CablePortOccupied = "cable.port-occupied";

    /// <summary>[Warning] A node is anchored to an entity that no longer exists.</summary>
    public const string CableAnchorMissing = "cable.anchor-missing";

    /// <summary>[Warning] Broken node/segment data (missing nodes, NaN positions, loops).</summary>
    public const string CableInvalidGeometry = "cable.invalid-geometry";

    // ---- data

    /// <summary>[Warning] A patched fixture has no data path from a console.</summary>
    public const string DataNoSignal = "data.no-signal";

    /// <summary>[Error] The fixture's patched universe is not the one arriving at its input.</summary>
    public const string DataUniverseMismatch = "data.universe-mismatch";

    /// <summary>[Warning] A DMX/Ethernet cable branches to more than two devices without a splitter.</summary>
    public const string DataYSplit = "data.y-split";

    /// <summary>[Warning] More than 32 devices on one DMX segment.</summary>
    public const string DataChainTooLong = "data.chain-too-long";

    /// <summary>[Error] The data cabling forms a loop.</summary>
    public const string DataLoop = "data.loop";

    /// <summary>[Warning] A DMX segment or an Ethernet link is longer than the cable type's maximum run.</summary>
    public const string DataRunTooLong = "data.run-too-long";

    /// <summary>[Error] Two data outputs are connected to each other.</summary>
    public const string DataOutputConflict = "data.output-conflict";

    /// <summary>[Warning] A device receives data from more than one console output.</summary>
    public const string DataMultipleSources = "data.multiple-sources";

    // ---- power

    /// <summary>[Error] An outlet circuit draws more than its breaker.</summary>
    public const string PowerBreakerOverload = "power.breaker-overload";

    /// <summary>[Error] A link (thru) output carries more than its rating (TRUE1 16 A by default).</summary>
    public const string PowerLinkOverload = "power.link-overload";

    /// <summary>[Error] A device inlet carries more than its connector rating (own draw + linked devices).</summary>
    public const string PowerInletOverload = "power.inlet-overload";

    /// <summary>[Error] A power cable carries more than its rated current.</summary>
    public const string PowerCableOverload = "power.cable-overload";

    /// <summary>[Error] A dimmer/PSU channel carries more than its per-channel limit.</summary>
    public const string PowerChannelOverload = "power.channel-overload";

    /// <summary>[Error] A dimmer/PSU delivers more than its total load rating.</summary>
    public const string PowerDistributionOverload = "power.distribution-overload";

    /// <summary>[Warning] A fixture (or fed distro) has no power.</summary>
    public const string PowerUnpowered = "power.unpowered";

    /// <summary>[Error] The supply voltage/frequency is not one the device accepts.</summary>
    public const string PowerVoltageMismatch = "power.voltage-mismatch";

    /// <summary>[Note] The phases of a three-phase source are unevenly loaded.</summary>
    public const string PowerPhaseImbalance = "power.phase-imbalance";

    /// <summary>[Error] Two power outputs are connected by one cable (back-feed).</summary>
    public const string PowerBackfeed = "power.backfeed";

    /// <summary>[Error] A device is fed from two places.</summary>
    public const string PowerMultipleFeeds = "power.multiple-feeds";

    /// <summary>[Error] A multicore cable has more tails than circuits.</summary>
    public const string PowerMulticoreCircuits = "power.multicore-circuits";

    /// <summary>[Note] An electronic load (LED, discharge) hangs on a dimmed channel.</summary>
    public const string PowerDimmedElectronicLoad = "power.dimmed-electronic-load";

    /// <summary>[Note] A device's draw is estimated or unknown.</summary>
    public const string PowerEstimatedLoad = "power.estimated-load";

    /// <summary>[Error] A power source has an invalid voltage.</summary>
    public const string PowerSourceInvalid = "power.source-invalid";

    // ---- fixtures

    /// <summary>[Note] The fixture's GDTF is not in the library: no ports, power or weight are known.</summary>
    public const string FixtureUnknownType = "fixture.unknown-type";

    /// <summary>[Note] The fixture's data is incomplete (GDTF unreadable, DMX mode missing); specs data is used.</summary>
    public const string FixtureDataIncomplete = "fixture.data-incomplete";

    // ---- rigging

    /// <summary>[Warning] A fixture above the floor is not attached to a truss/pipe.</summary>
    public const string RiggingNotMounted = "rigging.not-mounted";

    /// <summary>[Error] A fixture is mounted on an entity that does not exist.</summary>
    public const string RiggingMountMissing = "rigging.mount-missing";

    /// <summary>[Error] A truss carries more than its maximum uniform load.</summary>
    public const string RiggingTrussOverload = "rigging.truss-overload";

    /// <summary>[Error] A single load on a truss exceeds its maximum point load.</summary>
    public const string RiggingTrussPointOverload = "rigging.truss-point-overload";

    /// <summary>[Error] A rigging point carries more than its capacity.</summary>
    public const string RiggingPointOverload = "rigging.point-overload";

    /// <summary>[Warning] A rigging point carries an entity that does not exist.</summary>
    public const string RiggingPointMissingTarget = "rigging.point-missing-target";

    /// <summary>[Note] A mounted fixture's weight is unknown.</summary>
    public const string RiggingUnknownWeight = "rigging.unknown-weight";
}
