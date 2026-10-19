using System.Numerics;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.SceneGraph;
using Candela.Engine;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The manufacturer data of a device and its connectors in the properties panel: manufacturer, model, category,
/// weight, power, candela and flux, beam and field angles, CCT, CRI, colour mixing, noise, IP rating, supply, pan/tilt
/// range and features (<see cref="DeviceFacts"/>), then one row per port with an illustration of its connector
/// (<see cref="ConnectorIcons"/>), its signal and direction and the cable plugged into it; clicking the cable asks for
/// its link view (<see cref="StageToolState.RequestCableLink"/>) or, while no window shows cable links, selects it.
/// </summary>
internal sealed class DeviceFactsView
{
    private readonly StagePanelContext _stage;
    private DeviceFacts? _facts;
    private Entity? _for;
    private object? _profileOf;
    private object? _networkOf;
    private string _language = "";
    private string? _cableNotice;
    private string[] _cableLabels = Array.Empty<string>();
    private string[] _iconTooltips = Array.Empty<string>();
    private string _notConnected = "";
    private string _references = "";

    /// <summary>Creates the view.</summary>
    public DeviceFactsView(StagePanelContext stage)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
    }

    /// <summary>The facts shown (tests).</summary>
    internal DeviceFacts? Facts => _facts;

    /// <summary>Rebuilds the facts when the device, its DMX profile, the network or the language changed.</summary>
    internal void Refresh(StageEngine engine, Entity entity)
    {
        object? profile = engine.Dmx.GetState(entity.Id)?.Profile;
        object? network = engine.NetworkSolution;
        if (_facts is not null && ReferenceEquals(entity, _for) && ReferenceEquals(profile, _profileOf) && ReferenceEquals(network, _networkOf) &&
            string.Equals(_language, Loc.Language, StringComparison.Ordinal))
            return;
        _for = entity;
        _profileOf = profile;
        _networkOf = network;
        _language = Loc.Language;
        _facts = DeviceFacts.Build(engine, entity);
        _cableLabels = new string[_facts.Ports.Count];
        _iconTooltips = new string[_facts.Ports.Count];
        for (int i = 0; i < _facts.Ports.Count; i++)
        {
            var row = _facts.Ports[i];
            _cableLabels[i] = row.CableText is { } text ? StageIcons.Plug + "  " + text + "##cable" : "";
            _iconTooltips[i] = row.Port.ConnectorText is { Length: > 0 } t && t != row.Port.Connector.DisplayName
                ? row.Port.Connector.DisplayName + "\n" + t
                : row.Port.Connector.DisplayName;
        }

        _notConnected = StageIcons.Unplug + "  " + StageLoc.Get(StageStr.PropsNotConnected);
        _references = _facts.References.Count > 0 ? StageLoc.Format(StageStr.FactsReferences, string.Join(" · ", _facts.References)) : "";
    }

    /// <summary>Draws the fixture-type and connector sections (the type section only for fixtures).</summary>
    public void Draw(StageEngine engine, Entity entity, float labelWidth)
    {
        Refresh(engine, entity);
        var facts = _facts!;
        if (ShowsTypeSection(facts, entity) && ImGui.CollapsingHeader(StageLoc.With(Icons.BookOpen, StageStr.PropsFixtureType), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(facts.Title);
            ImGui.TextDisabled(facts.Subtitle);
            ImGui.PopTextWrapPos();
            if (facts.Note is { } note)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(Icons.TriangleAlert + "  " + note);
                ImGui.PopTextWrapPos();
                ImGui.PopStyleColor();
            }

            if (PropertyGrid.Begin("##facts", labelWidth))
            {
                foreach (var row in facts.Rows)
                {
                    PropertyGrid.Row(StageLoc.Get(row.Label));
                    ImGui.PushTextWrapPos(0f);
                    if (row.Source == Gdtf.SpecSource.Estimated)
                        ImGui.TextColored(Theme.TextMuted, row.Value);
                    else
                        ImGui.TextUnformatted(row.Value);
                    ImGui.PopTextWrapPos();
                    // Where the value comes from: the GDTF file, derived from it, a datasheet, or an estimate.
                    if (DeviceFacts.SourceText(row.Source) is { } source && ImGui.IsItemHovered())
                        ImGui.SetTooltip(StageLoc.Get(source));
                }

                PropertyGrid.End();
            }

            if (facts.HasEstimates || _references.Length > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
                ImGui.PushTextWrapPos(0f);
                if (facts.HasEstimates)
                    ImGui.TextUnformatted(StageLoc.Get(StageStr.FactsEstimatedNote));
                if (_references.Length > 0)
                    ImGui.TextUnformatted(_references);
                ImGui.PopTextWrapPos();
                ImGui.PopStyleColor();
            }

            if (facts.Description is { } description && ImGui.TreeNodeEx(StageLoc.Get(StageStr.PropsDescription), ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(description);
                ImGui.PopTextWrapPos();
                ImGui.PopStyleColor();
                ImGui.TreePop();
            }
        }

        if (facts.Ports.Count > 0 && ImGui.CollapsingHeader(StageLoc.With(Icons.Cable, StageStr.PropsConnectors), ImGuiTreeNodeFlags.DefaultOpen))
            Ports(entity, facts.Ports);
    }

    private void Ports(Entity entity, IReadOnlyList<PortRow> ports)
    {
        var style = ImGui.GetStyle();
        // As tall as the three text lines beside it.
        float icon = MathF.Round(ImGui.GetTextLineHeightWithSpacing() * 3f - style.ItemSpacing.Y);
        for (int i = 0; i < ports.Count; i++)
        {
            var row = ports[i];
            ImGui.PushID(i);
            var p = ImGui.GetCursorScreenPos();
            ConnectorIcons.Draw(ImGui.GetWindowDrawList(), p, icon, row.Port.Connector.IllustrationKey);
            ImGui.Dummy(new Vector2(icon));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_iconTooltips[i]);
            ImGui.SameLine(0f, style.ItemSpacing.X * 1.5f);
            ImGui.BeginGroup();
            ImGui.TextUnformatted(row.Title);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.TextUnformatted(row.Detail);
            ImGui.PopStyleColor();
            if (row.CableId is { } cable && row.CableText is { } cableText)
            {
                var dot = ImGui.GetCursorScreenPos();
                float h = ImGui.GetTextLineHeight();
                ImGui.GetWindowDrawList().AddRectFilled(dot + new Vector2(0f, h * 0.35f), dot + new Vector2(h * 1.2f, h * 0.7f), Theme.U32(row.CableColor), 2f);
                ImGui.Dummy(new Vector2(h * 1.3f, h));
                ImGui.SameLine(0f, style.ItemInnerSpacing.X);
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                if (ImGui.Selectable(_cableLabels[i], false, ImGuiSelectableFlags.None,
                        new Vector2(MathF.Min(ImGui.CalcTextSize(cableText).X + h * 2f, ImGui.GetContentRegionAvail().X), 0f)))
                    OpenCable(cable);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(StageLoc.Get(StageStr.PropsCableLinkHint));
            }
            else
            {
                ImGui.TextDisabled(_notConnected);
            }

            ImGui.EndGroup();
            ImGui.PopID();
            if (i < ports.Count - 1)
                ImGui.Separator();
        }

        if (_cableNotice is { } notice)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Note);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(notice);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        _ = entity;
    }

    /// <summary>Shows the cable's link view (or selects the cable while no window shows links).</summary>
    internal void OpenCable(Guid cable)
    {
        _cableNotice = null;
        if (_stage.Tools.RequestCableLink(cable))
            return;
        if (_stage.Scene.Find(cable) is not null)
        {
            _stage.Selection.Set(cable);
            _cableNotice = StageLoc.Get(StageStr.PropsCableSelected);
        }
    }

    /// <summary>The ports list of the facts (tests).</summary>
    internal IReadOnlyList<PortRow> PortsOf(StageEngine engine, Entity entity)
    {
        Refresh(engine, entity);
        return _facts!.Ports;
    }

    /// <summary>
    /// True when the fixture-type section is drawn: when there are rows, and for a fixture without any (an unresolved
    /// GDTF without a specs record) so that its note says why nothing is known.
    /// </summary>
    internal static bool ShowsTypeSection(DeviceFacts facts, Entity entity) =>
        facts.Rows.Count > 0 || facts.Note is not null && entity.Has<Core.SceneGraph.Components.FixtureComponent>();

    /// <summary>Kinds of devices that have ports in the properties panel.</summary>
    internal static bool HasPorts(Entity entity) =>
        entity.Has<Core.SceneGraph.Components.FixtureComponent>() || entity.Has<Core.SceneGraph.Components.ConsoleComponent>() ||
        entity.Has<Core.SceneGraph.Components.PowerSourceComponent>();
}
