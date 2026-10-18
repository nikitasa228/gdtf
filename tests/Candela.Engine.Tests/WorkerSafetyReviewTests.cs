using System.Diagnostics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine.Tests;

/// <summary>The isolated import worker under hostile behaviour: lingering children, garbage results, crashes on open.</summary>
public sealed class WorkerSafetyReviewTests
{
    private static string Script(string folder, string name, string body)
    {
        string script = Path.Combine(folder, name);
        File.WriteAllText(script, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static StageEngine EngineWithWorker(string worker, string name, TimeSpan? timeout = null) => new(new StageEngineOptions
    {
        Library = TestEnvironment.Library,
        CacheDirectory = TestEnvironment.NewTempDirectory(name + "-cache"),
        ImportWorkerPath = worker,
        ImportWorkerTimeout = timeout ?? TimeSpan.FromSeconds(2),
    });

    [SkippableFact]
    public void A_crashing_worker_whose_child_keeps_the_output_open_still_returns_within_the_timeout()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell script as the worker");
        string folder = TestEnvironment.NewTempDirectory("linger");
        string obj = SceneBuilder.WriteCubeObj(folder);
        // the crashing importer leaves a helper process behind that inherited stdout/stderr
        string script = Script(folder, "lingering-worker.sh", "sleep 25 &\necho 'crash' >&2\nexit 139");
        using var engine = EngineWithWorker(script, "linger");
        var watch = Stopwatch.StartNew();
        var result = engine.Import(obj);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), $"the import blocked for {watch.Elapsed.TotalSeconds:0} s");
        Assert.False(result.Success);
        Assert.Equal(EngineIssueCodes.ImportUnsafe, Assert.Single(result.Issues).Code);
        Assert.Equal(0, engine.Assets.Count);
    }

    [SkippableFact]
    public void A_worker_that_writes_garbage_is_reported_as_unsafe()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell script as the worker");
        string folder = TestEnvironment.NewTempDirectory("garbage");
        string obj = SceneBuilder.WriteCubeObj(folder);
        // $3 is the output folder (import-worker <input> <output-dir> ...)
        string script = Script(folder, "garbage-worker.sh", $"head -c 5000 /dev/urandom > \"$3/{ImportWorker.PackageFileName}\"\nexit 0");
        using var engine = EngineWithWorker(script, "garbage");
        var result = engine.Import(obj);
        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EngineIssueCodes.ImportUnsafe, issue.Code);
        Assert.Equal(0, engine.Assets.Count);
        Assert.Equal(0, engine.Scene.Count);
    }

    [SkippableFact]
    public void A_project_whose_model_crashes_the_worker_opens_and_renders_without_it()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell script as the worker");
        string folder = TestEnvironment.NewTempDirectory("open-crash");
        string obj = SceneBuilder.WriteCubeObj(folder, "riser");
        string path = Path.Combine(folder, "show.candela");
        using (var good = TestEnvironment.CreateEngine("open-crash-good"))
        {
            Assert.True(good.Import(obj).Success);
            SceneBuilder.Fixture(good.Scene, SceneBuilder.MacViper, "Basic", new System.Numerics.Vector3(0, 0, 6));
            Assert.True(good.SaveAs(path).Success);
        }

        string script = Script(folder, "crashing-worker.sh", "kill -SEGV $$");
        using var engine = EngineWithWorker(script, "open-crash");
        var opened = engine.Open(path);
        Assert.True(opened.Success, opened.Summary);
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Tick(0);
        engine.Tick(1.0 / 30);
        Assert.Empty(world.Violations);
        var model = engine.Scene.Query<MeshComponent>().Single().Entity;
        Assert.Empty(sync.InstancesOf(model.Id));
        var fixture = engine.Scene.Query<FixtureComponent>().Single().Entity;
        Assert.NotEmpty(sync.InstancesOf(fixture.Id));
        engine.RefreshIssues();
        Assert.Contains(engine.Issues.Current, i => i.Code == EngineIssueCodes.ImportUnsafe);
        // the asset itself is kept: saving again does not lose the model for a machine where it imports
        string again = Path.Combine(folder, "again.candela");
        Assert.True(engine.SaveAs(again).Success);
        using var check = TestEnvironment.CreateEngine("open-crash-check");
        Assert.True(check.Open(again).Success);
        var mesh = check.Scene.Query<MeshComponent>().Single().Component;
        Assert.True(check.Models.TryGet(mesh.ModelAssetId, out var decoded, out var error), error);
        Assert.Equal(12, decoded.TriangleCount);
    }
}
