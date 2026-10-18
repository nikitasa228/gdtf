using Candela.Protocols.Networking;

namespace Candela.Protocols.Osc;

/// <summary>A cue reference from Eos OSC output.</summary>
/// <param name="List">Cue list number ("1").</param>
/// <param name="Number">Cue number as shown on the console ("5", "5.5").</param>
public readonly record struct EosCue(string List, string Number)
{
    /// <summary>"1/5".</summary>
    public override string ToString() => $"{List}/{Number}";
}

/// <summary>What Candela knows about an ETC Eos console from its OSC output (immutable snapshot).</summary>
/// <param name="ShowName">Name of the open show file.</param>
/// <param name="ActiveCue">The cue currently running or completed in the active list.</param>
/// <param name="ActiveCueText">Eos's text for the active cue ("1/5 Blue wash 3 100%").</param>
/// <param name="ActiveCueProgress">Fade progress of the active cue 0..1.</param>
/// <param name="PendingCue">The next cue (GO target).</param>
/// <param name="PendingCueText">Eos's text for the pending cue.</param>
/// <param name="PreviousCue">The cue before the active one.</param>
/// <param name="LastFiredCue">The last cue fired (event).</param>
/// <param name="CommandLine">Current command line text.</param>
/// <param name="Live">True in Live, false in Blind, null when unknown.</param>
/// <param name="LastMessageUtc">When the last Eos message arrived.</param>
public sealed record EosConsoleState(
    string? ShowName,
    EosCue? ActiveCue,
    string? ActiveCueText,
    double ActiveCueProgress,
    EosCue? PendingCue,
    string? PendingCueText,
    EosCue? PreviousCue,
    EosCue? LastFiredCue,
    string? CommandLine,
    bool? Live,
    DateTime? LastMessageUtc)
{
    /// <summary>Nothing received yet.</summary>
    public static EosConsoleState Empty { get; } = new(null, null, null, 0, null, null, null, null, null, null, null);
}

/// <summary>
/// ETC Eos family integration via OSC (partial support): listens for the console's OSC output ("OSC UDP TX" in the Eos
/// shell, default port 8001 here) and tracks show name, active/pending/previous cue, cue fire events, the command line
/// and live/blind state. Every message is also exposed raw. Fixture levels must come from the console's Art-Net or
/// sACN output (start those protocols as well).
/// </summary>
public sealed class EtcEosReceiver : OscReceiverBase
{
    private const string Prefix = "/eos/out/";
    private EosConsoleState _state = EosConsoleState.Empty;

    /// <summary>Creates the receiver.</summary>
    public EtcEosReceiver(TimeProvider? timeProvider = null)
        : base(ProtocolRegistry.Get(ProtocolIds.EtcEos), timeProvider)
    {
    }

    /// <summary>The latest console state (thread-safe snapshot).</summary>
    public EosConsoleState State => Volatile.Read(ref _state);

    /// <summary>Raised on the receive thread when the console fires a cue (/eos/out/event/cue/{list}/{cue}/fire).</summary>
    public event Action<EosCue>? CueFired;

    /// <inheritdoc />
    protected override string ProtocolLabel => "ETC Eos OSC";

    /// <inheritdoc />
    protected override int PortFrom(ProtocolOptions options) => options.EosOscPort;

    /// <inheritdoc />
    protected override void OnConfigure(ProtocolOptions options) => Volatile.Write(ref _state, EosConsoleState.Empty);

    /// <inheritdoc />
    protected override void OnClosed()
    {
    }

    /// <inheritdoc />
    protected override void OnMessages(IReadOnlyList<OscMessage> messages, IPv4Endpoint sender, string senderText, DateTime nowUtc)
    {
        var state = State;
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (!m.Address.StartsWith(Prefix, StringComparison.Ordinal))
            {
                CountIgnoredPacket();
                continue;
            }
            state = Apply(state, m, out var fired) with { LastMessageUtc = nowUtc };
            if (fired is { } cue) CueFired?.Invoke(cue);
        }
        Volatile.Write(ref _state, state);
    }

    /// <summary>Applies one Eos output message to a state (pure; exposed for tests and replay).</summary>
    public static EosConsoleState Apply(EosConsoleState state, OscMessage message, out EosCue? firedCue)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(message);
        firedCue = null;
        var address = message.Address;
        if (!address.StartsWith(Prefix, StringComparison.Ordinal)) return state;
        var parts = address[Prefix.Length..].Split('/');
        string? text = message.Arguments.Count > 0 ? message.Arguments[0].AsString : null;
        double? number = message.Arguments.Count > 0 && message.Arguments[0].IsNumeric ? message.Arguments[0].AsDouble : null;

        switch (parts)
        {
            case ["show", "name"]:
                return state with { ShowName = text ?? state.ShowName };
            case ["cmd"]:
            case ["user", _, "cmd"]:
                return state with { CommandLine = text ?? "" };
            case ["event", "state"] when number is { } live:
                return state with { Live = live >= 0.5 };
            case ["active", "cue", "text"]:
                return state with { ActiveCueText = text };
            case ["active", "cue", var list, var cue]:
                // Progress arrives as a fraction; tolerate consoles/bridges that send a percentage.
                double progress = number is { } p ? (p > 1 ? p / 100 : p) : state.ActiveCueProgress;
                return state with { ActiveCue = new EosCue(list, cue), ActiveCueProgress = Math.Clamp(progress, 0, 1) };
            case ["active", "cue"]:
                return state with { ActiveCue = null, ActiveCueText = null, ActiveCueProgress = 0 };
            case ["pending", "cue", "text"]:
                return state with { PendingCueText = text };
            case ["pending", "cue", var list, var cue]:
                return state with { PendingCue = new EosCue(list, cue) };
            case ["pending", "cue"]:
                return state with { PendingCue = null, PendingCueText = null };
            case ["previous", "cue", "text"]:
                return state;
            case ["previous", "cue", var list, var cue]:
                return state with { PreviousCue = new EosCue(list, cue) };
            case ["event", "cue", var list, var cue, "fire"]:
                firedCue = new EosCue(list, cue);
                return state with { LastFiredCue = firedCue };
            default:
                return state;
        }
    }
}
