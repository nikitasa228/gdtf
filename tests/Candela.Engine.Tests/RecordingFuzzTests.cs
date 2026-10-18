using Candela.Dmx.Input;

namespace Candela.Engine.Tests;

/// <summary>Damaged DMX recordings: loading succeeds with the frames before the damage, or throws InvalidDataException.</summary>
public sealed class RecordingFuzzTests
{
    private static byte[] ValidRecording()
    {
        var hub = new DmxInputHub();
        var recorder = new DmxRecorder(hub);
        recorder.Start(0, "Fuzz");
        var slots = new byte[512];
        for (int frame = 0; frame < 40; frame++)
        {
            for (int s = 0; s < 512; s += 7) slots[s] = (byte)(frame * 5 + s);
            hub.Submit(new DmxSourceId("artnet", "desk"), 1 + frame % 3, slots);
            hub.SubmitTimecode(new Timecode(1, 2, frame / 25, frame % 25, TimecodeType.Ebu25));
            recorder.Capture(frame / 25.0);
        }
        using var stream = new MemoryStream();
        recorder.Stop()!.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Damaged_recordings_load_partially_or_are_refused_with_InvalidDataException()
    {
        var valid = ValidRecording();
        var random = new Random(1234);
        for (int round = 0; round < 3000; round++)
        {
            var bytes = (byte[])valid.Clone();
            int edits = 1 + random.Next(4);
            for (int e = 0; e < edits; e++)
            {
                // most damage lands in the header and the first compressed bytes, where it matters most
                int at = random.Next(4) == 0 ? random.Next(bytes.Length) : random.Next(Math.Min(bytes.Length, 40));
                bytes[at] = (byte)random.Next(256);
            }
            if (random.Next(5) == 0) bytes = bytes[..random.Next(bytes.Length)];
            try
            {
                var recording = DmxRecording.Load(new MemoryStream(bytes));
                // whatever loads can be played and sought without exceptions
                var hub = new DmxInputHub();
                using var player = new DmxPlayer(recording, hub);
                player.PlayTo(recording.Duration / 2);
                player.Seek(0, 1);
                player.PlayTo(recording.Duration + 2);
                if (recording.FirstTimecode is { } tc) player.SeekTimecode(tc, 3);
            }
            catch (InvalidDataException)
            {
                // refused with the documented exception
            }
        }
    }

    [Fact]
    public void A_header_with_a_malformed_name_length_is_refused()
    {
        var valid = ValidRecording();
        // magic (4) + version (4) + ticks (8), then the 7-bit encoded name length: five continuation bytes are malformed
        var bytes = valid[..16].Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }).Concat(valid[16..]).ToArray();
        Assert.Throws<InvalidDataException>(() => DmxRecording.Load(new MemoryStream(bytes)));
    }
}

/// <summary>Damaged model packages (the worker's output and the disk cache) are refused with InvalidDataException.</summary>
public sealed class ModelPackageFuzzTests
{
    [Fact]
    public void Damaged_packages_load_or_are_refused_with_InvalidDataException()
    {
        var model = Assets.ModelImporter.Import(SceneBuilder.WriteCubeObj(TestEnvironment.NewTempDirectory("package-fuzz")));
        using var stream = new MemoryStream();
        ModelPackage.Write(model, stream);
        var valid = stream.ToArray();
        var random = new Random(99);
        for (int round = 0; round < 3000; round++)
        {
            var bytes = (byte[])valid.Clone();
            int edits = 1 + random.Next(3);
            for (int e = 0; e < edits; e++)
            {
                int at = random.Next(bytes.Length);
                bytes[at] = random.Next(3) == 0 ? (byte)0xFF : (byte)random.Next(256);
            }
            try
            {
                var read = ModelPackage.Read(new MemoryStream(bytes));
                foreach (var part in read.Model.Parts) part.Mesh.Validate();
            }
            catch (InvalidDataException)
            {
                // refused with the documented exception (Validate throws it too for inconsistent meshes)
            }
        }
    }
}
