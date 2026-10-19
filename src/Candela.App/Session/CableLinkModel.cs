using System.Globalization;
using System.Numerics;
using Candela.App.Ui;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Engine;

namespace Candela.App.Session;

/// <summary>A device card of the cable link window.</summary>
/// <param name="EntityId">The device (a click selects it).</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What it is (null: an entity that is not a device).</param>
/// <param name="Icon">Icon glyph of its kind.</param>
/// <param name="Detail">Second line: the DMX address of a fixture, the universes of a console output, the outlet of a source.</param>
/// <param name="InPort">The port the line enters through ("in: DMX In"), or empty for the source.</param>
/// <param name="Depth">Position from the source (0 = the source).</param>
/// <param name="OnCable">True when the cable the window shows is plugged into the device.</param>
internal sealed record CableLinkCard(Guid EntityId, string Name, DeviceKind? Kind, string Icon, string Detail, string InPort, int Depth, bool OnCable);

/// <summary>A cable between two cards.</summary>
/// <param name="From">Index of the upstream card.</param>
/// <param name="To">Index of the downstream card.</param>
/// <param name="CableId">The cable entity (a click shows it).</param>
/// <param name="Label">The cable's label.</param>
/// <param name="Index">"DMX A · cable 3": the line's output and the cable's position on it.</param>
/// <param name="Length">"12.4 m".</param>
/// <param name="Ports">"DMX Out → DMX In".</param>
/// <param name="Color">The cable type's legend colour.</param>
/// <param name="Selected">True for the cable the window shows.</param>
internal sealed record CableLinkLink(int From, int To, Guid CableId, string Label, string Index, string Length, string Ports, Vector4 Color, bool Selected);

/// <summary>
/// The content of the cable link window (requirement "окно визуализации связей кабеля"): for one cable, every device of
/// the line it belongs to — the console output's daisy chain for a data cable, the outlet circuit for a power cable —
/// in signal order from the source, with the ports the line enters through, the fixtures' addresses and the cables
/// between them with their indices ("DMX A · cable 3") and lengths; a cable that is not part of a fed line shows the
/// devices plugged into it. Built from Candela.Electrical's <see cref="CableLinkView"/> in the UI language; plain data.
/// </summary>
internal sealed class CableLinkModel
{
    private CableLinkModel(Guid cableId, string title, string typeName, Vector4 color, string summary, string? source, bool fed,
        IReadOnlyList<CableLinkCard> cards, IReadOnlyList<CableLinkLink> links, int freeEnds)
    {
        CableId = cableId;
        Title = title;
        TypeName = typeName;
        Color = color;
        Summary = summary;
        Source = source;
        Fed = fed;
        Cards = cards;
        Links = links;
        FreeEnds = freeEnds;
        foreach (var l in links)
            HasBranches |= l.To != l.From + 1;
    }

    /// <summary>The cable shown.</summary>
    public Guid CableId { get; }

    /// <summary>The cable's label (or name).</summary>
    public string Title { get; }

    /// <summary>The cable type in the UI language.</summary>
    public string TypeName { get; }

    /// <summary>Legend colour of the type.</summary>
    public Vector4 Color { get; }

    /// <summary>"12.4 m · 0.7 kg".</summary>
    public string Summary { get; }

    /// <summary>"'Desk' DMX A · universe 1", or null for a cable that is not fed from a console or a power source.</summary>
    public string? Source { get; }

    /// <summary>True when the cable is part of a line fed by a console or a power source.</summary>
    public bool Fed { get; }

    /// <summary>The devices in signal order.</summary>
    public IReadOnlyList<CableLinkCard> Cards { get; }

    /// <summary>The cables between the devices.</summary>
    public IReadOnlyList<CableLinkLink> Links { get; }

    /// <summary>Ends of the cable that are not plugged into anything.</summary>
    public int FreeEnds { get; }

    /// <summary>True when a cable links two cards that are not next to each other (a branch: drawn as an arc above the cards).</summary>
    public bool HasBranches { get; }

    /// <summary>The model for <paramref name="cableId"/>, or null when the cable is not in the scene.</summary>
    public static CableLinkModel? Build(StageEngine engine, Guid cableId)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var solution = CablingNetwork.Current(engine);
        var view = CableLinkView.Build(solution, cableId);
        if (view is null || solution.FindCable(cableId) is not { } info)
            return null;
        var inv = CultureInfo.InvariantCulture;
        var units = StageUnits.Current;
        var color = info.Type is { } t ? ToColor(t) : new Vector4(0.8f, 0.8f, 0.8f, 1f);
        string typeName = info.Type is { } type ? CableLoc.CableName(type) : info.TypeId;
        string summary = string.Create(inv, $"{info.LengthM:0.0} {units.Metre} · {info.WeightKg:0.0#} {units.Kilogram}");

        // The line's root: the console output or the outlet the strip starts from.
        string? rootPort = null;
        string? source = null;
        if (info.Type is { IsData: true })
        {
            foreach (var line in solution.DataLines)
            {
                if (!line.Feeds.Any(f => f.CableEntityId == cableId))
                    continue;
                rootPort = line.Output.Name;
                string universes = line.UniverseCount <= 1 ? line.FirstUniverse.ToString(inv) : $"{line.FirstUniverse}–{line.FirstUniverse + line.UniverseCount - 1}";
                source = CableLoc.Format(CableStr.LinksSourceData, line.ConsoleName, line.Output.Name, universes);
                break;
            }
        }
        else if (info.Type is { IsPower: true })
        {
            foreach (var circuit in solution.PowerCircuits)
            {
                if (!circuit.Feeds.Any(f => f.CableEntityId == cableId))
                    continue;
                rootPort = circuit.Outlet.Circuits > 1 ? $"{circuit.Outlet.Name}/{circuit.Circuit}" : circuit.Outlet.Name;
                source = CableLoc.Format(CableStr.LinksSourcePower, circuit.SourceName, rootPort, circuit.Phase, circuit.VoltageV.ToString("0", inv));
                break;
            }
        }

        var cards = new List<CableLinkCard>(view.Devices.Count);
        foreach (var d in view.Devices)
        {
            var entity = engine.Scene.Find(d.EntityId);
            cards.Add(new CableLinkCard(d.EntityId, d.Name, d.Kind, IconOf(entity, d.Kind), DetailOf(entity, d, d.Index == 0 ? rootPort : null),
                d.InPort is { Length: > 0 } inPort ? CableLoc.Format(CableStr.LinksInPort, inPort) : "", d.Depth, d.OnSelectedCable));
        }

        var links = new List<CableLinkLink>(view.Connections.Count);
        foreach (var c in view.Connections)
        {
            var cable = solution.FindCable(c.CableEntityId);
            int depth = c.ToIndex < view.Devices.Count ? view.Devices[c.ToIndex].Depth : 0;
            string index = rootPort is not null && source is not null
                ? CableLoc.Format(CableStr.LinksCableIndex, rootPort, depth)
                : "";
            links.Add(new CableLinkLink(c.FromIndex, c.ToIndex, c.CableEntityId, c.CableLabel, index,
                string.Create(inv, $"{c.LengthM:0.0} {units.Metre}"), $"{c.FromPort} → {c.ToPort}",
                cable?.Type is { } ct ? ToColor(ct) : color, c.IsSelected));
        }

        int plugged = info.Endpoints.Count(e => e.NodeExists);
        int ends = CountEnds(engine.Scene.Find(cableId)?.Get<CableComponent>());
        return new CableLinkModel(cableId, info.DisplayLabel, typeName, color, summary, source, source is not null, cards, links, Math.Max(0, ends - plugged));
    }

    /// <summary>"DMX A · cable 3" of a fixture's data feed, or null when no console feeds it.</summary>
    public static string? FeedIndex(NetworkSolution solution, Guid fixture)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return solution.DataFeedOf(fixture) is { } feed ? CableLoc.Format(CableStr.LinksCableIndex, feed.ConsolePort, feed.ChainIndex) : null;
    }

    /// <summary>Nodes with a single segment (the cable's ends).</summary>
    private static int CountEnds(CableComponent? cable)
    {
        if (cable is null)
            return 0;
        var degree = new Dictionary<Guid, int>(cable.Nodes.Count);
        foreach (var s in cable.Segments)
        {
            degree[s.FromNode] = degree.GetValueOrDefault(s.FromNode) + 1;
            degree[s.ToNode] = degree.GetValueOrDefault(s.ToNode) + 1;
        }

        return degree.Values.Count(d => d == 1);
    }

    private static string DetailOf(Entity? entity, CableLinkDevice device, string? rootPort)
    {
        if (entity?.Get<FixtureComponent>() is { } fixture)
        {
            string address = fixture.Addresses.Count == 0 ? "—" : string.Join(" + ", fixture.Addresses.OrderBy(a => a.Break).Select(a => a.ToString()));
            return fixture.FixtureId > 0 ? $"#{fixture.FixtureId.ToString(CultureInfo.InvariantCulture)} · {address}" : address;
        }

        if (rootPort is not null)
            return rootPort;
        return device.Label;
    }

    private static string IconOf(Entity? entity, DeviceKind? kind)
    {
        if (entity is not null && DeviceCatalog.GroupOf(entity) is { } group)
            return DeviceIcons.Glyph(group);
        return kind switch
        {
            DeviceKind.Console => DeviceIcons.Glyph(DeviceGroup.Console),
            DeviceKind.PowerSource => DeviceIcons.Glyph(DeviceGroup.Mains),
            DeviceKind.Fixture => Icons.Lightbulb,
            _ => Icons.Box,
        };
    }

    private static Vector4 ToColor(CableType type)
    {
        var (r, g, b) = type.ColorSrgb;
        return new Vector4(r, g, b, 1f);
    }
}
