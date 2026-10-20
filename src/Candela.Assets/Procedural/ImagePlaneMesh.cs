using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets;

/// <summary>
/// Mesh and material of an <see cref="ImageComponent"/>: one rectangle in the local XZ plane facing −Y, centred on the
/// entity origin (−W/2 … W/2 along X, −H/2 … H/2 along Z). Unlike the other procedural meshes its UVs are normalised
/// (0 … 1): the image is stretched over the rectangle once, top-left of the image at the top-left corner.
/// </summary>
public static class ImagePlaneMesh
{
    /// <summary>Builds the plane for a component (width/height in metres; invalid sizes are clamped to 1 mm).</summary>
    public static MeshData Create(ImageComponent image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Create(image.Width, image.Height);
    }

    /// <summary>Builds a <paramref name="width"/> × <paramref name="height"/> m plane facing −Y with normalised UVs.</summary>
    public static MeshData Create(float width, float height)
    {
        float w = Valid(width), h = Valid(height);
        var b = new MeshBuilder(4, 6);
        b.AddRectangle(Vector3.Zero, -Vector3.UnitY, Vector3.UnitZ, w, h, 1f);
        var mesh = b.ToMesh("Image", tangents: false);
        var uv = mesh.TexCoords!;
        for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(uv[i].X / w, uv[i].Y / h);
        MeshTools.GenerateTangents(mesh);
        return mesh;
    }

    /// <summary>
    /// Material for the image, which is the base colour (sRGB texture) in both modes. <see cref="ImageMode.Poster"/>: a
    /// lit, rough print, opaque unless the opacity is below 1. <see cref="ImageMode.Reference"/>: the drawing at its
    /// opacity, always alpha-blended. The renderer draws reference instances (<c>InstanceFlags.Reference</c>) unlit: the
    /// base colour is shown as it is, whatever the stage light, the exposure or the view mode. A blended material is drawn
    /// at its own alpha, so the drawing's pixels appear at <see cref="ImageComponent.Opacity"/> over what lies behind.
    /// The component's double-sided flag is carried over.
    /// </summary>
    public static MaterialData CreateMaterial(ImageComponent image)
    {
        ArgumentNullException.ThrowIfNull(image);
        string key = AssetKeys.ForAsset(image.ImageAssetId);
        float opacity = float.IsFinite(image.Opacity) ? System.Math.Clamp(image.Opacity, 0f, 1f) : 1f;
        bool poster = image.Mode == ImageMode.Poster;
        return new MaterialData
        {
            Name = poster ? "Poster" : "Reference drawing",
            DoubleSided = image.DoubleSided,
            // A reference drawing is blended at its own opacity, also at 1: an opaque material on a reference instance
            // gets the renderer's fixed 50 % of ghost geometry instead.
            AlphaMode = poster && opacity >= 1f ? AlphaMode.Opaque : AlphaMode.Blend,
            Metallic = 0f,
            BaseColorFactor = new Vector4(1f, 1f, 1f, opacity),
            BaseColorTexture = key,
            // Printed paper or PVC banner: diffuse, satin finish. A drawing is never lit, so its roughness does not matter.
            Roughness = poster ? 0.6f : 1f,
        };
    }

    /// <summary>
    /// Size of a plane that shows an image of <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> pixels
    /// undistorted with the given width in metres.
    /// </summary>
    public static Vector2 SizeForAspect(int pixelWidth, int pixelHeight, float width)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return new Vector2(Valid(width), Valid(width));
        float w = Valid(width);
        return new Vector2(w, Valid(w * pixelHeight / pixelWidth));
    }

    private static float Valid(float v) => float.IsFinite(v) ? System.Math.Clamp(v, WallMesh.MinimumSize, WallMesh.MaximumSize) : WallMesh.MinimumSize;
}
