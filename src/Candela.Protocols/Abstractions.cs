namespace Candela.Protocols;

/// <summary>How completely Candela supports an external-control protocol. Shown next to every entry of the
/// external-control panel so the user knows what to expect before selecting it.</summary>
public enum ProtocolSupportLevel
{
    /// <summary>Fully implemented: the protocol can be started and its data reaches the DMX input hub (or, for
    /// tracking protocols, the tracker table).</summary>
    Implemented,

    /// <summary>Partly implemented; the support reason says which part works.</summary>
    Partial,

    /// <summary>Listed so the user sees the whole matrix; not implemented yet and cannot be started.</summary>
    Planned,

    /// <summary>Needs a vendor SDK or licence that Candela cannot ship; cannot be started.</summary>
    RequiresVendorSdk,
}

/// <summary>Run state of one protocol, as shown in the external-control panel.</summary>
public enum ProtocolState
{
    /// <summary>Not started (or stopped by the user).</summary>
    Stopped,

    /// <summary>Started and receiving (or waiting for) data.</summary>
    Running,

    /// <summary>Started, but something is degraded (for example a multicast group could not be joined; unicast still
    /// works). See <see cref="ProtocolStatus.Warnings"/>.</summary>
    Warning,

    /// <summary>Could not start or failed while running (port in use, interface missing, device unplugged). See
    /// <see cref="ProtocolStatus.LastError"/>.</summary>
    Error,

    /// <summary>The protocol is planned or needs a vendor SDK, so it cannot be started.</summary>
    Unavailable,
}

/// <summary>Static facts about a protocol: identity, name for the UI, honest support level and the reason for it.</summary>
/// <param name="Id">Stable machine id (see <see cref="ProtocolIds"/>), used in settings files.</param>
/// <param name="DisplayName">Name shown in the external-control panel.</param>
/// <param name="Description">One or two sentences: what the protocol is and which ports/devices it uses.</param>
/// <param name="SupportLevel">How completely Candela supports it.</param>
/// <param name="SupportReason">Why the level is what it is and what works today (shown as a tooltip).</param>
public sealed record ProtocolDescriptor(
    string Id,
    string DisplayName,
    string Description,
    ProtocolSupportLevel SupportLevel,
    string SupportReason)
{
    /// <summary>True when the protocol can be started (implemented or partial).</summary>
    public bool CanStart => SupportLevel is ProtocolSupportLevel.Implemented or ProtocolSupportLevel.Partial;
}

/// <summary>
/// One way a lighting console (or tracking system) can talk to Candela. Implementations feed DMX into
/// <see cref="Candela.Dmx.Input.DmxInputHub"/> from their own background thread and never throw from
/// <see cref="Start"/> for run-time problems (port in use, interface missing): those become <see cref="Status"/>
/// messages with <see cref="ProtocolState.Error"/>.
/// </summary>
public interface IExternalProtocol : IDisposable
{
    /// <summary>Stable machine id (see <see cref="ProtocolIds"/>).</summary>
    string Id { get; }

    /// <summary>Name shown in the external-control panel.</summary>
    string DisplayName { get; }

    /// <summary>What the protocol is and which ports/devices it uses.</summary>
    string Description { get; }

    /// <summary>How completely Candela supports the protocol.</summary>
    ProtocolSupportLevel SupportLevel { get; }

    /// <summary>Why the support level is what it is and what works today.</summary>
    string SupportReason { get; }

    /// <summary>True while the protocol is started (state <see cref="ProtocolState.Running"/> or
    /// <see cref="ProtocolState.Warning"/>).</summary>
    bool IsRunning { get; }

    /// <summary>A fresh snapshot of state, counters and per-universe statistics (thread-safe).</summary>
    ProtocolStatus Status { get; }

    /// <summary>
    /// Starts (or restarts with new options) the protocol. Returns false and sets <see cref="Status"/> to
    /// <see cref="ProtocolState.Error"/> or <see cref="ProtocolState.Unavailable"/> instead of throwing when the
    /// protocol cannot run.
    /// </summary>
    bool Start(ProtocolOptions options);

    /// <summary>Stops receiving and withdraws every universe this protocol submitted to the hub. Idempotent.</summary>
    void Stop();

    /// <summary>Raised (on any thread) when the state, message, error or warnings change — not per packet.</summary>
    event EventHandler<ProtocolStatusEventArgs>? StatusChanged;
}

/// <summary>Event data of <see cref="IExternalProtocol.StatusChanged"/>.</summary>
public sealed class ProtocolStatusEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public ProtocolStatusEventArgs(IExternalProtocol protocol, ProtocolStatus status)
    {
        Protocol = protocol;
        Status = status;
    }

    /// <summary>The protocol whose status changed.</summary>
    public IExternalProtocol Protocol { get; }

    /// <summary>Its status right after the change.</summary>
    public ProtocolStatus Status { get; }
}

/// <summary>
/// A protocol could not start for a reason the user can fix (port in use, interface missing, serial port absent).
/// The message is written for the external-control panel. Protocols throw it from their start routine; the base
/// class turns it into an <see cref="ProtocolState.Error"/> status, so it never escapes
/// <see cref="IExternalProtocol.Start"/>.
/// </summary>
public sealed class ProtocolStartException : Exception
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public ProtocolStartException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a user-facing message and the underlying cause.</summary>
    public ProtocolStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
