using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Candela.Core.Serialization;

namespace Candela.Project.Internal;

/// <summary>
/// Deserialises an object while resetting the values that cannot be read, instead of losing the whole object: when
/// System.Text.Json fails, the exception's JSON path (<c>$.nodes[3].anchor.entityId</c>) names the offending value; it is
/// removed (so the property takes its default, or the list element disappears) and the object is read again, up to
/// <see cref="MaxRepairs"/> times. One unreadable value in a cable with fifty nodes then costs that value, not the cable.
/// </summary>
internal static class JsonSalvage
{
    /// <summary>Most values reset in one object before it is given up.</summary>
    public const int MaxRepairs = 32;

    /// <summary>
    /// Reads <paramref name="element"/> as <typeparamref name="T"/>. Returns false with the first error when it cannot be
    /// read even after resetting values; <paramref name="resetPaths"/> lists the JSON paths of the values that were reset.
    /// </summary>
    public static bool TryDeserialize<T>(JsonNode? source, JsonElement element, List<string> resetPaths, out T? value, out Exception? error)
    {
        value = default;
        error = null;
        try
        {
            value = source is null ? element.Deserialize<T>(CandelaJson.Options) : source.Deserialize<T>(CandelaJson.Options);
            return true;
        }
        catch (Exception e) when (SceneJsonReader.IsDataError(e))
        {
            error = e;
        }
        if (element.ValueKind != JsonValueKind.Object) return false;

        var node = source?.DeepClone() ?? JsonNode.Parse(element.GetRawText());
        var current = error;
        string? previous = null;
        for (int attempt = 0; attempt < MaxRepairs; attempt++)
        {
            var path = (current as JsonException)?.Path;
            if (string.IsNullOrEmpty(path) || path == previous || !TryRemove(node, path)) return false;
            previous = path;
            resetPaths.Add(path);
            try
            {
                value = node.Deserialize<T>(CandelaJson.Options);
                error = null;
                return true;
            }
            catch (Exception e) when (SceneJsonReader.IsDataError(e))
            {
                current = e;
            }
        }
        return false;
    }

    /// <summary>Removes the value at a System.Text.Json path (<c>$.a.b[2]</c>, <c>$['odd name'].c</c>); false when it is not found.</summary>
    internal static bool TryRemove(JsonNode? root, string path)
    {
        if (root is null || path.Length < 2 || path[0] != '$') return false;
        var segments = new List<object>();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int start = ++i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                if (i == start) return false;
                segments.Add(path[start..i]);
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                var name = new StringBuilder();
                i += 2;
                while (i < path.Length && !(path[i] == '\'' && i + 1 < path.Length && path[i + 1] == ']'))
                {
                    if (path[i] == '\\' && i + 1 < path.Length) i++;
                    name.Append(path[i++]);
                }
                if (i >= path.Length) return false;
                i += 2;
                segments.Add(name.ToString());
            }
            else if (path[i] == '[')
            {
                int start = ++i;
                while (i < path.Length && path[i] != ']') i++;
                if (i >= path.Length || !int.TryParse(path.AsSpan(start, i - start), out int index)) return false;
                i++;
                segments.Add(index);
            }
            else
            {
                return false;
            }
        }
        if (segments.Count == 0) return false;

        var parent = root;
        for (int k = 0; k < segments.Count - 1; k++)
        {
            parent = segments[k] switch
            {
                string name when parent is JsonObject o => o[name],
                int index when parent is JsonArray a && index >= 0 && index < a.Count => a[index],
                _ => null,
            };
            if (parent is null) return false;
        }
        switch (segments[^1])
        {
            case string name when parent is JsonObject o:
                return name != "$type" && o.Remove(name);
            case int index when parent is JsonArray a && index >= 0 && index < a.Count:
                a.RemoveAt(index);
                return true;
            default:
                return false;
        }
    }
}
