using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using Candela.Core.SceneGraph;

namespace Candela.Core.Tests;

/// <summary>
/// Guards that make it impossible to forget a reference when a component (or a member of one) is added later:
/// every component type is registered for JSON, every <see cref="Guid"/> member reachable from a component is classified
/// (entity reference, asset reference or local id), and every entity and asset reference is rewritten by
/// <see cref="Component.RemapReferences"/> — the tests fill every classified member of every component type with known
/// ids through reflection and fail when one of them survives a remap.
/// </summary>
public class EntityReferenceCoverageTests
{
    private static readonly Type[] Classifications = { typeof(EntityReferenceAttribute), typeof(AssetReferenceAttribute), typeof(LocalIdAttribute) };

    /// <summary>Every concrete component type of Core.</summary>
    public static TheoryData<Type> ComponentTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var t in ConcreteComponentTypes()) data.Add(t);
        return data;
    }

    [Fact]
    public void Every_component_type_is_registered_with_a_json_discriminator()
    {
        var registered = typeof(Component).GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(a => a.DerivedType)
            .ToHashSet();
        var all = ConcreteComponentTypes().ToHashSet();
        Assert.Empty(all.Except(registered).Select(t => t.Name));
        Assert.Empty(registered.Except(all).Select(t => t.Name));
        Assert.True(all.Count >= 12);
    }

    [Fact]
    public void Every_guid_member_reachable_from_a_component_is_classified_exactly_once()
    {
        var problems = new List<string>();
        foreach (var type in ReachableDataTypes())
        {
            foreach (var member in DataMembers(type))
            {
                var memberType = MemberType(member);
                int classifications = Classifications.Count(a => member.IsDefined(a, inherit: true));
                if (HoldsGuid(memberType) && classifications != 1)
                    problems.Add($"{type.Name}.{member.Name} holds ids but has {classifications} of [EntityReference], [AssetReference], [LocalId].");
                else if (!HoldsGuid(memberType) && classifications > 0)
                    problems.Add($"{type.Name}.{member.Name} is classified as an id but holds no Guid.");
            }
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void The_reference_walk_reaches_the_known_references()
    {
        // Sanity check of the reflection walk itself: the references this version is known to have are found.
        var entityReferences = ReachableDataTypes()
            .SelectMany(t => DataMembers(t).Where(m => m.IsDefined(typeof(EntityReferenceAttribute), true)).Select(m => $"{t.Name}.{m.Name}"))
            .ToHashSet();
        Assert.Contains("FixtureComponent.MountedOn", entityReferences);
        Assert.Contains("RiggingPointComponent.Carries", entityReferences);
        Assert.Contains("CableAnchor.EntityId", entityReferences);
        Assert.Contains("CableEndpoint.EntityId", entityReferences);
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Every_entity_reference_is_rewritten_to_the_copy(Type componentType)
    {
        var (component, filled) = Fill(componentType);
        var map = filled.Entities.ToDictionary(id => id, _ => Guid.NewGuid());
        var remapper = new EntityReferenceRemapper(map, ExternalReferenceHandling.Clear);
        remapper.Apply(component);

        var after = Collect(component);
        Assert.Equal(filled.Entities.Count, remapper.Remapped);
        Assert.Empty(remapper.Cleared);
        Assert.Equal(map.Values.OrderBy(g => g), after.Entities.OrderBy(g => g));
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void References_outside_the_map_are_kept_with_keep(Type componentType)
    {
        var (component, filled) = Fill(componentType);
        var remapper = new EntityReferenceRemapper(new Dictionary<Guid, Guid>(), ExternalReferenceHandling.Keep);
        remapper.Apply(component);

        Assert.Equal(filled.Entities.Count, remapper.Kept);
        Assert.Equal(filled.Entities.OrderBy(g => g), Collect(component).Entities.OrderBy(g => g));
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void References_outside_the_map_are_removed_with_clear(Type componentType)
    {
        var (component, filled) = Fill(componentType);
        var remapper = new EntityReferenceRemapper(new Dictionary<Guid, Guid>(), ExternalReferenceHandling.Clear);
        remapper.Apply(component);

        Assert.Empty(Collect(component).Entities);
        Assert.Equal(filled.Entities.OrderBy(g => g), remapper.Cleared.Select(c => c.EntityId).OrderBy(g => g));
        Assert.All(remapper.Cleared, c => Assert.Same(component, c.Component));
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Keep_if_resolvable_asks_for_every_external_reference(Type componentType)
    {
        var (component, filled) = Fill(componentType);
        var resolvable = filled.Entities.Where((_, i) => i % 2 == 0).ToHashSet();
        var remapper = new EntityReferenceRemapper(new Dictionary<Guid, Guid>(), ExternalReferenceHandling.KeepIfResolvable)
        {
            IsResolvable = resolvable.Contains,
        };
        remapper.Apply(component);

        var after = Collect(component).Entities;
        Assert.All(after, id => Assert.Contains(id, resolvable));
        Assert.Equal(filled.Entities.Count - resolvable.Count, remapper.Cleared.Count);
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Every_asset_reference_follows_the_asset_map(Type componentType)
    {
        var (component, filled) = Fill(componentType);
        var assets = filled.Assets.ToDictionary(id => id, _ => Guid.NewGuid());
        var remapper = new EntityReferenceRemapper(new Dictionary<Guid, Guid>()) { AssetIdMap = assets };
        remapper.Apply(component);

        Assert.Equal(filled.Assets.Count, remapper.AssetsRemapped);
        Assert.Equal(assets.Values.OrderBy(g => g), Collect(component).Assets.OrderBy(g => g));

        // Without a map, asset ids stay as they are (copies inside one project).
        var (plain, plainFilled) = Fill(componentType);
        new EntityReferenceRemapper(new Dictionary<Guid, Guid>()).Apply(plain);
        Assert.Equal(plainFilled.Assets.OrderBy(g => g), Collect(plain).Assets.OrderBy(g => g));
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Local_ids_are_renewed_only_on_request(Type componentType)
    {
        var (component, _) = Fill(componentType);
        var before = Collect(component).LocalIds;
        new EntityReferenceRemapper(new Dictionary<Guid, Guid>()) { RegenerateLocalIds = false }.Apply(component);
        Assert.Equal(before, Collect(component).LocalIds);

        new EntityReferenceRemapper(new Dictionary<Guid, Guid>()).Apply(component);
        var renewed = Collect(component).LocalIds;
        Assert.Equal(before.Count, renewed.Count);
        Assert.Empty(renewed.Where(id => id != Guid.Empty).Intersect(before.Where(id => id != Guid.Empty)));
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Remapping_tolerates_malformed_data(Type componentType)
    {
        // Every reference-type member set to null, every list holding a null entry: the remap must not throw.
        var component = (Component)Activator.CreateInstance(componentType)!;
        foreach (var member in DataMembers(componentType).OfType<PropertyInfo>().Where(p => p.CanWrite))
        {
            var t = member.PropertyType;
            if (!t.IsValueType) member.SetValue(component, null);
        }
        var remapper = new EntityReferenceRemapper(new Dictionary<Guid, Guid>(), ExternalReferenceHandling.Clear);
        remapper.Apply(component);

        var (withNulls, _) = Fill(componentType);
        foreach (var member in DataMembers(componentType).OfType<PropertyInfo>())
        {
            if (member.GetValue(withNulls) is IList list && !member.PropertyType.GetGenericArguments()[0].IsValueType) list.Add(null);
        }
        remapper.Apply(withNulls);
    }

    // ---- reflection helpers -------------------------------------------------------------------------------------------

    private static IEnumerable<Type> ConcreteComponentTypes() =>
        typeof(Component).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Component)) && !t.IsAbstract).OrderBy(t => t.Name);

    /// <summary>Component types and every Candela data type reachable from their members (cable nodes, anchors, outlets…).</summary>
    private static IEnumerable<Type> ReachableDataTypes()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(ConcreteComponentTypes());
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            yield return t;
            foreach (var m in DataMembers(t))
                foreach (var nested in CandelaTypesIn(MemberType(m)))
                    queue.Enqueue(nested);
        }
    }

    /// <summary>Persisted members: public instance properties and fields that JSON reads and writes.</summary>
    private static IEnumerable<MemberInfo> DataMembers(Type t)
    {
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.GetMethod is not { IsPublic: true } || p.IsDefined(typeof(JsonIgnoreAttribute), true)) continue;
            yield return p;
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!f.IsDefined(typeof(JsonIgnoreAttribute), true)) yield return f;
    }

    private static Type MemberType(MemberInfo m) => m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;

    private static bool IsCandela(Type t) => !t.IsEnum && t.Namespace?.StartsWith("Candela.", StringComparison.Ordinal) == true;

    private static IEnumerable<Type> CandelaTypesIn(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (IsCandela(t))
        {
            yield return t;
            yield break;
        }
        if (t.IsArray)
        {
            foreach (var e in CandelaTypesIn(t.GetElementType()!)) yield return e;
            yield break;
        }
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                foreach (var e in CandelaTypesIn(a))
                    yield return e;
    }

    /// <summary>True for Guid, Guid? and collections or dictionaries with Guid keys, values or elements.</summary>
    private static bool HoldsGuid(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(Guid)) return true;
        if (t.IsArray) return HoldsGuid(t.GetElementType()!);
        return t.IsGenericType && !IsCandela(t) && t.GetGenericArguments().Any(HoldsGuid);
    }

    private sealed class Ids
    {
        public List<Guid> Entities { get; } = new();
        public List<Guid> Assets { get; } = new();
        public List<Guid> LocalIds { get; } = new();
    }

    private enum Mode
    {
        Fill,
        Collect,
    }

    /// <summary>
    /// A component of <paramref name="componentType"/> with every classified id member set: two entries per list, every
    /// nested object and nullable struct created. Throws (failing the test) for a reference shape the walk cannot fill,
    /// so a new kind of member must extend this test instead of being skipped.
    /// </summary>
    private static (Component Component, Ids Ids) Fill(Type componentType)
    {
        var component = (Component)Activator.CreateInstance(componentType)!;
        var ids = new Ids();
        Walk(component, Mode.Fill, ids, 0);
        return (component, ids);
    }

    /// <summary>Every non-empty classified id held by <paramref name="component"/> after a remap.</summary>
    private static Ids Collect(Component component)
    {
        var ids = new Ids();
        Walk(component, Mode.Collect, ids, 0);
        return ids;
    }

    private static void Walk(object target, Mode mode, Ids ids, int depth)
    {
        Assert.True(depth < 8, "Component data nests too deeply for the reference walk.");
        foreach (var member in DataMembers(target.GetType()))
        {
            var type = MemberType(member);
            List<Guid>? bucket = member.IsDefined(typeof(EntityReferenceAttribute), true) ? ids.Entities
                : member.IsDefined(typeof(AssetReferenceAttribute), true) ? ids.Assets
                : member.IsDefined(typeof(LocalIdAttribute), true) ? ids.LocalIds
                : null;
            if (bucket is not null)
            {
                WalkIds(target, member, type, mode, bucket, bucket == ids.LocalIds);
                continue;
            }
            if (!CandelaTypesIn(type).Any()) continue;

            var value = Get(target, member);
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (IsCandela(underlying))
            {
                if (value is null)
                {
                    if (mode == Mode.Collect) continue;
                    value = Activator.CreateInstance(underlying)!;
                }
                Walk(value, mode, ids, depth + 1);
                if (mode == Mode.Fill) Set(target, member, value); // also writes back boxed structs
                continue;
            }
            if (value is IList list && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var element = type.GetGenericArguments()[0];
                if (mode == Mode.Fill)
                {
                    list.Clear();
                    for (int i = 0; i < 2; i++)
                    {
                        var item = Activator.CreateInstance(element)!;
                        Walk(item, mode, ids, depth + 1);
                        list.Add(item);
                    }
                }
                else
                {
                    foreach (var item in list)
                        if (item is not null) Walk(item, mode, ids, depth + 1);
                }
                continue;
            }
            if (value is null && mode == Mode.Collect) continue;
            throw new InvalidOperationException(
                $"{target.GetType().Name}.{member.Name} ({type.Name}) holds Candela data in a shape the reference walk does not know; extend {nameof(EntityReferenceCoverageTests)}.");
        }
    }

    private static void WalkIds(object target, MemberInfo member, Type type, Mode mode, List<Guid> bucket, bool local)
    {
        if (type == typeof(Guid))
        {
            if (mode == Mode.Fill)
            {
                var id = local ? (Guid)Get(target, member)! : Guid.NewGuid();
                if (!local) Set(target, member, id);
                bucket.Add(id);
            }
            else if ((Guid)Get(target, member)! is var id && (local || id != Guid.Empty))
            {
                bucket.Add(id);
            }
            return;
        }
        if (type == typeof(Guid?))
        {
            if (mode == Mode.Fill)
            {
                var id = Guid.NewGuid();
                Set(target, member, (Guid?)id);
                bucket.Add(id);
            }
            else if (Get(target, member) is Guid id && id != Guid.Empty)
            {
                bucket.Add(id);
            }
            return;
        }
        if (type == typeof(List<Guid>))
        {
            var list = (List<Guid>?)Get(target, member);
            if (mode == Mode.Fill)
            {
                list ??= new List<Guid>();
                list.Clear();
                list.Add(Guid.NewGuid());
                list.Add(Guid.NewGuid());
                Set(target, member, list);
                bucket.AddRange(list);
            }
            else if (list is not null)
            {
                bucket.AddRange(list.Where(id => id != Guid.Empty));
            }
            return;
        }
        throw new InvalidOperationException(
            $"{target.GetType().Name}.{member.Name} is an id of type {type.Name}, a shape the reference walk does not know; extend {nameof(EntityReferenceCoverageTests)}.");
    }

    private static object? Get(object target, MemberInfo m) => m is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)m).GetValue(target);

    private static void Set(object target, MemberInfo m, object? value)
    {
        if (m is PropertyInfo p)
        {
            Assert.True(p.CanWrite, $"{target.GetType().Name}.{p.Name} cannot be written by the reference walk.");
            p.SetValue(target, value);
        }
        else
        {
            ((FieldInfo)m).SetValue(target, value);
        }
    }
}
