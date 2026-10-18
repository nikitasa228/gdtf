namespace Candela.Assets.Tests;

public class AssetKeysTests
{
    private static readonly Guid s_id = Guid.Parse("6f1c2a54-0d1e-4f6b-9a77-3b2f8f0e1c11");

    [Fact]
    public void AssetKeysRoundTrip()
    {
        Assert.Equal("asset:6f1c2a54-0d1e-4f6b-9a77-3b2f8f0e1c11", AssetKeys.ForAsset(s_id));
        Assert.Equal("asset:6f1c2a54-0d1e-4f6b-9a77-3b2f8f0e1c11/tex/wood.png", AssetKeys.ForFile(s_id, "tex\\wood.png"));
        Assert.Equal("asset:6f1c2a54-0d1e-4f6b-9a77-3b2f8f0e1c11/", AssetKeys.TexturePrefix(s_id));

        Assert.True(AssetKeys.TryParseAssetKey(AssetKeys.ForAsset(s_id), out var id, out var path));
        Assert.Equal(s_id, id);
        Assert.Null(path);
        Assert.True(AssetKeys.TryParseAssetKey(AssetKeys.ForFile(s_id, "a/b.png"), out id, out path));
        Assert.Equal(s_id, id);
        Assert.Equal("a/b.png", path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("builtin:concrete/albedo")]
    [InlineData("asset:")]
    [InlineData("asset:1234")]
    [InlineData("asset:6f1c2a54-0d1e-4f6b-9a77-3b2f8f0e1c11/")]
    [InlineData("asset:6f1c2a54-0d1e-4f6b-9a77-3b2f8f0e1c11/..")]
    public void MalformedAssetKeysAreRejected(string key)
    {
        Assert.False(AssetKeys.TryParseAssetKey(key, out _, out _));
    }

    [Theory]
    [InlineData("./textures/../textures/wood.png", "textures/wood.png")]
    [InlineData("..\\..\\outside\\wood.png", "outside/wood.png")]
    [InlineData("/abs/path.png", "abs/path.png")]
    [InlineData("a//b/./c.png", "a/b/c.png")]
    [InlineData("", "")]
    public void RelativePathsAreNormalisedAndCannotEscape(string input, string expected)
    {
        Assert.Equal(expected, AssetKeys.NormalizeRelativePath(input));
    }

    [Fact]
    public void BuiltinKeysSplitIntoMaterialAndChannel()
    {
        Assert.True(AssetKeys.TryParseBuiltinKey("builtin:wood-stage/albedo", out var material, out var channel));
        Assert.Equal("builtin:wood-stage", material);
        Assert.Equal("albedo", channel);
        Assert.False(AssetKeys.TryParseBuiltinKey("builtin:wood-stage", out _, out _));
        Assert.False(AssetKeys.TryParseBuiltinKey("builtin:wood-stage/", out _, out _));
        Assert.False(AssetKeys.TryParseBuiltinKey("asset:x/y", out _, out _));
        Assert.Equal("builtin:carpet/normal", MaterialLibrary.TextureKey("builtin:carpet", MaterialLibrary.NormalChannel));
    }

    [Fact]
    public void ModelFormatsAreDetectedByExtensionAndContent()
    {
        Assert.Equal(ModelFormat.Obj, AssetFormats.GetModelFormat("Chair.OBJ"));
        Assert.Equal(ModelFormat.Collada, AssetFormats.GetModelFormat(".dae"));
        Assert.Equal(ModelFormat.Glb, AssetFormats.GetModelFormat("x.glb"));
        Assert.Equal(ModelFormat.SketchUp, AssetFormats.GetModelFormat("x.skp"));
        Assert.Equal(ModelFormat.Unknown, AssetFormats.GetModelFormat("x.txt"));
        Assert.False(AssetFormats.IsModelFile("x.skp"));
        Assert.DoesNotContain(".skp", AssetFormats.ModelExtensions);
        foreach (var ext in new[] { ".obj", ".fbx", ".dae", ".3ds", ".stl", ".ply", ".gltf", ".glb" })
        {
            Assert.Contains(ext, AssetFormats.ModelExtensions);
            Assert.True(ModelImporter.IsSupported("model" + ext));
            Assert.Equal(AssetKind.Model, AssetFormats.GuessKind("model" + ext));
        }
        Assert.True(AssetFormats.LooksLikeSketchUp(TestFiles.SketchUpHeader()));
        Assert.True(AssetFormats.IsSketchUp("renamed.dae", TestFiles.SketchUpHeader()));
        Assert.False(AssetFormats.LooksLikeSketchUp("solid box"u8));
        Assert.True(AssetFormats.IsGlb(TestFiles.Glb()));
        Assert.False(AssetFormats.IsGlb("{\"asset\":{}}"u8));
    }

    [Fact]
    public void ContentHashCoversCompanionFiles()
    {
        var main = new byte[] { 1, 2, 3 };
        var h0 = AssetEntry.ComputeHash(main);
        var h1 = AssetEntry.ComputeHash(main, new Dictionary<string, byte[]> { ["a.png"] = new byte[] { 4 } });
        var h2 = AssetEntry.ComputeHash(main, new Dictionary<string, byte[]> { ["b.png"] = new byte[] { 4 } });
        var h3 = AssetEntry.ComputeHash(main, new Dictionary<string, byte[]> { ["a.png"] = new byte[] { 5 } });

        Assert.Equal(h0, AssetEntry.ComputeHash(main, new Dictionary<string, byte[]>()));
        Assert.Equal(4, new[] { h0, h1, h2, h3 }.Distinct().Count());
        Assert.Matches("^[0-9a-f]{64}$", h0);
    }
}
