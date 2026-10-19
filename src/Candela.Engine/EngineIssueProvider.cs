using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine;

/// <summary>
/// Issues the engine itself knows about: entities that refer to model or image assets the project does not contain
/// (<see cref="EngineIssueCodes.AssetMissing"/>), models that could not be decoded or imported safely, textures that are
/// missing (from the <see cref="AssetStore"/>), fixture models that failed to load in the renderer binding, and
/// problems of the simulations. GDTF resolution problems come from the patch provider (<c>patch.unknown-gdtf</c> …).
/// Never decodes anything: it reports what loading has found so far.
/// </summary>
public sealed class EngineIssueProvider : IIssueProvider
{
    private readonly StageEngine _engine;

    /// <summary>Creates the provider for <paramref name="engine"/>.</summary>
    public EngineIssueProvider(StageEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <inheritdoc />
    public string Name => "Assets and simulation";

    /// <inheritdoc />
    public IEnumerable<Issue> Analyze(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var issues = new List<Issue>();
        var missing = new Dictionary<Guid, List<Guid>>();
        foreach (var entity in scene.All())
        {
            foreach (var component in entity.Components)
            {
                Guid? asset = component switch
                {
                    MeshComponent m => m.ModelAssetId,
                    ImageComponent i => i.ImageAssetId,
                    TrussComponent t => t.ModelAssetId,
                    _ => null,
                };
                if (asset is not { } id || id == Guid.Empty || _engine.Assets.Contains(id)) continue;
                if (!missing.TryGetValue(id, out var users)) missing[id] = users = new List<Guid>();
                users.Add(entity.Id);
            }
        }
        foreach (var (id, users) in missing)
        {
            issues.Add(new Issue(IssueSeverity.Error, IssueCategory.Scene, EngineIssueCodes.AssetMissing,
                $"{users.Count} object(s) use an asset that is not in the project ({id}); they are not drawn.", users));
        }
        issues.AddRange(_engine.Models.Issues);
        issues.AddRange(_engine.Assets.Issues);
        if (_engine.Render is { } render) issues.AddRange(render.Issues);
        issues.AddRange(_engine.Simulation.Issues);
        var clamped = _engine.LightCollector.ClampedFieldFixtures;
        if (clamped.Count > 0)
        {
            issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Fixture, EngineIssueCodes.FieldClamped,
                $"{clamped.Count} fixture(s) have a beam angle wider than {StageLightCollector.MaxFieldAngle * 180f / MathF.PI:F0}° (light in nearly every direction); the renderer draws their light as a {StageLightCollector.MaxFieldAngle * 180f / MathF.PI:F0}° cone.",
                clamped.ToArray()));
        }
        return issues;
    }
}
