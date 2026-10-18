using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Candela.Project.Tests;

/// <summary>
/// Deterministic fuzzing of the reader: thousands of damaged variants of a rich project. Loading must either succeed or
/// throw the documented <see cref="ProjectFormatException"/> (never through the safety net that catches unexpected reader
/// failures), and whatever loads must save and load again to the same scene.
/// </summary>
public sealed class FuzzTests
{
    private static readonly Lazy<(string Name, byte[] Data)[]> s_entries = new(() =>
    {
        using var dir = new TempDir();
        var path = dir.File("rich.candela");
        ProjectFile.Save(path, TestData.RichProject(), new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf, CompressionLevel = CompressionLevel.Fastest });
        return TestData.EntryNames(path).Select(n => (n, TestData.ReadEntry(path, n))).ToArray();
    });

    private static readonly string[] s_tokens =
    {
        "null", "\"\\ud800\"", "\"\\udfff\\ud800\"", "[", "]", "{", "}", "1e999", "-1", "\"\"", "true", "\"$type\"", ",", ":",
        "\"x\\u0000\"", "99999999999999999999", "\"NaN\"", "[1,2]", "{\"$type\":\"fixture\"}",
    };

    [Fact]
    public void Byte_level_damage_to_json_documents_never_escapes_the_exception_contract()
    {
        var rng = new Random(12345);
        var entries = s_entries.Value;
        var json = Enumerable.Range(0, entries.Length).Where(i => entries[i].Name.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        var failures = new Dictionary<string, int>();
        for (int iteration = 0; iteration < 1500; iteration++)
        {
            int which = json[rng.Next(json.Length)];
            var data = entries[which].Data;
            switch (rng.Next(4))
            {
                case 0:
                    data = (byte[])data.Clone();
                    for (int k = 0; k < 1 + rng.Next(4); k++) data[rng.Next(data.Length)] = (byte)rng.Next(256);
                    break;
                case 1:
                    data = data[..rng.Next(data.Length)];
                    break;
                default:
                    var text = Encoding.UTF8.GetString(data);
                    int at = rng.Next(text.Length + 1);
                    int end = Math.Min(text.Length, at + rng.Next(12));
                    data = Encoding.UTF8.GetBytes(text[..at] + s_tokens[rng.Next(s_tokens.Length)] + text[end..]);
                    break;
            }
            Check(Replace(entries, which, data), failures, roundTrip: false);
        }
        Assert.Empty(failures.Select(f => $"{f.Value}x {f.Key}"));
    }

    [Fact]
    public void Structural_damage_to_json_documents_loads_tolerantly_and_round_trips()
    {
        var rng = new Random(777);
        var entries = s_entries.Value;
        var json = Enumerable.Range(0, entries.Length).Where(i => entries[i].Name.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        var failures = new Dictionary<string, int>();
        int loaded = 0;
        for (int iteration = 0; iteration < 1500; iteration++)
        {
            int which = json[rng.Next(json.Length)];
            var root = JsonNode.Parse(entries[which].Data)!;
            var nodes = new List<JsonNode>();
            Collect(root, nodes);
            for (int k = 0; k < 1 + rng.Next(8); k++)
            {
                var victim = nodes[rng.Next(nodes.Count)];
                if (victim.Parent is JsonObject parent)
                {
                    var name = parent.First(p => ReferenceEquals(p.Value, victim)).Key;
                    if (rng.Next(4) == 0) parent.Remove(name);
                    else parent[name] = RandomValue(rng);
                }
                else if (victim.Parent is JsonArray array)
                {
                    int index = array.IndexOf(victim);
                    if (rng.Next(4) == 0) array.RemoveAt(index);
                    else array[index] = RandomValue(rng);
                }
                nodes.Clear();
                Collect(root, nodes);
            }
            if (Check(Replace(entries, which, Encoding.UTF8.GetBytes(root.ToJsonString())), failures, roundTrip: true)) loaded++;
        }
        Assert.Empty(failures.Select(f => $"{f.Value}x {f.Key}"));
        Assert.True(loaded > 500, $"only {loaded} damaged variants loaded");
    }

    [Fact]
    public void Damage_to_the_archive_bytes_never_escapes_the_exception_contract()
    {
        using var dir = new TempDir();
        var path = dir.File("rich.candela");
        ProjectFile.Save(path, TestData.RichProject(), new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var original = File.ReadAllBytes(path);
        var rng = new Random(4242);
        var failures = new Dictionary<string, int>();
        for (int iteration = 0; iteration < 600; iteration++)
        {
            var bytes = (byte[])original.Clone();
            for (int k = 0; k < 1 + rng.Next(8); k++) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            if (rng.Next(10) == 0) bytes = bytes[..rng.Next(bytes.Length)];
            Check(bytes, failures, roundTrip: false);
        }
        Assert.Empty(failures.Select(f => $"{f.Value}x {f.Key}"));
    }

    /// <summary>Loads an archive; true when it loaded. Unexpected outcomes are counted in <paramref name="failures"/>.</summary>
    private static bool Check(byte[] archive, Dictionary<string, int> failures, bool roundTrip)
    {
        ProjectData project;
        try
        {
            project = ProjectFile.Load(new MemoryStream(archive));
        }
        catch (ProjectFormatException e) when (!e.Message.StartsWith(ProjectFile.UnexpectedReadFailure, StringComparison.Ordinal))
        {
            return false;
        }
        catch (Exception e)
        {
            Count(failures, "load", e);
            return false;
        }
        if (!roundTrip) return true;
        try
        {
            using var saved = new MemoryStream();
            ProjectFile.Save(saved, project);
            saved.Position = 0;
            var again = ProjectFile.Load(saved);
            if (TestData.SceneJson(again.Scene) != TestData.SceneJson(project.Scene))
            {
                var a = TestData.SceneJson(project.Scene).Split('\n');
                var b = TestData.SceneJson(again.Scene).Split('\n');
                int d = 0;
                while (d < Math.Min(a.Length, b.Length) && a[d] == b[d]) d++;
                Count(failures, $"round trip changed the scene near: {string.Join(" ", a.Skip(Math.Max(0, d - 8)).Take(10).Select(l => l.Trim()))}", null);
            }
            if (again.Assets.Count != project.Assets.Count || again.KeptAssetIds.Count != project.KeptAssetIds.Count)
                Count(failures, "round trip changed the assets", null);
            if (again.Settings.Count != project.Settings.Count) Count(failures, "round trip changed the settings", null);
        }
        catch (Exception e)
        {
            Count(failures, "save or reload", e);
        }
        return true;
    }

    private static void Count(Dictionary<string, int> failures, string stage, Exception? e)
    {
        var at = e?.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Candela.", StringComparison.Ordinal))?.Trim();
        var key = e is null ? stage : $"{stage}: {e.GetType().Name}: {e.Message} @ {at}";
        failures[key] = failures.GetValueOrDefault(key) + 1;
    }

    private static byte[] Replace((string Name, byte[] Data)[] entries, int index, byte[] data)
    {
        var copy = entries.ToArray();
        copy[index] = (copy[index].Name, data);
        return TestData.Zip(copy);
    }

    private static void Collect(JsonNode node, List<JsonNode> into)
    {
        into.Add(node);
        if (node is JsonObject o)
        {
            foreach (var (_, child) in o)
                if (child is not null) Collect(child, into);
        }
        else if (node is JsonArray a)
        {
            foreach (var child in a)
                if (child is not null) Collect(child, into);
        }
    }

    private static JsonNode? RandomValue(Random rng) => rng.Next(11) switch
    {
        0 => null,
        1 => JsonValue.Create("text"),
        2 => JsonValue.Create(-1),
        3 => JsonValue.Create(1e30),
        4 => JsonValue.Create(true),
        5 => new JsonArray(),
        6 => new JsonObject(),
        7 => JsonValue.Create(Guid.NewGuid().ToString("D")),
        8 => new JsonArray(JsonValue.Create(1), JsonValue.Create("x")),
        9 => new JsonObject { ["$type"] = "fixture", ["fixtureId"] = "x" },
        _ => JsonValue.Create(0),
    };
}
