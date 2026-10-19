using Candela.Core.Diagnostics;
using Candela.Gdtf;

namespace Candela.Engine;

public sealed partial class StageEngine
{
    /// <summary>
    /// Adds the <c>.gdtf</c> files directly in <paramref name="folder"/> to the fixture library while the program runs
    /// (the editor's first-run library setup, a folder added in Settings → Library): no XML is read, files whose name
    /// the library already has are skipped (the earlier folder wins, as at start-up), every fixture is re-resolved — a
    /// stand-in or unresolved fixture picks up a real GDTF that arrived — and <see cref="FixtureLibraryChanged"/> is raised
    /// with the new entries (<see cref="FixtureLibraryChangeSource.Folder"/>). The "no library folder" warning of the
    /// start-up disappears once the folder brought fixtures. A missing or unreadable folder is a failed result with the
    /// reason, never an exception.
    /// </summary>
    public EngineResult AddLibraryFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Fail(EngineIssueCodes.LibraryMissing, "No library folder was given.", IssueCategory.Program);

        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return Fail(EngineIssueCodes.LibraryMissing, $"'{folder}' is not a valid folder: {e.Message}", IssueCategory.Program);
        }

        if (!Directory.Exists(full))
            return Fail(EngineIssueCodes.LibraryMissing, $"The fixture library folder '{full}' does not exist.", IssueCategory.Program);

        var before = new HashSet<FixtureLibraryEntry>(Library.Entries, ReferenceEqualityComparer.Instance);
        int added = Library.AddFolder(full);
        if (added == 0)
        {
            int files = InstallLayout.CountGdtfFiles(full);
            return EngineResult.Ok(files == 0
                ? $"'{full}' holds no GDTF files yet; copy .gdtf files into it and they are read at the next start."
                : $"The {files} GDTF file(s) of '{full}' are already in the fixture library.");
        }

        ReloadFixtureTypes();
        DropLibraryMissingIssues();
        RefreshIssues();
        var entries = Library.Entries.Where(e => !before.Contains(e)).ToList();
        if (entries.Count > 0)
            FixtureLibraryChanged?.Invoke(new FixtureLibraryChange(entries, FixtureLibraryChangeSource.Folder));
        return EngineResult.Ok($"Added {added} fixture type file(s) from '{full}' to the fixture library.");
    }

    /// <summary>
    /// Forgets the start-up's "no library" warnings (a folder with fixtures was added since). The program messages are
    /// listed again without them, like after opening a file (messages of earlier operations go with them).
    /// </summary>
    private void DropLibraryMissingIssues()
    {
        static bool IsMissingLibrary(Issue i) => i.Code == EngineIssueCodes.LibraryMissing && i.Severity == IssueSeverity.Warning;
        if (!_libraryIssues.Any(IsMissingLibrary))
            return;
        _libraryIssues = _libraryIssues.Where(i => !IsMissingLibrary(i)).ToList();
        ResetProgramMessages();
    }
}
