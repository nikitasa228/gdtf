using Candela.Assets;
using Candela.Core.Diagnostics;

namespace Candela.Engine.Tests;

/// <summary>Regression tests from the review of the engine follow-ups (background model decoding).</summary>
public class FollowUpReviewTests
{
    [Fact]
    public async Task A_background_decode_that_throws_is_recorded_as_failed_and_not_started_again()
    {
        // a native-format model in a store that has not decoded it (an opened project), and a worker lookup that throws
        string obj = SceneBuilder.WriteCubeObj(TestEnvironment.NewTempDirectory("decode-throws"));
        var store = new AssetStore();
        var entry = store.Add(AssetKind.Model, "cube.obj", File.ReadAllBytes(obj));
        int lookups = 0;
        var models = new EngineModels(() => store, cacheDirectory: null, workerPath: () =>
        {
            Interlocked.Increment(ref lookups);
            throw new InvalidOperationException("worker lookup failed");
        }, isolateNativeImports: true, timeout: TimeSpan.FromSeconds(5));
        Assert.False(models.IsDecoded(entry.Id));

        Assert.False(await models.DecodeAsync(entry.Id));
        Assert.Equal(1, lookups);
        Assert.Contains(models.Issues, i => i.Code == EngineIssueCodes.AssetUnreadable && i.Severity == IssueSeverity.Error);
        // known to fail: the renderer binding's next request returns at once instead of decoding (and failing) again
        Assert.True(models.IsDecoded(entry.Id));
        Assert.True(store.TryGetDecodedModel(entry.Id, out var model, out var error) && model is null && error is not null);
        Assert.False(await models.DecodeAsync(entry.Id));
        Assert.False(models.TryGet(entry.Id, out _, out var reason));
        Assert.Contains("worker lookup failed", reason, StringComparison.Ordinal);
        Assert.Equal(1, lookups);
    }
}
