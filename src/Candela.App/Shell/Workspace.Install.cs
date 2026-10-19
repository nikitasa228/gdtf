using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.App.Windows;
using Candela.Core.Settings;

namespace Candela.App.Shell;

/// <summary>
/// The installed editor's first experience: the fixture library setup window, opened at start-up while the user has no
/// library (after the recovery prompt, never in headless runs, during development or when a file was opened from the
/// command line) and from Help → Fixture Library Setup.
/// </summary>
internal sealed partial class Workspace
{
    private LibrarySetup? _librarySetup;
    private LibrarySetupWindow? _librarySetupWindow;
    private Action? _drawLibrarySetup;
    private bool _libraryStartChecked;

    /// <summary>The library setup window (tests and scripts).</summary>
    internal LibrarySetupWindow LibrarySetupWindow => _librarySetupWindow ?? throw new InvalidOperationException("The workspace is not initialised.");

    /// <summary>Creates the library setup window (after the editing preferences were loaded).</summary>
    private void InitializeInstall()
    {
        _librarySetup = new LibrarySetup(_ctx.Session, _ctx.Settings, _ctx.Log);
        _librarySetupWindow = new LibrarySetupWindow(_librarySetup, _browser, _dispatcher,
            () => _preferences.ShowLibrarySetup,
            show =>
            {
                _preferences.ShowLibrarySetup = show;
                if (!_ctx.Settings.ReadOnly)
                    _preferences.Save(ConfigPaths.ConfigDir, _ctx.Log);
            },
            OpenFile, SetStatus);
        _drawLibrarySetup = () => _librarySetupWindow.Draw(_frameScale);
    }

    /// <summary>Draws the library setup window (guarded: a failure closes it).</summary>
    private void DrawLibrarySetup()
    {
        if (_drawLibrarySetup is not null && _librarySetupWindow is not null && !_guard.Run("library setup", _drawLibrarySetup))
            _librarySetupWindow.IsOpen = false;
    }

    /// <summary>
    /// Once, after the start-up recovery prompt: opens the library setup window when <see cref="LibrarySetup.ShouldOpenAtStart"/>
    /// says so (<c>--library-setup</c> forces it, also in headless runs for screenshots).
    /// </summary>
    private void OfferLibrarySetup()
    {
        if (_libraryStartChecked || _librarySetupWindow is null || _ctx.Session.Engine is not { } engine)
            return;
        _libraryStartChecked = true;
        if (LibrarySetup.ShouldOpenAtStart(_options.LibrarySetup, _options.IsScreenshotMode, _options.OpenPath is not null,
                _preferences.ShowLibrarySetup, _ctx.Session.Layout, _ctx.Settings.Settings.LibraryFolders, engine.Library))
            _librarySetupWindow.Open();
    }

    /// <summary>Help → Fixture Library Setup.</summary>
    private void DrawLibrarySetupMenuItem()
    {
        if (_menu.Item(InstallLoc.With(Icons.Library, InstallStr.SetupMenu), "", false, true, nameof(InstallStr.SetupMenu),
                InstallLoc.English_(InstallStr.SetupMenu)))
            _librarySetupWindow?.Open();
    }

    private void DisposeInstall()
    {
        _librarySetup?.Dispose();
        _librarySetup = null;
    }
}
