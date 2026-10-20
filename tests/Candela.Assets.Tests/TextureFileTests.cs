using System.Buffers.Binary;
using Candela.Core.SceneGraph.Components;
using StbImageWriteSharp;

namespace Candela.Assets.Tests;

/// <summary>
/// <see cref="AssetStore.TryGetTextureFile"/>: the encoded file of a texture key for writing it into another file (the
/// GLB models of an MVR export) — PNG and JPEG as imported, every other image re-encoded as PNG with the same pixels.
/// </summary>
public sealed class TextureFileTests
{
    private static byte[] Encode(Action<ImageWriter, Stream> write)
    {
        using var ms = new MemoryStream();
        write(new ImageWriter(), ms);
        return ms.ToArray();
    }

    private static readonly byte[] Pixels = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];

    [Fact]
    public void Png_and_jpeg_files_of_a_model_are_returned_as_imported()
    {
        using var dir = new TempDir();
        var png = TestFiles.Png(4, 4, 10, 20, 30);
        dir.Write("maps/albedo.png", png);
        dir.Write("box.mtl", "newmtl Painted\nKd 0.6 0.6 0.6\nmap_Kd maps/albedo.png\n");
        var store = new AssetStore();
        var entry = store.ImportModel(dir.Write("box.obj", TestFiles.Obj("box.mtl", "Painted")));

        Assert.True(store.TryGetTextureFile(AssetKeys.ForFile(entry.Id, "maps/albedo.png"), out var data));
        Assert.Equal(png, data);
        // file names in keys are matched like the image source matches them (case-insensitively)
        Assert.True(store.TryGetTextureFile(AssetKeys.ForFile(entry.Id, "MAPS/Albedo.PNG"), out data));
        Assert.Equal(png, data);
        Assert.False(store.TryGetTextureFile(AssetKeys.ForFile(entry.Id, "maps/missing.png"), out _));
        Assert.False(store.TryGetTextureFile(AssetKeys.ForFile(Guid.NewGuid(), "maps/albedo.png"), out _));
        Assert.False(store.TryGetTextureFile("gdtf:Some@Spot.gdtf#wheels/gobo.png", out _));
        Assert.False(store.TryGetTextureFile("", out _));
    }

    [Fact]
    public void Other_image_formats_are_re_encoded_as_png_with_the_same_pixels()
    {
        var tga = Encode((w, s) => w.WriteTga(Pixels, 2, 2, ColorComponents.RedGreenBlueAlpha, s));
        var store = new AssetStore();
        var image = store.Add(AssetKind.Texture, "checker.tga", tga);

        Assert.True(store.TryGetTextureFile(AssetKeys.ForAsset(image.Id), out var data));
        Assert.Equal("png", ImageImporter.DetectFormat(data));
        Assert.Equal(Pixels, ImageImporter.Decode(data).Pixels);

        // an image that is not an image or texture asset has no texture file
        var gdtf = store.Add(AssetKind.Gdtf, "spot.gdtf", "PK\u0003\u0004 not really"u8.ToArray());
        Assert.False(store.TryGetTextureFile(AssetKeys.ForAsset(gdtf.Id), out _));
    }

    [Fact]
    public void A_jpeg_rotated_by_its_exif_orientation_is_written_upright()
    {
        var jpeg = Encode((w, s) => new ImageWriter().WriteJpg(Pixels, 2, 2, ColorComponents.RedGreenBlueAlpha, s, 95));
        var rotated = WithExifOrientation(jpeg, 6);
        var store = new AssetStore();
        var plain = store.Add(AssetKind.Image, "plain.jpg", jpeg);
        var turned = store.Add(AssetKind.Image, "turned.jpg", rotated);

        Assert.True(store.TryGetTextureFile(AssetKeys.ForAsset(plain.Id), out var data));
        Assert.Equal(jpeg, data);
        Assert.True(store.TryGetTextureFile(AssetKeys.ForAsset(turned.Id), out data));
        Assert.Equal("png", ImageImporter.DetectFormat(data));
        Assert.Equal(store.GetImage(turned.Id).Pixels, ImageImporter.Decode(data).Pixels);
    }

    [Fact]
    public void Built_in_procedural_textures_are_encoded_as_png()
    {
        var store = new AssetStore();
        string key = Assert.IsType<string>(store.Materials.GetMaterial(MaterialIds.WoodStage).BaseColorTexture);
        Assert.True(store.TryGetTextureFile(key, out var data));
        Assert.Equal("png", ImageImporter.DetectFormat(data));
        var decoded = ImageImporter.Decode(data);
        var expected = store.Load(key)!;
        Assert.Equal((expected.Width, expected.Height), (decoded.Width, decoded.Height));
        Assert.False(store.TryGetTextureFile("builtin:no-such-material/albedo", out _));
    }

    [Fact]
    public void Embedded_images_of_a_decoded_model_are_found_without_decoding_it_again()
    {
        var store = new AssetStore();
        var entry = store.Add(AssetKind.Model, "textured.glb", TestFiles.Glb(withTexture: true));
        // not decoded yet: the image inside the GLB is not looked for (a model is never decoded for this)
        var model = store.GetModel(entry.Id);
        string key = Assert.Single(model.Model.Materials, m => m.BaseColorTexture is not null).BaseColorTexture!;
        Assert.True(store.TryGetTextureFile(key, out var data));
        Assert.True(ImageImporter.DetectFormat(data) is "png" or "jpeg");

        var undecoded = new AssetStore();
        var copy = undecoded.Add(AssetKind.Model, "textured.glb", TestFiles.Glb(withTexture: true), id: entry.Id);
        Assert.False(undecoded.TryGetTextureFile(key, out _));
        Assert.False(undecoded.TryGetDecodedModel(copy.Id, out _, out _));
    }

    /// <summary>Inserts an APP1/Exif segment with the given orientation right after the SOI marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        var tiff = new byte[26];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return jpeg[..2].Concat(segment).Concat(jpeg[2..]).ToArray();
    }
}
