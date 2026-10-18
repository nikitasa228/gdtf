using System.Text.Json;

namespace Candela.App.Files;

/// <summary>What the file browser is used for.</summary>
internal enum FileBrowserMode
{
    /// <summary>Pick an existing file.</summary>
    Open,

    /// <summary>Type or pick a file name to write (overwrite is confirmed).</summary>
    Save,

    /// <summary>Pick a folder.</summary>
    SelectFolder,
}

/// <summary>One row of the file list.</summary>
/// <param name="Name">File or folder name.</param>
/// <param name="FullPath">Absolute path.</param>
/// <param name="IsDirectory">Folder.</param>
/// <param name="Size">Bytes (0 for folders).</param>
/// <param name="Modified">Last write time (local).</param>
internal readonly record struct FileEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified);

/// <summary>Column the file list is sorted by.</summary>
internal enum FileSortColumn
{
    /// <summary>Name (natural, case-insensitive).</summary>
    Name,

    /// <summary>Size.</summary>
    Size,

    /// <summary>Last modification.</summary>
    Modified,
}

/// <summary>
/// State and rules of the portable file browser (GLFW has no native dialogs): current folder and its entries (folders
/// first, then files accepted by the selected <see cref="FileFilter"/>; hidden entries only on request), navigation,
/// sorting, places (home, documents, desktop, drives, working folder), recently used folders (persisted), and resolving
/// what the user typed into the final path (relative names, default extension). No ImGui: unit-tested; the dialog draws it.
/// Unreadable folders never throw: <see cref="Error"/> explains and the previous listing stays.
/// </summary>
internal sealed class FileBrowserModel
{
    /// <summary>Recently used folders kept.</summary>
    public const int MaxRecentPlaces = 10;

    private readonly List<FileEntry> _entries = new();
    private readonly List<string> _recent = new();
    private readonly string? _recentFile;

    /// <summary>Creates a model in <paramref name="mode"/> with filters (the first is selected).</summary>
    /// <param name="mode">Open, save or folder selection.</param>
    /// <param name="filters">Filters for the type dropdown (empty = all files).</param>
    /// <param name="recentPlacesFile">JSON file of recently used folders (null = not persisted).</param>
    public FileBrowserModel(FileBrowserMode mode, IReadOnlyList<FileFilter> filters, string? recentPlacesFile = null)
    {
        Mode = mode;
        Filters = filters is { Count: > 0 } ? filters : new[] { new FileFilter("All files", "*") };
        _recentFile = recentPlacesFile;
        LoadRecent();
    }

    /// <summary>The purpose.</summary>
    public FileBrowserMode Mode { get; }

    /// <summary>Available filters.</summary>
    public IReadOnlyList<FileFilter> Filters { get; }

    /// <summary>Index of the selected filter.</summary>
    public int FilterIndex { get; private set; }

    /// <summary>The selected filter.</summary>
    public FileFilter Filter => Filters[Math.Clamp(FilterIndex, 0, Filters.Count - 1)];

    /// <summary>The folder shown.</summary>
    public string CurrentDirectory { get; private set; } = "";

    /// <summary>Entries of <see cref="CurrentDirectory"/> after filtering and sorting.</summary>
    public IReadOnlyList<FileEntry> Entries => _entries;

    /// <summary>Show entries starting with '.' or marked hidden.</summary>
    public bool ShowHidden { get; private set; }

    /// <summary>Sort column.</summary>
    public FileSortColumn SortColumn { get; private set; }

    /// <summary>Sort direction.</summary>
    public bool SortDescending { get; private set; }

    /// <summary>Text of the file name field (save mode, or the selected file in open mode).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Why the last navigation or confirmation failed (empty when fine).</summary>
    public string Error { get; private set; } = "";

    /// <summary>Recently used folders, newest first.</summary>
    public IReadOnlyList<string> RecentPlaces => _recent;

    /// <summary>Changes whenever <see cref="Entries"/> changes (new listing or new order), so views can cache row texts.</summary>
    public int Version { get; private set; }

    /// <summary>Shows a folder; returns false (and sets <see cref="Error"/>) when it cannot be read.</summary>
    public bool Navigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string full;
        try
        {
            full = Path.GetFullPath(ExpandHome(Environment.ExpandEnvironmentVariables(path.Trim())));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            Error = e.Message;
            return false;
        }

        if (!Directory.Exists(full))
        {
            Error = $"'{full}' is not a folder.";
            return false;
        }

        var listing = new List<FileEntry>();
        try
        {
            var dir = new DirectoryInfo(full);
            // Not IgnoreInaccessible: for the folder itself it would turn "access denied" into an empty listing without
            // an explanation; the entries are not opened (no recursion), so they cannot fail this way.
            foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false }))
            {
                bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                if (!ShowHidden && IsHidden(info))
                    continue;
                if (!isDir && (Mode == FileBrowserMode.SelectFolder || !Filter.Matches(info.Name)))
                    continue;
                long size = !isDir && info is FileInfo fi ? SafeLength(fi) : 0;
                DateTime modified;
                try
                {
                    modified = info.LastWriteTime;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    modified = DateTime.MinValue;
                }

                listing.Add(new FileEntry(info.Name, info.FullName, isDir, size, modified));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Error = e.Message;
            return false;
        }

        CurrentDirectory = full;
        _entries.Clear();
        _entries.AddRange(listing);
        Sort();
        Error = "";
        return true;
    }

    /// <summary>"~" and "~/…" (typed into the path bar) stand for the user's home folder, as in a shell.</summary>
    internal static string ExpandHome(string path)
    {
        if (path.Length == 0 || path[0] != '~' || (path.Length > 1 && path[1] != '/' && path[1] != Path.DirectorySeparatorChar))
            return path;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? path : path.Length == 1 ? home : Path.Combine(home, path[2..]);
    }

    /// <summary>Re-reads the current folder.</summary>
    public bool Refresh() => CurrentDirectory.Length > 0 && Navigate(CurrentDirectory);

    /// <summary>Goes to the parent folder.</summary>
    public bool Up()
    {
        var parent = Directory.GetParent(CurrentDirectory);
        return parent is not null && Navigate(parent.FullName);
    }

    /// <summary>Selects a filter and refreshes; in save mode the typed name gets the new default extension.</summary>
    public void SelectFilter(int index)
    {
        if (index < 0 || index >= Filters.Count || index == FilterIndex)
            return;
        FilterIndex = index;
        if (Mode == FileBrowserMode.Save && FileName.Length > 0 && Filter.DefaultExtension is { } ext)
            FileName = Path.ChangeExtension(FileName, ext);
        Refresh();
    }

    /// <summary>Shows or hides hidden entries.</summary>
    public void SetShowHidden(bool show)
    {
        if (ShowHidden == show)
            return;
        ShowHidden = show;
        Refresh();
    }

    /// <summary>Sorts by a column (the same column again flips the direction).</summary>
    public void SortBy(FileSortColumn column, bool? descending = null)
    {
        SortDescending = descending ?? (column == SortColumn && !SortDescending);
        SortColumn = column;
        Sort();
    }

    /// <summary>
    /// The path the dialog would return for the current input: open = the typed or selected existing file; save = the
    /// typed name (relative to the current folder, default extension added); folder = the typed or current folder.
    /// Null (with <see cref="Error"/>) when the input is not acceptable.
    /// </summary>
    public string? Resolve()
    {
        string name = FileName.Trim();
        try
        {
            switch (Mode)
            {
                case FileBrowserMode.SelectFolder:
                    {
                        string folder = name.Length == 0 ? CurrentDirectory : Path.GetFullPath(Path.Combine(CurrentDirectory, name));
                        if (Directory.Exists(folder))
                            return folder;
                        Error = $"'{folder}' is not a folder.";
                        return null;
                    }

                case FileBrowserMode.Open:
                    {
                        if (name.Length == 0)
                        {
                            Error = "Select a file.";
                            return null;
                        }

                        string path = Path.GetFullPath(Path.Combine(CurrentDirectory, name));
                        if (IsReadableFile(path))
                            return path;
                        Error = $"'{path}' does not exist.";
                        return null;
                    }

                default:
                    {
                        if (name.Length == 0 || name.EndsWith(Path.DirectorySeparatorChar) || name.EndsWith(Path.AltDirectorySeparatorChar))
                        {
                            Error = "Type a file name.";
                            return null;
                        }

                        if (name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                        {
                            Error = "The name contains characters that are not allowed.";
                            return null;
                        }

                        string path = Path.GetFullPath(Path.Combine(CurrentDirectory, name));
                        if (Filter.DefaultExtension is { } ext && !Filter.Matches(Path.GetFileName(path)))
                            path += ext;
                        if (Directory.Exists(path))
                        {
                            Error = $"'{path}' is a folder.";
                            return null;
                        }

                        string? dir = Path.GetDirectoryName(path);
                        if (dir is null || !Directory.Exists(dir))
                        {
                            Error = $"The folder '{dir}' does not exist.";
                            return null;
                        }

                        return path;
                    }
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            Error = e.Message;
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a file that can be opened: it exists and, when it is a symbolic link, the link
    /// leads to an existing file (.NET reports a broken or looping link as an existing file).
    /// </summary>
    public static bool IsReadableFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;
            if (info.LinkTarget is null)
                return true;
            return info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // a link loop, a vanished file, an unreadable folder on the way
        }
    }

    /// <summary>True when saving to <paramref name="path"/> would replace an existing file.</summary>
    public static bool WouldOverwrite(string path) => File.Exists(path);

    /// <summary>Remembers the folder of an accepted path (newest first, persisted).</summary>
    public void AddRecentPlace(string path)
    {
        string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
            return;
        _recent.RemoveAll(p => string.Equals(p, folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        _recent.Insert(0, folder);
        if (_recent.Count > MaxRecentPlaces)
            _recent.RemoveRange(MaxRecentPlaces, _recent.Count - MaxRecentPlaces);
        SaveRecent();
    }

    /// <summary>Standard places: home, documents, desktop, the working folder and the drives / file system root.</summary>
    public static IReadOnlyList<(string Label, string Path)> StandardPlaces(string home, string documents, string desktop, string working)
    {
        var places = new List<(string, string)>();
        void Add(string label, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !places.Any(p => p.Item2 == path))
                places.Add((label, path));
        }

        Add(home, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(documents, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(desktop, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(working, Environment.CurrentDirectory);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is DriveType.Ram or DriveType.NoRootDirectory || !drive.IsReady)
                    continue;
                if (!OperatingSystem.IsWindows() && drive.Name != "/" && !drive.Name.StartsWith("/media", StringComparison.Ordinal) &&
                    !drive.Name.StartsWith("/mnt", StringComparison.Ordinal) && !drive.Name.StartsWith("/Volumes", StringComparison.Ordinal))
                    continue;
                Add(drive.Name, drive.RootDirectory.FullName);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Drive enumeration is best effort.
        }

        return places;
    }

    /// <summary>Human-readable size ("1.2 MB").</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v:0.#} {units[u]}");
    }

    private void Sort()
    {
        Version++;
        int dir = SortDescending ? -1 : 1;
        _entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1; // folders always first
            int c = SortColumn switch
            {
                FileSortColumn.Size => a.Size.CompareTo(b.Size),
                FileSortColumn.Modified => a.Modified.CompareTo(b.Modified),
                _ => 0,
            };
            if (c == 0)
                c = NaturalCompare(a.Name, b.Name);
            return c * dir;
        });
    }

    /// <summary>Case-insensitive comparison with digit runs compared by value ("file2" &lt; "file10").</summary>
    internal static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i]))
                    i++;
                while (j < b.Length && char.IsDigit(b[j]))
                    j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length)
                    return na.Length.CompareTo(nb.Length);
                int d = na.SequenceCompareTo(nb);
                if (d != 0)
                    return d;
                continue;
            }

            int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (c != 0)
                return c;
            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static bool IsHidden(FileSystemInfo info)
    {
        if (info.Name.StartsWith('.'))
            return true;
        try
        {
            return (info.Attributes & FileAttributes.Hidden) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long SafeLength(FileInfo fi)
    {
        try
        {
            return fi.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void LoadRecent()
    {
        if (_recentFile is null || !File.Exists(_recentFile))
            return;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_recentFile));
            if (list is null)
                return;
            foreach (var p in list)
                if (!string.IsNullOrWhiteSpace(p) && _recent.Count < MaxRecentPlaces && !_recent.Contains(p))
                    _recent.Add(p);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged list is ignored (and replaced on the next save).
        }
    }

    private void SaveRecent()
    {
        if (_recentFile is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_recentFile))!);
            string tmp = _recentFile + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_recent));
            File.Move(tmp, _recentFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember a folder is not worth an error.
        }
    }
}
