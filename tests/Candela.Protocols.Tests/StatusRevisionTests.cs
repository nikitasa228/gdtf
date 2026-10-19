namespace Candela.Protocols.Tests;

/// <summary>Review of the structured status: every text a protocol reports carries a translatable key.</summary>
public sealed class StatusRevisionTests
{
    private sealed class FailingStop() : ExternalProtocolBase(
        new ProtocolDescriptor("test.failing-stop", "Failing stop", "A protocol whose stop throws.", ProtocolSupportLevel.Implemented, "test"))
    {
        protected override string OnStart(ProtocolOptions options) =>
            Running(ProtocolText.Create(ProtocolMessages.ListeningAll, 7000), "0.0.0.0:7000", null, 0);

        protected override void OnStop() => throw new IOException("device gone");
    }

    [Fact]
    public void A_protocol_that_does_not_stop_cleanly_reports_a_keyed_error()
    {
        using var protocol = new FailingStop();
        Assert.True(protocol.Start(new ProtocolOptions()));
        Assert.Equal(ProtocolMessages.ListeningAll, protocol.Status.Text.Key);
        protocol.Stop();
        var status = protocol.Status;
        Assert.Equal(ProtocolState.Stopped, status.State);
        Assert.NotNull(status.ErrorText);
        Assert.Equal(ProtocolMessages.StopFailed, status.ErrorText!.Key);
        Assert.Equal(new[] { "Failing stop", "device gone" }, status.ErrorText.Arguments);
        Assert.Equal("Failing stop did not stop cleanly: device gone", status.LastError);
        Assert.Null(status.BoundEndpoint);
    }
}
