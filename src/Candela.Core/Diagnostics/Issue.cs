using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.Core.Diagnostics;

public enum IssueSeverity
{
    /// <summary>A remark (e.g. a value is estimated).</summary>
    Note,

    /// <summary>Probably wrong, but the scene works (e.g. DMX chain longer than recommended).</summary>
    Warning,

    /// <summary>Physically impossible or unsafe (overloaded circuit, unpowered fixture, truss overload).</summary>
    Error,
}

/// <summary>Area an issue belongs to; used for filtering in the issues panel.</summary>
public static class IssueCategory
{
    public const string Rigging = "Rigging";
    public const string Power = "Power";
    public const string Data = "Data";
    public const string Patch = "Patch";
    public const string Scene = "Scene";
    public const string Fixture = "Fixture";
    public const string Program = "Program";
}

/// <summary>
/// One entry of the issues panel. <see cref="Code"/> is a stable machine id ("power.overload") so the UI can
/// filter/silence; <see cref="Entities"/> are selected when the user clicks the issue.
/// </summary>
public sealed record Issue(IssueSeverity Severity, string Category, string Code, string Message, IReadOnlyList<Guid> Entities)
{
    public Issue(IssueSeverity severity, string category, string code, string message, params Guid[] entities)
        : this(severity, category, code, message, (IReadOnlyList<Guid>)entities) { }
}

/// <summary>A source of issues: rigging checks, power and data network checks, patch checks, program errors…</summary>
public interface IIssueProvider
{
    string Name { get; }

    IEnumerable<Issue> Analyze(SceneModel scene);
}

/// <summary>Collects issues from all providers plus program-level messages (import failures, missing GDTF files…).</summary>
public sealed class IssueLog
{
    private readonly List<IIssueProvider> _providers = new();
    private readonly List<Issue> _program = new();
    private List<Issue> _current = new();

    public IReadOnlyList<Issue> Current => _current;
    public event Action? Changed;

    public void Register(IIssueProvider provider) => _providers.Add(provider);

    /// <summary>Adds a program message (kept until <see cref="ClearProgramMessages"/>).</summary>
    public void Report(Issue issue)
    {
        _program.Add(issue);
        _current = _current.Append(issue).ToList();
        Changed?.Invoke();
    }

    public void ClearProgramMessages()
    {
        _program.Clear();
        Changed?.Invoke();
    }

    /// <summary>Re-runs every provider. A provider that throws becomes a Program error instead of breaking the panel.</summary>
    public void Refresh(SceneModel scene)
    {
        var all = new List<Issue>(_program);
        foreach (var p in _providers)
        {
            try
            {
                all.AddRange(p.Analyze(scene));
            }
            catch (Exception e)
            {
                all.Add(new Issue(IssueSeverity.Error, IssueCategory.Program, "program.analyzer-failed", $"{p.Name} failed: {e.Message}"));
            }
        }
        _current = all.OrderByDescending(i => i.Severity).ThenBy(i => i.Category).ToList();
        Changed?.Invoke();
    }
}
