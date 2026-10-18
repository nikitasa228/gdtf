using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Candela.Core.Serialization;

namespace Candela.Project;

/// <summary>
/// Project-level settings stored in <c>settings.json</c> as named JSON sections. Each higher layer (renderer preview and
/// final-render settings, view and overlay settings, overlay gradients, panel layouts, …) owns one or more sections and
/// their schema, so this module never references those types: it stores and returns raw JSON.
/// <para>
/// Sections are immutable <see cref="JsonElement"/> values that do not depend on any <see cref="JsonDocument"/>'s
/// lifetime (they are cloned on the way in), so a snapshot of the dictionary can be written on a background thread.
/// Typed helpers serialise with <see cref="CandelaJson.Options"/> (camelCase, enums as strings, vectors as arrays).
/// Section names are case-sensitive; by convention they are short lower-case identifiers ("preview", "render", "view",
/// "luxOverlay", "layout").
/// </para>
/// This class is not thread-safe; it belongs to the main thread like the scene.
/// </summary>
public sealed class ProjectSettings : IReadOnlyDictionary<string, JsonElement>
{
    private readonly Dictionary<string, JsonElement> _sections = new(StringComparer.Ordinal);

    /// <summary>Number of sections.</summary>
    public int Count => _sections.Count;

    /// <summary>Names of all sections.</summary>
    public IEnumerable<string> Keys => _sections.Keys;

    /// <summary>All section values.</summary>
    public IEnumerable<JsonElement> Values => _sections.Values;

    /// <summary>Raw JSON of a section; throws <see cref="KeyNotFoundException"/> when it does not exist.</summary>
    public JsonElement this[string key] => _sections[key];

    /// <summary>Raised after a section was set or removed (argument: the section name; null after <see cref="Clear"/>).</summary>
    public event Action<string?>? Changed;

    /// <summary>True when the section exists.</summary>
    public bool ContainsKey(string key) => _sections.ContainsKey(key);

    /// <summary>Raw JSON of a section, or false when it does not exist.</summary>
    public bool TryGetValue(string key, out JsonElement value) => _sections.TryGetValue(key, out value);

    /// <summary>
    /// Stores raw JSON under <paramref name="name"/> (replacing an existing section). The element is cloned, so the
    /// caller's document may be disposed afterwards.
    /// </summary>
    public void SetRaw(string name, JsonElement value)
    {
        ValidateName(name);
        if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("A settings section cannot be undefined.", nameof(value));
        _sections[name] = value.Clone();
        Changed?.Invoke(name);
    }

    /// <summary>Stores raw JSON text under <paramref name="name"/>. Throws <see cref="JsonException"/> for invalid JSON.</summary>
    public void SetJson(string name, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json, ReadOptions);
        SetRaw(name, doc.RootElement);
    }

    /// <summary>Serialises <paramref name="value"/> with <see cref="CandelaJson.Options"/> and stores it as a section.</summary>
    public void Set<T>(string name, T value)
    {
        ValidateName(name);
        _sections[name] = JsonSerializer.SerializeToElement(value, CandelaJson.Options);
        Changed?.Invoke(name);
    }

    /// <summary>
    /// Deserialises a section into <typeparamref name="T"/>. Returns false (never throws) when the section does not
    /// exist or does not match the type, so callers can fall back to their defaults after a format change.
    /// </summary>
    public bool TryGet<T>(string name, [MaybeNullWhen(false)] out T value)
    {
        value = default;
        if (!_sections.TryGetValue(name, out var element)) return false;
        try
        {
            var result = element.Deserialize<T>(CandelaJson.Options);
            if (result is null) return false;
            value = result;
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The section deserialised into <typeparamref name="T"/>, or <paramref name="fallback"/> when missing or unreadable.</summary>
    public T GetOrDefault<T>(string name, T fallback) => TryGet<T>(name, out var value) ? value : fallback;

    /// <summary>Removes a section; false when it did not exist.</summary>
    public bool Remove(string name)
    {
        if (!_sections.Remove(name)) return false;
        Changed?.Invoke(name);
        return true;
    }

    /// <summary>Removes every section.</summary>
    public void Clear()
    {
        _sections.Clear();
        Changed?.Invoke(null);
    }

    /// <summary>Raw JSON text of a section (compact), or null when it does not exist.</summary>
    public string? GetJson(string name) => _sections.TryGetValue(name, out var e) ? e.GetRawText() : null;

    /// <summary>A copy of all sections (elements are immutable and shared).</summary>
    public IReadOnlyDictionary<string, JsonElement> Snapshot() => new Dictionary<string, JsonElement>(_sections, StringComparer.Ordinal);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => _sections.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void SetLoaded(string name, JsonElement clonedValue) => _sections[name] = clonedValue;

    internal static JsonDocumentOptions ReadOptions => new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A settings section needs a name.", nameof(name));
    }
}
