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

    [Fact]
    public void The_worker_rejects_unreadable_files_with_a_reason()
    {
        string folder = TestEnvironment.NewTempDirectory("reject");
        string bad = Path.Combine(folder, "broken.fbx");
        File.WriteAllText(bad, "this is not an FBX file");
        using var engine = TestEnvironment.CreateEngine("reject");
        var result = engine.Import(bad);
        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EngineIssueCodes.ImportFailed, issue.Code);
        Assert.DoesNotContain("safely", issue.Message, StringComparison.Ordinal);

        var skp = Path.Combine(folder, "venue.skp");
        File.WriteAllBytes(skp, [0xFF, 0xFE, 0xFF, 0x0E, 0x53, 0x00]);
        Assert.Contains("SketchUp", engine.Import(skp).Summary, StringComparison.Ordinal);
        Assert.False(engine.Import(Path.Combine(folder, "missing.obj")).Success);
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
