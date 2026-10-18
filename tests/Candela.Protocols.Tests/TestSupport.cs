using System.Diagnostics;
using System.Net;
using Candela.Dmx.Input;
using Candela.Protocols.Networking;

namespace Candela.Protocols.Tests;

/// <summary>A clock the test moves by hand (for timeouts such as ArtSync's 4 s).</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;

    public DateTime UtcDateTime => Now.UtcDateTime;
}

internal static class TestSupport
{
    /// <summary>Loopback options with every UDP port set to 0 (a free port is chosen at start).</summary>
    public static ProtocolOptions LoopbackOptions(params int[] subscribed) => new()
    {
        NetworkInterface = "",
        ArtNetPort = 0,
        SacnPort = 0,
        OscPort = 0,
        EosOscPort = 0,
        PsnPort = 0,
        SubscribedUniverses = subscribed.Length > 0 ? subscribed : ProtocolOptions.DefaultUniverses,
    };

    public static IPEndPoint Loopback(int port) => new(IPAddress.Loopback, port);

    public static IPv4Endpoint Sender(string ip = "10.0.0.5", int port = 6454) => IPv4Endpoint.From(new IPEndPoint(IPAddress.Parse(ip), port));

    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses.</summary>
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    public static byte[] Read(DmxInputHub hub, int universe)
    {
        var buffer = new byte[512];
        hub.TryRead(universe, buffer, out _);
        return buffer;
    }

    public static bool HasUniverse(DmxInputHub hub, int universe) => hub.TryRead(universe, new byte[512], out _);
}
