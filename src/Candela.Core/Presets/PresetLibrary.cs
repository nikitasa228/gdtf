using Candela.Core.Serialization;

namespace Candela.Core.Presets;

/// <summary>
/// Named presets for any configurable thing (lux gradient, noise gradient, key bindings, preview/render settings,
/// panel layouts…). The UI shows a dropdown: the built-in <c>Default</c> (read-only), any saved named presets, and
/// <c>Custom</c> as soon as the user edits the current value. <see cref="SaveAs"/> stores the custom state under a name.
/// Values are cloned through JSON so callers can edit <see cref="Current"/> freely.
/// </summary>
public sealed class PresetLibrary<T> where T : class
{
    public const string DefaultName = "Default";
    public const string CustomName = "Custom";

    private readonly Func<T> _defaultFactory;
    private readonly IPresetStore _store;

    public PresetLibrary(string kind, Func<T> defaultFactory, IPresetStore store)
    {
        Kind = kind;
        _defaultFactory = defaultFactory;
        _store = store;
        Current = defaultFactory();
        CurrentName = DefaultName;
    }

    /// <summary>Folder/category name, e.g. "lux-gradient".</summary>
    public string Kind { get; }

    /// <summary>The value in use (edit it, then call <see cref="MarkModified"/>).</summary>
    public T Current { get; private set; }

    /// <summary>"Default", a saved preset name, or "Custom" after an edit.</summary>
    public string CurrentName { get; private set; }

    public bool IsCustom => CurrentName == CustomName;

    public event Action? Changed;

    /// <summary>Entries for the dropdown: Default, saved names (sorted), and Custom when the value was edited.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            var list = new List<string> { DefaultName };
            list.AddRange(_store.List(Kind).Where(n => n != DefaultName && n != CustomName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            if (IsCustom) list.Add(CustomName);
            return list;
        }
    }

    public void Select(string name)
    {
        if (name == CustomName) return;
        T? value = name == DefaultName ? _defaultFactory() : Load(name);
        if (value is null) throw new KeyNotFoundException($"Preset '{name}' of kind '{Kind}' does not exist.");
        Current = value;
        CurrentName = name;
        Changed?.Invoke();
    }

    /// <summary>Call after editing <see cref="Current"/>; the dropdown switches to "Custom".</summary>
    public void MarkModified()
    {
        CurrentName = CustomName;
        Changed?.Invoke();
    }

    /// <summary>Replaces the current value (e.g. from a project file) and marks it as custom.</summary>
    public void SetCustom(T value)
    {
        Current = CandelaJson.DeepClone(value);
        MarkModified();
    }

    /// <summary>Saves the current value under <paramref name="name"/> and selects it.</summary>
    public void SaveAs(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == DefaultName || name == CustomName)
            throw new ArgumentException($"'{name}' cannot be used as a preset name.");
        _store.Save(Kind, name, CandelaJson.Serialize(Current));
        CurrentName = name;
        Changed?.Invoke();
    }

    public void Delete(string name)
    {
        if (name is DefaultName or CustomName) return;
        _store.Delete(Kind, name);
        if (CurrentName == name) CurrentName = CustomName;
        Changed?.Invoke();
    }

    public void ResetToDefault() => Select(DefaultName);

    private T? Load(string name)
    {
        var json = _store.Load(Kind, name);
        return json is null ? null : CandelaJson.Deserialize<T>(json);
    }
}

/// <summary>Storage of serialised presets, by kind and name.</summary>
public interface IPresetStore
{
    IEnumerable<string> List(string kind);
    string? Load(string kind, string name);
    void Save(string kind, string name, string json);
    void Delete(string kind, string name);
}

/// <summary>Presets as files: <c>{root}/{kind}/{name}.json</c> (user config folder).</summary>
public sealed class FilePresetStore(string root) : IPresetStore
{
    public string Root { get; } = root;

    public IEnumerable<string> List(string kind)
    {
        var dir = Path.Combine(Root, kind);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.json").Select(f => Unescape(Path.GetFileNameWithoutExtension(f))).ToList();
    }

    public string? Load(string kind, string name)
    {
        var f = FileFor(kind, name);
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }

    public void Save(string kind, string name, string json)
    {
        var f = FileFor(kind, name);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var tmp = f + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, f, overwrite: true);
    }

    public void Delete(string kind, string name)
    {
        var f = FileFor(kind, name);
        if (File.Exists(f)) File.Delete(f);
    }

    private string FileFor(string kind, string name) => Path.Combine(Root, kind, Escape(name) + ".json");

    private static string Escape(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => bad.Contains(c) || c == '%' ? $"%{(int)c:X2}" : c.ToString()));
    }

    private static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && int.TryParse(s.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                sb.Append((char)code);
                i += 2;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

/// <summary>In-memory store for tests and for presets that live inside a project.</summary>
public sealed class MemoryPresetStore : IPresetStore
{
    private readonly Dictionary<(string, string), string> _items = new();

    public IEnumerable<string> List(string kind) => _items.Keys.Where(k => k.Item1 == kind).Select(k => k.Item2).ToList();
    public string? Load(string kind, string name) => _items.GetValueOrDefault((kind, name));
    public void Save(string kind, string name, string json) => _items[(kind, name)] = json;
    public void Delete(string kind, string name) => _items.Remove((kind, name));
}
