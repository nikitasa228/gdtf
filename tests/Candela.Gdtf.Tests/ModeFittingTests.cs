namespace Candela.Gdtf.Tests;

/// <summary>
/// The one rule for the DMX mode of a stand-in GDTF (<see cref="FixtureType.FitModeToSpacing(DmxMode?, int?)"/>,
/// <see cref="DmxMode.FirstBreakFootprint"/>, <see cref="DmxPatchSpacing"/>), shared by the MVR importer and the DMX engine.
/// </summary>
public sealed class ModeFittingTests
{
    /// <summary>Ayrton MagicPanel 602: Extended 160, Standard 20, Basic (fewest) channels.</summary>
    private static FixtureType Panel() => GdtfReader.ReadFile(TestData.Fixture("Ayrton@MagicPanel_602@V2.5_Add_Mode"));

    [Fact]
    public void The_requested_mode_is_kept_when_it_fits_else_the_most_complete_mode_that_fits()
    {
        var panel = Panel();
        Assert.Equal(160, panel.FindMode("Extended")!.FirstBreakFootprint);
        Assert.Equal(20, panel.FindMode("Standard")!.FirstBreakFootprint);
        Assert.Equal("Standard", panel.FitModeToSpacing("Extended", 44)!.Name); // 20 of the 44 channels
        Assert.Equal("Extended", panel.FitModeToSpacing("Extended", 160)!.Name); // the requested one fits
        Assert.Equal("Basic", panel.FitModeToSpacing("Basic", 44)!.Name); // a requested mode that fits is kept
        Assert.Equal("Basic", panel.FitModeToSpacing("Extended", 19)!.Name);
        Assert.Equal("Standard", panel.FitModeToSpacing("extended ", 44)!.Name); // names as FindMode reads them
        Assert.Equal("Standard", panel.FitModeToSpacing((DmxMode?)null, 44)!.Name); // nothing requested: by spacing
    }

    [Fact]
    public void When_nothing_fits_the_smallest_mode_is_used_and_without_a_spacing_the_name_decides()
    {
        var panel = Panel();
        int smallest = panel.DmxModes.Min(m => m.FirstBreakFootprint);
        Assert.Equal(smallest, panel.FitModeToSpacing("Extended", 1)!.FirstBreakFootprint);
        Assert.Equal("Extended", panel.FitModeToSpacing("Extended", null)!.Name);
        Assert.Equal("Extended", panel.FitModeToSpacing("Extended", 0)!.Name); // no usable spacing
        Assert.Same(panel.DmxModes[0], panel.FitModeToSpacing("No such mode", null));
        Assert.Same(panel.DmxModes[0], panel.FitModeToSpacing("", null));
        Assert.Null(new FixtureType().FitModeToSpacing("Any", 10));
    }

    [Fact]
    public void Ties_keep_the_gdtf_mode_order_and_a_mode_without_layout_falls_back_to_its_declared_channels()
    {
        var type = new FixtureType();
        var a = new DmxMode { Name = "A", BreakFootprints = new Dictionary<int, int> { [1] = 12 } };
        var b = new DmxMode { Name = "B", BreakFootprints = new Dictionary<int, int> { [1] = 12 } };
        var c = new DmxMode { Name = "C", BreakFootprints = new Dictionary<int, int> { [2] = 4, [3] = 0 } };
        type.DmxModes.AddRange([a, b, c]);
        type.InvalidateLookups();
        Assert.Same(a, type.FitModeToSpacing("Nope", 12));
        Assert.Same(c, type.FitModeToSpacing("Nope", 11));
        Assert.Equal(4, c.FirstBreakFootprint); // the lowest break that uses slots
        Assert.Equal(0, new DmxMode().FirstBreakFootprint); // no channels at all
    }

    [Fact]
    public void Patch_spacing_is_the_smallest_gap_within_a_universe()
    {
        Assert.Equal(44, DmxPatchSpacing.Infer([89, 1, 45, 45, 600]));
        Assert.Equal(3, DmxPatchSpacing.Infer([510, 513, 516])); // 510 is universe 1, 513 and 516 universe 2
        Assert.Null(DmxPatchSpacing.Infer([1, 513, 1025])); // one fixture per universe
        Assert.Null(DmxPatchSpacing.Infer([10, 10])); // the same address twice says nothing
        Assert.Null(DmxPatchSpacing.Infer([0, -5, 7]));
        Assert.Null(DmxPatchSpacing.Infer([]));
    }
}
