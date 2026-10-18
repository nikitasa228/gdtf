using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml;

namespace Candela.Electrical.Tests;

public class CatalogTests
{
    [Fact]
    public void Default_catalogue_has_the_stage_cable_types_with_sane_ratings()
    {
        var catalog = CableCatalog.Default;
        string[] ids =
        [
            "dmx-xlr3",
            "dmx-xlr5",
            "ethernet",
            "powercon",
            "powercon-true1",
            "powercon-true1-top",
            "schuko",
            "iec",
            "cee16",
            "cee32-3p",
            "cee63-3p",
            "socapex",
            "harting16",
        ];
        Assert.Equal(ids, catalog.All.Select(t => t.Id));
        foreach (var t in catalog.All)
        {
            Assert.True(t.ConnectorA.IsKnown && t.ConnectorB.IsKnown, t.Id);
            Assert.Matches("^#[0-9A-F]{6}$", t.ColorHex);
            Assert.False(string.IsNullOrWhiteSpace(t.IconKey));
            Assert.True(t.WeightKgPerM > 0 && t.ConductorCrossSectionMm2 > 0, t.Id);
            if (t.IsPower) Assert.True(t.RatedCurrentA > 0, t.Id);
        }
        Assert.Equal(catalog.All.Count, catalog.All.Select(t => t.ColorHex).Distinct().Count());
        Assert.Equal(300f, catalog.Find("DMX-XLR5")!.MaxRunM);
        Assert.Equal(100f, catalog.Find("ethernet")!.MaxRunM);
        Assert.Equal(16f, catalog.Find("powercon-true1")!.RatedCurrentA);
        Assert.Equal(20f, catalog.Find("powercon")!.RatedCurrentA);
        Assert.Equal(10f, catalog.Find("iec")!.RatedCurrentA);
        Assert.Equal(6, catalog.Find("socapex")!.Circuits);
        Assert.Equal(6, catalog.Find("harting16")!.Circuits);
        Assert.Equal(3, catalog.Find("cee63-3p")!.Phases);
        Assert.Equal(SignalKind.Multicore, catalog.Find("harting16")!.Signal);
        Assert.Null(catalog.Find("no-such-cable"));
        Assert.Throws<ArgumentException>(() => new CableCatalog([catalog.All[0], catalog.All[0]]));
    }

    [Fact]
    public void Every_specs_connector_value_resolves_to_a_catalogue_connector()
    {
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in TestData.Specs.All)
        {
            foreach (var text in new[] { r.PowerConnectorIn, r.PowerConnectorOut }.Concat(r.DataConnectors))
                foreach (var c in ConnectorCatalog.ResolveAll(text))
                    if (!c.IsKnown) unknown.Add(c.Id);
        }
        Assert.True(unknown.Count == 0, "Unknown specs connectors: " + string.Join(", ", unknown));
        Assert.Equal(1091, TestData.Specs.Count);
    }

    [Fact]
    public void Every_gdtf_connector_type_in_the_library_resolves()
    {
        var files = Directory.GetFiles(TestData.RepoRoot, "*.gdtf");
        Assert.Equal(1091, files.Length);
        var names = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        Parallel.ForEach(files, file =>
        {
            using var zip = ZipFile.OpenRead(file);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("description.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                string? type = reader.LocalName switch
                {
                    "WiringObject" => reader.GetAttribute("ConnectorType"),
                    "Connector" => reader.GetAttribute("Type"),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(type)) names.TryAdd(type, Path.GetFileName(file));
            }
        });
        Assert.True(names.Count > 30, $"Only {names.Count} connector names found");
        var unknown = names.Keys.Where(n => !ConnectorCatalog.Resolve(n).IsKnown).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0, "Unrecognised GDTF connector types: " + string.Join(", ", unknown.Select(u => $"{u} ({names[u]})")));
    }

    [Theory]
    [InlineData("PowerconTRUE1", "powerCON TRUE1")]
    [InlineData("powerCONTRUE1TOP", "powerCON TRUE1 TOP")]
    [InlineData("Powercon TRUE1", "powerCON TRUE1")]
    [InlineData("NAC3FCA", "powerCON blue")]
    [InlineData("NAC3MPB", "powerCON grey")]
    [InlineData("PowerConBlue", "powerCON blue")]
    [InlineData("CEE 7/7", "Schuko")]
    [InlineData("IEC 60320-C13/14", "IEC C14")]
    [InlineData("RJ-45", "RJ45")]
    [InlineData("TBLK", "Terminal Block")]
    [InlineData("DC Power Connector (5,5mm x 2,1mm)", "DC Jack")]
    [InlineData("Martin PDE", "Martin proprietary (P3/PDE)")]
    [InlineData("Edison", "E27 lamp socket")]
    [InlineData("cee32-3p", "CEE 32A 3P")]
    public void Gdtf_and_specs_spellings_map_to_canonical_connectors(string written, string canonical)
    {
        Assert.Equal(canonical, ConnectorCatalog.Resolve(written).Id);
    }

    [Fact]
    public void Specs_alternatives_and_combinations_are_split()
    {
        var alt = ConnectorCatalog.ResolveAll("powerCON / powerCON TRUE1");
        Assert.Equal(["powerCON", "powerCON TRUE1"], alt.Select(c => c.Id));
        Assert.True(ConnectorCatalog.ListsSeveralPresent("A + B"));
        Assert.Equal("Martin 8-pin", ConnectorCatalog.Resolve("Martin 8-pin / Terminal Block").Id);
        var unknown = ConnectorCatalog.Resolve("Frobnicator 9");
        Assert.False(unknown.IsKnown);
        Assert.Equal("Frobnicator 9", unknown.Id);
    }

    [Fact]
    public void Connector_rules_cover_true1_xlr_schuko_powercon_and_signals()
    {
        ConnectorMatch M(string a, string b) => ConnectorCompatibility.CheckConnectors(ConnectorCatalog.Resolve(a), ConnectorCatalog.Resolve(b)).Match;

        Assert.Equal(ConnectorMatch.Compatible, M("powerCON TRUE1", "powerCON TRUE1 TOP"));
        Assert.Equal(ConnectorMatch.Compatible, M("powerCON TRUE1 TOP", "PowerconTRUE1"));
        Assert.Equal(ConnectorMatch.Compatible, M("etherCON", "RJ45"));
        Assert.Equal(ConnectorMatch.NeedsAdapter, M("XLR3", "XLR5"));
        Assert.Equal(ConnectorMatch.NeedsAdapter, M("Schuko", "CEE 16A"));
        Assert.Equal(ConnectorMatch.NeedsAdapter, M("CEE 32A 3P", "Schuko"));
        Assert.Equal(ConnectorMatch.NeedsAdapter, M("powerCON", "powerCON TRUE1"));
        Assert.Equal(ConnectorMatch.Incompatible, M("powerCON blue", "powerCON grey"));
        Assert.Equal(ConnectorMatch.Compatible, M("powerCON blue", "powerCON"));
        Assert.Equal(ConnectorMatch.Compatible, M("Socapex", "powerCON TRUE1"));
        Assert.Equal(ConnectorMatch.NeedsAdapter, M("Schuko", "Socapex"));
        Assert.Equal(ConnectorMatch.Compatible, M("XLR", "XLR3"));
        Assert.Equal(ConnectorMatch.Compatible, M("Fixed Cable", "Schuko"));
        Assert.Equal(ConnectorMatch.Incompatible, M("XLR5", "powerCON TRUE1"));
        Assert.Equal(ConnectorMatch.Unknown, M("Frobnicator", "XLR5"));

        var dmx = CableCatalog.Default.Find("dmx-xlr5")!;
        var true1 = CableCatalog.Default.Find("powercon-true1")!;
        var powerIn = new Port
        {
            Name = "Power In",
            Direction = PortDirection.Input,
            Signal = SignalKind.Power,
            Connector = ConnectorCatalog.Resolve("powerCON TRUE1 TOP"),
            Role = PortRole.PowerIn,
            Origin = PortOrigin.Specs,
        };
        Assert.Equal(ConnectorMatch.Incompatible, ConnectorCompatibility.Check(dmx, powerIn).Match);
        Assert.Equal(ConnectorMatch.Compatible, ConnectorCompatibility.Check(true1, powerIn).Match);

        // The legacy powerCON cable is grey at the source end and blue at the load end.
        var powercon = CableCatalog.Default.Find("powercon")!;
        var blueIn = powerIn with { Connector = ConnectorCatalog.Resolve("NAC3MPA") };
        var greyOut = powerIn with { Name = "Out", Direction = PortDirection.Output, Role = PortRole.PowerLink, Connector = ConnectorCatalog.Resolve("NAC3MPB") };
        Assert.Equal(ConnectorMatch.Compatible, ConnectorCompatibility.Check(powercon, blueIn).Match);
        Assert.Equal(ConnectorMatch.Compatible, ConnectorCompatibility.Check(powercon, greyOut).Match);
        Assert.Equal(ConnectorMatch.Incompatible, ConnectorCompatibility.Check(powercon, greyOut with { Direction = PortDirection.Input, Role = PortRole.PowerIn }).Match);
    }
}
