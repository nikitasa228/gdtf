using System.Diagnostics;
using System.Text;
using System.Xml;

namespace Candela.Gdtf.Tests;

/// <summary>
/// Pathological descriptions (very deep or very large trees, huge values, reference explosions): reading must stay
/// roughly linear, keep what it can and report the rest as warnings instead of hanging, overflowing the stack or
/// exhausting memory.
/// </summary>
public sealed class HostileInputTests
{
    /// <summary>Generous bound for work that takes milliseconds when linear (CI machines may be heavily loaded).</summary>
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(5);

    private static string Fixture(string geometries, string dmxModes = "", string models = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="Hostile" Manufacturer="Test">
            <Models>{models}</Models>
            <Geometries>{geometries}</Geometries>
            <DMXModes>{dmxModes}</DMXModes>
          </FixtureType>
        </GDTF>
        """;

    private static string Nested(string element, int depth)
    {
        var sb = new StringBuilder(depth * 40);
        for (int i = 0; i < depth; i++) sb.Append('<').Append(element).Append(" Name=\"g").Append(i).Append("\">");
        for (int i = 0; i < depth; i++) sb.Append("</").Append(element).Append('>');
        return sb.ToString();
    }

    private static int TreeDepth(GdtfGeometry g)
    {
        int depth = 0;
        for (var n = g; n.Children.Count > 0; n = n.Children[0]) depth++;
        return depth;
    }

    [Fact]
    public void Thirty_thousand_nested_geometries_load_quickly_and_are_cut_with_a_warning()
    {
        string xml = Fixture(Nested("Geometry", 30_000), """
            <DMXMode Name="M" Geometry="g0"><DMXChannels>
              <DMXChannel Offset="1" Geometry="g200"><LogicalChannel Attribute="Dimmer"><ChannelFunction Name="D"/></LogicalChannel></DMXChannel>
            </DMXChannels></DMXMode>
            """);
        var sw = Stopwatch.StartNew();
        var ft = TestData.ReadXml(xml);
        var geometry = new FixtureGeometryBuilder(ft).Build();
        sw.Stop();

        Assert.True(sw.Elapsed < Fast, $"Reading took {sw.Elapsed}.");
        var root = Assert.Single(ft.Geometries);
        int depth = TreeDepth(root);
        Assert.InRange(depth, 200, GdtfReader.MaxXmlDepth);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("nested deeper than", StringComparison.Ordinal));
        Assert.Equal(depth + 1, geometry.Parts.Count);
        // The channel's geometry is inside the kept part of the tree and resolves.
        Assert.Equal("g200", ft.DmxModes[0].Channels[0].Geometry?.Name);
        Assert.All(ft.Warnings, w => Assert.True(w.Path.Length <= 2 * WarningSink.MaxPathLength, w.Path));
    }

    [Fact]
    public void Deep_nesting_outside_the_geometry_tree_is_skipped_by_the_xml_reader()
    {
        string xml = Fixture("<Geometry Name=\"Body\"/>").Replace("<Models>", Nested("Mystery", 100_000) + "<Models>", StringComparison.Ordinal);
        var sw = Stopwatch.StartNew();
        var ft = TestData.ReadXml(xml);
        Assert.True(sw.Elapsed < Fast, $"Reading took {sw.Elapsed}.");
        Assert.Equal("Body", Assert.Single(ft.Geometries).Name);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("nested deeper than", StringComparison.Ordinal));
    }

    [Fact]
    public void Limited_reader_skips_deep_elements_and_truncates_after_the_element_budget()
    {
        const string xml = "<a><b><c><d/></c></b><b2/><b3><c3/></b3><b4/></a>";
        using (var r = new LimitedXmlReader(XmlReader.Create(new StringReader(xml)), maxDepth: 1, maxNodes: 100))
        {
            var doc = System.Xml.Linq.XDocument.Load(r);
            Assert.Equal(new[] { "a", "b", "b2", "b3", "b4" }, doc.Descendants().Select(e => e.Name.LocalName));
            Assert.Equal(2, r.SkippedElements); // <c> (with <d>) and <c3>
            Assert.False(r.Truncated);
        }
        using (var r = new LimitedXmlReader(XmlReader.Create(new StringReader(xml)), maxDepth: 10, maxNodes: 3))
        {
            var doc = System.Xml.Linq.XDocument.Load(r);
            Assert.True(r.Truncated);
            Assert.Equal(3, doc.Descendants().Count()); // a, b, c: the document ends where the budget ran out
        }
    }

    [Fact]
    public void Attributes_count_against_the_node_budget()
    {
        const string xml = "<a x='1' y='2' z='3'><b p='1' q='2'/><c/></a>";
        using var r = new LimitedXmlReader(XmlReader.Create(new StringReader(xml)), maxDepth: 10, maxNodes: 6);
        var doc = System.Xml.Linq.XDocument.Load(r);
        Assert.True(r.Truncated); // a with 3 attributes (4) + b with 2 (3) = 7 > 6: cut before b
        Assert.Equal("a", Assert.Single(doc.Descendants()).Name.LocalName);
    }

    [Fact]
    public void Oversized_descriptions_are_rejected_without_decompressing_them()
    {
        var bytes = TestData.Zip(("description.xml", new byte[GdtfReader.MaxDescriptionBytes + 1]));
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<GdtfFormatException>(() => GdtfReader.Read(new MemoryStream(bytes), "huge.gdtf"));
        Assert.Contains("not a fixture description", e.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < Fast);
    }

    [Fact]
    public void Wide_trees_with_thousands_of_geometries_channels_and_references_stay_linear()
    {
        const int pixels = 4000;
        var geometries = new StringBuilder("<Geometry Name=\"Body\">");
        for (int i = 0; i < pixels; i++)
            geometries.Append($"<GeometryReference Name=\"P{i}\" Geometry=\"Pixel\"><Break DMXBreak=\"1\" DMXOffset=\"{i * 3 + 1}\"/></GeometryReference>");
        for (int i = 0; i < pixels; i++) geometries.Append($"<Geometry Name=\"Fixed{i}\"/>");
        geometries.Append("</Geometry><Geometry Name=\"Pixel\"><Beam Name=\"PixelBeam\"/></Geometry>");
        var channels = new StringBuilder();
        foreach (var c in new[] { "R", "G", "B" })
            channels.Append($"<DMXChannel Offset=\"{"RGB".IndexOf(c, StringComparison.Ordinal) + 1}\" Geometry=\"PixelBeam\"><LogicalChannel Attribute=\"ColorAdd_{c}\"><ChannelFunction Name=\"{c}\"/></LogicalChannel></DMXChannel>");
        for (int i = 0; i < pixels; i++)
            channels.Append($"<DMXChannel Offset=\"{3 * pixels + i + 1}\" Geometry=\"Fixed{i}\"><LogicalChannel Attribute=\"Dimmer\"><ChannelFunction Name=\"D\"/></LogicalChannel></DMXChannel>");

        var sw = Stopwatch.StartNew();
        var ft = TestData.ReadXml(Fixture(geometries.ToString(), $"<DMXMode Name=\"M\" Geometry=\"Body\"><DMXChannels>{channels}</DMXChannels></DMXMode>"));
        var parts = new FixtureGeometryBuilder(ft).Build();
        sw.Stop();

        Assert.True(sw.Elapsed < Fast, $"Reading took {sw.Elapsed}.");
        var mode = ft.DmxModes.Single();
        Assert.Equal(3 * pixels + pixels, mode.ChannelInstances.Count);
        Assert.Equal(4 * pixels, mode.Footprint);
        Assert.Equal(1 + 2 * pixels + pixels, parts.Parts.Count); // body, pixel instances with their beams, fixed parts
    }

    [Fact]
    public void Reference_explosions_are_capped_without_a_flood_of_warnings()
    {
        // Ten levels of ten references each would be 10^10 instances.
        var g = new StringBuilder();
        for (int level = 0; level < 10; level++)
        {
            g.Append($"<Geometry Name=\"L{level}\">");
            for (int i = 0; i < 10; i++)
                g.Append($"<GeometryReference Name=\"L{level}R{i}\" Geometry=\"L{level + 1}\"><Break DMXBreak=\"1\" DMXOffset=\"{i + 1}\"/></GeometryReference>");
            g.Append("</Geometry>");
        }
        g.Append("<Geometry Name=\"L10\"><Beam Name=\"Leaf\"/></Geometry>");
        string modes = """
            <DMXMode Name="M" Geometry="L0"><DMXChannels>
              <DMXChannel Offset="1" Geometry="Leaf"><LogicalChannel Attribute="Dimmer"><ChannelFunction Name="D"/></LogicalChannel></DMXChannel>
            </DMXChannels></DMXMode>
            """;
        var sw = Stopwatch.StartNew();
        var ft = TestData.ReadXml(Fixture(g.ToString(), modes));
        var parts = new FixtureGeometryBuilder(ft).Build();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"Reading took {sw.Elapsed}.");
        var mode = ft.DmxModes.Single();
        Assert.InRange(mode.ChannelInstances.Count, 1, DmxLayout.MaxChains);
        Assert.Single(ft.Warnings, w => w.Message.Contains("geometry reference instances", StringComparison.Ordinal));
        Assert.InRange(parts.Parts.Count, 1, FixtureGeometryBuilder.MaxParts + 1);
        Assert.Single(parts.Warnings, w => w.Message.Contains("parts", StringComparison.Ordinal));
    }

    [Fact]
    public void Huge_names_and_values_produce_bounded_warnings()
    {
        string huge = new('x', 2_000_000);
        var ft = TestData.ReadXml(Fixture($"<Geometry Name=\"{huge}\" Model=\"{huge}\" Position=\"{huge}\"/>",
            $"<DMXMode Name=\"{huge}\" Geometry=\"{huge}y\"/>"));
        Assert.NotEmpty(ft.Warnings);
        Assert.All(ft.Warnings, w =>
        {
            Assert.True(w.Path.Length <= WarningSink.MaxPathLength, $"path of {w.Path.Length} chars");
            Assert.True(w.Message.Length <= WarningSink.MaxMessageLength, $"message of {w.Message.Length} chars");
        });
    }

    [Fact]
    public void Warning_floods_are_capped()
    {
        var channels = new StringBuilder();
        for (int i = 0; i < 5000; i++)
            channels.Append($"<DMXChannel Offset=\"{i + 1}\" Geometry=\"Nope{i}\"><LogicalChannel Attribute=\"Nope{i}\"><ChannelFunction Name=\"F\" Wheel=\"W{i}\"/></LogicalChannel></DMXChannel>");
        var ft = TestData.ReadXml(Fixture("<Geometry Name=\"Body\"/>", $"<DMXMode Name=\"M\" Geometry=\"Body\"><DMXChannels>{channels}</DMXChannels></DMXMode>"));
        Assert.InRange(ft.Warnings.Count, 100, 501);
        Assert.Contains("further warning(s) suppressed", ft.Warnings[^1].Message, StringComparison.Ordinal);
        Assert.Equal(5000, ft.DmxModes[0].Channels.Count);
    }

    [Fact]
    public void Entity_expansion_is_not_performed()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">]>
            <GDTF DataVersion="1.2"><FixtureType Name="&lol3;"/></GDTF>
            """;
        // Either the DTD is ignored (undeclared entity: not well formed) or the name stays unexpanded; never 100 "lol"s.
        try
        {
            var ft = TestData.ReadXml(xml);
            Assert.True(ft.Name.Length < 100);
        }
        catch (GdtfFormatException)
        {
        }
    }
}
