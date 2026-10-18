using Candela.Core.Diagnostics;
using Candela.Engine;

namespace Candela.Cli;

/// <summary>Options of <c>candela validate</c>.</summary>
internal sealed class ValidateOptions
{
    public string Scene { get; private set; } = "";
    public bool Plot { get; private set; }
    public List<string> Libraries { get; } = new();

    public static ValidateOptions Parse(string[] args)
    {
        var o = new ValidateOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (name == "--library") o.Libraries.Add(CliArgs.Value(args, ref i, name));
            else if (name == "--plot") o.Plot = true;
            else if (name.StartsWith('-')) throw new UsageException($"Unknown option '{name}'.");
            else if (o.Scene.Length > 0) throw new UsageException("validate takes one scene.");
            else o.Scene = name;
        }
        if (o.Scene.Length == 0) throw new UsageException("validate needs a scene file (.mvr or .candela).");
        return o;
    }
}

/// <summary>
/// <c>candela validate</c>: loads a scene and prints its issue list (patch, power and data networks, rigging, assets,
/// import notes), grouped by category. Exit code 4 when there are error-level issues.
/// </summary>
internal static class ValidateCommand
{
    public static int Run(ValidateOptions options, TextWriter output, TextWriter error)
    {
        using var engine = new StageEngine(CliArgs.EngineOptions(options.Libraries, null));
        var opened = engine.Open(options.Scene);
        output.WriteLine(opened.Summary);
        if (!opened.Success)
        {
            RenderCommand.PrintIssues(error, opened.Issues, 20);
            return Program.ExitFailure;
        }
        engine.Tick(0);
        engine.RefreshIssues();
        var issues = engine.Issues.Current;
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        output.WriteLine($"{issues.Count} issues: {errors} errors, {warnings} warnings, {issues.Count - errors - warnings} notes");
        foreach (var group in issues.GroupBy(i => i.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine();
            output.WriteLine($"{group.Key}:");
            foreach (var issue in group.OrderByDescending(i => i.Severity).ThenBy(i => i.Code, StringComparer.Ordinal))
            {
                string where = issue.Entities.Count == 0
                    ? ""
                    : " [" + string.Join(", ", issue.Entities.Take(4).Select(id => engine.Scene.Find(id)?.Name ?? id.ToString())) +
                      (issue.Entities.Count > 4 ? $", +{issue.Entities.Count - 4}" : "") + "]";
                output.WriteLine($"  {RenderCommand.Severity(issue.Severity)} {issue.Code}: {issue.Message}{where}");
            }
        }
        if (options.Plot)
        {
            output.WriteLine();
            output.Write(engine.LightMeter.BuildLightPlot().ToText());
        }
        return errors > 0 ? Program.ExitInputErrors : Program.ExitOk;
    }
}
