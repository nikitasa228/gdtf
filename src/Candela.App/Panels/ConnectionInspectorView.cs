using System.Globalization;
using System.Numerics;
using Candela.App.Editing;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Engine;
using Candela.Protocols;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The cabling sections of the properties panel: for a <b>cable</b> its type, label, length, weight, slack, the devices
/// it plugs into (a click selects one) and the selected cable point with its position, anchor and plug; for a
/// <b>console</b> its model, the external input that feeds it (Art-Net / sACN, with a switch to turn that input on) and
/// its outputs with their universes; for a <b>power source</b> its supply (voltage, frequency, phases), the load on each
/// outlet and phase from the power solver, and its outlets with connector, breaker and phase. Every change is one undo
/// step.
/// </summary>
internal sealed class ConnectionInspectorView
{
    private static readonly string[] PowerConnectors = ConnectorCatalog.All
        .Where(c => c.Domain is ConnectorDomain.Power or ConnectorDomain.Multicore && !c.IsWildcard)
        .Select(c => c.Id).ToArray();

    private readonly StagePanelContext _stage;
    private readonly Func<CableNodeRef?> _selectedNode;
    private object? _textsOf;
    private Guid _textsFor;
    private string _textsLanguage = "";
    private CableTexts? _cable;
    private PowerTexts? _power;
    private string _consoleText = "";
    private string _labelBuffer = "";
    private string _modelBuffer = "";
    private Guid _buffersFor;
    private readonly Dictionary<int, string> _nameBuffers = new();

    /// <summary>Creates the view; <paramref name="selectedNode"/> gives the selected cable point (null: none).</summary>
    public ConnectionInspectorView(StagePanelContext stage, Func<CableNodeRef?>? selectedNode = null)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _selectedNode = selectedNode ?? (static () => null);
    }

    /// <summary>True when the view has something to show for <paramref name="entity"/>.</summary>
    public static bool Applies(Entity entity) =>
        entity.Has<CableComponent>() || entity.Has<ConsoleComponent>() || entity.Has<PowerSourceComponent>();

    /// <summary>Draws the sections that apply to <paramref name="entity"/>.</summary>
    public void Draw(StageEngine engine, Entity entity, float labelWidth)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(entity);
        Refresh(engine, entity);
        if (entity.Get<CableComponent>() is { } cable)
            DrawCable(engine, entity, cable, labelWidth);
        if (entity.Get<ConsoleComponent>() is { } console)
            DrawConsole(engine, entity, console, labelWidth);
        if (entity.Get<PowerSourceComponent>() is { } source)
            DrawPowerSource(entity, source, labelWidth);
    }

    /// <summary>Rebuilds the texts when the entity, the network solution or the language changed.</summary>
    internal void Refresh(StageEngine engine, Entity entity)
    {
        var solution = engine.NetworkSolution;
        if (ReferenceEquals(solution, _textsOf) && entity.Id == _textsFor && _textsLanguage == Loc.Language)
            return;
        _textsOf = solution;
        _textsFor = entity.Id;
        _textsLanguage = Loc.Language;
        _cable = entity.Has<CableComponent>() ? CableTexts.Build(engine, solution, entity) : null;
        _power = entity.Has<PowerSourceComponent>() ? PowerTexts.Build(solution, entity) : null;
        _consoleText = entity.Has<ConsoleComponent>() ? CableLoc.Format(CableStr.PropsConsoleDriven, DrivenBy(solution, entity.Id)) : "";
        if (_buffersFor != entity.Id)
        {
            _buffersFor = entity.Id;
            _labelBuffer = entity.Get<CableComponent>()?.Label ?? "";
            _modelBuffer = entity.Get<ConsoleComponent>()?.Model ?? "";
            _nameBuffers.Clear();
        }
    }

    /// <summary>The texts of the cable section (tests).</summary>
    internal CableTexts? CableInfo => _cable;

    /// <summary>The texts of the power source section (tests).</summary>
    internal PowerTexts? PowerInfo => _power;

    /// <summary>Fixtures fed by a console.</summary>
    internal static int DrivenBy(NetworkSolution solution, Guid console)
    {
        var fixtures = new HashSet<Guid>();
        foreach (var line in solution.DataLines)
        {
            if (line.ConsoleEntityId != console)
                continue;
            foreach (var feed in line.Feeds)
            {
                if (solution.Devices.TryGetValue(feed.DeviceEntityId, out var d) && d.Kind == DeviceKind.Fixture && solution.IsConsoleDriven(feed.DeviceEntityId))
                    fixtures.Add(feed.DeviceEntityId);
            }
        }

        return fixtures.Count;
    }

    // ---- cable ------------------------------------------------------------------------------------------------------

    private void DrawCable(StageEngine engine, Entity entity, CableComponent cable, float labelWidth)
    {
        if (!ImGui.CollapsingHeader(CableLoc.With(Icons.Cable, CableStr.PropsCable), ImGuiTreeNodeFlags.DefaultOpen))
            return;
        var texts = _cable;
        if (PropertyGrid.Begin("##cable", labelWidth))
        {
            PropertyGrid.Row(CableLoc.Get(CableStr.PropsCableType));
            var catalog = engine.Network.Catalog;
            var type = catalog.Find(cable.CableTypeId);
            if (ImGui.BeginCombo("##type", type is null ? cable.CableTypeId : CableLoc.CableName(type), ImGuiComboFlags.HeightLarge))
            {
                var style = ImGui.GetStyle();
                if (CableTypeMenu.Draw(catalog, cable.CableTypeId, style.FontScaleMain * style.FontScaleDpi) is { } chosen &&
                    !string.Equals(chosen, cable.CableTypeId, StringComparison.OrdinalIgnoreCase))
                {
                    _stage.Undo.Execute(ModifyComponentCommand.Create(cable, c => c.CableTypeId = chosen, "Cable type"));
                    _stage.Undo.BreakMerge();
                }

                ImGui.EndCombo();
            }

            PropertyGrid.Row(CableLoc.Get(CableStr.PropsCableLabel));
            ImGui.InputTextWithHint("##label", entity.Name, ref _labelBuffer, 64);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                string? label = string.IsNullOrWhiteSpace(_labelBuffer) ? null : _labelBuffer.Trim();
                if (!string.Equals(label, cable.Label, StringComparison.Ordinal))
                {
                    _stage.Undo.Execute(ModifyComponentCommand.Create(cable, c => c.Label = label, "Cable label"));
                    _stage.Undo.BreakMerge();
                }
            }

            if (texts is not null)
            {
                PropertyGrid.Row(CableLoc.Get(CableStr.PropsCableLength));
                ImGui.TextUnformatted(texts.Length);
                PropertyGrid.Row(CableLoc.Get(CableStr.PropsCableWeight));
                ImGui.TextUnformatted(texts.Weight);
            }

            PropertyGrid.Row(CableLoc.Get(CableStr.PropsCableSlack));
            float slack = float.IsFinite(cable.SlackFactor) ? cable.SlackFactor : 1f;
            if (ImGui.SliderFloat("##slack", ref slack, 1f, 1.5f, "×%.2f") && float.IsFinite(slack))
            {
                _stage.Undo.Execute(new SetFieldCommand<CableComponent, float>(new[] { cable }, "slack", static c => c.SlackFactor,
                    static (c, v) => c.SlackFactor = v, Math.Clamp(slack, 1f, 1.5f), CableLoc.Get(CableStr.PropsCableSlack)));
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
                _stage.Undo.BreakMerge();
            PropertyGrid.End();
        }

        if (texts is not null && texts.Ends.Length > 0)
        {
            ImGui.TextDisabled(CableLoc.Get(CableStr.PropsCableEnds));
            for (int i = 0; i < texts.Ends.Length; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Selectable(texts.Ends[i].Text, false) && _stage.Scene.Find(texts.Ends[i].Device) is not null)
                    _stage.Selection.Set(texts.Ends[i].Device);
                ImGui.PopID();
            }
        }

        if (ImGui.Button(CableLoc.With(Icons.ExternalLink, CableStr.PropsCableShowLinks)))
            _stage.Tools.RequestCableLink(entity.Id);
        DrawNode(entity, cable);
    }

    /// <summary>The selected point of this cable: its world position (editable, one undo step per edit), anchor and plug.</summary>
    private void DrawNode(Entity entity, CableComponent cable)
    {
        if (_selectedNode() is not { } reference || reference.CableId != entity.Id || cable.FindNode(reference.NodeId) is not { } node)
            return;
        int index = cable.Nodes.IndexOf(node) + 1;
        ImGui.SeparatorText(CableLoc.Format(CableStr.NodeSelected, index, entity.Name));
        var scene = _stage.Scene;
        var world = CableNodeEdits.WorldPosition(scene, entity, node);
        if (PropertyGrid.Begin("##cable-node", 120f))
        {
            PropertyGrid.Row(CableLoc.Get(CableStr.NodePosition));
            if (ImGui.DragFloat3("##pos", ref world, 0.01f, -10000f, 10000f, "%.3f m"))
                CableNodeEdits.Move(scene, _stage.Undo, reference, world, breakMerge: false);
            if (ImGui.IsItemDeactivatedAfterEdit())
                _stage.Undo.BreakMerge();
            PropertyGrid.Row(CableLoc.Get(CableStr.NodeAnchor));
            string anchor = node.Anchor is { } a && scene.Find(a.EntityId) is { } target ? target.Name : CableLoc.Get(CableStr.NodeAnchorFree);
            ImGui.TextUnformatted(anchor);
            var plug = cable.Endpoints.Find(e => e?.NodeId == node.Id);
            if (plug is not null)
            {
                PropertyGrid.Row("");
                ImGui.TextDisabled(CableLoc.Format(CableStr.NodePlugged, scene.Find(plug.EntityId)?.Name ?? "?", plug.PortName));
            }

            PropertyGrid.End();
        }
    }

    // ---- console ----------------------------------------------------------------------------------------------------

    private void DrawConsole(StageEngine engine, Entity entity, ConsoleComponent console, float labelWidth)
    {
        if (!ImGui.CollapsingHeader(CableLoc.With(DeviceIcons.Glyph(DeviceGroup.Console), CableStr.PropsConsole), ImGuiTreeNodeFlags.DefaultOpen))
            return;
        if (PropertyGrid.Begin("##console", labelWidth))
        {
            PropertyGrid.Row(CableLoc.Get(CableStr.PropsConsoleModel));
            ImGui.InputText("##model", ref _modelBuffer, 96);
            if (ImGui.IsItemDeactivatedAfterEdit() && !string.Equals(_modelBuffer.Trim(), console.Model, StringComparison.Ordinal))
            {
                string model = _modelBuffer.Trim();
                _stage.Undo.Execute(ModifyComponentCommand.Create(console, c => c.Model = model, "Console model"));
                _stage.Undo.BreakMerge();
            }

            PropertyGrid.Row(CableLoc.Get(CableStr.PropsConsoleProtocol));
            string current = ProtocolName(console.ProtocolId);
            if (ImGui.BeginCombo("##protocol", current))
            {
                foreach (var id in new[] { ProtocolIds.ArtNet, ProtocolIds.Sacn, null })
                {
                    if (ImGui.Selectable(ProtocolName(id), string.Equals(id, console.ProtocolId, StringComparison.Ordinal)) &&
                        !string.Equals(id, console.ProtocolId, StringComparison.Ordinal))
                    {
                        _stage.Undo.Execute(ModifyComponentCommand.Create(console, c => c.ProtocolId = id, "Console input"));
                        _stage.Undo.BreakMerge();
                    }
                }

                ImGui.EndCombo();
            }

            PropertyGrid.End();
        }

        // Whether the console's input runs: the fixtures it drives follow it only when it does.
        if (console.ProtocolId is { Length: > 0 } protocol && ProtocolRegistry.Find(protocol) is { } descriptor)
        {
            var external = _stage.Tools.External;
            bool on = external.IsEnabled(protocol);
            ImGui.PushStyleColor(ImGuiCol.Text, on ? Theme.TextMuted : Theme.Warning);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(CableLoc.Format(on ? CableStr.PropsConsoleInputOn : CableStr.PropsConsoleInputOff, descriptor.DisplayName));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            if (!on && descriptor.CanStart && ImGui.Button(CableLoc.Format(CableStr.PropsConsoleEnableInput, descriptor.DisplayName) + "##enable-input"))
            {
                external.SetEnabled(engine, _stage.Settings, protocol, true);
                _stage.SettingsChanged?.Invoke();
            }
        }

        ImGui.TextDisabled(_consoleText);
        ImGui.SeparatorText(CableLoc.Get(CableStr.PropsConsoleOutputs));
        const ImGuiTableFlags flags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings;
        int remove = -1;
        if (ImGui.BeginTable("##outputs", 5, flags))
        {
            float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("signal", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("universe", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("count", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("remove", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 2f * scale);
            for (int i = 0; i < console.Outputs.Count; i++)
            {
                if (console.Outputs[i] is not { } output)
                    continue;
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                string name = _nameBuffers.TryGetValue(i, out var buffered) ? buffered : output.PortName;
                if (ImGui.InputText("##name", ref name, 32))
                    _nameBuffers[i] = name;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    _nameBuffers.Remove(i);
                    string trimmed = name.Trim();
                    if (trimmed.Length > 0 && trimmed != output.PortName && !console.Outputs.Any(o => o is not null && !ReferenceEquals(o, output) &&
                            string.Equals(o.PortName, trimmed, StringComparison.OrdinalIgnoreCase)))
                        EditConsole(console, i, o => o.PortName = trimmed, "Console output");
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                bool ethernet = !string.Equals(output.Signal, "DMX512", StringComparison.OrdinalIgnoreCase);
                if (ImGui.BeginCombo("##signal", ethernet ? "Ethernet" : "DMX512"))
                {
                    if (ImGui.Selectable("DMX512", !ethernet) && ethernet)
                        EditConsole(console, i, o =>
                        {
                            o.Signal = "DMX512";
                            o.UniverseCount = 1;
                        }, "Console output");
                    if (ImGui.Selectable("Ethernet", ethernet) && !ethernet)
                        EditConsole(console, i, o => o.Signal = "Ethernet", "Console output");
                    ImGui.EndCombo();
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                int universe = output.FirstUniverse;
                if (ImGui.InputInt("##universe", ref universe, 0, 0) && universe != output.FirstUniverse)
                    EditConsole(console, i, o => o.FirstUniverse = Math.Clamp(universe, 1, 63999), "Console universe", merge: true);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    _stage.Undo.BreakMerge();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(StageLoc.Get(StageStr.PatchUniverse));
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                ImGui.BeginDisabled(!ethernet);
                int count = ethernet ? output.UniverseCount : 1;
                if (ImGui.InputInt("##count", ref count, 0, 0) && count != output.UniverseCount)
                    EditConsole(console, i, o => o.UniverseCount = Math.Clamp(count, 1, 512), "Console universes", merge: true);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    _stage.Undo.BreakMerge();
                ImGui.EndDisabled();
                ImGui.TableNextColumn();
                if (ImGui.SmallButton(Icons.Trash + "##remove"))
                    remove = i;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(CableLoc.Get(CableStr.PropsConsoleRemoveOutput));
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (remove >= 0)
            RemoveConsoleOutput(console, remove);
        if (ImGui.Button(CableLoc.With(Icons.Plus, CableStr.PropsConsoleAddOutput)))
            AddConsoleOutput(console);
    }

    /// <summary>Adds the next DMX output ("DMX E" on the next universe) as one undo step.</summary>
    internal void AddConsoleOutput(ConsoleComponent console)
    {
        int next = console.Outputs.Where(o => o is not null).Select(o => o.FirstUniverse + Math.Max(1, o.UniverseCount)).DefaultIfEmpty(1).Max();
        string name = NextOutputName(console);
        _stage.Undo.Execute(ModifyComponentCommand.Create(console, c => c.Outputs.Add(new ConsoleOutput { PortName = name, Signal = "DMX512", FirstUniverse = next }),
            "Add console output"));
        _stage.Undo.BreakMerge();
        _nameBuffers.Clear();
    }

    /// <summary>Removes output <paramref name="index"/> as one undo step (a cable plugged into it hangs free).</summary>
    internal void RemoveConsoleOutput(ConsoleComponent console, int index)
    {
        if (index < 0 || index >= console.Outputs.Count)
            return;
        _stage.Undo.Execute(ModifyComponentCommand.Create(console, c => c.Outputs.RemoveAt(index), "Remove console output"));
        _stage.Undo.BreakMerge();
        _nameBuffers.Clear();
    }

    private static string NextOutputName(ConsoleComponent console)
    {
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string name = "DMX " + c;
            if (!console.Outputs.Any(o => o is not null && string.Equals(o.PortName, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return "Output " + (console.Outputs.Count + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void EditConsole(ConsoleComponent console, int index, Action<ConsoleOutput> edit, string description, bool merge = false)
    {
        _stage.Undo.Execute(ModifyComponentCommand.Create(console, c =>
        {
            if (index < c.Outputs.Count && c.Outputs[index] is { } o)
                edit(o);
        }, description));
        if (!merge)
            _stage.Undo.BreakMerge();
    }

    private static string ProtocolName(string? id) =>
        id is null ? CableLoc.Get(CableStr.PropsConsoleProtocolNone) : ProtocolRegistry.Find(id)?.DisplayName ?? id;

    // ---- power source -----------------------------------------------------------------------------------------------

    private void DrawPowerSource(Entity entity, PowerSourceComponent source, float labelWidth)
    {
        if (!ImGui.CollapsingHeader(CableLoc.With(DeviceIcons.Glyph(DeviceGroup.Distro), CableStr.PropsPowerSource), ImGuiTreeNodeFlags.DefaultOpen))
            return;
        var units = StageUnits.Current;
        if (PropertyGrid.Begin("##power", labelWidth))
        {
            PropertyGrid.Row(CableLoc.Get(CableStr.PropsPowerVoltage));
            float volts = source.VoltageV;
            if (ImGui.InputFloat("##voltage", ref volts, 0f, 0f, "%.0f " + units.Volt) && float.IsFinite(volts) && volts != source.VoltageV)
                EditSource(source, s => s.VoltageV = Math.Clamp(volts, 1f, 1000f), "Supply voltage", merge: true);
            if (ImGui.IsItemDeactivatedAfterEdit())
                _stage.Undo.BreakMerge();
            PropertyGrid.Row(CableLoc.Get(CableStr.PropsPowerFrequency));
            if (ImGui.BeginCombo("##frequency", string.Create(CultureInfo.InvariantCulture, $"{source.FrequencyHz:0} {units.Hertz}")))
            {
                foreach (float hz in new[] { 50f, 60f })
                {
                    if (ImGui.Selectable(string.Create(CultureInfo.InvariantCulture, $"{hz:0} {units.Hertz}"), source.FrequencyHz == hz) && source.FrequencyHz != hz)
                        EditSource(source, s => s.FrequencyHz = hz, "Supply frequency");
                }

                ImGui.EndCombo();
            }

            PropertyGrid.Row(CableLoc.Get(CableStr.PropsPowerPhases));
            int phases = source.Phases == 3 ? 1 : 0;
            if (ImGui.Combo("##phases", ref phases, "1\03\0") && (phases == 1 ? 3 : 1) != source.Phases)
                EditSource(source, s => s.Phases = phases == 1 ? 3 : 1, "Supply phases");
            PropertyGrid.End();
        }

        if (_power is { } power)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, power.Unfed ? Theme.Warning : Theme.TextMuted);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(power.Total);
            if (power.Unfed)
                ImGui.TextUnformatted(CableLoc.Get(CableStr.PropsPowerUnfed));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        ImGui.SeparatorText(CableLoc.Get(CableStr.PropsPowerOutlets));
        int remove = -1;
        const ImGuiTableFlags flags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings;
        if (ImGui.BeginTable("##outlets", 5, flags))
        {
            float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("connector", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("breaker", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("phase", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupColumn("remove", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 2f * scale);
            for (int i = 0; i < source.Outlets.Count; i++)
            {
                if (source.Outlets[i] is not { } outlet)
                    continue;
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                string name = _nameBuffers.TryGetValue(i, out var buffered) ? buffered : outlet.PortName;
                if (ImGui.InputText("##name", ref name, 32))
                    _nameBuffers[i] = name;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    _nameBuffers.Remove(i);
                    string trimmed = name.Trim();
                    if (trimmed.Length > 0 && trimmed != outlet.PortName && !source.Outlets.Any(o => o is not null && !ReferenceEquals(o, outlet) &&
                            string.Equals(o.PortName, trimmed, StringComparison.OrdinalIgnoreCase)))
                        EditOutlet(source, i, o => o.PortName = trimmed, "Outlet name");
                }

                if (_power?.Loads.TryGetValue(outlet.PortName, out var load) == true && ImGui.IsItemHovered())
                    ImGui.SetTooltip(load.Text);
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##connector", outlet.Connector))
                {
                    foreach (var connector in PowerConnectors)
                    {
                        if (ImGui.Selectable(connector, string.Equals(connector, outlet.Connector, StringComparison.OrdinalIgnoreCase)) && connector != outlet.Connector)
                            EditOutlet(source, i, o => o.Connector = connector, "Outlet connector");
                    }

                    ImGui.EndCombo();
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                float breaker = outlet.BreakerA;
                if (ImGui.InputFloat("##breaker", ref breaker, 0f, 0f, "%.0f A") && float.IsFinite(breaker) && breaker != outlet.BreakerA)
                    EditOutlet(source, i, o => o.BreakerA = Math.Clamp(breaker, 0f, 10000f), "Outlet breaker", merge: true);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    _stage.Undo.BreakMerge();
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                int phase = Math.Clamp(outlet.Phase, 1, 3) - 1;
                if (ImGui.Combo("##phase", ref phase, "L1\0L2\0L3\0") && phase + 1 != outlet.Phase)
                    EditOutlet(source, i, o => o.Phase = phase + 1, "Outlet phase");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton(Icons.Trash + "##remove"))
                    remove = i;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(CableLoc.Get(CableStr.PropsPowerRemoveOutlet));
                ImGui.PopID();

                // The load on the outlet, under its row (red above the breaker).
                if (_power?.Loads.TryGetValue(outlet.PortName, out var row) == true)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(row.Overloaded ? Theme.Error : Theme.TextMuted, row.Text);
                }
            }

            ImGui.EndTable();
        }

        if (remove >= 0)
        {
            _stage.Undo.Execute(ModifyComponentCommand.Create(source, s => s.Outlets.RemoveAt(remove), "Remove outlet"));
            _stage.Undo.BreakMerge();
            _nameBuffers.Clear();
        }

        if (ImGui.Button(CableLoc.With(Icons.Plus, CableStr.PropsPowerAddOutlet)))
            AddOutlet(source);
        _ = entity;
    }

    /// <summary>Adds an outlet like the last one ("Schuko 7" on the next phase) as one undo step.</summary>
    internal void AddOutlet(PowerSourceComponent source)
    {
        var last = source.Outlets.LastOrDefault(o => o is not null);
        string connector = last?.Connector ?? "Schuko";
        float breaker = last?.BreakerA ?? 16f;
        int phase = source.Phases == 3 && last is not null ? last.Phase % 3 + 1 : 1;
        int n = source.Outlets.Count + 1;
        string name;
        do
        {
            name = $"Outlet {n.ToString(CultureInfo.InvariantCulture)}";
            n++;
        }
        while (source.Outlets.Any(o => o is not null && string.Equals(o.PortName, name, StringComparison.OrdinalIgnoreCase)));
        _stage.Undo.Execute(ModifyComponentCommand.Create(source, s => s.Outlets.Add(new PowerOutlet { PortName = name, Connector = connector, BreakerA = breaker, Phase = phase }),
            "Add outlet"));
        _stage.Undo.BreakMerge();
        _nameBuffers.Clear();
    }

    private void EditSource(PowerSourceComponent source, Action<PowerSourceComponent> edit, string description, bool merge = false)
    {
        _stage.Undo.Execute(ModifyComponentCommand.Create(source, edit, description));
        if (!merge)
            _stage.Undo.BreakMerge();
    }

    private void EditOutlet(PowerSourceComponent source, int index, Action<PowerOutlet> edit, string description, bool merge = false)
    {
        _stage.Undo.Execute(ModifyComponentCommand.Create(source, s =>
        {
            if (index < s.Outlets.Count && s.Outlets[index] is { } o)
                edit(o);
        }, description));
        if (!merge)
            _stage.Undo.BreakMerge();
    }

    /// <summary>Texts of the cable section.</summary>
    internal sealed record CableTexts(string Length, string Weight, (Guid Device, string Text)[] Ends)
    {
        public static CableTexts Build(StageEngine engine, NetworkSolution solution, Entity entity)
        {
            var inv = CultureInfo.InvariantCulture;
            var units = StageUnits.Current;
            var info = solution.FindCable(entity.Id);
            var cable = entity.Get<CableComponent>()!;
            var geometry = info?.Geometry ?? CableGeometry.Compute(engine.Scene, entity, cable);
            var type = engine.Network.Catalog.Find(cable.CableTypeId);
            float length = geometry.LengthM;
            string lengthText = string.Create(inv, $"{length:0.00} {units.Metre} ({geometry.RoutedLengthM:0.00} {units.Metre} × {geometry.SlackFactor:0.00})");
            string weight = type is null ? "—" : string.Create(inv, $"{length * type.WeightKgPerM:0.00} {units.Kilogram}");
            var ends = new List<(Guid, string)>();
            foreach (var e in cable.Endpoints)
            {
                if (e is null)
                    continue;
                string device = engine.Scene.Find(e.EntityId)?.Name ?? "?";
                ends.Add((e.EntityId, $"{StageIcons.Plug}  {device} · {e.PortName}##end{ends.Count}"));
            }

            return new CableTexts(lengthText, weight, ends.ToArray());
        }
    }

    /// <summary>Texts of the power source section: total and per-phase currents, and the load of each outlet.</summary>
    internal sealed record PowerTexts(string Total, bool Unfed, Dictionary<string, (string Text, bool Overloaded)> Loads)
    {
        public static PowerTexts Build(NetworkSolution solution, Entity entity)
        {
            var inv = CultureInfo.InvariantCulture;
            var load = solution.PowerSources.FirstOrDefault(p => p.SourceEntityId == entity.Id);
            var loads = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            if (load is null)
                return new PowerTexts("", false, loads);
            double Phase(int i) => i < load.PhaseCurrentsA.Count ? load.PhaseCurrentsA[i] : 0;
            string total = CableLoc.Format(CableStr.PropsPowerTotal, load.TotalW.ToString("0", inv), Phase(0).ToString("0.0", inv), Phase(1).ToString("0.0", inv),
                Phase(2).ToString("0.0", inv));
            foreach (var group in load.Circuits.GroupBy(c => c.Outlet.Name, StringComparer.OrdinalIgnoreCase))
            {
                double w = group.Sum(c => c.LoadW);
                double a = group.Max(c => c.CurrentA);
                var breaker = group.First().BreakerA;
                bool over = group.Any(c => c.IsOverloaded);
                string text = breaker is { } b && b > 0
                    ? CableLoc.Format(CableStr.PropsPowerLoad, w.ToString("0", inv), a.ToString("0.0", inv), (100.0 * a / b).ToString("0", inv), b.ToString("0", inv))
                    : string.Create(inv, $"{w:0} W · {a:0.0} A");
                loads[group.Key] = (text, over);
            }

            return new PowerTexts(total, !load.IsPowered, loads);
        }
    }
}
