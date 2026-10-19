using System.Collections;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.Core.Diagnostics;

/// <summary>How serious an <see cref="Issue"/> is; the issues panel sorts and filters by it (most severe first).</summary>
public enum IssueSeverity
{
    /// <summary>A remark (e.g. a value is estimated).</summary>
    Note,

    /// <summary>Probably wrong, but the scene works (e.g. DMX chain longer than recommended).</summary>
    Warning,

    /// <summary>Physically impossible or unsafe (overloaded circuit, unpowered fixture, truss overload).</summary>
    Error,
}

/// <summary>
/// Area an issue belongs to; used for filtering in the issues panel. The values are stable machine names (stored in
/// filters and compared ordinally), not display text: the editor translates them.
/// </summary>
public static class IssueCategory
{
    /// <summary>Trusses, rigging points and the loads they carry (overload, fixtures not mounted).</summary>
    public const string Rigging = "Rigging";

    /// <summary>Power distribution: sources, distros, circuits and the loads on them (overload, unpowered fixture).</summary>
    public const string Power = "Power";

    /// <summary>Control data networks: DMX/Ethernet cabling from consoles to fixtures (no signal, chain too long, Y-split).</summary>
    public const string Data = "Data";

    /// <summary>DMX patch: universes and addresses of fixtures (overlaps, slots past 512, stand-in GDTFs).</summary>
    public const string Patch = "Patch";

    /// <summary>Scene content: files, models, images and entities (damaged elements repaired on load, missing assets).</summary>
    public const string Scene = "Scene";

    /// <summary>Fixture types: GDTF files, modes and their physical data (missing or substituted GDTF, unknown mode).</summary>
    public const string Fixture = "Fixture";

    /// <summary>The program itself: operations that failed (open, save, import), settings, libraries, analyzers.</summary>
    public const string Program = "Program";
}

/// <summary>
/// One entry of the issues panel. <see cref="Code"/> is a stable machine id ("power.overload") so the UI can
/// filter/silence; <see cref="Entities"/> are selected when the user clicks the issue.
/// </summary>
/// <param name="Severity">How serious the issue is.</param>
/// <param name="Category">Area of the issue (one of <see cref="IssueCategory"/>).</param>
/// <param name="Code">Stable machine id of the kind of issue ("power.overload").</param>
/// <param name="Message">One line for the issues panel, in English.</param>
/// <param name="Entities">Entities the issue is about (selected when the user clicks it); may be empty.</param>
public sealed record Issue(IssueSeverity Severity, string Category, string Code, string Message, IReadOnlyList<Guid> Entities)
{
    /// <summary>Creates an issue about any number of entities.</summary>
    /// <param name="severity">How serious the issue is.</param>
    /// <param name="category">Area of the issue (one of <see cref="IssueCategory"/>).</param>
    /// <param name="code">Stable machine id of the kind of issue.</param>
    /// <param name="message">One line for the issues panel, in English.</param>
    /// <param name="entities">Entities the issue is about.</param>
    public Issue(IssueSeverity severity, string category, string code, string message, params Guid[] entities)
        : this(severity, category, code, message, (IReadOnlyList<Guid>)entities) { }
}

/// <summary>A source of issues: rigging checks, power and data network checks, patch checks, program errors…</summary>
public interface IIssueProvider
{
    /// <summary>Name of the analysis (shown when the provider itself fails).</summary>
    string Name { get; }

    /// <summary>
    /// Analyses <paramref name="scene"/> and returns its issues. Called on the main thread by
    /// <see cref="IssueLog.Refresh"/> (after edits, not every frame); an exception becomes a Program error.
    /// </summary>
    IEnumerable<Issue> Analyze(SceneModel scene);
}

/// <summary>
/// Collects issues from all providers plus program-level messages (import failures, missing GDTF files…). Main thread.
/// </summary>
/// <remarks>
/// <see cref="Current"/> is an immutable snapshot: every change (<see cref="Report"/>, <see cref="Refresh"/>,
/// <see cref="ClearProgramMessages"/>) publishes a new list object, so a view can detect a change by reference
/// (<c>ReferenceEquals(log.Current, seen)</c>) and keep iterating an old snapshot safely. <see cref="Report"/> costs
/// O(1): the snapshots share one append-only list and differ in their length.
/// </remarks>
public sealed class IssueLog
{
    private readonly List<IIssueProvider> _providers = new();
    private readonly List<Issue> _program = new();
    private List<Issue> _analyzed = new();
    private List<Issue> _items = new();
    private Snapshot _current = Snapshot.Empty;

    /// <summary>
    /// Every issue: the providers' results and program messages as of the last <see cref="Refresh"/> (most severe first,
    /// then by category), followed by the program messages reported since. A new object after every change.
    /// </summary>
    public IReadOnlyList<Issue> Current => _current;

    /// <summary>Raised on the main thread after <see cref="Current"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Adds an analysis that <see cref="Refresh"/> runs.</summary>
    public void Register(IIssueProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers.Add(provider);
    }

    /// <summary>Adds a program message (kept until <see cref="ClearProgramMessages"/>); amortised O(1).</summary>
    public void Report(Issue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        _program.Add(issue);
        // Snapshots share the append-only list; an older snapshot keeps its own length, so it never sees this entry.
        _items.Add(issue);
        _current = new Snapshot(_items, _items.Count);
        Changed?.Invoke();
    }

    /// <summary>
    /// Forgets every program message (a new file was opened). <see cref="Current"/> is rebuilt at once from the
    /// providers' results of the last <see cref="Refresh"/>, so the messages disappear without waiting for the next one.
    /// </summary>
    public void ClearProgramMessages()
    {
        _program.Clear();
        Publish(_analyzed);
        Changed?.Invoke();
    }

    /// <summary>Re-runs every provider. A provider that throws becomes a Program error instead of breaking the panel.</summary>
    public void Refresh(SceneModel scene)
    {
        var analyzed = new List<Issue>();
        foreach (var p in _providers)
        {
            try
            {
                analyzed.AddRange(p.Analyze(scene));
            }
            catch (Exception e)
            {
                analyzed.Add(new Issue(IssueSeverity.Error, IssueCategory.Program, "program.analyzer-failed", $"{p.Name} failed: {e.Message}"));
            }
        }
        _analyzed = analyzed;
        var all = new List<Issue>(_program.Count + analyzed.Count);
        all.AddRange(_program);
        all.AddRange(analyzed);
        Publish(all);
        Changed?.Invoke();
    }

    /// <summary>Sorts <paramref name="issues"/> (most severe first, then by category; stable) into a new backing list.</summary>
    private void Publish(List<Issue> issues)
    {
        var sorted = issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Category, StringComparer.Ordinal).ToList();
        _items = sorted;
        _current = new Snapshot(sorted, sorted.Count);
    }

    /// <summary>A read-only view of the first <see cref="Count"/> entries of an append-only list.</summary>
    private sealed class Snapshot(List<Issue> items, int count) : IReadOnlyList<Issue>
    {
        public static readonly Snapshot Empty = new(new List<Issue>(), 0);

        public int Count => count;

        public Issue this[int index] => (uint)index < (uint)count ? items[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<Issue> GetEnumerator()
        {
            for (int i = 0; i < count; i++) yield return items[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
