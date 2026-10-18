using Xunit.Abstractions;

namespace Candela.Mvr.Tests;

public sealed class SmokeTests
{
    private readonly ITestOutputHelper _out;

    public SmokeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Smoke()
    {
        var r = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library });
        _out.WriteLine($"entities {r.Scene.Count}, roots {r.RootEntities.Count}, layers {r.Scene.Layers.Count}");
        foreach (var g in r.Gdtfs) _out.WriteLine(g.Describe());
        foreach (var g in r.Gdtfs) foreach (var m in g.Modes) _out.WriteLine($"  {m}");
        foreach (var i in r.Issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");
    }
}
