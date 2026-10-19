using System.Text;

namespace Candela.App.Docking;

/// <summary>
/// Named layouts of the user, one JSON file each in a folder (<c>ConfigPaths.LayoutsDir</c>), and the workspace as it was
/// at exit in <c>_workspace.layout.json</c> (<see cref="SaveWorkspace"/>). Names are escaped into safe file names — a
/// leading underscore included, so no user layout can ever replace the workspace file — and written atomically
/// (temporary file + rename).
/// </summary>
internal sealed class LayoutStore
{
    /// <summary>File name (without extension) of the automatically saved workspace; user names never map to it.</summary>
    public const string WorkspaceName = "_workspace";

    private const string Extension = ".layout.json";

    /// <summary>Creates a store over <paramref name="directory"/>.</summary>
    public LayoutStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Folder = directory;
    }

    /// <summary>The layouts folder.</summary>
    public string Folder { get; }

    /// <summary>Saved layout names (sorted, the workspace file excluded). Unreadable folders give an empty list.</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return Array.Empty<string>();
            return Directory.EnumerateFiles(Folder, "*" + Extension)
                .Select(f => Path.GetFileName(f)[..^Extension.Length])
                .Where(stem => stem != WorkspaceName)
                .Select(Unescape)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>True when a layout with this name exists.</summary>
    public bool Exists(string name) => File.Exists(PathOf(name));

    /// <summary>Writes <paramref name="layout"/> under <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">Empty name.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(string name, LayoutFile layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        name = ValidateName(name);
        layout.Name = name;
        Write(PathOf(name), layout);
    }

    /// <summary>Writes the workspace restored at the next start.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void SaveWorkspace(LayoutFile layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Write(WorkspacePath, layout);
    }

    /// <summary>Reads the saved workspace; null when there is none. Throws <see cref="LayoutFormatException"/> when damaged.</summary>
    public LayoutFile? LoadWorkspace() => Read(WorkspacePath);

    /// <summary>Reads a layout; null when it does not exist. Throws <see cref="LayoutFormatException"/> when damaged.</summary>
    public LayoutFile? Load(string name) => Read(PathOf(ValidateName(name)));

    private string WorkspacePath => Path.Combine(Folder, WorkspaceName + Extension);

    private void Write(string path, LayoutFile layout)
    {
        Directory.CreateDirectory(Folder);
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, layout.ToJson(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static LayoutFile? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LayoutFormatException($"The layout file could not be read: {e.Message}", e);
        }

        return LayoutFile.FromJson(json);
    }

    /// <summary>Deletes a layout (no error when absent).</summary>
    public void Delete(string name)
    {
        string path = PathOf(ValidateName(name));
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>The file of a layout.</summary>
    public string PathOf(string name) => Path.Combine(Folder, Escape(name) + Extension);

    private static string ValidateName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException("A layout needs a name.", nameof(name));
        if (name.Length > 120)
            name = name[..120];
        return name;
    }

    /// <summary>
    /// File-name-safe form: unsafe characters, the dots of names made only of dots and a leading underscore (reserved for
    /// the workspace file) become <c>%XXXX</c>.
    /// </summary>
    internal static string Escape(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        bool onlyDots = name.Trim('.').Length == 0;
        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (Array.IndexOf(bad, c) >= 0 || c is '%' or '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ||
                char.IsControl(c) || (onlyDots && c == '.') || (i == 0 && c == '_'))
                sb.Append('%').Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Inverse of <see cref="Escape"/>.</summary>
    internal static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 4 < s.Length &&
                int.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int code))
            {
                sb.Append((char)code);
                i += 4;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }
}
