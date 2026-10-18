using System.Collections;
using System.Reflection;

namespace Candela.Project.Internal;

/// <summary>
/// Replaces nulls that JSON allowed but the data model forbids. System.Text.Json writes an explicit <c>null</c> into a
/// non-nullable property (<c>"addresses": null</c>, <c>"name": null</c>, a null element in a node list), and the first
/// module that iterates it would crash. For every public read-write property whose declared nullability is "not null":
/// strings become empty, collections and objects with a parameterless constructor are created, null elements are removed
/// from lists, entries with null values are removed from dictionaries, and nested Candela data objects are repaired
/// recursively. Plans are built once per type from the nullability metadata, so new component types are covered without
/// changes here.
/// </summary>
internal static class NullRepair
{
    private const int MaxDepth = 8;
    private static readonly object s_gate = new();
    private static readonly Dictionary<Type, PropertyPlan[]> s_plans = new();

    /// <summary>Repairs <paramref name="target"/> in place; returns the number of values that were fixed.</summary>
    public static int Repair(object target) => Repair(target, 0);

    private static int Repair(object target, int depth)
    {
        if (depth > MaxDepth) return 0;
        int fixes = 0;
        foreach (var plan in PlanFor(target.GetType()))
        {
            object? value;
            try
            {
                value = plan.Property.GetValue(target);
            }
            catch (TargetInvocationException)
            {
                continue;
            }
            if (value is null)
            {
                if (!plan.NotNull) continue;
                value = plan.CreateDefault();
                if (value is null) continue;
                plan.Property.SetValue(target, value);
                fixes++;
            }
            if (plan.ListElementNotNull && value is IList list && !list.IsReadOnly && !list.IsFixedSize)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is null)
                    {
                        list.RemoveAt(i);
                        fixes++;
                    }
                }
            }
            if (plan.DictionaryValueNotNull && value is IDictionary dict && !dict.IsReadOnly)
            {
                List<object>? nullKeys = null;
                foreach (DictionaryEntry e in dict)
                    if (e.Value is null) (nullKeys ??= new List<object>()).Add(e.Key);
                if (nullKeys is not null)
                {
                    foreach (var k in nullKeys) dict.Remove(k);
                    fixes += nullKeys.Count;
                }
            }
            if (plan.RecurseElements && value is IEnumerable items)
            {
                foreach (var item in items)
                    if (item is not null) fixes += Repair(item, depth + 1);
            }
            else if (plan.RecurseValue)
            {
                fixes += Repair(value, depth + 1);
            }
        }
        return fixes;
    }

    private static PropertyPlan[] PlanFor(Type type)
    {
        lock (s_gate)
        {
            if (s_plans.TryGetValue(type, out var cached)) return cached;
            var context = new NullabilityInfoContext();
            var plans = new List<PropertyPlan>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.GetMethod is not { IsPublic: true } || p.SetMethod is not { IsPublic: true }) continue;
                if (p.PropertyType.IsValueType) continue;
                var info = context.Create(p);
                bool notNull = info.WriteState == NullabilityState.NotNull || info.ReadState == NullabilityState.NotNull;
                var plan = new PropertyPlan(p, notNull);
                var t = p.PropertyType;
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var element = t.GetGenericArguments()[0];
                    plan.ListElementNotNull = !element.IsValueType && info.GenericTypeArguments.Length == 1 &&
                                              info.GenericTypeArguments[0].ReadState == NullabilityState.NotNull;
                    plan.RecurseElements = IsCandelaData(element);
                }
                else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    var valueType = t.GetGenericArguments()[1];
                    plan.DictionaryValueNotNull = !valueType.IsValueType && info.GenericTypeArguments.Length == 2 &&
                                                  info.GenericTypeArguments[1].ReadState == NullabilityState.NotNull;
                }
                else
                {
                    plan.RecurseValue = IsCandelaData(t);
                }
                plans.Add(plan);
            }
            var result = plans.ToArray();
            s_plans[type] = result;
            return result;
        }
    }

    /// <summary>Plain data classes of Candela assemblies (nested component data such as cable nodes or outlets).</summary>
    private static bool IsCandelaData(Type t) =>
        t.IsClass && t != typeof(string) && !t.IsAbstract && t.Namespace?.StartsWith("Candela.", StringComparison.Ordinal) == true &&
        t.GetConstructor(Type.EmptyTypes) is not null;

    private sealed class PropertyPlan(PropertyInfo property, bool notNull)
    {
        public PropertyInfo Property { get; } = property;

        public bool NotNull { get; } = notNull;

        public bool ListElementNotNull { get; set; }

        public bool DictionaryValueNotNull { get; set; }

        public bool RecurseElements { get; set; }

        public bool RecurseValue { get; set; }

        public object? CreateDefault()
        {
            var t = Property.PropertyType;
            if (t == typeof(string)) return "";
            if (t.IsAbstract || t.IsInterface) return null;
            return t.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(t) : null;
        }
    }
}
