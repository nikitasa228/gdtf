using Candela.Core.Diagnostics;
using Candela.Dmx.Input;

namespace Candela.Engine.Tests;

/// <summary>DMX recorder and player: record live universes with timecode, save, load, play back, seek.</summary>
public sealed class RecordingTests
{
    private static readonly DmxSourceId Console = new("artnet", "10.0.0.1");

    private static byte[] Universe(int frame, int universe)
    {
        var slots = new byte[512];
        for (int i = 0; i < 8; i++) slots[i] = (byte)((frame * 3 + i * universe) & 0xFF);
        slots[100 + universe] = (byte)(frame % 7 == 0 ? 255 : 10);
        return slots;
    }

    private static Timecode Tc(double seconds) =>
        new((int)(seconds / 3600), (int)(seconds / 60) % 60, (int)seconds % 60, (int)(seconds * 25) % 25, TimecodeType.Ebu25);

    private static DmxRecording Record(out List<(double Time, byte[] U1, byte[] U2)> expected)
    {
        // a hub clock that stands still: the packets are stamped with the engine frames (the floor of the timeline)
        var now = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        var hub = new DmxInputHub(() => now);
        var recorder = new DmxRecorder(hub);
        expected = new();
        recorder.Start(100.0, "Show");
        for (int f = 0; f < 120; f++)
        {
            double t = 100.0 + f / 30.0;
            var u1 = Universe(f, 1);
            var u2 = Universe(f / 2, 2); // universe 2 changes every other frame
            // the engine frame at t, then the packets that arrive during it (stamped no earlier than t)
            recorder.Capture(t);
            hub.Submit(Console, 1, u1);
            if (f < 90) hub.Submit(Console, 2, u2);
            else if (f == 90) hub.Terminate(Console, 2);
            hub.SubmitTimecode(Tc(3600 + f / 30.0));
            expected.Add((f / 30.0, u1, f < 90 ? u2 : Array.Empty<byte>()));
        }
        Assert.True(recorder.IsRecording);
        var recording = recorder.Stop();
        Assert.NotNull(recording);
        Assert.False(recorder.IsRecording);
        return recording!;
    }

    [Fact]
    public void Recording_round_trips_through_a_file_and_plays_back_the_same_universes()
    {
        var recording = Record(out var expected);
        Assert.Equal(119 / 30.0, recording.Duration, 6);
        Assert.Equal(new[] { 1, 2 }, recording.Universes);
        Assert.Equal(Tc(3600), recording.FirstTimecode);

        string path = Path.Combine(TestEnvironment.NewTempDirectory("rec"), "show" + DmxRecording.Extension);
        recording.Save(path);
        var loaded = DmxRecording.Load(path);
        Assert.Empty(loaded.Issues);
        Assert.Equal("Show", loaded.Name);
        Assert.Equal(recording.Frames.Count, loaded.Frames.Count);
        // delta encoding keeps the file small: 120 frames × 2 universes × 512 slots raw would be 120 KiB
        Assert.True(new FileInfo(path).Length < 8 * 1024, $"{new FileInfo(path).Length} bytes");

        var hub = new DmxInputHub();
        using var player = new DmxPlayer(loaded, hub, offset: 50.0);
        var slots = new byte[512];
        foreach (var (time, u1, u2) in expected)
        {
            player.PlayTo(50.0 + time);
            Assert.True(hub.TryRead(1, slots, out _));
            Assert.Equal(u1, slots);
            if (u2.Length > 0)
            {
                Assert.True(hub.TryRead(2, slots, out _));
                Assert.Equal(u2, slots);
            }
            else
            {
                Assert.False(hub.TryRead(2, slots, out _), "universe 2 was lost at frame 90");
            }
        }
        Assert.True(player.IsAtEnd);

        // backwards: the state is rebuilt from the nearest keyframe
        player.PlayTo(50.0 + 1.0);
        Assert.True(hub.TryRead(2, slots, out _));
        Assert.Equal(expected[30].U2, slots);
        Assert.Equal(expected[30].U1, ReadU1(hub));
    }

    [Fact]
    public void Seeking_by_timecode_lands_on_the_frame_that_received_it()
    {
        var recording = Record(out var expected);
        var hub = new DmxInputHub();
        using var player = new DmxPlayer(recording, hub);
        Assert.True(player.SeekTimecode(Tc(3601.2), engineTime: 10.0));
        Assert.Equal(1.2, player.Position, 2);
        Assert.Equal(expected[36].U1, ReadU1(hub));
        Assert.Equal(Tc(3601.2), hub.LastTimecode);
        Assert.False(player.SeekTimecode(Tc(7200), 10.0));
    }

    [Fact]
    public void A_truncated_recording_keeps_the_frames_before_the_damage()
    {
        var recording = Record(out _);
        using var full = new MemoryStream();
        recording.Save(full);
        var bytes = full.ToArray();
        var cut = DmxRecording.Load(new MemoryStream(bytes[..(bytes.Length * 2 / 3)]));
        Assert.Contains(cut.Issues, i => i.Code == EngineIssueCodes.RecordingDamaged && i.Severity == IssueSeverity.Warning);
        Assert.InRange(cut.Frames.Count, 1, recording.Frames.Count - 1);
        for (int i = 0; i < cut.Frames.Count; i++) Assert.Equal(recording.Frames[i].Time, cut.Frames[i].Time);

        Assert.Throws<InvalidDataException>(() => DmxRecording.Load(new MemoryStream("not a recording"u8.ToArray())));
        var flipped = (byte[])bytes.Clone();
        for (int i = 40; i < flipped.Length; i += 7) flipped[i] ^= 0x5A;
        var damaged = DmxRecording.Load(new MemoryStream(flipped)); // never throws past the header
        Assert.True(damaged.Frames.Count < recording.Frames.Count || damaged.Issues.Count > 0);
    }

    [Fact]
    public void Engine_records_its_live_input_and_plays_it_back_to_its_fixtures()
    {
        using var engine = TestEnvironment.CreateEngine("rec-engine");
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.Naja, null, new System.Numerics.Vector3(0, 0, 5), 1, 1);
        engine.Tick(0);
        var profile = engine.Dmx.GetState(head.Id)!.Profile;
        var buffer = new Candela.Dmx.FixtureDmxBuffer(profile);
        buffer.ResetToDefaults();
        Candela.Dmx.DmxEncoder.Encode(buffer, "Dimmer", 100f);
        var universe = new byte[512];
        buffer.GetBreak(0).CopyTo(universe);

        engine.Recorder.Start(engine.Time, "Take 1");
        engine.Input.Submit(Console, 1, universe);
        for (int i = 0; i < 10; i++) engine.Tick(0.05);
        engine.Input.Terminate(Console, 1);
        engine.Tick(0.05);
        var recording = engine.Recorder.Stop()!;
        Assert.True(recording.Frames.Count >= 2);
        float live = engine.Dmx.GetState(head.Id)!.Beams.Max(b => b.Intensity);
        Assert.Equal(0f, live); // the console stopped: manual values (dark)

        engine.Play(recording);
        engine.Tick(0.1);
        Assert.True(engine.Dmx.IsConsoleDriven(head.Id));
        Assert.True(engine.Dmx.GetState(head.Id)!.Beams.Max(b => b.Intensity) > 0.9f);
        engine.Play(null);
        engine.Tick(0.05);
        Assert.False(engine.Dmx.IsConsoleDriven(head.Id));
    }

    private static byte[] ReadU1(DmxInputHub hub)
    {
        var slots = new byte[512];
        Assert.True(hub.TryRead(1, slots, out _));
        return slots;
    }
}
