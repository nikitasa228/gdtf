using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Candela.Gdtf.Tests;

/// <summary>
/// The persistent fixture type cache: a loaded fixture type is indistinguishable from the parsed one (checked on every
/// library file), the key follows content and reader version, damaged files fall back to parsing, the folder stays
/// within its size limit, and re-opening is measurably faster.
/// </summary>
public sealed class DiskCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "candela-gdtf-cache-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public DiskCacheTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly string Sample = TestData.Fixture("Robe_Lighting@Robin_MegaPointe@");

    private static FixtureType RoundTrip(FixtureType ft)
    {
        string key = new string('0', 64);
        var bytes = FixtureTypeDiskCache.Encode(ft, key);
        return FixtureTypeDiskCache.Decode(bytes, key) ?? throw new InvalidOperationException("header rejected");
    }

    [Fact]
    public void Every_library_file_round_trips_through_the_cache_format_unchanged()
    {
        var failures = new ConcurrentBag<string>();
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(TestData.GdtfFiles, file =>
        {
            var parsed = GdtfReader.ReadFile(file);
            var encoded = FixtureTypeDiskCache.Encode(parsed, new string('A', 64));
            Interlocked.Add(ref bytes, encoded.Length);
            var loaded = FixtureTypeDiskCache.Decode(encoded, new string('A', 64))!;
            var diffs = ModelComparer.Compare(parsed, loaded, max: 5);
            if (diffs.Count > 0) failures.Add($"{Path.GetFileName(file)}:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", diffs)}");
        });
        _output.WriteLine($"{TestData.GdtfFiles.Count} fixture types: {bytes / 1e6:F1} MB of cache data, round trips compared in {sw.Elapsed.TotalSeconds:F1} s.");
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(5)));
    }

    [Fact]
    public void Synthetic_fixture_with_every_element_round_trips()
    {
        // The reader tests' LED bar uses every geometry type, relations, macros, presets and protocols.
        var ft = TestData.ReadXml(ReaderTests.LedBar);
        Assert.NotEmpty(ft.DmxModes);
        var loaded = RoundTrip(ft);
        Assert.Empty(ModelComparer.Compare(ft, loaded));
    }

    [Fact]
    public void Second_read_is_a_hit_and_equals_the_parse()
    {
        var cache = new FixtureTypeDiskCache(_dir);
        var first = cache.ReadFile(Sample);
        Assert.Equal(new FixtureTypeCacheStatistics(0, 1, 1, 0, 0), cache.Statistics);
        var second = cache.ReadFile(Sample);
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.NotSame(first, second);
        Assert.Empty(ModelComparer.Compare(first, second));
        Assert.Equal(Path.GetFileName(Sample), second.SourceFileName);
        Assert.Single(Directory.GetFiles(_dir, "*" + FixtureTypeDiskCache.FileExtension));
    }

    [Fact]
    public void Key_follows_content_not_name()
    {
        var cache = new FixtureTypeDiskCache(_dir);
        Directory.CreateDirectory(_dir);
        string copy = Path.Combine(_dir, "Renamed@Copy@1.gdtf");
        File.Copy(Sample, copy);
        cache.ReadFile(Sample);
        var renamed = cache.ReadFile(copy);
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.Equal("Renamed@Copy@1.gdtf", renamed.SourceFileName);

        // Same name, other content: a different key.
        static string KeyOf(byte[] gdtf)
        {
            using var a = GdtfArchive.FromBytes(gdtf, "x.gdtf");
            return FixtureTypeDiskCache.ComputeKey(a);
        }
        string k1 = KeyOf(TestData.GdtfWithDescription("<GDTF DataVersion=\"1.2\"><FixtureType Name=\"A\"/></GDTF>"));
        string k2 = KeyOf(TestData.GdtfWithDescription("<GDTF DataVersion=\"1.2\"><FixtureType Name=\"B\"/></GDTF>"));
        string k3 = KeyOf(TestData.Zip(("description.xml", Encoding.UTF8.GetBytes("<GDTF DataVersion=\"1.2\"><FixtureType Name=\"A\"/></GDTF>")),
            ("wheels/gobo.png", new byte[] { 1 })));
        Assert.Equal(64, k1.Length);
        Assert.Equal(k1, KeyOf(TestData.GdtfWithDescription("<GDTF DataVersion=\"1.2\"><FixtureType Name=\"A\"/></GDTF>")));
        Assert.NotEqual(k1, k2);
        Assert.NotEqual(k1, k3); // resources change what the reader resolves
    }

    public static TheoryData<string> Damage => new() { "flip", "truncate", "garbage", "empty", "version" };

    [Theory]
    [MemberData(nameof(Damage))]
    public void Damaged_cache_files_are_discarded_and_the_file_is_parsed_again(string damage)
    {
        var cache = new FixtureTypeDiskCache(_dir);
        var parsed = cache.ReadFile(Sample);
        string file = Assert.Single(Directory.GetFiles(_dir, "*" + FixtureTypeDiskCache.FileExtension));
        var bytes = File.ReadAllBytes(file);
        switch (damage)
        {
            case "flip":
                bytes[bytes.Length / 2] ^= 0x5A;
                break;
            case "truncate":
                bytes = bytes[..(bytes.Length / 3)];
                break;
            case "garbage":
                new Random(7).NextBytes(bytes);
                break;
            case "empty":
                bytes = Array.Empty<byte>();
                break;
            case "version":
                bytes[4] ^= 0xFF; // format version in the header
                break;
        }
        File.WriteAllBytes(file, bytes);

        var again = cache.ReadFile(Sample);
        Assert.Empty(ModelComparer.Compare(parsed, again));
        var stats = cache.Statistics;
        Assert.Equal(0, stats.Hits);
        Assert.Equal(1, stats.Damaged);
        Assert.Equal(2, stats.Stores); // re-created
        Assert.True(cache.TryLoad(Path.GetFileNameWithoutExtension(file), out var repaired));
        Assert.Empty(ModelComparer.Compare(parsed, repaired!));
    }

    [Fact]
    public void Payloads_with_a_valid_checksum_but_bad_content_are_rejected()
    {
        // The serializer validates counts, indices and enum values on its own (defence in depth behind the checksum).
        var ft = GdtfReader.ReadFile(Sample);
        var w = new CacheWriter();
        FixtureTypeSerializer.Write(ft, w);
        var payload = w.Written.ToArray();
        var rng = new Random(11);
        int rejected = 0;
        for (int i = 0; i < 200; i++)
        {
            var bad = (byte[])payload.Clone();
            for (int k = 0; k < 3; k++) bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
            try
            {
                FixtureTypeSerializer.Read(new CacheReader(bad, 0, bad.Length));
            }
            catch (InvalidDataException)
            {
                rejected++;
            }
        }
        // Random damage may also hit plain values (floats, string bytes) and go unnoticed by the structure checks;
        // the file checksum catches those. What must never happen is any other exception or a hang.
        Assert.True(rejected > 50, $"{rejected} of 200 damaged payloads rejected");
    }

    [Fact]
    public void Unwritable_cache_folder_only_disables_storing()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var cache = new FixtureTypeDiskCache(blocker); // a file where the folder should be
        var ft = cache.ReadFile(Sample);
        Assert.NotEmpty(ft.DmxModes);
        Assert.Equal(1, cache.Statistics.WriteFailures);
        using var archive = GdtfArchive.Open(Sample);
        Assert.False(cache.TryLoad(FixtureTypeDiskCache.ComputeKey(archive), out _));
    }

    [Fact]
    public void Folder_is_pruned_to_its_size_limit_least_recently_used_first()
    {
        var cache = new FixtureTypeDiskCache(_dir, maxBytes: 1 << 20);
        var files = TestData.GdtfFiles.OrderByDescending(f => new FileInfo(f).Length).Take(40).ToList();
        foreach (var f in files) cache.ReadFile(f);
        long total = Directory.GetFiles(_dir).Sum(f => new FileInfo(f).Length);
        Assert.InRange(total, 1, 1 << 20);
        Assert.True(Directory.GetFiles(_dir).Length < files.Count);
    }

    [Fact]
    public void Concurrent_readers_of_one_file_all_succeed()
    {
        var cache = new FixtureTypeDiskCache(_dir);
        var results = new FixtureType[16];
        Parallel.For(0, results.Length, i => results[i] = cache.ReadFile(Sample));
        Assert.All(results, r => Assert.Equal(results[0].DmxModes.Count, r.DmxModes.Count));
        var s = cache.Statistics;
        Assert.Equal(results.Length, s.Hits + s.Misses);
        Assert.Equal(0, s.Damaged);
    }

    [Fact]
    public void Library_uses_the_disk_cache_transparently()
    {
        var cache = new FixtureTypeDiskCache(_dir);
        var first = new FixtureLibrary(TestData.Specs) { DiskCache = cache };
        first.AddFiles(new[] { Sample });
        var entry = first.Entries.Single();
        var parsed = first.GetFixtureType(entry);
        Assert.Equal(1, cache.Statistics.Stores);

        var second = new FixtureLibrary(TestData.Specs) { DiskCache = cache };
        second.AddFiles(new[] { Sample });
        var loaded = second.GetFixtureType(second.Entries.Single());
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.Empty(ModelComparer.Compare(parsed, loaded));

        var uncached = new FixtureLibrary(TestData.Specs) { DiskCache = null };
        uncached.AddFiles(new[] { Sample });
        Assert.Empty(ModelComparer.Compare(parsed, uncached.GetFixtureType(uncached.Entries.Single())));
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.NotNull(new FixtureLibrary().DiskCache); // on by default (the shared per-user cache)
    }

    [Fact]
    public void Reopening_from_the_cache_is_much_faster_than_parsing()
    {
        // The 60 files with the largest descriptions dominate parse time in real projects.
        var files = TestData.GdtfFiles
            .Select(f =>
            {
                using var a = GdtfArchive.Open(f);
                return (File: f, Size: a.GetSize(GdtfArchive.DescriptionEntry));
            })
            .OrderByDescending(x => x.Size).Take(60).Select(x => x.File).ToList();
        var cache = new FixtureTypeDiskCache(_dir);
        foreach (var f in files) cache.ReadFile(f); // fill (and warm up the code paths)

        double parse = Measure(() => { foreach (var f in files) GdtfReader.ReadFile(f); });
        double cached = Measure(() => { foreach (var f in files) cache.ReadFile(f); });
        _output.WriteLine($"{files.Count} largest fixtures: parse {parse * 1000:F0} ms, from cache {cached * 1000:F0} ms ({parse / cached:F1}× faster).");
        Assert.Equal(3L * files.Count, cache.Statistics.Hits); // every re-open was a hit
        Assert.True(cached * 2 < parse, $"cache {cached:F3} s vs parse {parse:F3} s");
    }

    /// <summary>Best of three runs (robust against a busy machine).</summary>
    private static double Measure(Action action)
    {
        double best = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            best = System.Math.Min(best, sw.Elapsed.TotalSeconds);
        }
        return best;
    }
}
