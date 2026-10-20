using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Candela.App.Diagnostics;
using Candela.App.Files;
using Candela.App.Input;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.App.Windows;
using Candela.Engine;
using Candela.Gdtf;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// The installed editor's first experience (docs/install.md): the fixture library setup (when it opens, counting a
/// folder in the background, a chosen folder reaching the settings and the running engine, the window drawn in both
/// languages), GDTF imports copied into the library folder, crash reports and their folder, the no-Vulkan message, the
/// first start's language, the window icon and the completeness of the new texts. Each session has its own small
/// library, so the shared one of the other tests never changes.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed partial class InstallExperienceTests : IDisposable
{
    private const string Zonda = "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf";
    private const string Atomic = "Martin_Professional@VDO_Atomic_Bold@20260620.gdtf";
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public InstallExperienceTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
    }

    public void Dispose()
    {
        Loc.SetLanguage("en");
        _log.Dispose();
        _dir.Dispose();
    }

    /// <summary>A folder (created) inside the test folder, with Cyrillic letters and spaces as in a Russian Windows profile.</summary>
    private string Folder(string name)
    {
        string path = _dir.File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepositoryGdtf(string name) => Path.Combine(EngineTestSupport.Root, name);

    /// <summary>An "installed" layout: a program folder outside the repository (no repository root).</summary>
    private InstallLayout InstalledLayout() => new(Folder("Program Files/Candela"), null);

    /// <summary>A session over a private, empty library (generic devices only) and the given layout.</summary>
    private EditorSession CreateSession(InstallLayout layout)
    {
        var options = EngineTestSupport.Options(_dir);
        return new EditorSession(_log, _settings, options with
        {
            Engine = options.Engine! with { Library = new FixtureLibrary(EngineTestSupport.Library.Specs) },
            Layout = layout,
        });
    }

    // ---- when the setup window opens ----------------------------------------------------------------------------

    [Fact]
    public void The_setup_window_opens_at_the_first_start_of_an_installed_copy_only()
    {
        var installed = InstalledLayout();
        var development = new InstallLayout(Folder("bin"), EngineTestSupport.Root);
        var empty = new FixtureLibrary();
        string library = Folder("Мои приборы");
        File.Copy(RepositoryGdtf(Zonda), Path.Combine(library, Zonda));
        var withFixtures = FixtureLibrary.Scan(library);
        var configured = new[] { library };

        // installed, nothing configured: open (unless switched off, headless, or a file was opened)
        Assert.True(LibrarySetup.ShouldOpenAtStart(false, false, false, true, installed, Array.Empty<string>(), empty));
        Assert.False(LibrarySetup.ShouldOpenAtStart(false, false, false, false, installed, Array.Empty<string>(), empty));
        Assert.False(LibrarySetup.ShouldOpenAtStart(false, true, false, true, installed, Array.Empty<string>(), empty));
        Assert.False(LibrarySetup.ShouldOpenAtStart(false, false, true, true, installed, Array.Empty<string>(), empty));
        // a configured folder whose fixtures the library has: no window; an empty configured folder: the window
        Assert.False(LibrarySetup.ShouldOpenAtStart(false, false, false, true, installed, configured, withFixtures));
        Assert.True(LibrarySetup.ShouldOpenAtStart(false, false, false, true, installed, new[] { Folder("empty") }, withFixtures));
        // development: the repository is the library
        Assert.False(LibrarySetup.ShouldOpenAtStart(false, false, false, true, development, Array.Empty<string>(), empty));
        // --library-setup forces it everywhere
        Assert.True(LibrarySetup.ShouldOpenAtStart(true, true, true, false, development, configured, withFixtures));

        Assert.True(LibrarySetup.HasUserLibrary(new[] { library + Path.DirectorySeparatorChar }, withFixtures));
        Assert.False(LibrarySetup.HasUserLibrary(new[] { Folder("other") }, withFixtures));
        Assert.False(LibrarySetup.HasUserLibrary(new[] { "", "\0" }, withFixtures));
    }

    [Fact]
    public void A_folder_is_counted_with_its_subfolders_and_a_missing_one_says_so()
    {
        string folder = Folder("Библиотека с пробелами");
        File.WriteAllText(Path.Combine(folder, "a.gdtf"), "");
        File.WriteAllText(Path.Combine(folder, "b.GDTF"), "");
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "");
        Directory.CreateDirectory(Path.Combine(folder, "Robe", "old"));
        File.WriteAllText(Path.Combine(folder, "Robe", "c.gdtf"), "");
        File.WriteAllText(Path.Combine(folder, "Robe", "old", "d.gdtf"), "");

        var probe = LibrarySetup.ProbeFolder(folder, CancellationToken.None);
        Assert.Equal((true, 2, 2, true), (probe.Exists, probe.Direct, probe.InSubfolders, probe.Complete));
        Assert.Equal(Path.GetFullPath(folder), probe.Folder);

        var missing = LibrarySetup.ProbeFolder(Path.Combine(folder, "nope"), CancellationToken.None);
        Assert.False(missing.Exists);
        Assert.Equal(0, missing.Direct);

        // a cancelled count (the time limit) stops descending and says it is incomplete
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cut = LibrarySetup.ProbeFolder(folder, cancelled.Token);
        Assert.Equal(2, cut.Direct);
        Assert.False(cut.Complete);

        Assert.False(LibrarySetup.ProbeFolder("\0", CancellationToken.None).Exists);
    }

    // ---- choosing a folder ------------------------------------------------------------------------------------------

    [Fact]
    public void A_chosen_folder_goes_into_the_settings_and_the_running_engine()
    {
        string library = Folder("Документы/Candela/Fixture Library");
        File.Copy(RepositoryGdtf(Zonda), Path.Combine(library, Zonda));
        File.Copy(RepositoryGdtf(Atomic), Path.Combine(library, Atomic));
        using var session = CreateSession(InstalledLayout());
        var engine = session.Engine!;
        int before = engine.Library.Count;
        var changes = new List<FixtureLibraryChange>();
        engine.FixtureLibraryChanged += changes.Add;
        using var setup = new LibrarySetup(session, _settings, _log);
        Assert.Null(setup.Folder);

        Assert.True(setup.UseFolder(library + Path.DirectorySeparatorChar), setup.Error);

        Assert.Equal(Path.GetFullPath(library), setup.Folder);
        Assert.Equal(Path.GetFullPath(library), _settings.Settings.LibraryFolders[0]);
        Assert.Equal(before + 2, engine.Library.Count);
        Assert.Equal(FixtureLibraryChangeSource.Folder, Assert.Single(changes).Source);
        Assert.NotNull(engine.Library.FindByFileName(Zonda));
        // saved at once (a crash right after the first start keeps the choice)
        Assert.Contains("Fixture Library", File.ReadAllText(_settings.FilePath), StringComparison.Ordinal);

        // the background count arrives
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (setup.Probe is null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            setup.Update();
        }

        Assert.Equal(2, setup.Probe?.Direct);

        // choosing it again keeps one entry; a missing folder is refused with the reason
        Assert.True(setup.UseFolder(library));
        Assert.Single(_settings.Settings.LibraryFolders);
        Assert.False(setup.UseFolder(Path.Combine(library, "missing")));
        Assert.Contains("missing", setup.Error, StringComparison.Ordinal);
        Assert.Single(_settings.Settings.LibraryFolders);

        // a new setup starts from the settings' folder
        using var later = new LibrarySetup(session, _settings, _log);
        Assert.Equal(Path.GetFullPath(library), later.Folder);
    }

    private static void WaitForProbe(LibrarySetup setup)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do
        {
            Thread.Sleep(20);
            setup.Update();
        }
        while ((setup.Probing || setup.Probe is null) && DateTime.UtcNow < deadline);
    }

    [Fact]
    public void Files_copied_into_the_chosen_folder_are_read_by_rescan_and_counting_waits_for_the_window()
    {
        // Review regression: after "Use Documents\Candela\Fixture Library" the folder is empty; the user copies GDTF files
        // into it in Explorer and comes back — the window kept saying "no .gdtf files" until the editor was restarted.
        string library = Folder("Документы/Candela/Fixture Library");
        _settings.Settings.LibraryFolders.Add(library);
        using var session = CreateSession(InstalledLayout());
        var engine = session.Engine!;
        using var setup = new LibrarySetup(session, _settings, _log);

        // a start that never shows the window does not walk the folder (a network share, a whole drive)
        Thread.Sleep(50);
        setup.Update();
        Assert.False(setup.Probing);
        Assert.Null(setup.Probe);
        setup.EnsureProbe();
        WaitForProbe(setup);
        Assert.Equal(0, setup.Probe?.Direct);

        File.Copy(RepositoryGdtf(Zonda), Path.Combine(library, Zonda));
        int before = engine.Library.Count;
        Assert.True(setup.Rescan(), setup.Error);
        Assert.Equal(before + 1, engine.Library.Count);
        Assert.NotNull(engine.Library.FindByFileName(Zonda));
        WaitForProbe(setup);
        Assert.Equal(1, setup.Probe?.Direct);
        Assert.Single(_settings.Settings.LibraryFolders);

        // no folder: nothing to read again
        _settings.Settings.LibraryFolders.Clear();
        using var none = new LibrarySetup(session, _settings, _log);
        Assert.False(none.Rescan());
        none.EnsureProbe();
        Assert.False(none.Probing);
    }

    [Fact]
    public void The_default_folder_is_created_when_it_is_used()
    {
        using var session = CreateSession(InstalledLayout());
        using var setup = new LibrarySetup(session, _settings, _log);
        string target = _dir.File("Мои документы/Candela/Fixture Library");
        Assert.False(Directory.Exists(target));

        Assert.True(setup.UseDefaultFolder(target), setup.Error);
        Assert.True(Directory.Exists(target));
        Assert.Equal(Path.GetFullPath(target), _settings.Settings.LibraryFolders[0]);

        // a folder that cannot be created (a file is in the way) is reported, nothing changes
        string blocked = _dir.File("blocked");
        File.WriteAllText(blocked, "");
        Assert.False(setup.UseDefaultFolder(Path.Combine(blocked, "Fixture Library")));
        Assert.NotNull(setup.Error);
        Assert.Single(_settings.Settings.LibraryFolders);
    }

    // ---- saving after "Open Sample Scene" -------------------------------------------------------------------------

    [Fact]
    public void A_save_dialog_never_starts_in_the_program_folder()
    {
        // Review regression: after Open Sample Scene, Save As and Export started in C:\Program Files\Candela\samples
        // (the scene's folder), where saving fails for the user (and an upgrade would replace the file).
        var layout = InstalledLayout();
        string samples = Folder("Program Files/Candela/samples");
        string projects = Folder("Мои проекты");
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // reading from it is fine
        Assert.Equal(samples, FileBrowserDialog.StartFolderFor(FileBrowserMode.Open, samples, Array.Empty<string>(), layout));
        // writing starts in the most recent place outside it, else in Documents
        Assert.Equal(projects, FileBrowserDialog.StartFolderFor(FileBrowserMode.Save, samples, new[] { samples, projects }, layout));
        Assert.Equal(documents, FileBrowserDialog.StartFolderFor(FileBrowserMode.Save, samples, new[] { samples }, layout));
        Assert.Equal(documents, FileBrowserDialog.StartFolderFor(FileBrowserMode.SelectFolder, layout.ProgramDirectory, Array.Empty<string>(), layout));
        // any other folder stays the start
        Assert.Equal(projects, FileBrowserDialog.StartFolderFor(FileBrowserMode.Save, projects, new[] { samples }, layout));
        Assert.Equal(projects, FileBrowserDialog.StartFolderFor(FileBrowserMode.Save, Path.Combine(projects, "missing"), new[] { projects }, layout));

        // the dialog itself uses it
        var dialog = new FileBrowserDialog(_dir.File("recent-places.json")) { Layout = layout };
        dialog.Show(FileBrowserMode.Save, "Save", Array.Empty<FileFilter>(), samples, "Фокина.candela", _ => { });
        Assert.NotEqual(Path.GetFullPath(samples), Path.GetFullPath(dialog.Model!.CurrentDirectory));
    }

    // ---- GDTF imports are kept in the library folder ---------------------------------------------------------------

    [Fact]
    public void An_imported_gdtf_is_copied_into_the_library_folder_and_a_refused_one_is_not_kept()
    {
        string library = Folder("Fixture Library");
        _settings.Settings.LibraryFolders.Add(library);
        using var session = CreateSession(InstalledLayout());
        string downloads = Folder("Загрузки");
        string source = Path.Combine(downloads, Zonda);
        File.Copy(RepositoryGdtf(Zonda), source);

        var result = session.Import(source, ImportKind.Gdtf);

        Assert.True(result.Succeeded, result.Message);
        string copy = Path.Combine(library, Zonda);
        Assert.True(File.Exists(copy));
        Assert.Equal(Path.GetFullPath(copy), result.LibraryEntry!.FilePath);
        File.Delete(source); // the library does not depend on the downloaded file any more
        Assert.True(File.Exists(copy));

        // a file that is not a GDTF is refused and leaves nothing behind in the library folder
        string broken = Path.Combine(downloads, "Broken@Fixture@1.gdtf");
        File.WriteAllText(broken, "not a zip");
        var refused = session.Import(broken, ImportKind.Gdtf);
        Assert.False(refused.Succeeded);
        Assert.False(File.Exists(Path.Combine(library, "Broken@Fixture@1.gdtf")));
        Assert.True(File.Exists(broken));
    }

    [Fact]
    public void Read_only_runs_and_development_keep_imported_files_where_they_are()
    {
        // development (the repository is the library): no copy
        Assert.Null(EditorSession.LibraryImportFolder(Array.Empty<string>(), new InstallLayout(Folder("bin"), EngineTestSupport.Root), out _));
        // installed without a folder: the default folder
        Assert.Equal(InstallLayout.DefaultUserLibraryFolder, EditorSession.LibraryImportFolder(Array.Empty<string>(), InstalledLayout(), out bool isDefault));
        Assert.True(isDefault);
        // the first configured folder that exists
        string existing = Folder("exists");
        Assert.Equal(Path.GetFullPath(existing),
            EditorSession.LibraryImportFolder(new[] { _dir.File("missing"), existing }, InstalledLayout(), out isDefault));
        Assert.False(isDefault);
        // configured but none exists: no copy (and no default folder behind the user's back)
        Assert.Null(EditorSession.LibraryImportFolder(new[] { _dir.File("missing") }, InstalledLayout(), out _));

        // a headless (read-only) run imports from where the file is
        string library = Folder("ro-library");
        _settings.Settings.LibraryFolders.Add(library);
        _settings.ReadOnly = true;
        using var session = CreateSession(InstalledLayout());
        string source = Path.Combine(Folder("ro-source"), Atomic);
        File.Copy(RepositoryGdtf(Atomic), source);
        var result = session.Import(source, ImportKind.Gdtf);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Path.GetFullPath(source), result.LibraryEntry!.FilePath);
        Assert.Empty(Directory.GetFiles(library));
    }

    [Fact]
    public void Copying_never_replaces_a_different_file_of_the_same_name()
    {
        string library = Folder("lib");
        string source = Path.Combine(Folder("src"), Zonda);
        File.Copy(RepositoryGdtf(Zonda), source);

        Assert.Equal(Path.Combine(Path.GetFullPath(library), Zonda), EditorSession.CopyGdtfInto(source, library, out bool created));
        Assert.True(created);
        // the identical file is already there: used as it is
        Assert.Equal(Path.Combine(Path.GetFullPath(library), Zonda), EditorSession.CopyGdtfInto(source, library, out created));
        Assert.False(created);
        // a file inside the library folder itself: nothing to copy
        Assert.Null(EditorSession.CopyGdtfInto(Path.Combine(library, Zonda), library, out created));
        // another file of the same name: the library's file stays, the original is used for this session
        string other = Path.Combine(Folder("other"), Zonda);
        File.WriteAllText(other, "different content");
        Assert.Null(EditorSession.CopyGdtfInto(other, library, out created));
        Assert.Equal(new FileInfo(RepositoryGdtf(Zonda)).Length, new FileInfo(Path.Combine(library, Zonda)).Length);
        Assert.Empty(Directory.GetFiles(library, "*.tmp"));
    }

    // ---- the window ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_setup_window_draws_its_states_in_both_languages()
    {
        string library = Folder("Fixture Library");
        File.Copy(RepositoryGdtf(Zonda), Path.Combine(library, Zonda));
        Directory.CreateDirectory(Path.Combine(library, "Ayrton"));
        File.Copy(RepositoryGdtf(Atomic), Path.Combine(library, "Ayrton", Atomic));
        using var session = CreateSession(InstalledLayout());
        using var setup = new LibrarySetup(session, _settings, _log);
        var browser = new FileBrowserDialog(_dir.File("recent-places.json"));
        var dispatcher = new KeyBindingDispatcher(new CommandRegistry(), KeyBindingsService.CreateDefault());
        bool show = true;
        var opened = new List<string>();
        var status = new List<string>();
        var window = new LibrarySetupWindow(setup, default, browser, dispatcher, () => show, v => show = v, opened.Add, status.Add);
        using var ui = new HeadlessImGui(new Vector2(1600, 900));

        window.Open();
        foreach (var language in new[] { "en", "ru" })
        {
            Loc.SetLanguage(language);
            for (int i = 0; i < 3; i++)
                ui.Frame(() => window.Draw(1f));
        }

        window.ChooseFolder(library);
        Assert.Contains(status, s => s.Contains(library, StringComparison.Ordinal));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (setup.Probe is null && DateTime.UtcNow < deadline)
            ui.Frame(() => window.Draw(1.5f));
        Assert.Equal((1, 1), (setup.Probe?.Direct, setup.Probe?.InSubfolders));
        foreach (var language in new[] { "ru", "en" })
        {
            Loc.SetLanguage(language);
            ui.Frame(() => window.Draw(2f));
        }

        Assert.True(window.IsOpen);
        // the sample scene of this layout does not exist: nothing is opened
        window.OpenSample();
        Assert.Empty(opened);
        Assert.True(window.IsOpen);
        window.IsOpen = false;
        ui.Frame(() => window.Draw(1f));
    }

    [Fact]
    public void The_setup_window_opens_the_sample_scene_and_closes()
    {
        var layout = new InstallLayout(Folder("bin"), EngineTestSupport.Root); // the repository's stages/Фокина.mvr
        using var session = CreateSession(layout);
        using var setup = new LibrarySetup(session, _settings, _log);
        var opened = new List<string>();
        var window = new LibrarySetupWindow(setup, default, new FileBrowserDialog(_dir.File("recent.json")),
            new KeyBindingDispatcher(new CommandRegistry(), KeyBindingsService.CreateDefault()), () => true, _ => { }, opened.Add, _ => { });
        window.Open();
        window.OpenSample();
        Assert.Equal(EngineTestSupport.Fokina, Assert.Single(opened));
        Assert.False(window.IsOpen);
    }

    // ---- diagnostics and Windows messages -------------------------------------------------------------------------

    [Fact]
    public void Logs_go_to_local_app_data_on_windows_and_stay_with_an_explicit_config_folder()
    {
        Assert.Equal(Path.Combine("C:/Users/Ира/AppData/Local", "Candela", "logs"),
            AppFolders.LogsDirectoryFor(null, windows: true, "C:/Users/Ира/AppData/Local", "C:/Users/Ира/AppData/Roaming/Candela"));
        Assert.Equal(Path.Combine("/home/u/.config/Candela", "logs"),
            AppFolders.LogsDirectoryFor(null, windows: false, "/home/u/.local/share", "/home/u/.config/Candela"));
        Assert.Equal(Path.Combine("/tmp/run", "logs"), AppFolders.LogsDirectoryFor("/tmp/run", windows: true, "C:/x", "C:/y"));
        Assert.Equal(Path.Combine("C:/y", "logs"), AppFolders.LogsDirectoryFor("", windows: true, "", "C:/y"));
        Assert.EndsWith(AppFolders.LogFileName, AppFolders.LogFile, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crash_report_holds_the_error_and_the_last_log_lines_and_old_reports_are_pruned()
    {
        using var log = TestLog.Create();
        for (int i = 0; i < CrashReport.LogLines + 20; i++)
            log.Info("Test", $"line {i}");
        var error = new InvalidOperationException("outer", new IOException("inner cause"));
        string folder = _dir.File("Журналы с пробелами");

        string? path = CrashReport.Write(folder, "The editor stopped.", error, log, new DateTime(2026, 10, 19, 23, 0, 0));

        Assert.NotNull(path);
        Assert.Equal("crash-20261019-230000.txt", Path.GetFileName(path));
        byte[] bytes = File.ReadAllBytes(path!);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // UTF-8 with BOM: Notepad shows Cyrillic paths
        string text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("The editor stopped.", text, StringComparison.Ordinal);
        Assert.Contains(CrashReport.FullVersion, text, StringComparison.Ordinal);
        Assert.Contains("inner cause", text, StringComparison.Ordinal);
        Assert.Contains($"line {CrashReport.LogLines + 19}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0\n", text.Replace("\r\n", "\n"), StringComparison.Ordinal);

        // the same second again: a second file, not an overwrite
        string? second = CrashReport.Write(folder, "again", null, null, new DateTime(2026, 10, 19, 23, 0, 0));
        Assert.Equal("crash-20261019-230000-2.txt", Path.GetFileName(second));

        // at most MaxReports are kept
        for (int i = 0; i < CrashReport.MaxReports + 5; i++)
        {
            string? p = CrashReport.Write(folder, "many", null, null, new DateTime(2026, 1, 1).AddMinutes(i));
            File.SetLastWriteTimeUtc(p!, new DateTime(2026, 1, 1).AddMinutes(i));
        }

        Assert.Equal(CrashReport.MaxReports, Directory.GetFiles(folder, CrashReport.FilePrefix + "*.txt").Length);

        // a folder that cannot be created: no report, no exception
        string blocked = _dir.File("blocked-file");
        File.WriteAllText(blocked, "");
        Assert.Null(CrashReport.Write(Path.Combine(blocked, "logs"), "x", null, log));

        // telling the user without a message box (tests, Linux) only prints
        CrashReport.Notify(started: true, path, interactive: false, log);
        CrashReport.Notify(started: false, null, interactive: true, log);
    }

    [Fact]
    public void The_no_vulkan_message_names_the_cause_the_card_and_its_driver_page()
    {
        Assert.Equal(GpuVendor.Nvidia, GraphicsDriverHelp.VendorOf("NVIDIA GeForce RTX 3060"));
        Assert.Equal(GpuVendor.Amd, GraphicsDriverHelp.VendorOf("AMD Radeon RX 6600"));
        Assert.Equal(GpuVendor.Amd, GraphicsDriverHelp.VendorOf("Advanced Micro Devices, Inc."));
        Assert.Equal(GpuVendor.Intel, GraphicsDriverHelp.VendorOf("Intel(R) UHD Graphics 620"));
        Assert.Equal(GpuVendor.Unknown, GraphicsDriverHelp.VendorOf("Microsoft Basic Display Adapter"));
        Assert.Equal(GpuVendor.Unknown, GraphicsDriverHelp.VendorOf(null));
        Assert.Equal(GraphicsDriverHelp.NvidiaDrivers, GraphicsDriverHelp.DriverPage(GpuVendor.Nvidia));
        Assert.Null(GraphicsDriverHelp.DriverPage(GpuVendor.Unknown));

        var adapters = new[] { new DisplayAdapter("Microsoft Basic Display Adapter", GpuVendor.Unknown), new DisplayAdapter("Intel(R) Iris(R) Xe", GpuVendor.Intel) };
        Assert.Equal("Intel(R) Iris(R) Xe", GraphicsDriverHelp.MainAdapter(adapters)?.Name);
        Assert.Equal("Microsoft Basic Display Adapter", GraphicsDriverHelp.MainAdapter(adapters[..1])?.Name);
        Assert.Null(GraphicsDriverHelp.MainAdapter(Array.Empty<DisplayAdapter>()));
        if (!OperatingSystem.IsWindows())
            Assert.Empty(GraphicsDriverHelp.DisplayAdapters());

        string missing = GraphicsDriverHelp.Message("no loader", loaderPresent: false, adapters[1], "C:/logs/candela-app.log", askToOpen: true);
        Assert.Contains("vulkan-1.dll", missing, StringComparison.Ordinal);
        Assert.Contains("Intel(R) Iris(R) Xe (Intel)", missing, StringComparison.Ordinal);
        Assert.Contains(GraphicsDriverHelp.IntelDrivers, missing, StringComparison.Ordinal);
        Assert.Contains("C:/logs/candela-app.log", missing, StringComparison.Ordinal);
        Assert.EndsWith(InstallLoc.Get(InstallStr.NoVulkanOpenPage), missing, StringComparison.Ordinal);

        Loc.SetLanguage("ru");
        string old = GraphicsDriverHelp.Message("Vulkan 1.1 only", loaderPresent: true, null, "log", askToOpen: false);
        Assert.Contains("Vulkan 1.1 only", old, StringComparison.Ordinal);
        Assert.Contains("видеокарт", old, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallLoc.Get(InstallStr.NoVulkanOpenPage), old, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_start_speaks_the_language_of_the_operating_system()
    {
        Assert.Equal("ru", SystemLanguage.FromWindowsLanguageId(0x0419)); // Russian (Russia)
        Assert.Equal("ru", SystemLanguage.FromWindowsLanguageId(0x0819)); // Russian (Moldova)
        Assert.Equal("en", SystemLanguage.FromWindowsLanguageId(0x0409));
        Assert.Equal("en", SystemLanguage.FromWindowsLanguageId(0x0422)); // Ukrainian
        Assert.Equal("ru", SystemLanguage.FromLocale(null, null, "ru_RU.UTF-8"));
        Assert.Equal("ru", SystemLanguage.FromLocale("", "ru", "en_US.UTF-8"));
        Assert.Equal("en", SystemLanguage.FromLocale("C.UTF-8", null, "ru_RU.UTF-8")); // LC_ALL wins
        Assert.Equal("en", SystemLanguage.FromLocale(null, null, null));
        Assert.Equal("en", SystemLanguage.FromLocale(null, null, "rus"));
        Assert.Contains(SystemLanguage.Detect(), new[] { "en", "ru" });
    }

    [Fact]
    public void The_window_icon_has_every_size_and_reduces_without_dark_fringes()
    {
        var images = WindowIcon.Images;
        Assert.Equal(WindowIcon.Sizes, images.Select(i => i.Size).ToArray());
        foreach (var (size, rgba) in images)
        {
            Assert.Equal(size * size * 4, rgba.Length);
            Assert.Contains(rgba.Where((_, i) => i % 4 == 3), a => a == 255); // opaque pixels
            Assert.Contains(rgba.Where((_, i) => i % 4 == 3), a => a == 0); // transparent rounded corners
        }

        // half-transparent orange averaged with transparent black stays orange (alpha-weighted), alpha halves
        byte[] source = new byte[2 * 2 * 4];
        source[0] = 255;
        source[1] = 122;
        source[3] = 255;
        byte[] reduced = WindowIcon.Downscale(source, 2, 1);
        Assert.Equal(new byte[] { 255, 122, 0, 64 }, reduced);
        Assert.Throws<ArgumentException>(() => WindowIcon.Downscale(new byte[3], 2, 1));
    }

    [Fact]
    public void Every_install_text_has_both_languages_with_the_same_placeholders()
    {
        var keys = Enum.GetValues<InstallStr>();
        var entries = InstallLoc.Entries.ToList();
        Assert.Equal(keys.Length, entries.Count);
        Assert.Equal(keys.Length, entries.Select(e => e.Key).Distinct().Count());
        foreach (var (key, en, ru) in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key}: English missing");
            Assert.False(string.IsNullOrWhiteSpace(ru), $"{key}: Russian missing");
            Assert.Equal(Placeholders(en), Placeholders(ru));
        }

        Loc.SetLanguage("ru");
        Assert.Equal("Библиотека приборов", InstallLoc.Get(InstallStr.SetupTitle));
        Assert.Equal(Icons.Library + "  Библиотека приборов", InstallLoc.With(Icons.Library, InstallStr.SetupTitle));
        Assert.Same(InstallLoc.With(Icons.Library, InstallStr.SetupTitle), InstallLoc.With(Icons.Library, InstallStr.SetupTitle));
        Assert.Equal("Fixture Library", InstallLoc.English_(InstallStr.SetupTitle));
        Loc.SetLanguage("en");
        Assert.Equal("Fixture Library", InstallLoc.Get(InstallStr.SetupTitle));
    }

    [Fact]
    public void A_first_window_larger_than_the_screen_is_maximised_and_one_that_sticks_out_is_centred()
    {
        // Review regression: 1600 × 900 screen coordinates become 2400 × 1350 pixels at 150 % (a typical Full HD laptop):
        // the editor opened larger than the screen, its status bar and bottom panels off the screen.
        var frame = new WindowFrame(8, 31, 8, 8);
        var laptop = new WorkArea(0, 0, 1920, 1032); // 1080 minus the task bar
        var big = WindowPlacement.Fit(26, 57, 2400, 1350, frame, laptop);
        Assert.True(big.Maximize);

        // fits but was placed partly off the screen: centred, client area below the title bar
        var off = WindowPlacement.Fit(900, 600, 1600, 900, frame, laptop);
        Assert.False(off.Maximize);
        Assert.True(off.Move);
        Assert.Equal((1920 - 1616) / 2 + 8, off.X);
        Assert.Equal((1032 - 939) / 2 + 31, off.Y);

        // fits where it is: unchanged; a monitor left of the primary one (negative coordinates) works the same
        var fine = WindowPlacement.Fit(100, 100, 1600, 900, frame, new WorkArea(0, 0, 2560, 1400));
        Assert.False(fine.Maximize || fine.Move);
        var left = WindowPlacement.Fit(-1800, 100, 1600, 900, frame, new WorkArea(-1920, 0, 1920, 1040));
        Assert.False(left.Maximize || left.Move);

        // an unknown work area (no monitor information) changes nothing
        Assert.Equal(new WindowPlacement(false, false, 5, 6), WindowPlacement.Fit(5, 6, 1600, 900, frame, new WorkArea(0, 0, 0, 0)));
    }

    [Fact]
    public void A_missing_visual_cpp_runtime_is_named_with_its_download_instead_of_a_generic_report()
    {
        // Review regression: without the runtime (Setup's runtime install declined or failed, a copied folder) GLFW and
        // cimgui cannot load and the user only read "Candela could not start" with a crash report.
        var none = VcRuntime.Missing(name => false);
        Assert.Equal(VcRuntime.Libraries, none);
        Assert.Empty(VcRuntime.Missing(name => true));
        Assert.Equal(new[] { "vcruntime140_1.dll" }, VcRuntime.Missing(name => name == "vcruntime140.dll"));
        Assert.Equal(VcRuntime.Libraries, VcRuntime.Missing(name => throw new BadImageFormatException()));
        // this test host has what it needs (nothing is missing outside Windows)
        if (!OperatingSystem.IsWindows())
            Assert.Empty(VcRuntime.Missing());

        string en = VcRuntime.Message(none, @"C:\Users\Иван\AppData\Local\Candela\logs\candela-app.log");
        Assert.Contains("vcruntime140.dll, vcruntime140_1.dll", en, StringComparison.Ordinal);
        Assert.Contains(VcRuntime.DownloadUrl, en, StringComparison.Ordinal);
        Assert.Contains("candela-app.log", en, StringComparison.Ordinal);
        Loc.SetLanguage("ru");
        string ru = VcRuntime.Message(none, "log.txt");
        Assert.Contains("Visual C++", ru, StringComparison.Ordinal);
        Assert.Contains("Установите", ru, StringComparison.Ordinal);
        Assert.Contains(VcRuntime.DownloadUrl, ru, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_file_version_sorts_after_its_previews()
    {
        // Review regression: the release was 0.1.0.0 and its previews 0.1.0.N, so the installer of the release took the
        // installed preview for a newer version ("A newer version of Candela is installed. Replace it?").
        Version FileVersion(string preview)
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "msbuild", Path.Combine(EngineTestSupport.Root, "src", "Candela.Core", "Candela.Core.csproj"),
                         "-nologo", "-getProperty:FileVersion", "-p:CandelaVersion=0.4.2", "-p:CandelaPreview=" + preview })
                start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "msbuild did not finish");
            Assert.True(process.ExitCode == 0, output);
            return Version.Parse(output.Trim().Split('\n')[^1].Trim());
        }

        var first = FileVersion("1");
        var later = FileVersion("12");
        var release = FileVersion("");
        Assert.Equal(new Version(0, 4, 2, 1), first);
        Assert.True(later > first);
        Assert.True(release > later, $"{release} must sort after {later}");
        Assert.Equal(new Version(0, 4, 2), new Version(release.Major, release.Minor, release.Build));
    }

    [Fact]
    public void The_library_setup_option_is_parsed_and_documented()
    {
        Assert.True(AppOptions.Parse(new[] { "--library-setup" }).LibrarySetup);
        Assert.False(AppOptions.Parse(Array.Empty<string>()).LibrarySetup);
        Assert.Contains("--library-setup", AppOptions.Usage, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%", AppOptions.Usage, StringComparison.Ordinal);
    }

    private static string[] Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
