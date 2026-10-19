using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Electrical.Tests;

/// <summary>
/// The <c>Mains In</c> of power sources has a real connector (<see cref="PowerSourceComponent.MainsInConnector"/>, or
/// one derived from the kind of source), so an outgoing cable no longer mates a supply's own inlet as if it were an
/// outlet, while the feeder cable of a distro still does.
/// </summary>
public sealed class MainsInletTests
{
    private static PowerSourceComponent Preset(string id) =>
        Assert.IsType<PowerSourceComponent>(DeviceCatalog.Find(id)!.CreateComponent());

    private static Port MainsIn(PowerSourceComponent source) =>
        Assert.Single(PortResolver.FromPowerSource(source), p => p.Name == PortResolver.MainsInPortName);

    [Theory]
    [InlineData("distro-16-true1", "CEE 16A")]
    [InlineData("distro-32-schuko", "CEE 32A 3P")]
    [InlineData("distro-32-true1", "CEE 32A 3P")]
    [InlineData("distro-63-socapex", "CEE 63A 3P")]
    [InlineData("distro-125", "CEE 125A 3P")]
    [InlineData("distro-powerlock-400", "Powerlock")]
    [InlineData("mains-schuko-16", "Schuko")]
    [InlineData("mains-cee16", "CEE 16A")]
    [InlineData("mains-cee32-3p", "CEE 32A 3P")]
    [InlineData("mains-cee125-3p", "CEE 125A 3P")]
    [InlineData("mains-powerlock-400", "Powerlock")]
    public void Built_in_sources_have_a_real_inlet_and_the_derived_default_agrees_with_it(string presetId, string connector)
    {
        var source = Preset(presetId);
        Assert.Equal(connector, source.MainsInConnector);
        var inlet = MainsIn(source);
        Assert.True(inlet.Connector.IsKnown && !inlet.Connector.IsWildcard, inlet.Connector.Id);
        Assert.Equal(ConnectorCatalog.Resolve(connector).Id, inlet.Connector.Id);
        Assert.Equal((PortDirection.Input, PortRole.PowerIn, SignalKind.Power), (inlet.Direction, inlet.Role, inlet.Signal));

        // a source saved before the property existed (null) gets the same inlet from its outlets and phases
        source.MainsInConnector = null;
        Assert.Equal(ConnectorCatalog.Resolve(connector).Id, ConnectorCatalog.Resolve(PortResolver.MainsInConnectorOf(source)).Id);
        Assert.Equal(ConnectorCatalog.Resolve(connector).Id, MainsIn(source).Connector.Id);
    }

    [Fact]
    public void The_derived_inlet_follows_phases_and_outlet_ratings_and_never_throws()
    {
        static PowerSourceComponent Source(int phases, params PowerOutlet?[] outlets) =>
            new() { Phases = phases, Outlets = new List<PowerOutlet>(outlets!) };
        static PowerOutlet Outlet(string connector, float breaker) => new() { PortName = connector, Connector = connector, BreakerA = breaker };

        Assert.Equal("CEE 16A", PortResolver.MainsInConnectorOf(Source(1)));
        Assert.Equal("CEE 16A 3P", PortResolver.MainsInConnectorOf(Source(3)));
        Assert.Equal("CEE 32A", PortResolver.MainsInConnectorOf(Source(1, Outlet("Schuko", 16), Outlet("Schuko", 16))));
        Assert.Equal("CEE 63A 3P", PortResolver.MainsInConnectorOf(Source(1, Outlet("CEE 32A", 32), Outlet("Schuko", 16))));
        // an outlet with an unusable rating counts its connector's rating; unknown connectors and null outlets are skipped
        Assert.Equal("CEE 32A 3P", PortResolver.MainsInConnectorOf(Source(3, Outlet("powerCON TRUE1", float.NaN), null, Outlet("??", -5))));
        // a single outlet with a non-power connector: the standard inlet of its rating
        Assert.Equal("CEE 16A", PortResolver.MainsInConnectorOf(Source(1, Outlet("Loose End", 10))));
        Assert.Equal("CEE 32A 3P", PortResolver.MainsInConnectorOf(Source(3, Outlet("Socapex", 16))));
        // an explicit value wins (trimmed), whatever it names
        Assert.Equal("Harting 16", PortResolver.MainsInConnectorOf(new PowerSourceComponent { MainsInConnector = "  Harting 16 " }));
        Assert.Equal("Powerlock", PortResolver.MainsInConnectorOf(Source(3, Outlet("CEE 125A 3P", 125), Outlet("CEE 125A 3P", 125))));
        Assert.Equal("Powerlock", PortResolver.MainsInConnectorOf(Source(3, Outlet("CEE 125A 3P", 900), Outlet("Schuko", 16))));
    }

    [Fact]
    public void An_outgoing_cable_mates_the_outlets_but_not_the_supply_s_own_inlet()
    {
        var catalog = CableCatalog.Default;
        var resolver = new PortResolver(null);
        var distro = new Entity("Distro");
        distro.Add(Preset("distro-32-true1"));

        // a TRUE1 cable from the distro: its six outlets mate, the CEE 32 A inlet does not (it is ranked last, as an adapter)
        var outgoing = resolver.CompatiblePorts(distro, catalog.Find(CableCatalog.Ids.PowerconTrue1)!);
        Assert.Equal(6, outgoing.Count(p => p.Check.Match == ConnectorMatch.Compatible));
        Assert.All(outgoing.Where(p => p.Check.Match == ConnectorMatch.Compatible), p => Assert.Equal(PortDirection.Output, p.Port.Direction));
        Assert.Equal(PortResolver.MainsInPortName, outgoing[^1].Port.Name);
        Assert.Equal(ConnectorMatch.NeedsAdapter, outgoing[^1].Check.Match);

        // the feeder (CEE 32 A three phase) mates the inlet
        var feeder = ConnectorCompatibility.Check(catalog.Find(CableCatalog.Ids.Cee32ThreePhase)!, resolver.FindPort(distro, PortResolver.MainsInPortName)!);
        Assert.Equal(ConnectorMatch.Compatible, feeder.Match);
    }

    [Fact]
    public void A_distro_fed_through_its_inlet_is_powered_and_a_mismatching_feeder_is_reported()
    {
        var kit = new SceneKit();
        var supply = kit.Source("Supply", new System.Numerics.Vector3(-10, -10, 0), phases: 3,
            outlets: [SceneKit.Outlet("CEE 32 A 3~", "CEE 32A 3P", 32f), SceneKit.Outlet("Schuko", "Schuko", 16f)]);
        supply.Get<PowerSourceComponent>()!.MainsInConnector = "Powerlock";
        var distro = new Entity("Distro") { Local = new Core.Math.Transform(new System.Numerics.Vector3(0, -5, 0)) };
        distro.Add(Preset("distro-32-true1"));
        kit.Scene.Add(distro);
        var other = new Entity("Other distro") { Local = new Core.Math.Transform(new System.Numerics.Vector3(5, -5, 0)) };
        other.Add(Preset("distro-16-true1"));
        kit.Scene.Add(other);
        kit.Cable(CableCatalog.Ids.Cee32ThreePhase, "Feeder", (supply, "CEE 32 A 3~"), (distro, PortResolver.MainsInPortName));
        kit.Cable(CableCatalog.Ids.Schuko, "Improvised", (supply, "Schuko"), (other, PortResolver.MainsInPortName));

        var s = kit.Solve();
        Assert.True(s.PowerSources.Single(p => p.SourceEntityId == distro.Id).IsFed);
        Assert.DoesNotContain(distro.Id, s.UnpoweredDevices);
        // a Schuko lead into a CEE 16 A inlet needs an adapter: still fed, with a warning naming the inlet
        Assert.True(s.PowerSources.Single(p => p.SourceEntityId == other.Id).IsFed);
        Assert.Contains(s.Issues.WithCode(IssueCodes.CableAdapterNeeded), i => i.Message.Contains("CEE 16", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Issues.WithCode(IssueCodes.CableAdapterNeeded), i => i.Message.Contains("'Feeder'", StringComparison.Ordinal));
    }
}
