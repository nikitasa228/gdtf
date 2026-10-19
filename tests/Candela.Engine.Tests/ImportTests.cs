using System.Numerics;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine.Tests;

/// <summary>Importing models and images: the isolated worker, its failure modes, the model package, undo.</summary>
public sealed class ImportTests
{
    [Fact]
    public void The_import_worker_program_is_found_next_to_the_application()
    {
        string? worker = ImportWorker.FindWorker();
        Assert.NotNull(worker);
        Assert.Matches(@"candela(\.dll|\.exe)?$", Path.GetFileName(worker!));
        Assert.True(ImportWorker.UsesNativeImporter("chair.OBJ"));
        Assert.True(ImportWorker.UsesNativeImporter("truss.3ds"));
        Assert.False(ImportWorker.UsesNativeImporter("chair.glb"));
        Assert.False(ImportWorker.UsesNativeImporter("part.stl"));
    }

    [Fact]
    public void Native_model_formats_are_imported_in_the_worker_process()
    {
        string folder = TestEnvironment.NewTempDirectory("obj");
        string obj = SceneBuilder.WriteCubeObj(folder);
        using var engine = TestEnvironment.CreateEngine("worker");

        var result = engine.Import(obj, new EngineImportOptions { Placement = new Core.Math.Transform(new Vector3(1, 2, 0)) });
        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain(result.Issues, i => i.Code == EngineIssueCodes.ImportInProcess);
        var entity = engine.Scene.Find(Assert.Single(result.Entities))!;
        var mesh = entity.Get<MeshComponent>()!;
        var asset = engine.Assets.Find(mesh.ModelAssetId)!;
        Assert.Equal(AssetKind.Model, asset.Kind);
        Assert.Contains("cube.mtl", asset.CompanionFiles.Keys);
        Assert.Contains("tex.png", asset.CompanionFiles.Keys);

        Assert.True(engine.Models.TryGet(asset.Id, out var model, out var error), error);
        Assert.Equal(12, model.TriangleCount);
        var bounds = model.Model.ComputeBounds();
        Assert.Equal(1f, bounds.Size.Length() / MathF.Sqrt(3f), 2);
        // the texture key is the asset's and resolves without decoding the model again
        string key = Assert.Single(model.Model.Materials, m => m.BaseColorTexture is not null).BaseColorTexture!;
        Assert.StartsWith(AssetKeys.TexturePrefix(asset.Id), key, StringComparison.Ordinal);
        var image = engine.Images.Load(key);
        Assert.NotNull(image);
        Assert.Equal(2, image!.Width);

        // the decoded model is cached on disk: a new engine with the same cache reads it without the worker
        var cached = Directory.GetFiles(engine.CacheDirectory, "*" + ModelPackage.Extension, SearchOption.AllDirectories);
        Assert.Single(cached);

        // undo removes the imported entity, redo brings it back
        engine.Undo.Undo();
        Assert.Null(engine.Scene.Find(entity.Id));
        engine.Undo.Redo();
        Assert.NotNull(engine.Scene.Find(entity.Id));
    }

    [SkippableFact]
    public void A_crashing_worker_is_reported_as_not_imported_safely()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell script as the worker");
        string folder = TestEnvironment.NewTempDirectory("crash");
        string obj = SceneBuilder.WriteCubeObj(folder);
        string script = Path.Combine(folder, "crashing-worker.sh");
        File.WriteAllText(script, "#!/bin/sh\necho 'Segmentation fault in libassimp' >&2\nkill -SEGV $$\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var engine = new StageEngine(new StageEngineOptions
        {
            Library = TestEnvironment.Library,
            CacheDirectory = TestEnvironment.NewTempDirectory("crash-cache"),
            ImportWorkerPath = script,
        });
        int before = engine.Scene.Count;
        var result = engine.Import(obj);
        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EngineIssueCodes.ImportUnsafe, issue.Code);
        Assert.Contains("could not be imported safely", issue.Message, StringComparison.Ordinal);
        Assert.Equal(before, engine.Scene.Count);
        Assert.Equal(0, engine.Assets.Count);
        Assert.Contains(engine.Issues.Current, i => i.Code == EngineIssueCodes.ImportUnsafe);
    }

    [SkippableFact]
    public void A_hanging_worker_is_stopped_after_the_timeout()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell script as the worker");
        string folder = TestEnvironment.NewTempDirectory("hang");
        string obj = SceneBuilder.WriteCubeObj(folder);
        string script = Path.Combine(folder, "hanging-worker.sh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var engine = new StageEngine(new StageEngineOptions
        {
            Library = TestEnvironment.Library,
            CacheDirectory = TestEnvironment.NewTempDirectory("hang-cache"),
            ImportWorkerPath = script,
            ImportWorkerTimeout = TimeSpan.FromSeconds(1),
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = engine.Import(obj);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        Assert.False(result.Success);
        Assert.Equal(EngineIssueCodes.ImportUnsafe, Assert.Single(result.Issues).Code);
        Assert.Contains("longer than", result.Issues[0].Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Models_of_an_opened_project_decode_in_the_background_without_stalling_frames()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell script as the worker");
        // a project with an OBJ model, saved by an engine with the real worker
        string project = Path.Combine(TestEnvironment.NewTempDirectory("bg-project"), "riser.candela");
        Guid meshEntity;
        using (var author = TestEnvironment.CreateEngine("bg-author"))
        {
            var imported = author.Import(SceneBuilder.WriteCubeObj(TestEnvironment.NewTempDirectory("bg-model")));
            Assert.True(imported.Success, imported.Summary);
            meshEntity = Assert.Single(imported.Entities);
            Assert.True(author.SaveAs(project).Success);
        }

        // opened on a machine whose worker hangs (and with an empty model cache): frames keep coming
        string script = Path.Combine(TestEnvironment.NewTempDirectory("bg-hang"), "hanging-worker.sh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (var engine = new StageEngine(new StageEngineOptions
        {
            Library = TestEnvironment.Library,
            CacheDirectory = TestEnvironment.NewTempDirectory("bg-hang-cache"),
            ImportWorkerPath = script,
            ImportWorkerTimeout = TimeSpan.FromSeconds(2),
        }))
        {
            Assert.True(engine.Open(project).Success);
            var world = new RecordingRenderWorld();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var sync = engine.AttachRenderWorld(world);
            for (int i = 0; i < 5; i++) engine.Tick(1.0 / 30);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"frames took {watch.Elapsed.TotalSeconds:F1} s while the worker hangs");
            Assert.Equal(1, sync.PendingModels);
            Assert.Empty(sync.InstancesOf(meshEntity));

            // the worker is stopped after its timeout; the failure is reported once and not retried every frame
            Assert.True(sync.CompletePendingLoads(TimeSpan.FromSeconds(20)));
            Assert.Equal(0, sync.PendingModels);
            Assert.Empty(sync.InstancesOf(meshEntity));
            Assert.Contains(engine.Models.Issues, i => i.Code == EngineIssueCodes.ImportUnsafe);
            var asset = engine.Scene.Find(meshEntity)!.Get<MeshComponent>()!.ModelAssetId;
            Assert.True(engine.Assets.TryGetDecodedModel(asset, out var none, out var error) && none is null && error is not null);
            engine.Tick(1.0 / 30);
            Assert.Equal(0, sync.PendingModels);
        }

        // with a working worker the model appears once it has decoded, and stays in the asset store
        using (var engine = TestEnvironment.CreateEngine("bg-ok"))
        {
            Assert.True(engine.Open(project).Success);
            var world = new RecordingRenderWorld();
            var sync = engine.AttachRenderWorld(world);
            engine.Tick(1.0 / 30);
            Assert.True(sync.CompletePendingLoads(TimeSpan.FromSeconds(60)));
            engine.Tick(1.0 / 30);
            Assert.NotEmpty(sync.InstancesOf(meshEntity));
            var asset = engine.Scene.Find(meshEntity)!.Get<MeshComponent>()!.ModelAssetId;
            Assert.True(engine.Assets.TryGetDecodedModel(asset, out var model, out _) && model is not null);
            Assert.True(engine.Models.IsDecoded(asset));
            Assert.Empty(world.Violations);
        }
    }

    [Fact]
    public void Without_a_worker_models_are_imported_in_process_with_a_note()
    {
        string folder = TestEnvironment.NewTempDirectory("noworker");
        string obj = SceneBuilder.WriteCubeObj(folder);
        using var engine = new StageEngine(new StageEngineOptions
        {
            Library = TestEnvironment.Library,
            CacheDirectory = TestEnvironment.NewTempDirectory("noworker-cache"),
            ImportWorkerPath = Path.Combine(folder, "does-not-exist"),
        });
        var result = engine.Import(obj);
        Assert.True(result.Success, result.Summary);
        Assert.Contains(result.Issues, i => i.Code == EngineIssueCodes.ImportInProcess && i.Severity == IssueSeverity.Note);
    }

    /// <summary>
    /// Imports <paramref name="path"/> through the real worker (<c>candela import-worker</c>) until a run completes. Each
    /// attempt gets its own, short timeout: under a fully parallel test run merely starting the worker process can take
    /// long, and a run stopped by the timeout (<see cref="EngineIssueCodes.ImportUnsafe"/>, "longer than") says nothing
    /// about the file — it is repeated with twice the time. Every other outcome is returned as it is, so the caller
    /// still tells a rejection from a crash or a timeout.
    /// </summary>
    private static EngineResult ImportUntilTheWorkerCompletes(string path, string name, out int attempts)
    {
        var timeout = TimeSpan.FromSeconds(15);
        for (attempts = 1; ; attempts++)
        {
            using var engine = new StageEngine(new StageEngineOptions
            {
                Library = TestEnvironment.Library,
                CacheDirectory = TestEnvironment.NewTempDirectory(name + "-cache"),
                ImportWorkerTimeout = timeout,
            });
            var result = engine.Import(path);
            bool timedOut = !result.Success && result.Issues.Any(i => i.Code == EngineIssueCodes.ImportUnsafe &&
                                                                       i.Message.Contains("longer than", StringComparison.Ordinal));
            if (!timedOut || attempts == 4) return result;
            timeout *= 2; // 15 + 30 + 60 + 120 s at worst, only on a machine too busy to start a process
        }
    }

    [Fact]
    public void The_worker_rejects_unreadable_files_with_a_reason()
    {
        string folder = TestEnvironment.NewTempDirectory("reject");
        string bad = Path.Combine(folder, "broken.fbx");
        File.WriteAllText(bad, "this is not an FBX file");
        Assert.NotNull(ImportWorker.FindWorker()); // the real worker, not the in-process fallback

        var result = ImportUntilTheWorkerCompletes(bad, "reject", out int attempts);
        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues);
        Assert.True(issue.Code == EngineIssueCodes.ImportFailed,
            $"expected the worker to reject the file with a reason after {attempts} attempt(s), got {issue.Code}: {issue.Message}");
        Assert.DoesNotContain("safely", issue.Message, StringComparison.Ordinal);
        Assert.Contains("broken.fbx", issue.Message, StringComparison.Ordinal);
        Assert.True(issue.Message.Length > "'broken.fbx': ".Length + 5, issue.Message); // the importer's reason follows the name

        using var engine = TestEnvironment.CreateEngine("reject");
        var skp = Path.Combine(folder, "venue.skp");
        File.WriteAllBytes(skp, [0xFF, 0xFE, 0xFF, 0x0E, 0x53, 0x00]);
        Assert.Contains("SketchUp", engine.Import(skp).Summary, StringComparison.Ordinal);
        Assert.False(engine.Import(Path.Combine(folder, "missing.obj")).Success);
    }

    [SkippableFact]
    public void A_worker_rejection_reaches_the_user_word_for_word_and_is_never_called_unsafe()
    {
        // the deterministic half of the test above: a worker that rejects at once, whatever the machine's load
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell script as the worker");
        string folder = TestEnvironment.NewTempDirectory("reject-script");
        string obj = SceneBuilder.WriteCubeObj(folder);
        string script = Path.Combine(folder, "rejecting-worker.sh");
        // candela import-worker <input> <output-dir> …: the reason goes to <output-dir>/error.txt, exit code 4
        File.WriteAllText(script, "#!/bin/sh\nprintf 'The file has no geometry the importer understands.' > \"$3/error.txt\"\nexit 4\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var engine = new StageEngine(new StageEngineOptions
        {
            Library = TestEnvironment.Library,
            CacheDirectory = TestEnvironment.NewTempDirectory("reject-script-cache"),
            ImportWorkerPath = script,
            ImportWorkerTimeout = TimeSpan.FromMinutes(1),
        });
        int before = engine.Scene.Count;
        var result = engine.Import(obj);
        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EngineIssueCodes.ImportFailed, issue.Code);
        Assert.Contains("The file has no geometry the importer understands.", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("safely", issue.Message, StringComparison.Ordinal);
        Assert.Equal(before, engine.Scene.Count);
        Assert.Equal(0, engine.Assets.Count);
    }

    [Fact]
    public void Model_package_round_trips_and_rejects_damaged_data()
    {
        string folder = TestEnvironment.NewTempDirectory("package");
        var model = ModelImporter.Import(SceneBuilder.WriteCubeObj(folder));
        using var stream = new MemoryStream();
        ModelPackage.Write(model, stream);
        var bytes = stream.ToArray();
        var read = ModelPackage.Read(new MemoryStream(bytes));
        Assert.Equal(model.MainFile, read.MainFile);
        Assert.Equal(model.Files.Keys.Order(), read.Files.Keys.Order());
        Assert.Equal(model.Model.Parts.Count, read.Model.Parts.Count);
        for (int i = 0; i < model.Model.Parts.Count; i++)
        {
            Assert.Equal(model.Model.Parts[i].Mesh.Positions, read.Model.Parts[i].Mesh.Positions);
            Assert.Equal(model.Model.Parts[i].Mesh.Indices, read.Model.Parts[i].Mesh.Indices);
            Assert.Equal(model.Model.Parts[i].Transform, read.Model.Parts[i].Transform);
        }
        Assert.Equal(model.Model.Materials[0].BaseColorTexture, read.Model.Materials[0].BaseColorTexture);

        for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 40))
            Assert.Throws<InvalidDataException>(() => ModelPackage.Read(new MemoryStream(bytes[..cut])));
        var huge = (byte[])bytes.Clone();
        huge[^8] = 0xFF;
        huge[^7] = 0xFF;
        huge[^6] = 0xFF;
        huge[^5] = 0x7F;
        try
        {
            ModelPackage.Read(new MemoryStream(huge));
        }
        catch (InvalidDataException)
        {
            // a hostile count is rejected before anything is allocated for it
        }
    }

    [Fact]
    public void Images_become_reference_drawings_or_posters()
    {
        string folder = TestEnvironment.NewTempDirectory("image");
        string png = Path.Combine(folder, "plan.png");
        ImageImporter.SavePng(new Core.Geometry.ImageData { Width = 4, Height = 2, Pixels = new byte[4 * 2 * 4] }, png);
        using var engine = TestEnvironment.CreateEngine("image");
        var result = engine.Import(png, new EngineImportOptions { ImageWidth = 8f, ImageMode = ImageMode.Poster });
        Assert.True(result.Success, result.Summary);
        var image = engine.Scene.Find(result.Entities[0])!.Get<ImageComponent>()!;
        Assert.Equal(ImageMode.Poster, image.Mode);
        Assert.Equal(8f, image.Width);
        Assert.Equal(4f, image.Height);

        var world = new RecordingRenderWorld();
        engine.AttachRenderWorld(world);
        engine.Tick(0);
        var instance = Assert.Single(world.Instances.Values);
        Assert.False(instance.Flags.HasFlag(Rendering.InstanceFlags.Reference));
        image.Mode = ImageMode.Reference;
        image.NotifyChanged();
        engine.Tick(0);
        Assert.True(Assert.Single(world.Instances.Values).Flags.HasFlag(Rendering.InstanceFlags.Reference));
        Assert.Empty(world.Violations);
    }
}
