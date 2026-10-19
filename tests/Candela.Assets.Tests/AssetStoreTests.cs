using System.Numerics;
using Candela.Core.Diagnostics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets.Tests;

public class AssetStoreTests
{
    /// <summary>Folder that holds the repository's .gdtf files (found by walking up from the test binaries).</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "_specs"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (with _specs/) not found.");
    }

    private static (TempDir Dir, string ObjPath) ObjWithTexture()
    {
        var dir = new TempDir();
        dir.Write("maps/albedo.png", TestFiles.Png(4, 4, 10, 20, 30));
        dir.Write("box.mtl", "newmtl Painted\nKd 0.6 0.6 0.6\nmap_Kd maps/albedo.png\n");
        return (dir, dir.Write("box.obj", TestFiles.Obj("box.mtl", "Painted")));
    }

    [Fact]
    public void ImageAssetResolvesByKeyAndKeepsOriginalBytes()
    {
        using var dir = new TempDir();
        var bytes = TestFiles.Png(3, 2, 1, 2, 3);
        var path = dir.Write("plan.png", bytes);
        var store = new AssetStore();

        var entry = store.ImportFile(path);

        Assert.Equal(AssetKind.Image, entry.Kind);
        Assert.Equal("plan.png", entry.OriginalFileName);
        Assert.Equal(Path.GetFullPath(path), entry.SourcePath);
        Assert.Equal(bytes, store.GetBytes(entry.Id));
        Assert.Empty(store.GetCompanionFiles(entry.Id));
        Assert.Equal(AssetKeys.ForAsset(entry.Id), entry.ImageKey);
        var image = store.Load(entry.ImageKey);
        Assert.NotNull(image);
        Assert.Equal((3, 2), (image!.Width, image.Height));
        Assert.Same(image, store.GetImage(entry.Id)); // decoded once, cached
        Assert.Equal(entry.ContentHash, AssetEntry.ComputeHash(bytes));
        Assert.Equal(64, entry.ContentHash.Length);
    }

    [Fact]
    public void ImportingIdenticalContentReturnsTheExistingEntry()
    {
        using var dir = new TempDir();
        var a = dir.Write("a.png", TestFiles.Png(2, 2, 9, 9, 9));
        var b = dir.Write("copy of a.png", TestFiles.Png(2, 2, 9, 9, 9));
        var store = new AssetStore();

        var first = store.ImportFile(a);
        var second = store.ImportFile(b);
        var texture = store.ImportFile(b, AssetKind.Texture);

        Assert.Same(first, second);
        Assert.NotEqual(first.Id, texture.Id);
        Assert.Equal(AssetKind.Texture, texture.Kind);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ModelTexturesResolveThroughFileKeys()
    {
        var (dir, obj) = ObjWithTexture();
        using var _ = dir;
        var store = new AssetStore();

        var entry = store.ImportFile(obj);
        var key = store.GetModel(entry.Id).Model.Materials[0].BaseColorTexture!;

        Assert.Equal($"asset:{entry.Id:D}/maps/albedo.png", key);
        Assert.True(AssetKeys.TryParseAssetKey(key, out var id, out var relative));
        Assert.Equal(entry.Id, id);
        Assert.Equal("maps/albedo.png", relative);
        var image = store.Load(key);
        Assert.NotNull(image);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, image!.Pixels[..4]);
        // Paths are case-insensitive like the file systems models are authored on.
        Assert.NotNull(store.Load($"asset:{entry.Id:D}/Maps/Albedo.PNG"));
        // The model asset itself is not an image.
        Assert.Null(store.Load(AssetKeys.ForAsset(entry.Id)));
    }

    [Fact]
    public void ProjectRoundTripThroughPublicPersistenceApi()
    {
        var (dir, obj) = ObjWithTexture();
        using var _ = dir;
        var original = new AssetStore();
        var model = original.ImportFile(obj, options: new ImportOptions { UnitScale = 0.5f });
        var image = original.ImportFile(dir.Write("poster.png", TestFiles.Png(5, 5, 200, 0, 0)));
        var gdtfPath = Directory.EnumerateFiles(RepositoryRoot(), "*.gdtf").Order(StringComparer.Ordinal).First();
        var gdtf = original.ImportFile(gdtfPath);

        // What Candela.Project stores: kind, name, bytes, companions, id, import options.
        var saved = original.Entries.Select(e => (e.Id, e.Kind, e.OriginalFileName, Data: original.GetBytes(e.Id),
            Companions: original.GetCompanionFiles(e.Id), e.ImportOptions)).ToList();
        var loaded = new AssetStore();
        foreach (var s in saved) loaded.Add(s.Kind, s.OriginalFileName, s.Data, s.Companions, id: s.Id, importOptions: s.ImportOptions);

        Assert.Equal(original.Entries.Select(e => (e.Id, e.Kind, e.ContentHash)), loaded.Entries.Select(e => (e.Id, e.Kind, e.ContentHash)));
        Assert.Equal(AssetKind.Gdtf, gdtf.Kind);
        Assert.Equal(new ImportOptions { UnitScale = 0.5f }, loaded.Find(model.Id)!.ImportOptions);
        var a = original.GetModel(model.Id);
        var b = loaded.GetModel(model.Id);
        Assert.Equal(a.Model.ComputeBounds().Size, b.Model.ComputeBounds().Size);
        Geo.AssertSize(new Vector3(0.5f, 0.25f, 1f), b.Model.ComputeBounds().Size);
        Assert.Equal(a.Model.Materials[0].BaseColorTexture, b.Model.Materials[0].BaseColorTexture);
        Assert.Equal(original.Load(a.Model.Materials[0].BaseColorTexture!)!.Pixels, loaded.Load(b.Model.Materials[0].BaseColorTexture!)!.Pixels);
        Assert.Equal(original.GetImage(image.Id).Pixels, loaded.GetImage(image.Id).Pixels);
        Assert.Throws<InvalidOperationException>(() => loaded.Add(AssetKind.Image, "dup.png", saved[1].Data, id: image.Id));
    }

    [Fact]
    public void InvalidContentIsRejectedOnAdd()
    {
        var store = new AssetStore();

        Assert.Throws<AssetImportException>(() => store.Add(AssetKind.Image, "x.png", new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<AssetImportException>(() => store.Add(AssetKind.Gdtf, "x.gdtf", new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<AssetImportException>(() => store.Add(AssetKind.Model, "x.obj", Array.Empty<byte>()));
        Assert.Throws<AssetImportException>(() => store.Add(AssetKind.Model, "x.skp", new byte[] { 1 }));
        Assert.Throws<AssetImportException>(() => store.ImportFile("/nonexistent/folder/missing.png"));
        Assert.Throws<AssetImportException>(() => store.ImportFile("notes.txt"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void UndecodableModelReportsAnIssueInsteadOfThrowingFromLoad()
    {
        var store = new AssetStore();
        var garbage = new byte[256];
        new Random(7).NextBytes(garbage);
        var entry = store.Add(AssetKind.Model, "broken.fbx", garbage);

        Assert.False(store.TryGetModel(entry.Id, out var model, out var error));
        Assert.Null(model);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<AssetImportException>(() => store.GetModel(entry.Id));
        Assert.Null(store.Load(AssetKeys.ForFile(entry.Id, "texture.png")));
        Assert.Contains(store.Issues, i => i.Code == "asset.decode-failed" && i.Severity == IssueSeverity.Error);
        // The issue is reported once, however often the asset is asked for.
        Assert.Single(store.Issues, i => i.Code == "asset.decode-failed");
    }

    [Fact]
    public void UnknownKeysReturnNullAndReportMissingAssets()
    {
        var store = new AssetStore();
        var missing = Guid.NewGuid();
        var reported = new List<Issue>();
        store.IssueReported += reported.Add;

        Assert.Null(store.Load(""));
        Assert.Null(store.Load("gdtf:foo.gdtf#x.png"));
        Assert.Null(store.Load("asset:not-a-guid"));
        Assert.Null(store.Load(AssetKeys.ForAsset(missing)));
        Assert.Null(store.Load("builtin:no-such-material/albedo"));
        Assert.Null(store.Load("builtin:wood-stage/no-such-channel"));

        var issue = Assert.Single(reported);
        Assert.Equal("asset.missing", issue.Code);
        Assert.Contains(missing.ToString("D"), issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltinKeysAreDelegatedToTheMaterialLibrary()
    {
        var store = new AssetStore();

        var wood = store.Load("builtin:wood-stage/albedo");

        Assert.NotNull(wood);
        Assert.Same(MaterialLibrary.Default.LoadTexture("builtin:wood-stage/albedo"), wood);
        Assert.Same(MaterialLibrary.Default, store.Materials);
    }

    [Fact]
    public void ConcurrentRequestsDecodeOnce()
    {
        var store = new AssetStore();
        var entry = store.Add(AssetKind.Model, "box.glb", TestFiles.Glb());
        var image = store.Add(AssetKind.Image, "p.png", TestFiles.Png(64, 64, 1, 1, 1));
        var models = new ImportedModel[32];
        var images = new ImageData?[32];
        var textures = new ImageData?[32];

        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            models[i] = store.GetModel(entry.Id);
            images[i] = store.Load(image.ImageKey);
            textures[i] = store.Load(models[i].Model.Materials[0].BaseColorTexture!);
        });

        Assert.All(models, m => Assert.Same(models[0], m));
        Assert.All(images, m => Assert.Same(images[0], m));
        Assert.All(textures, m => Assert.Same(textures[0], m));
        Assert.NotNull(textures[0]);
    }

    [Fact]
    public void SetImportOptionsReDecodesTheModel()
    {
        var store = new AssetStore();
        var entry = store.Add(AssetKind.Model, "box.stl", TestFiles.BinaryStl(TestFiles.BoxTriangles(yUp: false)));
        var changed = new List<Guid>();
        store.AssetChanged += changed.Add;

        var before = store.GetModel(entry.Id);
        var updated = store.SetImportOptions(entry.Id, new ImportOptions { UnitScale = 2f });
        var after = store.GetModel(entry.Id);

        Assert.NotSame(before, after);
        Assert.Equal(2f, updated.ImportOptions!.UnitScale);
        Geo.AssertSize(new Vector3(2f, 1f, 4f), after.Model.ComputeBounds().Size);
        Assert.Equal(new[] { entry.Id }, changed);
        Assert.Throws<KeyNotFoundException>(() => store.SetImportOptions(Guid.NewGuid(), ImportOptions.Default));
    }

    [Fact]
    public void A_model_decoded_elsewhere_is_attached_and_served_without_decoding()
    {
        // e.g. decoded by an isolated import worker: the store must not decode the file itself
        var store = new AssetStore();
        var entry = store.Add(AssetKind.Model, "box.stl", TestFiles.BinaryStl(TestFiles.BoxTriangles(yUp: false)));
        Assert.False(store.TryGetDecodedModel(entry.Id, out _, out _));
        var external = ModelImporter.Import("box.stl", entry.Data, entry.CompanionFiles,
            ImportOptions.Default with { TextureKeyPrefix = AssetKeys.TexturePrefix(entry.Id) });
        Assert.True(store.SetDecodedModel(entry, external));
        Assert.True(store.TryGetDecodedModel(entry.Id, out var peeked, out var error));
        Assert.Same(external, peeked);
        Assert.Null(error);
        Assert.Same(external, store.GetModel(entry.Id));

        // a late result for an entry that was replaced since (new import options) is refused
        var replaced = store.SetImportOptions(entry.Id, new ImportOptions { UnitScale = 2f });
        Assert.False(store.SetDecodedModel(entry, external));
        Assert.False(store.TryGetDecodedModel(entry.Id, out _, out _));
        Assert.NotSame(external, store.GetModel(entry.Id));
        Assert.True(store.TryGetDecodedModel(entry.Id, out var decoded, out _) && decoded is not null);

        // a failure is remembered: no decode is attempted, the reason is returned
        Assert.True(store.SetModelDecodeError(replaced, "the import worker crashed"));
        Assert.True(store.TryGetDecodedModel(entry.Id, out var none, out var reason));
        Assert.Null(none);
        Assert.Equal("the import worker crashed", reason);
        Assert.False(store.TryGetModel(entry.Id, out _, out var again));
        Assert.Equal("the import worker crashed", again);

        // unknown ids, other kinds and removed assets are refused
        var image = store.Add(AssetKind.Image, "p.png", TestFiles.Png(2, 2, 1, 1, 1));
        Assert.False(store.SetDecodedModel(image, external));
        Assert.False(store.TryGetDecodedModel(image.Id, out _, out _));
        Assert.False(store.TryGetDecodedModel(Guid.NewGuid(), out _, out _));
        store.Remove(entry.Id);
        Assert.False(store.SetDecodedModel(replaced, external));
        Assert.Throws<ArgumentNullException>(() => store.SetDecodedModel(replaced, null!));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var store = new AssetStore();
        var a = store.Add(AssetKind.Image, "a.png", TestFiles.Png(1, 1, 0, 0, 0));
        var b = store.Add(AssetKind.Image, "b.png", TestFiles.Png(1, 1, 9, 0, 0));

        Assert.True(store.Remove(a.Id));
        Assert.False(store.Remove(a.Id));
        Assert.Null(store.Find(a.Id));
        Assert.Null(store.Load(a.ImageKey));
        Assert.Throws<KeyNotFoundException>(() => store.GetBytes(a.Id));
        Assert.Equal(new[] { b.Id }, store.Entries.Select(e => e.Id));

        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Empty(store.Issues);
    }

    [Fact]
    public void ImagePlaneMaterialUsesTheAssetKey()
    {
        var store = new AssetStore();
        var entry = store.Add(AssetKind.Image, "banner.png", TestFiles.Png(8, 2, 255, 255, 255));
        var poster = new ImageComponent { ImageAssetId = entry.Id, Mode = ImageMode.Poster, Opacity = 1f };
        var reference = new ImageComponent { ImageAssetId = entry.Id, Mode = ImageMode.Reference, Opacity = 0.5f };

        var posterMaterial = ImagePlaneMesh.CreateMaterial(poster);
        var referenceMaterial = ImagePlaneMesh.CreateMaterial(reference);

        Assert.Equal(entry.ImageKey, posterMaterial.BaseColorTexture);
        Assert.Equal(AlphaMode.Opaque, posterMaterial.AlphaMode);
        Assert.Equal(0f, posterMaterial.EmissiveLuminance);
        Assert.Equal(entry.ImageKey, referenceMaterial.EmissiveTexture);
        Assert.Equal(ImagePlaneMesh.ReferenceLuminance, referenceMaterial.EmissiveLuminance);
        Assert.Equal(AlphaMode.Blend, referenceMaterial.AlphaMode);
        Assert.Equal(0.5f, referenceMaterial.BaseColorFactor.W);
        Assert.NotNull(store.Load(posterMaterial.BaseColorTexture!));
    }
}
