using Candela.Core.Diagnostics;
using Candela.Gdtf;

namespace Candela.Engine;

public sealed partial class StageEngine
{
    /// <summary>The warning of a run whose settings name no library folder at all.</summary>
    internal const string NoLibraryFolderMessage =
        "No fixture library folder is set; only GDTF files inside projects and MVR files are available.";

    private IReadOnlyList<string> _missingLibraryFolders = Array.Empty<string>();

    /// <summary>
    /// Adds the <c>.gdtf</c> files directly in <paramref name="folder"/> to the fixture library while the program runs
    /// (the editor's first-run library setup and its Rescan button, a folder added in Settings → Library): no XML is
    /// read, files whose name the library already has are skipped (the earlier folder wins, as at start-up) — so adding
    /// a folder again reads only the files that arrived since —, every fixture is re-resolved — a stand-in or
    /// unresolved fixture picks up a real GDTF that arrived — and <see cref="FixtureLibraryChanged"/> is raised with the
    /// new entries (<see cref="FixtureLibraryChangeSource.Folder"/>). The start-up warnings about the library are renewed:
    /// "no library folder" disappears once the folder brought fixtures, "folder missing" keeps naming only the configured
    /// folders that are still missing (a drive that was plugged in and added again). A missing or unreadable folder is a
    /// failed result with the reason, never an exception.
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
            RenewLibraryWarnings(broughtFixtures: false);
            int files = InstallLayout.CountGdtfFiles(full);
            return EngineResult.Ok(files == 0
                ? $"'{full}' holds no GDTF files yet; copy .gdtf files into it, then add it again (or restart) to read them."
                : $"The {files} GDTF file(s) of '{full}' are already in the fixture library.");
        }

        ReloadFixtureTypes();
        RenewLibraryWarnings(broughtFixtures: true);
        RefreshIssues();
        var entries = Library.Entries.Where(e => !before.Contains(e)).ToList();
        if (entries.Count > 0)
            FixtureLibraryChanged?.Invoke(new FixtureLibraryChange(entries, FixtureLibraryChangeSource.Folder));
        return EngineResult.Ok($"Added {added} fixture type file(s) from '{full}' to the fixture library.");
    }

    /// <summary>The start-up warning that names the configured library folders that do not exist.</summary>
    private static Issue MissingLibraryFoldersIssue(IReadOnlyList<string> missing) =>
        new(IssueSeverity.Warning, IssueCategory.Program, EngineIssueCodes.LibraryMissing,
            $"The fixture library folder(s) {string.Join(", ", missing.Select(f => $"'{f}'"))} do not exist or cannot be reached " +
            "(an unplugged drive or an offline network share?); their fixtures are not in the library.");

    /// <summary>
    /// Renews the start-up warnings about the library after a folder was added: the "no library folder" warning goes
    /// once a folder brought fixtures (<paramref name="broughtFixtures"/>), the "folder missing" warning names only the
    /// configured folders that are still missing, and every other warning (unreadable researched data) stays. The program
    /// messages are listed again when something changed, like after opening a file (messages of earlier operations go
    /// with them).
    /// </summary>
    private void RenewLibraryWarnings(bool broughtFixtures)
    {
        static bool IsNotSet(Issue i) => i.Code == EngineIssueCodes.LibraryMissing && i.Message == NoLibraryFolderMessage;
        bool dropNotSet = broughtFixtures && _libraryIssues.Any(IsNotSet);
        var stillMissing = _missingLibraryFolders.Where(f => !Directory.Exists(f)).ToList();
        bool missingChanged = stillMissing.Count != _missingLibraryFolders.Count;
        if (!dropNotSet && !missingChanged)
            return;
        var renewed = _libraryIssues.Where(i => !(dropNotSet && IsNotSet(i))).ToList();
        if (missingChanged)
        {
            var old = MissingLibraryFoldersIssue(_missingLibraryFolders);
            renewed.RemoveAll(i => i.Code == old.Code && i.Message == old.Message);
            if (stillMissing.Count > 0)
                renewed.Add(MissingLibraryFoldersIssue(stillMissing));
            _missingLibraryFolders = stillMissing;
        }

        _libraryIssues = renewed;
        ResetProgramMessages();
    }
}
