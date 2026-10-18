using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Candela.Core.SceneGraph;
using Candela.Core.Serialization;

namespace Candela.Project.Internal;

/// <summary>
/// The JSON configuration used to write scenes and to copy components: <see cref="CandelaJson.Options"/>, except that a
/// property whose value is null is written (<c>"protocolId": null</c>) when its default is not null. CandelaJson omits
/// nulls, which reads back as the default: a console set to "no protocol input" (<c>ProtocolId = null</c>) would come
/// back as Art-Net after saving and loading, or after a merge copied it. Properties whose default is null are still
/// omitted, so files stay as compact as before.
/// </summary>
internal static class ProjectJson
{
    /// <summary>Indented options for <c>scene.json</c>.</summary>
    public static JsonSerializerOptions Options { get; } = Create(CandelaJson.Options);

    /// <summary>Compact options for in-memory copies.</summary>
    public static JsonSerializerOptions CompactOptions { get; } = Create(CandelaJson.CompactOptions);

    /// <summary>A detached deep copy of <paramref name="component"/> (same concrete type, nulls preserved).</summary>
    public static Component Clone(Component component) =>
        JsonSerializer.Deserialize<Component>(JsonSerializer.SerializeToUtf8Bytes(component, CompactOptions), CompactOptions)
        ?? throw new InvalidOperationException("A component could not be copied.");

    private static JsonSerializerOptions Create(JsonSerializerOptions source)
    {
        var options = new JsonSerializerOptions(source)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WriteNullsThatDifferFromDefaults } },
        };
        options.MakeReadOnly();
        return options;
    }

    private static void WriteNullsThatDifferFromDefaults(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.CreateObject is null || info.Type.IsAbstract) return;
        object sample;
        try
        {
            sample = info.CreateObject();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or MissingMethodException)
        {
            return;
        }
        foreach (var property in info.Properties)
        {
            if (property.Get is null) continue;
            var type = property.PropertyType;
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null) continue;
            if (property.Get(sample) is not null) property.ShouldSerialize = static (_, _) => true;
        }
    }
}
