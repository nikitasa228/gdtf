using Candela.Electrical;
using Candela.Engine;

namespace Candela.App.Session;

/// <summary>
/// The data/power network the editor's panels show (cable lengths and weights, plugs, feeds, outlet loads). The
/// engine keeps its <see cref="StageEngine.NetworkSolution"/> until the scene's structure changes, so after a device, a
/// truss or a wall that cable points are fixed to moved, its lengths still describe the old positions; this returns
/// that solution while it describes the scene as it is now, and otherwise the solver's solution for the current scene
/// version (computed once per version and shared, about a millisecond for a 90-fixture rig with a cable on each).
/// </summary>
internal static class CablingNetwork
{
    /// <summary>The network solution of <paramref name="engine"/>'s scene at its current version.</summary>
    public static NetworkSolution Current(StageEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var solution = engine.NetworkSolution;
        var scene = engine.Scene;
        return solution.SceneId == scene.Id && solution.SceneVersion == scene.Version ? solution : engine.Network.GetOrSolve(scene);
    }
}
