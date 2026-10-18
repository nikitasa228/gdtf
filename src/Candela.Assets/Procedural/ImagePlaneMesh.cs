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
    /// <summary>
    /// Luminance (cd/m²) given to white in an unlit reference drawing. Core materials have no "unlit" switch, so a
    /// reference image is shown through its emissive channel at roughly the luminance of a monitor's white.
    /// </summary>
    public const float ReferenceLuminance = 100f;

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
    /// Material for the image: a lit, rough print for <see cref="ImageMode.Poster"/>; for <see cref="ImageMode.Reference"/>
    /// a black, emissive surface at <see cref="ReferenceLuminance"/> so the drawing reads the same under any stage light.
    /// Opacity below 1 switches to alpha blending; the component's double-sided flag is carried over.
    /// </summary>
    public static MaterialData CreateMaterial(ImageComponent image)
    {
        ArgumentNullException.ThrowIfNull(image);
        string key = AssetKeys.ForAsset(image.ImageAssetId);
        float opacity = float.IsFinite(image.Opacity) ? System.Math.Clamp(image.Opacity, 0f, 1f) : 1f;
        var m = new MaterialData
        {
            Name = image.Mode == ImageMode.Poster ? "Poster" : "Reference drawing",
            DoubleSided = image.DoubleSided,
            AlphaMode = opacity < 1f ? AlphaMode.Blend : AlphaMode.Opaque,
            Metallic = 0f,
        };
        if (image.Mode == ImageMode.Poster)
        {
            // Printed paper / PVC banner: diffuse, satin finish.
            m.BaseColorFactor = new Vector4(1f, 1f, 1f, opacity);
            m.BaseColorTexture = key;
            m.Roughness = 0.6f;
        }
        else
        {
            m.BaseColorFactor = new Vector4(0f, 0f, 0f, opacity);
            m.Roughness = 1f;
            m.EmissiveTexture = key;
            m.EmissiveColor = Vector3.One;
            m.EmissiveLuminance = ReferenceLuminance;
        }
        return m;
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
