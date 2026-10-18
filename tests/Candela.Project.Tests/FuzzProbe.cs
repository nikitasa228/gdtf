using System.Text;
using Xunit.Abstractions;

namespace Candela.Project.Tests;

public sealed class FuzzProbe : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ITestOutputHelper _out;

    public FuzzProbe(ITestOutputHelper output) => _out = output;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Mutated_json_never_escapes_the_exception_contract()
    {
        var path = _dir.File("rich.candela");
        ProjectFile.Save(path, TestData.RichProject(), new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf, CompressionLevel = System.IO.Compression.CompressionLevel.Fastest });
        var entries = new List<(string Name, byte[] Data)>();
        using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
        {
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                entries.Add((e.FullName, ms.ToArray()));
            }
        }
        var jsonIdx = entries.Select((e, i) => (e, i)).Where(x => x.e.Name.EndsWith(".json", StringComparison.Ordinal)).Select(x => x.i).ToArray();
        var rng = new Random(12345);
        var failures = new Dictionary<string, int>();
        string[] tokens = { "null", "\"\\ud800\"", "\"\\udfff\\ud800\"", "[", "]", "{", "}", "1e999", "-1", "\"\"", "true", "\"$type\"", ",", ":", "\"x\\u0000\"", "99999999999999999999" };
        for (int iter = 0; iter < 3000; iter++)
        {
            var copy = entries.Select(e => (e.Name, (byte[])e.Data.Clone())).ToArray();
            int which = jsonIdx[rng.Next(jsonIdx.Length)];
            var data = copy[which].Item2;
            int mode = rng.Next(5);
            if (mode == 0 && data.Length > 0)
            {
                for (int k = 0; k < 1 + rng.Next(4); k++) data[rng.Next(data.Length)] = (byte)rng.Next(256);
            }
            else if (mode == 1 && data.Length > 0)
            {
                data = data[..rng.Next(data.Length)];
            }
            else
            {
                var text = Encoding.UTF8.GetString(data);
                int pos = rng.Next(text.Length + 1);
                // Replace a JSON value-ish token at a random position.
                int end = Math.Min(text.Length, pos + rng.Next(12));
                text = text[..pos] + tokens[rng.Next(tokens.Length)] + text[end..];
                data = Encoding.UTF8.GetBytes(text);
            }
            copy[which].Item2 = data;
            var bytes = TestData.Zip(copy);
            try
            {
                using var ms = new MemoryStream(bytes);
                ProjectFile.Load(ms);
            }
            catch (ProjectFormatException)
            {
            }
            catch (Exception e)
            {
                var key = copy[which].Name + ": " + e.GetType().Name + ": " + e.Message + " @ " + e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Candela"))?.Trim();
                failures[key] = failures.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var (k, v) in failures) _out.WriteLine($"{v}x {k}");
        Assert.Empty(failures);
    }

    [Fact]
    public void Mutated_archive_bytes_never_escape_the_exception_contract()
    {
        var path = _dir.File("rich.candela");
        ProjectFile.Save(path, TestData.RichProject(), new ProjectSaveOptions { GdtfProvider = TestData.LibraryGdtf });
        var original = File.ReadAllBytes(path);
        var rng = new Random(777);
        var failures = new Dictionary<string, int>();
        for (int iter = 0; iter < 1500; iter++)
        {
            var bytes = (byte[])original.Clone();
            int n = 1 + rng.Next(8);
            for (int k = 0; k < n; k++) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            if (rng.Next(10) == 0) bytes = bytes[..rng.Next(bytes.Length)];
            try
            {
                using var ms = new MemoryStream(bytes);
                ProjectFile.Load(ms);
            }
            catch (ProjectFormatException)
            {
            }
            catch (Exception e)
            {
                var key = e.GetType().Name + ": " + e.Message + " @ " + e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Candela"))?.Trim();
                failures[key] = failures.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var (k, v) in failures) _out.WriteLine($"{v}x {k}");
        Assert.Empty(failures);
    }
}
