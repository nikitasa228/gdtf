using System.IO.Compression;
using System.Text;
using Candela.Dmx.Input;

namespace Candela.Engine.Tests;

/// <summary>
/// Packet-rate DMX recording: every change is stored with its own arrival time from the hub's receive path, not once
/// per engine frame; format version 2 with version-1 files still loading; seeking by timecode between engine frames.
/// </summary>
public sealed class PacketRecordingTests
{
    private static readonly DmxSourceId Desk = new("artnet", "10.0.0.9:6454");

    private static Timecode Tc(int frames) => new(0, 0, frames / 25, frames % 25, TimecodeType.Ebu25);

    [Fact]
    public void A_44_hz_console_recorded_by_a_30_fps_engine_keeps_every_packet_at_its_arrival_time()
    {
        var start = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        var now = start;
        var hub = new DmxInputHub(() => now);
        var recorder = new DmxRecorder(hub);
        recorder.Start(engineTime: 10.0, "Live");
        var slots = new byte[512];
        int packets = 0;
        double nextFrame = 0;
        // one second: packets every 1/44 s, engine frames every 1/30 s, timecode every 1/25 s
        for (int step = 0; step <= 1000; step++)
        {
            double t = step / 1000.0;
            now = start + TimeSpan.FromSeconds(t);
            if (step == 0 || step * 44 / 1000 != (step - 1) * 44 / 1000)
            {
                slots[0] = (byte)packets;
                slots[1] = (byte)(255 - packets);
                hub.Submit(Desk, 1, slots);
                packets++;
            }
            if (step % 40 == 0) hub.SubmitTimecode(Tc(step / 40), Desk);
            if (t >= nextFrame)
            {
                recorder.Capture(10.0 + t);
                nextFrame += 1 / 30.0;
            }
        }
        var recording = recorder.Stop()!;
        Assert.Equal(DmxCaptureMode.Packets, recording.Capture);

        var dmxFrames = recording.Frames.Where(f => f.Changes.Count > 0).ToList();
        Assert.Equal(packets, dmxFrames.Count); // every packet changed the universe: one frame each, more than 30
        Assert.True(packets > 40);
        // the times are the arrival times (whole milliseconds here), not the engine frames
        Assert.Contains(dmxFrames, f => Math.Abs(f.Time * 30 - Math.Round(f.Time * 30)) > 0.05);
        Assert.All(recording.Frames, f => Assert.True(Math.Abs(f.Time * 1000 - Math.Round(f.Time * 1000)) < 1e-3, $"{f.Time}"));

        // the timecode arrivals are flagged, and a seek lands on the arrival, between engine frames
        Assert.Equal(26, recording.Frames.Count(f => f.TimecodeReceived));
        Assert.Equal(0.32, recording.FindTimecode(Tc(8))!.Value, 6);
        var hub2 = new DmxInputHub(() => now);
        using var player = new DmxPlayer(recording, hub2);
        Assert.True(player.SeekTimecode(Tc(8), engineTime: 0));
        Assert.Equal(0.32, player.Position, 6);
        var played = new byte[512];
        Assert.True(hub2.TryRead(1, played, out _));
        int expected = (int)Math.Floor(0.32 * 44); // packets 0 … floor(0.32·44) had arrived by then
        Assert.Equal((byte)expected, played[0]);
        Assert.Equal(new DmxSourceId(DmxPlayer.SourceProtocol, "Live"), hub2.LastTimecodeSource);
    }

    [Fact]
    public void Unchanged_packets_are_not_recorded_and_lost_universes_are_stamped_when_they_timed_out()
    {
        var start = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        var now = start;
        var hub = new DmxInputHub(() => now) { SourceTimeout = TimeSpan.FromSeconds(1) };
        var slots = new byte[512];
        slots[5] = 99;
        hub.Submit(Desk, 3, slots); // already live when the recording starts: recorded in full at time 0
        var recorder = new DmxRecorder(hub);
        recorder.Start("Idle");
        for (int i = 1; i <= 10; i++)
        {
            now = start + TimeSpan.FromSeconds(i * 0.02);
            hub.Submit(Desk, 3, slots);
        }
        now = start + TimeSpan.FromSeconds(3);
        recorder.Capture(3.0); // the periodic scan expires the silent source
        var recording = recorder.Stop()!;
        Assert.Equal(2, recording.Frames.Count);
        var full = recording.Frames[0];
        Assert.Equal(0, full.Time);
        Assert.Equal(DmxUniverseChangeKind.Full, Assert.Single(full.Changes).Kind);
        var lost = recording.Frames[1];
        Assert.Equal(DmxUniverseChangeKind.Lost, Assert.Single(lost.Changes).Kind);
        Assert.Equal(0.2 + 1.0, lost.Time, 6); // last packet + timeout
        Assert.Null(recorder.Stop());
    }

    [Fact]
    public void Version_1_recordings_still_load_and_are_saved_as_version_2()
    {
        var created = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        using var v1 = new MemoryStream();
        using (var header = new BinaryWriter(v1, Encoding.UTF8, leaveOpen: true))
        {
            header.Write("CDMX"u8.ToArray());
            header.Write(1);
            header.Write(created.Ticks);
            header.Write("Old show");
        }
        using (var z = new ZLibStream(v1, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(z, Encoding.UTF8, leaveOpen: true))
        {
            for (int f = 0; f < 30; f++)
            {
                w.Write((byte)0xF1);
                w.Write(f / 30.0);
                // the v1 recorder stamped every frame with the last timecode: a new one every 1.2 frames here
                var tc = Tc(f * 25 / 30);
                w.Write((byte)1);
                w.Write((byte)tc.Hours);
                w.Write((byte)tc.Minutes);
                w.Write((byte)tc.Seconds);
                w.Write((byte)tc.Frames);
                w.Write((byte)tc.Type);
                w.Write7BitEncodedInt(1);
                w.Write7BitEncodedInt(1);
                if (f == 0)
                {
                    w.Write((byte)DmxUniverseChangeKind.Full);
                    w.Write7BitEncodedInt(1);
                    w.Write7BitEncodedInt(0);
                    w.Write7BitEncodedInt(512);
                    w.Write(new byte[512]);
                }
                else
                {
                    w.Write((byte)DmxUniverseChangeKind.Delta);
                    w.Write7BitEncodedInt(1);
                    w.Write7BitEncodedInt(0);
                    w.Write7BitEncodedInt(1);
                    w.Write((byte)f);
                }
            }
        }
        v1.Position = 0;
        var old = DmxRecording.Load(v1);
        Assert.Empty(old.Issues);
        Assert.Equal(1, old.LoadedFormatVersion);
        Assert.Equal(DmxCaptureMode.EngineFrames, old.Capture);
        Assert.Equal("Old show", old.Name);
        Assert.Equal(created, old.CreatedUtc);
        Assert.Equal(30, old.Frames.Count);
        // the carried timecodes are not arrivals: a seek lands on the first frame that had the timecode
        Assert.Equal(6 / 30.0, old.FindTimecode(Tc(5))!.Value, 6);

        using var v2 = new MemoryStream();
        old.Save(v2);
        Assert.Equal(DmxRecording.FormatVersion, BitConverter.ToInt32(v2.ToArray(), 4));
        v2.Position = 0;
        var migrated = DmxRecording.Load(v2);
        Assert.Equal(2, migrated.LoadedFormatVersion);
        Assert.Equal(DmxCaptureMode.EngineFrames, migrated.Capture);
        Assert.Equal(old.Frames.Select(f => (f.Time, f.Timecode)), migrated.Frames.Select(f => (f.Time, f.Timecode)));
        Assert.Equal(old.FindTimecode(Tc(5)), migrated.FindTimecode(Tc(5)));
        Assert.Equal(25, migrated.Frames.Count(f => f.TimecodeReceived)); // the arrivals derived from version 1 are kept

        // a file from a newer Candela is refused clearly
        var newer = v2.ToArray();
        BitConverter.GetBytes(DmxRecording.FormatVersion + 1).CopyTo(newer, 4);
        var e = Assert.Throws<InvalidDataException>(() => DmxRecording.Load(new MemoryStream(newer)));
        Assert.Contains("newer", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_records_at_packet_rate_while_it_ticks()
    {
        using var engine = TestEnvironment.CreateEngine("packet-rec");
        var slots = new byte[512];
        engine.Recorder.Start(engine.Time, "Engine");
        for (int i = 0; i < 6; i++)
        {
            // three packets per engine frame
            for (int k = 0; k < 3; k++)
            {
                slots[0] = (byte)(i * 3 + k + 1);
                engine.Input.Submit(Desk, 1, slots);
            }
            engine.Tick(1 / 30.0);
        }
        var recording = engine.Recorder.Stop()!;
        Assert.Equal(18, recording.Frames.Count(f => f.Changes.Count > 0));
        Assert.True(recording.Frames.Zip(recording.Frames.Skip(1)).All(p => p.First.Time <= p.Second.Time));
    }
}
