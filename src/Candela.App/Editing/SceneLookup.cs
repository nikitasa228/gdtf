using Candela.Core.SceneGraph;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>Allocation-free scene lookups for code that runs every frame (Core's <c>Scene.FindLayer</c> uses LINQ).</summary>
internal static class SceneLookup
{
    /// <summary>The layer with <paramref name="layerId"/>, or null.</summary>
    public static Layer? LayerOf(SceneModel scene, Guid layerId)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var layers = scene.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Id == layerId)
                return layers[i];
        }

        return null;
    }
}
