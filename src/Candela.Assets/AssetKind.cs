namespace Candela.Assets;

/// <summary>What an entry of an <see cref="AssetStore"/> holds.</summary>
public enum AssetKind
{
    /// <summary>
    /// A 3D model (OBJ, FBX, DAE, 3DS, STL, PLY, glTF/GLB, …) with its companion files (MTL, textures, .bin buffers).
    /// Decoded to <see cref="Candela.Core.Geometry.ModelData"/> by <see cref="ModelImporter"/>.
    /// </summary>
    Model,

    /// <summary>A picture placed in the scene as a reference drawing or poster (ImageComponent).</summary>
    Image,

    /// <summary>A GDTF fixture file kept with the project so it opens on another machine (not decoded here).</summary>
    Gdtf,

    /// <summary>A standalone texture image the user applies to materials (albedo, normal map, …).</summary>
    Texture,
}
