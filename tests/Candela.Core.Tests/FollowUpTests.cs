using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Core.Tests;

/// <summary>Follow-ups of the Core review: linear issue reporting, program messages cleared at once, deep JSON.</summary>
public class FollowUpTests
{
    private sealed class FixedProvider(params Issue[] issues) : IIssueProvider
    {
        public string Name => "fixed";

        public IEnumerable<Issue> Analyze(Scene scene) => issues;
    }

    private static Issue Program(string code) => new(IssueSeverity.Warning, IssueCategory.Program, code, code);

    [Fact]
    public void Reporting_is_linear_and_every_report_publishes_a_new_snapshot()
    {
        var log = new IssueLog();
        int changes = 0;
        log.Changed += () => changes++;
        var first = log.Current;
        log.Report(Program("a"));
        var afterOne = log.Current;
        Assert.NotSame(first, afterOne);
        Assert.Empty(first);
        Assert.Single(afterOne);

        // 50 000 reports: quadratic copying would move 1.25·10⁹ references; linear work finishes in milliseconds
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 50_000; i++) log.Report(Program("p" + i));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(50_001, log.Current.Count);
        Assert.Equal(50_001, changes);

        // older snapshots keep their content although they share storage with newer ones
        Assert.Single(afterOne);
        Assert.Equal("a", afterOne[0].Code);
        Assert.Equal("p49999", log.Current[^1].Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => afterOne[1]);
        Assert.Equal(log.Current.Count, log.Current.Count());
    }

    [Fact]
    public void Clearing_program_messages_rebuilds_the_current_list_at_once()
    {
        var log = new IssueLog();
        var analyzed = new Issue(IssueSeverity.Error, IssueCategory.Power, "power.overload", "overload");
        log.Register(new FixedProvider(analyzed));
        log.Report(Program("before-refresh"));
        log.Refresh(new Scene());
        log.Report(Program("after-refresh"));
        Assert.Equal(new[] { "power.overload", "before-refresh", "after-refresh" }, log.Current.Select(i => i.Code));

        var beforeClear = log.Current;
        log.ClearProgramMessages();
        Assert.NotSame(beforeClear, log.Current);
        Assert.Equal(new[] { "power.overload" }, log.Current.Select(i => i.Code));
        Assert.Equal(3, beforeClear.Count); // a snapshot never changes

        // a refresh after the clear does not bring them back
        log.Refresh(new Scene());
        Assert.Equal(new[] { "power.overload" }, log.Current.Select(i => i.Code));
    }

    [Fact]
    public void Refresh_sorts_by_severity_then_category_and_keeps_report_order_within()
    {
        var log = new IssueLog();
        log.Register(new FixedProvider(
            new Issue(IssueSeverity.Note, IssueCategory.Scene, "n", "n"),
            new Issue(IssueSeverity.Error, IssueCategory.Rigging, "r", "r"),
            new Issue(IssueSeverity.Error, IssueCategory.Data, "d", "d")));
        log.Report(Program("w1"));
        log.Report(Program("w2"));
        log.Refresh(new Scene());
        Assert.Equal(new[] { "d", "r", "w1", "w2", "n" }, log.Current.Select(i => i.Code));
    }

    private static string Nested(int levels, string innermost = "1")
    {
        var sb = new StringBuilder();
        for (int i = 0; i < levels; i++) sb.Append("{\"x\":");
        sb.Append(innermost);
        for (int i = 0; i < levels; i++) sb.Append('}');
        return sb.ToString();
    }

    private static string SceneWithComponentData(string data) =>
        "{\"formatVersion\":1,\"name\":\"Deep\",\"entities\":[{\"id\":\"" + Guid.NewGuid() +
        "\",\"name\":\"Wall\",\"components\":[{\"$type\":\"wall\",\"width\":3,\"future\":" + data + "}]}]}";

    [Fact]
    public void Scenes_with_data_nested_far_beyond_64_levels_load_and_round_trip()
    {
        // 200 levels inside a component (5 levels deep in the file): System.Text.Json's default limit of 64 refused it
        string json = SceneWithComponentData(Nested(200));
        var issues = new List<Issue>();
        var scene = SceneSerializer.FromJson(json, issues);
        Assert.Empty(issues);
        var wall = scene.Roots.Single().Get<WallComponent>()!;
        Assert.Equal(3f, wall.Width);
        var future = wall.UnknownProperties!["future"];
        int depth = 0;
        while (future.ValueKind == JsonValueKind.Object)
        {
            future = future.GetProperty("x");
            depth++;
        }
        Assert.Equal(200, depth);

        // written back and read again: nothing lost
        var again = SceneSerializer.FromJson(SceneSerializer.ToJson(scene), issues);
        Assert.Empty(issues);
        Assert.Equal(future.GetRawText(), Innermost(again.Roots.Single().Get<WallComponent>()!.UnknownProperties!["future"]).GetRawText());
    }

    private static JsonElement Innermost(JsonElement element)
    {
        while (element.ValueKind == JsonValueKind.Object) element = element.GetProperty("x");
        return element;
    }

    [Fact]
    public void Text_nested_deeper_than_the_limit_is_refused_with_a_clear_error_and_syntax_errors_stay_json_errors()
    {
        var tooDeep = Assert.Throws<InvalidDataException>(() => SceneSerializer.FromJson(SceneWithComponentData(Nested(SceneSerializer.MaxDepth + 10))));
        Assert.Contains(SceneSerializer.MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture), tooDeep.Message, StringComparison.Ordinal);
        Assert.Contains("levels deep", tooDeep.Message, StringComparison.Ordinal);

        // a hostile file a million levels deep is refused just the same (no stack overflow, no hang)
        Assert.Throws<InvalidDataException>(() => SceneSerializer.FromJson(new string('[', 1_000_000)));

        // exactly at the limit: accepted
        string atLimit = new string('[', SceneSerializer.MaxDepth - 1) + new string(']', SceneSerializer.MaxDepth - 1);
        Assert.Throws<InvalidDataException>(() => SceneSerializer.FromJson(atLimit)); // the root is not an object
        string objectsAtLimit = Nested(SceneSerializer.MaxDepth - 1, "{}");
        var scene = SceneSerializer.FromJson(objectsAtLimit);
        Assert.Empty(scene.Roots);

        // broken JSON keeps throwing JsonException
        Assert.ThrowsAny<JsonException>(() => SceneSerializer.FromJson("{\"name\": \"x\", "));
        Assert.ThrowsAny<JsonException>(() => SceneSerializer.FromJson("{\"name\": [1, 2 }"));
    }
}
