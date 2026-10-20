namespace Candela.Dmx.Tests;

/// <summary>Channel-set and wheel-slot names that decide whether a mechanism is in the beam (names taken from library files).</summary>
public class NameParsingTests
{
    [Theory]
    [InlineData("Open")]
    [InlineData("open")]
    [InlineData("OPEN")]
    [InlineData("Open/Hole")]
    [InlineData("No prism")]
    [InlineData("No Prism(Open)")]
    [InlineData("Off")]
    [InlineData("Removed")]
    [InlineData("Close")]
    [InlineData("Closed")]
    [InlineData("No function")]
    [InlineData("No Effect")]
    [InlineData("No Flower Effect")]
    [InlineData("Animation Rotation OFF")]
    [InlineData("Wheel Slot 0 OFF")]
    [InlineData("Animator OPEN")]
    [InlineData("Empty")]
    [InlineData("No color")]
    [InlineData("No Colour")]
    [InlineData("No Color 1")]
    public void Out_of_beam_names(string name) => Assert.True(NameParsing.IsWheelOutName(name), name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Gobo 1")]
    [InlineData("Open to Dots")]
    [InlineData("Open > Close")]
    [InlineData("Open -> In")]
    [InlineData("Red/Open")]
    [InlineData("Half open")]
    [InlineData("No rotation")]
    [InlineData("No shake")]
    [InlineData("Shake off")]
    [InlineData("Strobe off")]
    [InlineData("Animation 1")]
    [InlineData("Beam Shaper")]
    // Roscolux "No Color …" gels on colour wheels and virtual colour wheels (Astera, Chauvet COLOrado, CLF, Ayrton Zonda)
    [InlineData("No Colour Blue")]
    [InlineData("No Color Pink")]
    [InlineData("NO COLOR STRAW")]
    [InlineData("No color Blue L144")]
    public void In_beam_or_neutral_names(string? name) => Assert.False(NameParsing.IsWheelOutName(name), name ?? "null");

    [Theory]
    [InlineData("Prism", true)]
    [InlineData("Prism 2", true)]
    [InlineData("Prism Insert", true)]
    [InlineData("Prism On", true)]
    [InlineData("Both Prisms", true)]
    [InlineData("6 facet linear", true)]
    [InlineData("Open prism", false)]
    [InlineData("No prism", false)]
    [InlineData("Prism 1 OPEN", false)]
    [InlineData("Open", false)]
    [InlineData("Frost", false)]
    [InlineData(null, false)]
    public void Prism_slot_names(string? name, bool prism) => Assert.Equal(prism, NameParsing.NamesPrism(name));

    [Theory]
    [InlineData("Open", true)]
    [InlineData("Open/Hole", true)]
    [InlineData("Open position", true)]
    [InlineData("Open (2)", true)]
    [InlineData("Hole", true)]
    [InlineData("No gobo", true)]
    [InlineData("white", true)]
    [InlineData("Open 2", false)] // ACME Ares: a reducer aperture with an image
    [InlineData("Open+Red", false)]
    [InlineData("Open/Close 1", false)] // Flash shape wheel
    [InlineData("Gobo 3", false)]
    [InlineData(null, false)]
    public void Plain_open_slot_names(string? name, bool expected) => Assert.Equal(expected, NameParsing.IsPlainOpenSlotName(name));

    [Theory]
    [InlineData("", true)] // Cameo Oron H2: the unnamed range between "Open" and "Animation In"
    [InlineData(null, true)]
    [InlineData("Open to Dots", true)]
    [InlineData("Open > In", true)]
    [InlineData("Insertion in proportion", true)]
    [InlineData("Animation In", false)]
    [InlineData("Dots to Open", false)]
    [InlineData("Gobo 1", false)]
    public void Insertion_ramp_names(string? name, bool expected) => Assert.Equal(expected, NameParsing.IsInsertionRampName(name));

    [Theory]
    [InlineData("Ramping from open to full position max", 0f, 1f, 1)]
    [InlineData("Ramping from open to half position min", 0f, 0.5f, -1)]
    [InlineData("Ramp from half position to full position", 0.5f, 1f, 0)]
    public void Insertion_cycles(string name, float from, float to, int speedEnd)
    {
        Assert.True(NameParsing.TryParseInsertionCycle(name, out float f, out float t, out int end));
        Assert.Equal(from, f);
        Assert.Equal(to, t);
        Assert.Equal(speedEnd, end);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ramping")]
    [InlineData("Ramp from open to open")]
    [InlineData("Ramp from dots to full")]
    [InlineData("Open to full")]
    public void Not_insertion_cycles(string? name) => Assert.False(NameParsing.TryParseInsertionCycle(name, out _, out _, out _));

    [Theory]
    [InlineData("Beam Shaper", NameParsing.DefaultShaperStretch)]
    [InlineData(null, NameParsing.DefaultShaperStretch)]
    [InlineData("Oval 2:1", 2f)]
    [InlineData("Shaper 3x1", 3f)]
    [InlineData("Lens 1:4", 4f)]
    [InlineData("Shaper x2.5", 2.5f)]
    [InlineData("Shaper 50:1", 10f)]
    [InlineData("Shaper 0:0", NameParsing.DefaultShaperStretch)]
    public void Shaper_stretch(string? name, float expected) => Assert.Equal(expected, NameParsing.ParseStretch(name), 1e-5f);
}
