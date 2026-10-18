using System.Globalization;
using Candela.Core.Diagnostics;

namespace Candela.Project.Internal;

/// <summary>
/// Collects issues with a cap per code: a file with ten thousand broken elements produces a readable list (the first
/// <see cref="MaxPerCode"/> problems of each kind, then one summary per kind) instead of ten thousand lines.
/// </summary>
internal sealed class IssueCollector
{
    private readonly List<Issue> _issues = new();
    private readonly Dictionary<string, (int Count, IssueSeverity Severity, string Category)> _counts = new(StringComparer.Ordinal);

    public IssueCollector(int maxPerCode = 50)
    {
        MaxPerCode = System.Math.Max(1, maxPerCode);
    }

    public int MaxPerCode { get; }

    public int Count => _issues.Count;

    public void Add(IssueSeverity severity, string category, string code, string message, params Guid[] entities) =>
        Add(new Issue(severity, category, code, message, (IReadOnlyList<Guid>)entities));

    public void Add(IssueSeverity severity, string category, string code, string message, IReadOnlyList<Guid> entities) =>
        Add(new Issue(severity, category, code, message, entities));

    public void Add(Issue issue)
    {
        _counts.TryGetValue(issue.Code, out var c);
        var severity = c.Count == 0 || issue.Severity > c.Severity ? issue.Severity : c.Severity;
        _counts[issue.Code] = (c.Count + 1, severity, issue.Category);
        if (c.Count < MaxPerCode) _issues.Add(issue);
    }

    public void AddRange(IEnumerable<Issue> issues)
    {
        foreach (var i in issues) Add(i);
    }

    /// <summary>The collected issues followed by one summary per code that exceeded the cap.</summary>
    public IReadOnlyList<Issue> ToList()
    {
        var list = new List<Issue>(_issues);
        foreach (var (code, c) in _counts)
        {
            if (c.Count <= MaxPerCode) continue;
            list.Add(new Issue(c.Severity, c.Category, ProjectIssueCodes.MoreIssues,
                string.Create(CultureInfo.InvariantCulture, $"{c.Count - MaxPerCode} more problems of the same kind ({code}) are not listed."),
                Array.Empty<Guid>()));
        }
        return list;
    }
}
