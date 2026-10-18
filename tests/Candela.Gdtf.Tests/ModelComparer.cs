using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Candela.Gdtf.Tests;

/// <summary>
/// Structural comparison of two object graphs through all public instance properties: values must be equal, and
/// object links must point to corresponding objects (the first time an object is reached it is paired with its
/// counterpart; every later reference to it must reach that same counterpart). Used to prove that a fixture type
/// loaded from the disk cache is indistinguishable from the parsed one.
/// </summary>
internal static class ModelComparer
{
    private static readonly ConcurrentDictionary<Type, (string Name, Func<object, object?> Get)[]> Getters = new();

    /// <summary>Differences between <paramref name="expected"/> and <paramref name="actual"/> (at most <paramref name="max"/>).</summary>
    public static List<string> Compare(object expected, object actual, int max = 20)
    {
        var diffs = new List<string>();
        var map = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        // Object graphs can be deep (geometry trees, links between channels): run on a thread with a large stack.
        var thread = new Thread(() => Walk(expected, actual, "", map, diffs, max), 256 << 20);
        thread.Start();
        thread.Join();
        return diffs;
    }

    private static void Walk(object? a, object? b, string path, Dictionary<object, object> map, List<string> diffs, int max)
    {
        if (diffs.Count >= max) return;
        if (a is null || b is null)
        {
            if (a is not null || b is not null) diffs.Add($"{path}: {Show(a)} vs {Show(b)}");
            return;
        }
        var type = a.GetType();
        if (type != b.GetType())
        {
            diffs.Add($"{path}: type {type.Name} vs {b.GetType().Name}");
            return;
        }
        if (a is ITuple ta && type.IsValueType)
        {
            var tb = (ITuple)b;
            for (int i = 0; i < ta.Length; i++) Walk(ta[i], tb[i], $"{path}.Item{i + 1}", map, diffs, max);
            return;
        }
        if (type.IsValueType || a is string)
        {
            if (!a.Equals(b)) diffs.Add($"{path}: {Show(a)} vs {Show(b)}");
            else if (type.Assembly == typeof(DmxValue).Assembly && !type.IsEnum)
                foreach (var (name, get) in Getters.GetOrAdd(type, Build))
                    Walk(get(a), get(b), $"{path}.{name}", map, diffs, max); // e.g. DmxValue.ByteCount, which Equals normalises
            return;
        }
        if (a is Array arrayA && type.GetElementType()!.IsValueType)
        {
            // Arrays of values (offsets, raw matrices) are values: shared or not, only their content counts.
            var arrayB = (Array)b;
            if (arrayA.Length != arrayB.Length) diffs.Add($"{path}: {arrayA.Length} vs {arrayB.Length} items");
            else
                for (int i = 0; i < arrayA.Length; i++)
                    if (!Equals(arrayA.GetValue(i), arrayB.GetValue(i))) diffs.Add($"{path}[{i}]: {arrayA.GetValue(i)} vs {arrayB.GetValue(i)}");
            return;
        }
        if (map.TryGetValue(a, out var paired))
        {
            if (!ReferenceEquals(paired, b)) diffs.Add($"{path}: links to a different object ({Show(a)})");
            return;
        }
        map[a] = b;

        if (a is IDictionary da)
        {
            var db = (IDictionary)b;
            if (da.Count != db.Count) diffs.Add($"{path}: {da.Count} vs {db.Count} entries");
            foreach (DictionaryEntry e in da)
            {
                if (!db.Contains(e.Key)) diffs.Add($"{path}[{e.Key}]: missing");
                else Walk(e.Value, db[e.Key], $"{path}[{e.Key}]", map, diffs, max);
            }
            return;
        }
        if (a is IEnumerable ea)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = ((IEnumerable)b).Cast<object?>().ToList();
            if (la.Count != lb.Count) diffs.Add($"{path}: {la.Count} vs {lb.Count} items");
            for (int i = 0; i < System.Math.Min(la.Count, lb.Count); i++) Walk(la[i], lb[i], $"{path}[{i}]", map, diffs, max);
            return;
        }
        foreach (var (name, get) in Getters.GetOrAdd(type, Build))
            Walk(get(a), get(b), $"{path}.{name}", map, diffs, max);
    }

    private static (string, Func<object, object?>)[] Build(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p =>
            {
                var o = Expression.Parameter(typeof(object));
                var body = Expression.Convert(Expression.Property(Expression.Convert(o, type), p), typeof(object));
                return (p.Name, Expression.Lambda<Func<object, object?>>(body, o).Compile());
            })
            .ToArray();

    private static string Show(object? o) => o switch
    {
        null => "null",
        string s => $"\"{(s.Length > 60 ? s[..60] + "…" : s)}\"",
        _ => o.ToString() ?? o.GetType().Name,
    };
}
