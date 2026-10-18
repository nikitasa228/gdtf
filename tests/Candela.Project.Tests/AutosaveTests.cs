using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Project.Tests;

public sealed class AutosaveTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly TempDir _dir = new();
    private readonly ManualClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private string AutosaveDir => Path.Combine(_dir.Path, "autosave");

    private AutosaveOptions Options(int keep = 3) => new() { Directory = AutosaveDir, Interval = Interval, KeepCount = keep, TimeProvider = _clock };

    private ProjectSaveOptions SaveOptions => new() { GdtfProvider = TestData.LibraryGdtf, TimeProvider = _clock };

    /// <summary>Lets the interval pass, runs one main-loop update, waits for the background write and delivers its result.</summary>
    private bool Tick(AutosaveService service)
    {
        _clock.Advance(Interval + TimeSpan.FromSeconds(1));
        bool started = service.Update();
        service.FlushAsync().Wait();
        service.Update();
        return started;
    }

    [Fact]
    public void Autosave_writes_periodically_keeps_the_last_n_and_lists_recovery_files()
    {
        var project = TestData.RichProject();
        var original = _dir.File("show.candela");
        ProjectFile.Save(original, project, SaveOptions);

        var saved = new List<AutosaveResult>();
        using (var service = AutosaveService.ForProject(project, SaveOptions, Options(keep: 2)))
        {
            service.Saved += saved.Add;
            Assert.Equal(Path.Combine(AutosaveDir, project.Id.ToString("D") + ".candela"), service.FilePath);

            // Nothing is due yet, and an unchanged project is never written.
            Assert.False(service.Update());
            Assert.False(Tick(service));
            Assert.Empty(service.Files);

            project.Scene.Add(new Entity("Edit 1"));
            Assert.False(service.Update()); // the interval restarted at the last check
            Assert.True(Tick(service));
            Assert.Single(saved);
            Assert.Equal(service.FilePath, saved[0].Path);
            Assert.Empty(saved[0].Issues);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime, service.LastSavedUtc);
            Assert.True(File.Exists(service.FilePath));

            // A live session is never offered for recovery (not even to another instance).
            Assert.Empty(AutosaveService.FindRecoverable(AutosaveDir));

            project.Scene.Add(new Entity("Edit 2"));
            Assert.True(Tick(service));
            project.Settings.SetJson("view", """{"mode":"Wireframe"}""");
            Assert.True(Tick(service));
            Assert.Equal(3, saved.Count);
            Assert.Equal(2, service.Files.Count);
            Assert.Equal(service.FilePath, service.Files[0]);
            Assert.Null(service.LastError);
            // The background write did not disturb the main thread's scene.
            Assert.Equal(TestData.RichProject().Scene.Count + 2, project.Scene.Count);
        }

        // Disposed without CompleteSession (as after a crash): the session is recoverable.
        var session = Assert.Single(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Equal(project.Id, session.ProjectId);
        Assert.Equal("Rich stage", session.SceneName);
        Assert.Equal(Path.GetFullPath(original), session.OriginalPath);
        Assert.Equal(2, session.Files.Count);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, session.LastSavedUtc);

        var recovered = session.Load();
        Assert.Empty(recovered.LoadIssues);
        Assert.Equal(TestData.SceneJson(project.Scene), TestData.SceneJson(recovered.Scene));
        Assert.Equal("""{"mode":"Wireframe"}""", recovered.Settings.GetJson("view"));
        Assert.Equal(Path.GetFullPath(original), recovered.FilePath);
        Assert.Equal(2, recovered.GdtfFiles.Count);

        // The previous autosave is the older file.
        var older = ProjectFile.Load(session.Files[1]);
        Assert.Equal(project.Scene.Count, older.Scene.Count);
        Assert.DoesNotContain("Wireframe", older.Settings.GetJson("view"));

        session.Delete();
        Assert.Empty(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Empty(Directory.GetFiles(AutosaveDir));
    }

    [Fact]
    public void Clean_exit_deletes_the_session()
    {
        var project = TestData.RichProject();
        var service = AutosaveService.ForProject(project, SaveOptions, Options(), startDirty: true);
        Assert.True(Tick(service));
        Assert.Single(service.Files);
        service.CompleteSession();
        Assert.Empty(Directory.GetFiles(AutosaveDir));
        Assert.Empty(AutosaveService.FindRecoverable(AutosaveDir));
        service.Dispose();
        Assert.False(service.Update());
    }

    [Fact]
    public void Recovery_falls_back_to_an_older_autosave_when_the_newest_is_damaged()
    {
        var project = new ProjectData(new Scene("Fallback"));
        using (var service = AutosaveService.ForProject(project, SaveOptions, Options()))
        {
            project.Scene.Add(new Entity("A"));
            Assert.True(Tick(service));
            project.Scene.Add(new Entity("B"));
            Assert.True(Tick(service));
            File.WriteAllText(service.FilePath, "damaged by the crash");
        }

        var session = Assert.Single(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Null(session.OriginalPath);
        var recovered = session.Load();
        var issue = Assert.Single(recovered.LoadIssues);
        Assert.Equal(ProjectIssueCodes.RecoveredOlder, issue.Code);
        Assert.Equal(new[] { "A" }, recovered.Scene.All().Select(e => e.Name));
        Assert.Null(recovered.FilePath);

        foreach (var file in session.Files) File.WriteAllText(file, "all gone");
        Assert.Throws<ProjectFormatException>(() => session.Load());
    }

    [Fact]
    public async Task Second_instance_of_the_same_project_gets_its_own_session()
    {
        var project = new ProjectData(new Scene("Twice"));
        using var first = new AutosaveService(project.Id, () => ProjectSnapshot.Capture(project), Options());
        using var second = new AutosaveService(project.Id, () => ProjectSnapshot.Capture(project), Options());
        Assert.Equal(project.Id.ToString("D"), first.SessionKey);
        Assert.Equal(project.Id.ToString("D") + "~2", second.SessionKey);
        await first.SaveNowAsync();
        await second.SaveNowAsync();
        Assert.Empty(AutosaveService.FindRecoverable(AutosaveDir));

        second.Dispose();
        var session = Assert.Single(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Equal(project.Id, session.ProjectId);
        Assert.Equal(second.SessionKey, session.SessionKey);
    }

    [Fact]
    public void Failed_writes_are_reported_on_the_main_thread_and_retried()
    {
        var project = new ProjectData(new Scene("Flaky"));
        var fixture = new Entity("Fixture");
        fixture.Add(new FixtureComponent { GdtfSpec = TestData.GdtfA });
        project.Scene.Add(fixture);
        bool fail = true;
        var options = new ProjectSaveOptions { GdtfProvider = name => fail ? throw new IOException("network share gone") : TestData.LibraryGdtf(name) };

        using var service = AutosaveService.ForProject(project, options, Options(), startDirty: true);
        var failures = new List<Exception>();
        int threadOfEvent = -1;
        service.Failed += e =>
        {
            failures.Add(e);
            threadOfEvent = Environment.CurrentManagedThreadId;
        };

        Assert.True(Tick(service));
        var failure = Assert.Single(failures);
        Assert.Equal("network share gone", failure.Message);
        Assert.Same(failure, service.LastError);
        Assert.Equal(Environment.CurrentManagedThreadId, threadOfEvent);
        Assert.Empty(service.Files);

        // The change is still unsaved, so the next interval tries again.
        fail = false;
        Assert.True(Tick(service));
        Assert.Null(service.LastError);
        Assert.Single(service.Files);
        Assert.Equal(TestData.LibraryGdtf(TestData.GdtfA), ProjectFile.Load(service.FilePath).GdtfFiles[TestData.GdtfA]);
    }

    [Fact]
    public async Task Snapshot_callback_controls_what_is_written()
    {
        var project = new ProjectData(new Scene("Callback"));
        ProjectSnapshot? next = null;
        int calls = 0;
        using var service = new AutosaveService(project.Id, () =>
        {
            calls++;
            return next;
        }, Options());

        // Null means "nothing to save".
        Assert.False(Tick(service));
        Assert.Equal(1, calls);
        Assert.Empty(service.Files);

        // Disabled: no capture at all.
        service.Enabled = false;
        Assert.False(Tick(service));
        Assert.Equal(1, calls);

        // SaveNowAsync captures immediately, whatever the interval or Enabled.
        next = ProjectSnapshot.Capture(project);
        await service.SaveNowAsync();
        Assert.Equal(2, calls);
        Assert.Single(service.Files);
        Assert.Equal("Callback", ProjectFile.ReadManifest(service.FilePath).SceneName);

        // A throwing callback is reported, never thrown into the main loop.
        service.Enabled = true;
        Exception? reported = null;
        service.Failed += e => reported = e;
        int before = calls;
        using var throwing = new AutosaveService(Guid.NewGuid(), () => throw new InvalidOperationException("capture bug"), Options());
        throwing.Failed += e => reported = e;
        _clock.Advance(Interval * 2);
        Assert.False(throwing.Update());
        Assert.Equal("capture bug", reported?.Message);
        Assert.Equal(before, calls);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Interval = TimeSpan.Zero);
    }

    [Fact]
    public void Update_does_not_allocate_while_nothing_is_due()
    {
        var project = new ProjectData(new Scene("Quiet"));
        using var service = AutosaveService.ForProject(project, SaveOptions, Options());
        service.Update();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) service.Update();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public async Task A_new_session_never_overwrites_or_deletes_crash_data_that_was_not_recovered()
    {
        var project = new ProjectData(new Scene("Crashed"));
        project.Scene.Add(new Entity("Unsaved work"));
        using (var crashed = AutosaveService.ForProject(project, SaveOptions, Options(), startDirty: true))
        {
            await crashed.SaveNowAsync();
        } // the process "crashes": no CompleteSession
        var crashFile = Assert.Single(Directory.GetFiles(AutosaveDir, "*.candela"));
        var crashBytes = File.ReadAllBytes(crashFile);

        // The program starts again and the user opens the project file before deciding about the recovery offer.
        var reopened = new ProjectData(new Scene("Crashed")) { Id = project.Id };
        var session = AutosaveService.ForProject(reopened, SaveOptions, Options(keep: 1), startDirty: true);
        Assert.NotEqual(project.Id.ToString("D"), session.SessionKey);
        for (int i = 0; i < 3; i++)
        {
            reopened.Scene.Add(new Entity("Edit " + i));
            Assert.True(Tick(session));
        }
        session.CompleteSession();

        var recoverable = Assert.Single(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Equal(project.Id, recoverable.ProjectId);
        Assert.Equal(crashBytes, File.ReadAllBytes(Assert.Single(recoverable.Files)));
        Assert.Contains(recoverable.Load().Scene.All(), e => e.Name == "Unsaved work");
    }

    [Fact]
    public async Task A_recovered_session_is_resumed_and_cleaned_up_on_a_clean_exit()
    {
        var project = new ProjectData(new Scene("Resume"));
        project.Scene.Add(new Entity("Before the crash"));
        using (var crashed = AutosaveService.ForProject(project, SaveOptions, Options(), startDirty: true))
        {
            await crashed.SaveNowAsync();
        }

        var offer = Assert.Single(AutosaveService.FindRecoverable(AutosaveDir));
        var recovered = offer.Load();
        using var resumed = AutosaveService.ForProject(recovered, SaveOptions, new AutosaveOptions
        {
            Directory = Path.Combine(_dir.Path, "elsewhere"), // the recovered session's own folder wins
            Interval = Interval,
            TimeProvider = _clock,
            Resume = offer,
        });
        Assert.Equal(offer.SessionKey, resumed.SessionKey);
        Assert.Equal(offer.Directory, resumed.Directory);
        Assert.Empty(AutosaveService.FindRecoverable(AutosaveDir)); // it is live again

        recovered.Scene.Add(new Entity("After the recovery"));
        Assert.True(Tick(resumed));
        Assert.Equal(2, resumed.Files.Count); // continues the series: the recovered file became the older copy
        resumed.CompleteSession();
        Assert.Empty(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Empty(Directory.GetFiles(AutosaveDir));

        var other = new ProjectData(new Scene("Other"));
        Assert.Throws<ArgumentException>(() => new AutosaveService(other.Id, () => null, new AutosaveOptions { Resume = offer }));
    }

    [Fact]
    public async Task Saving_the_project_removes_the_autosaves_it_supersedes()
    {
        var project = new ProjectData(new Scene("Saved"));
        var path = _dir.File("saved.candela");
        using (var service = AutosaveService.ForProject(project, SaveOptions, Options()))
        {
            project.Scene.Add(new Entity("Edit"));
            Assert.True(Tick(service));
            Assert.Single(service.Files);

            ProjectFile.Save(path, project, SaveOptions);
            await service.MarkSaved();
            Assert.Empty(service.Files);
            Assert.False(Tick(service)); // nothing changed since the save

            project.Scene.Add(new Entity("Edit after saving"));
            Assert.True(Tick(service));
            Assert.Single(service.Files);
            await service.MarkSaved();
        } // a crash right after saving offers nothing older than the file
        Assert.Empty(AutosaveService.FindRecoverable(AutosaveDir));
    }

    [Fact]
    public void Recovery_that_is_older_than_the_saved_file_is_flagged()
    {
        var project = new ProjectData(new Scene("Stale"));
        var path = _dir.File("stale.candela");
        ProjectFile.Save(path, project, SaveOptions);
        using (var service = AutosaveService.ForProject(project, SaveOptions, Options()))
        {
            project.Scene.Add(new Entity("Edit"));
            Assert.True(Tick(service));
            _clock.Advance(TimeSpan.FromMinutes(2));
            ProjectFile.Save(path, project, SaveOptions); // saved, but the program ends before MarkSaved
        }
        var offer = Assert.Single(AutosaveService.FindRecoverable(AutosaveDir));
        Assert.Equal(Path.GetFullPath(path), offer.OriginalPath);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, offer.OriginalModifiedUtc);
        Assert.True(offer.IsOlderThanOriginal);
        offer.Delete();

        // The usual case: the autosave is newer than the file.
        using (var service = AutosaveService.ForProject(project, SaveOptions, Options()))
        {
            project.Scene.Add(new Entity("Unsaved edit"));
            Assert.True(Tick(service));
        }
        Assert.False(Assert.Single(AutosaveService.FindRecoverable(AutosaveDir)).IsOlderThanOriginal);
    }

    [Fact]
    public void Autosave_file_names_are_parsed_strictly()
    {
        var id = Guid.NewGuid().ToString("D");
        Assert.Equal((id, 0), AutosaveService.ParseAutosaveName(id + ".candela"));
        Assert.Equal((id, 3), AutosaveService.ParseAutosaveName(id + ".3.candela"));
        Assert.Equal((id + "~2", 1), AutosaveService.ParseAutosaveName(id + "~2.1.candela"));
        Assert.Null(AutosaveService.ParseAutosaveName("." + id + ".candela.abc.tmp"));
        Assert.Null(AutosaveService.ParseAutosaveName(id + ".session.json"));
        Assert.EndsWith("autosave", AutosaveService.DefaultDirectory);
    }
}
