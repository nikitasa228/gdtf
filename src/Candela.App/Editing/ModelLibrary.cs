using System.Text.Json;
using Candela.App.Diagnostics;
using Candela.Assets;

namespace Candela.App.Editing;

/// <summary>Kind of a model-library item.</summary>
internal enum ModelItemKind
{
    /// <summary>A 3D model (OBJ, FBX, DAE, 3DS, glTF/GLB, STL, PLY).</summary>
    Model,

    /// <summary>An image used as a reference drawing or poster.</summary>
    Image,
}

/// <summary>A file of the model library.</summary>
/// <param name="Path">Full path of the file (the original stays where it is; projects store their own copy on import).</param>
/// <param name="Kind">Model or image.</param>
/// <param name="AddedUtc">When it was added.</param>
internal sealed record ModelLibraryItem(string Path, ModelItemKind Kind, DateTime AddedUtc)
{
    /// <summary>Name shown under the tile (the file name without extension).</summary>
    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Format label ("OBJ", "PNG").</summary>
    public string Format { get; } = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
}

/// <summary>
/// The user's library of custom 3D models and images (the Library panel's Models tab; requirement: the library "can be
/// extended by loading custom models"). It lists files by path — a model's companion files (OBJ + MTL + textures) stay
/// next to it, and placing an item imports it into the project, which keeps its own copy — and is stored as JSON
/// (<c>model-library.json</c> in the config folder, written atomically). A damaged file gives an empty library and a
/// warning; entries of an unknown kind are skipped. Headless runs keep changes in memory only.
/// </summary>
internal sealed class ModelLibrary
{
    /// <summary>Most items kept (the oldest are dropped beyond it).</summary>
    public const int MaxItems = 2000;

    private readonly string _path;
    private readonly AppLog _log;
    private readonly List<ModelLibraryItem> _items = new();

    /// <summary>Creates a library stored at <paramref name="path"/> (read with <see cref="Load"/>).</summary>
    public ModelLibrary(string path, AppLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Items, newest first.</summary>
    public IReadOnlyList<ModelLibraryItem> Items => _items;

    /// <summary>Incremented on every change (panels rebuild their filtered lists).</summary>
    public int Version { get; private set; }

    /// <summary>When true nothing is written to disk (headless verification runs).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The kind of a file the library accepts, or null (not a model or image).</summary>
    public static ModelItemKind? KindOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (AssetFormats.IsImageFile(path))
            return ModelItemKind.Image;
        if (AssetFormats.IsModelFile(path) && AssetFormats.GetModelFormat(path) != ModelFormat.SketchUp)
            return ModelItemKind.Model;
        return null;
    }

    /// <summary>Reads the stored library (a missing file is an empty library).</summary>
    public void Load()
    {
        _items.Clear();
        Version++;
        if (!File.Exists(_path))
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return;
            // Duplicates are found with a set (a file of thousands of entries is read in linear time).
            var seen = new HashSet<string>(PathComparer);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String)
                    continue;
                string path = p.GetString() ?? "";
                if (KindOf(path) is not { } kind || !seen.Add(path))
                    continue;
                var added = item.TryGetProperty("added", out var a) && a.ValueKind == JsonValueKind.String && a.TryGetDateTime(out var d)
                    ? d.ToUniversalTime()
                    : DateTime.UnixEpoch;
                _items.Add(new ModelLibraryItem(path, kind, added));
                if (_items.Count >= MaxItems)
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            _log.Warn("Library", $"The model library '{_path}' could not be read ({e.Message}); it starts empty.");
            _items.Clear();
        }
    }

    /// <summary>Adds a file (newest first); false when it is not a model or image, or already listed (it moves to the top).</summary>
    public bool Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (KindOf(full) is not { } kind)
            return false;
        int existing = _items.FindIndex(i => SamePath(i.Path, full));
        bool added = existing < 0;
        var item = added ? new ModelLibraryItem(full, kind, DateTime.UtcNow) : _items[existing];
        if (!added)
            _items.RemoveAt(existing);
        _items.Insert(0, item);
        if (_items.Count > MaxItems)
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Version++;
        Save();
        return added;
    }

    /// <summary>Removes a file from the library (the file itself is not touched).</summary>
    public bool Remove(string path)
    {
        int index = _items.FindIndex(i => SamePath(i.Path, path));
        if (index < 0)
            return false;
        _items.RemoveAt(index);
        Version++;
        Save();
        return true;
    }

    /// <summary>Writes the library (atomic replace); failures are logged, never thrown.</summary>
    public void Save()
    {
        if (ReadOnly)
            return;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string temp = _path + ".tmp-" + Environment.ProcessId;
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("format", 1);
                writer.WriteStartArray("items");
                foreach (var item in _items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", item.Path);
                    writer.WriteString("kind", item.Kind == ModelItemKind.Image ? "image" : "model");
                    writer.WriteString("added", item.AddedUtc);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Library", $"The model library could not be saved: {e.Message}");
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool SamePath(string a, string b) => PathComparer.Equals(a, b);
}
