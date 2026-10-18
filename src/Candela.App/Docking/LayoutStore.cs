using System.Text;

namespace Candela.App.Docking;

/// <summary>
/// Named layouts of the user, one JSON file each in a folder (<c>ConfigPaths.LayoutsDir</c>). The workspace as it was
/// at exit is stored under the reserved name <see cref="WorkspaceName"/>. Names are escaped into safe file names and
/// written atomically (temporary file + rename).
/// </summary>
internal sealed class LayoutStore
{
    /// <summary>File name (without extension) of the automatically saved workspace.</summary>
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
                .Select(f => Unescape(Path.GetFileName(f)[..^Extension.Length]))
                .Where(n => n != WorkspaceName)
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
        Directory.CreateDirectory(Folder);
        string path = PathOf(name);
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        layout.Name = name == WorkspaceName ? layout.Name : name;
        File.WriteAllText(tmp, layout.ToJson(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads a layout; null when it does not exist. Throws <see cref="LayoutFormatException"/> when damaged.</summary>
    public LayoutFile? Load(string name)
    {
        string path = PathOf(ValidateName(name));
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

    /// <summary>File-name-safe form: unsafe characters (and the dots of names made only of dots) become <c>%XXXX</c>.</summary>
    internal static string Escape(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        bool onlyDots = name.Trim('.').Length == 0;
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (Array.IndexOf(bad, c) >= 0 || c is '%' or '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ||
                char.IsControl(c) || (onlyDots && c == '.'))
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
