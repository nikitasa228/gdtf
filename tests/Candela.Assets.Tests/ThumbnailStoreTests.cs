using Candela.Core.Settings;

namespace Candela.Assets.Tests;

public class ThumbnailStoreTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void DefaultLocationIsUnderTheCacheDirectory()
    {
        var store = new ThumbnailStore();

        Assert.Equal(Path.GetFullPath(Path.Combine(ConfigPaths.CacheDir, "thumbnails")), store.Directory);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        using var dir = new TempDir();
        var store = new ThumbnailStore(dir.Path);
        var image = TestFiles.Image(64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), 128, 255));

        Assert.False(store.Contains(Hash, 64));
        Assert.Null(store.Load(Hash, 64));
        store.Save(Hash, 64, image);

        Assert.True(store.Contains(Hash, 64));
        var loaded = store.Load(Hash, 64);
        Assert.NotNull(loaded);
        Assert.Equal(image.Pixels, loaded!.Pixels);
        Assert.Equal(Path.Combine(store.Directory, "01", Hash + "_64.png"), store.GetPath(Hash, 64));
        Assert.True(File.Exists(store.GetPath(Hash, 64)));
        // Different sizes and variants are different entries; the hash is case-insensitive.
        Assert.False(store.Contains(Hash, 128));
        Assert.False(store.Contains(Hash, 64, "fixture"));
        Assert.True(store.Contains(Hash.ToUpperInvariant(), 64));
    }

    [Fact]
    public void EncodedPngRoundTripAndVariants()
    {
        using var dir = new TempDir();
        var store = new ThumbnailStore(dir.Path);
        var png = TestFiles.Png(16, 16, 1, 2, 3);

        store.SavePng(Hash, 16, png, "Preview Dark");

        Assert.Equal(png, store.LoadPng(Hash, 16, "preview dark"));
        Assert.EndsWith(Hash + "_16_preview-dark.png", store.GetPath(Hash, 16, "Preview Dark"), StringComparison.Ordinal);
        Assert.Null(store.LoadPng(Hash, 16));
        Assert.Throws<ArgumentException>(() => store.SavePng(Hash, 16, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void CorruptFilesAreTreatedAsMissingAndDeleted()
    {
        using var dir = new TempDir();
        var store = new ThumbnailStore(dir.Path);
        var path = store.GetPath(Hash, 32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 });

        Assert.Null(store.Load(Hash, 32));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RemoveTrimAndClear()
    {
        using var dir = new TempDir();
        var store = new ThumbnailStore(dir.Path);
        var other = new string('a', 64);
        var image = TestFiles.Image(32, 32, (x, y) => ((byte)(x * 8), (byte)(y * 8), (byte)(x ^ y), 255));
        store.Save(Hash, 32, image);
        store.Save(Hash, 64, image);
        store.Save(Hash, 32, image, "dark");
        store.Save(other, 32, image);
        File.SetLastWriteTimeUtc(store.GetPath(other, 32), DateTime.UtcNow.AddDays(-10));

        Assert.Equal(3, store.Remove(Hash));
        Assert.Equal(0, store.Remove(Hash));
        Assert.True(store.Contains(other, 32));

        store.Save(Hash, 32, image);
        long size = store.SizeOnDisk();
        Assert.True(size > 0);
        // Trimming to just over one file removes the least recently used one.
        Assert.Equal(1, store.Trim(size - 1));
        Assert.False(store.Contains(other, 32));
        Assert.True(store.Contains(Hash, 32));
        Assert.Equal(0, store.Trim(long.MaxValue));

        store.Clear();
        Assert.Equal(0, store.SizeOnDisk());
        Assert.False(Directory.Exists(store.Directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("not-hex-not-hex-not-hex")]
    [InlineData("../../etc/passwd")]
    public void InvalidHashesAreRejected(string hash)
    {
        var store = new ThumbnailStore(Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => store.GetPath(hash, 64));
    }

    [Fact]
    public void InvalidSizesAndVariantsAreRejected()
    {
        var store = new ThumbnailStore(Path.GetTempPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPath(Hash, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPath(Hash, ThumbnailStore.MaxSize + 1));
        Assert.Throws<ArgumentException>(() => store.GetPath(Hash, 64, "///"));
    }

    [Fact]
    public void AssetContentHashIsAValidKey()
    {
        using var dir = new TempDir();
        var store = new ThumbnailStore(dir.Path);
        var assets = new AssetStore();
        var entry = assets.Add(AssetKind.Model, "box.glb", TestFiles.Glb());

        store.SavePng(entry.ContentHash, 128, TestFiles.Png(128, 128, 50, 50, 50));

        Assert.True(store.Contains(entry.ContentHash, 128));
    }

    [Fact]
    public void ConcurrentWritersAndReadersNeverSeePartialFiles()
    {
        using var dir = new TempDir();
        var store = new ThumbnailStore(dir.Path);
        var a = TestFiles.Image(48, 48, (_, _) => (255, 0, 0, 255));
        var b = TestFiles.Image(48, 48, (_, _) => (0, 0, 255, 255));

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            if ((i & 1) == 0) store.Save(Hash, 48, (i & 2) == 0 ? a : b);
            else
            {
                var loaded = store.Load(Hash, 48);
                if (loaded is not null) Assert.True(loaded.Pixels.SequenceEqual(a.Pixels) || loaded.Pixels.SequenceEqual(b.Pixels));
            }
        });

        Assert.NotNull(store.Load(Hash, 48));
        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp", SearchOption.AllDirectories));
    }
}
