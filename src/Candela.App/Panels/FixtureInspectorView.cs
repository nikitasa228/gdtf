using System.Globalization;
using System.Numerics;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The fixture part of the properties panel (requirement: "first its current state, below its manufacturer data"):
/// the live state (colour swatch with CIE xy and CCT, intensity in % and cd, pan/tilt, zoom, focus, iris, frost, strobe,
/// gobo, prism), the manual controls of every attribute of its DMX mode in physical units — locked, with the reason,
/// while a console or live DMX drives the fixture — the DMX patch (fixture id, mode, universe and address per break;
/// undoable) and the manufacturer data with the connectors (<see cref="DeviceFactsView"/>). With several fixtures
/// selected, a control sets every selected fixture that has that attribute and is not driven by a console.
/// </summary>
internal sealed class FixtureInspectorView
{
    private static readonly StageStr[] GroupNames =
    {
        StageStr.GroupIntensity, StageStr.GroupPosition, StageStr.GroupColour, StageStr.GroupBeam, StageStr.GroupGoboEffects,
        StageStr.GroupShapers, StageStr.GroupAtmosphere, StageStr.GroupOther,
    };

    private readonly StagePanelContext _stage;
    private readonly FixtureControls _controls = new();
    private readonly DeviceFactsView _facts;
    private readonly int[] _patchUniverse = new int[8];
    private readonly int[] _patchAddress = new int[8];
    private Guid _patchFor;
    private int _fixtureId;
    private string _liveLine1 = "", _liveLine2 = "", _liveLine3 = "", _lockText = "", _patchRange = "";
    private double _textsAt = double.NegativeInfinity;
    private Guid _textsFor;
    private IReadOnlyList<Issue>? _issuesOf;
    private Candela.Dmx.Patch? _patchRangeOf;
    private Guid _patchRangeFor;
    private string _patchRangeLanguage = "";
    private Guid _issuesFor;
    private readonly List<Issue> _issues = new();
    private int _selectionTargets;
    private string _targetsText = "";

    /// <summary>Creates the view.</summary>
    public FixtureInspectorView(StagePanelContext stage)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _facts = new DeviceFactsView(stage);
    }

    /// <summary>The control model (tests).</summary>
    internal FixtureControls Controls => _controls;

    /// <summary>Draws the fixture sections for <paramref name="entity"/>.</summary>
    public void Draw(Entity entity, FixtureComponent fixture, float labelWidth)
    {
        var engine = _stage.Engine;
        if (engine is null)
        {
            // Nothing of a previous stage (profile, modes) may show for this fixture.
            _controls.Clear();
            Muted(StageLoc.Get(StageStr.PropsNoEngine));
            Patch(entity, fixture, null, labelWidth);
            return;
        }

        _controls.Refresh(engine, entity);
        RefreshTexts(engine, entity);
        if (ImGui.CollapsingHeader(StageLoc.With(StageIcons.ScanEye, StageStr.PropsCurrentState), ImGuiTreeNodeFlags.DefaultOpen))
        {
            LockBanner(entity);
            LiveState();
            ManualControls(engine, entity, labelWidth);
        }

        if (ImGui.CollapsingHeader(StageLoc.With(Icons.Network, StageStr.PropsPatch), ImGuiTreeNodeFlags.DefaultOpen))
            Patch(entity, fixture, engine, labelWidth);
        _facts.Draw(engine, entity, labelWidth);
    }

    private void RefreshTexts(StageEngine engine, Entity entity)
    {
        double now = ImGui.GetTime();
        if (entity.Id == _textsFor && now - _textsAt < 0.1)
            return;
        _textsFor = entity.Id;
        _textsAt = now;
        var inv = CultureInfo.InvariantCulture;
        ref readonly var live = ref _controls.Live;
        if (!live.Valid)
        {
            _liveLine1 = _controls.ResolutionError is { } error ? StageLoc.Format(StageStr.PropsUnresolved, error) : StageLoc.Get(StageStr.PropsNoState);
            _liveLine2 = _liveLine3 = "";
        }
        else
        {
            // No-break spaces keep each value with its unit (and digit groups together) when the line wraps.
            string cct = float.IsFinite(live.Cct) ? string.Create(inv, $" · {live.Cct:0}\u00a0K") : "";
            string flux = string.Create(inv, $"{live.Flux:#,0}\u00a0{StageUnits.Current.Lumen}").Replace(',', '\u202f');
            _liveLine1 = string.Create(inv,
                $"{live.Intensity * 100f:0}% · {DeviceFacts.FormatCandela(live.PeakCandela).Replace(' ', '\u00a0')} · {flux} · x\u00a0{live.Xy.X:0.000}\u00a0y\u00a0{live.Xy.Y:0.000}{cct}");
            string pan = float.IsFinite(live.Pan) ? string.Create(inv, $"{StageLoc.Get(StageStr.LivePan)} {live.Pan * 180f / MathF.PI:0.0}°  ") : "";
            string tilt = float.IsFinite(live.Tilt) ? string.Create(inv, $"{StageLoc.Get(StageStr.LiveTilt)} {live.Tilt * 180f / MathF.PI:0.0}°  ") : "";
            _liveLine2 = pan + tilt + string.Create(inv,
                $"{StageLoc.Get(StageStr.LiveBeam)} {live.BeamAngle * 180f / MathF.PI:0.#}° / {live.FieldAngle * 180f / MathF.PI:0.#}°");
            var parts = new List<string>(6)
            {
                string.Create(inv, $"{StageLoc.Get(StageStr.LiveFocus)} {live.Focus * 100f:0}%"),
                string.Create(inv, $"{StageLoc.Get(StageStr.LiveIris)} {live.Iris * 100f:0}%"),
                string.Create(inv, $"{StageLoc.Get(StageStr.LiveFrost)} {live.Frost * 100f:0}%"),
            };
            if (live.Strobe > 0f)
                parts.Add(string.Create(inv, $"{StageLoc.Get(StageStr.LiveStrobe)} {live.Strobe:0.#} {StageUnits.Current.Hertz}"));
            if (live.GoboSlot > 0)
                parts.Add(string.Create(inv, $"{StageLoc.Get(StageStr.LiveGobo)} {live.GoboSlot}"));
            if (live.PrismFacets > 0)
                parts.Add(string.Create(inv, $"{StageLoc.Get(StageStr.LivePrism)} {live.PrismFacets}×"));
            if (live.Outputs.Any)
                parts.Add(string.Create(inv,
                    $"{StageLoc.Get(StageStr.LiveOutputs)} {Math.Max(Math.Max(live.Outputs.Fog, live.Outputs.Haze), Math.Max(live.Outputs.FanSpeed, live.Outputs.Flame)) * 100f:0}%"));
            _liveLine3 = string.Join(" · ", parts);
        }

        _lockText = _controls.Lock switch
        {
            ControlLock.Console => LockReasonConsole(engine, entity),
            ControlLock.LiveDmx => LockReasonDmx(entity),
            ControlLock.Unresolved => StageLoc.Get(StageStr.LockUnresolved),
            _ => StageLoc.Get(StageStr.LockManual),
        };

        // Multi-selection: how many fixtures a control would set.
        int targets = 0;
        foreach (var id in _stage.Selection.Ids)
        {
            if (_stage.Scene.Find(id) is { } e && e.Has<FixtureComponent>() && !engine.Dmx.IsConsoleDriven(id))
                targets++;
        }

        _selectionTargets = targets;
        _targetsText = targets > 1 ? StageLoc.Format(StageStr.PropsAppliesTo, targets) : "";
    }

    private string LockReasonConsole(StageEngine engine, Entity entity)
    {
        var feed = engine.NetworkSolution.DataFeedOf(entity.Id);
        if (feed is null)
            return StageLoc.Get(StageStr.LockConsole);
        string console = _stage.Scene.Find(feed.ConsoleEntityId)?.Name ?? "?";
        return StageLoc.Format(StageStr.LockConsoleNamed, console, feed.ConsolePort, feed.FirstUniverse, feed.ChainIndex);
    }

    private string LockReasonDmx(Entity entity)
    {
        var fixture = entity.Get<FixtureComponent>();
        string universes = fixture is null ? "" : ExternalControl.UniverseRanges(fixture.Addresses.Select(a => a.Universe));
        return StageLoc.Format(StageStr.LockLiveDmx, universes);
    }

    private void LockBanner(Entity entity)
    {
        _ = entity;
        bool locked = _controls.Lock is ControlLock.Console or ControlLock.LiveDmx;
        var color = locked ? Theme.Warning : _controls.Lock == ControlLock.Unresolved ? Theme.Error : Theme.TextMuted;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted((locked ? StageIcons.Lock : StageIcons.LockOpen) + "  " + _lockText);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private void LiveState()
    {
        ref readonly var live = ref _controls.Live;
        float swatch = ImGui.GetTextLineHeightWithSpacing() * 2.6f;
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var color = live.Valid ? live.Swatch with { W = 1f } : new Vector4(0.2f, 0.2f, 0.22f, 1f);
        // The swatch is the light's chromaticity; its fill shows the intensity (dark = off).
        dl.AddRectFilled(p, p + new Vector2(swatch), Theme.U32(Theme.Rgb(0x101114)), 4f);
        float level = live.Valid ? Math.Clamp(live.Intensity, 0f, 1f) : 0f;
        dl.AddRectFilled(p + new Vector2(0f, swatch * (1f - level)), p + new Vector2(swatch), Theme.U32(color), 4f,
            level >= 0.99f ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersBottom);
        dl.AddRect(p, p + new Vector2(swatch), Theme.U32(Theme.WithAlpha(color, 0.9f)), 4f, ImDrawFlags.None, 1.5f);
        ImGui.Dummy(new Vector2(swatch));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(StageStr.LiveSwatchHint));
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(_liveLine1);
        ImGui.TextDisabled(_liveLine2);
        ImGui.TextDisabled(_liveLine3);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
    }

    private void ManualControls(StageEngine engine, Entity entity, float labelWidth)
    {
        var controls = _controls.Controls;
        if (controls.Count == 0)
            return;
        bool locked = _controls.Lock is ControlLock.Console or ControlLock.LiveDmx;
        if (_targetsText.Length > 0)
            Muted(_targetsText);
        int group = -1;
        bool open = true, inTable = false;
        for (int i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            if ((int)c.Group != group)
            {
                if (inTable)
                {
                    PropertyGrid.End();
                    inTable = false;
                }

                if (group == (int)ControlGroup.Other && open)
                    ImGui.TreePop();
                group = (int)c.Group;
                if (c.Group == ControlGroup.Other)
                {
                    open = ImGui.TreeNodeEx(StageLoc.Get(GroupNames[group]), ImGuiTreeNodeFlags.SpanAvailWidth);
                }
                else
                {
                    ImGui.SeparatorText(StageLoc.Get(GroupNames[group]));
                    open = true;
                }

                if (open)
                    inTable = PropertyGrid.Begin(GroupTableIds[group], labelWidth);
            }

            if (!open || !inTable)
                continue;
            ControlRow(engine, entity, c, locked);
        }

        if (inTable)
            PropertyGrid.End();
        if (group == (int)ControlGroup.Other && open)
            ImGui.TreePop();
        if (!locked && HasManualValues())
        {
            if (ImGui.Button(StageLoc.With(Icons.RotateCcw, StageStr.PropsResetManual)))
                ResetManual(entity);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StageLoc.Get(StageStr.PropsResetManualHint));
        }
    }

    private static readonly string[] GroupTableIds =
    {
        "##ctl-intensity", "##ctl-position", "##ctl-colour", "##ctl-beam", "##ctl-gobo", "##ctl-shapers", "##ctl-atmosphere", "##ctl-other",
    };

    private void ControlRow(StageEngine engine, Entity entity, FixtureControl c, bool locked)
    {
        PropertyGrid.Row(c.Label);
        ImGui.BeginDisabled(locked || c.IsConstant);
        if (c.Manual is not null && !locked)
        {
            // Manual values are marked with an accent bar on the left edge of the field.
            var p = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(p - new Vector2(4f, 0f), p + new Vector2(-2f, ImGui.GetFrameHeight()), Theme.U32(Theme.Accent));
        }

        // While a console or live DMX drives the fixture its manual values are not in effect: show what drives it.
        bool unknown = locked ? !float.IsFinite(c.Live) : c.IsUnknown;
        float value = !locked ? c.Shown : float.IsFinite(c.Live) ? c.Live : c.Min;
        bool changed = false, discrete = false;
        if (c.Choices is { } choices)
        {
            int index = unknown ? -1 : c.ChoiceIndex(value);
            if (ImGui.BeginCombo(c.Id, index >= 0 ? choices[index] : "—"))
            {
                for (int i = 0; i < choices.Length; i++)
                {
                    if (ImGui.Selectable(choices[i], i == index) && i != index)
                    {
                        value = c.ChoiceValues![i];
                        changed = true;
                        discrete = true;
                    }
                }

                ImGui.EndCombo();
            }
        }
        else
        {
            float shown = value * c.DisplayScale;
            // A control whose channel currently runs another function shows no number until it is set.
            if (ImGui.SliderFloat(c.Id, ref shown, c.Min * c.DisplayScale, c.Max * c.DisplayScale, unknown ? "—" : c.Format))
            {
                value = shown / c.DisplayScale;
                changed = true;
            }
        }

        bool finished = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(locked ? _lockText : c.Attribute);
        if (!locked && c.Manual is not null && ImGui.BeginPopupContextItem(c.Id + "-menu"))
        {
            if (ImGui.MenuItem(StageLoc.Get(StageStr.PropsClearManual)))
                SetManual(engine, entity, c, null, discrete: true);
            ImGui.EndPopup();
        }

        if (changed && !locked)
            SetManual(engine, entity, c, Math.Clamp(value, c.Min, c.Max), discrete);
        if (finished)
            _stage.Undo.BreakMerge();
    }

    /// <summary>
    /// Sets (or clears) the manual value of a control on the primary fixture and every other selected fixture that has
    /// the attribute. Slider steps of one drag merge into one undo step; a <paramref name="discrete"/> change (a wheel
    /// slot picked, a value cleared) is a step of its own.
    /// </summary>
    internal void SetManual(StageEngine engine, Entity entity, FixtureControl control, float? value, bool discrete = false)
    {
        var targets = new List<FixtureComponent>();
        if (entity.Get<FixtureComponent>() is { } primary && !engine.Dmx.IsConsoleDriven(entity.Id))
            targets.Add(primary);
        foreach (var id in _stage.Selection.Ids)
        {
            if (id == entity.Id || _stage.Scene.Find(id) is not { } other || other.Get<FixtureComponent>() is not { } fixture)
                continue;
            if (engine.Dmx.IsConsoleDriven(id))
                continue;
            if (engine.Dmx.GetState(id)?.Profile is { } profile && profile.FindChannels(control.Attribute).Count > 0)
                targets.Add(fixture);
        }

        if (targets.Count == 0)
            return;
        string description = targets.Count > 1
            ? $"Set {control.Attribute} of {targets.Count} fixtures"
            : $"Set {control.Attribute} of {entity.Name}";
        _stage.Undo.Execute(new ManualAttributeCommand(targets, control.Attribute, value, description));
        if (discrete)
            _stage.Undo.BreakMerge();
    }

    private bool HasManualValues()
    {
        foreach (var c in _controls.Controls)
        {
            if (c.Manual is not null)
                return true;
        }

        return false;
    }

    private void ResetManual(Entity entity)
    {
        if (entity.Get<FixtureComponent>() is not { } fixture)
            return;
        _stage.Undo.Execute(ModifyComponentCommand.Create(fixture, f => f.ManualAttributes = new Dictionary<string, float>(),
            $"Reset manual values of {entity.Name}"));
        _stage.Undo.BreakMerge();
    }

    private void Patch(Entity entity, FixtureComponent fixture, StageEngine? engine, float labelWidth)
    {
        if (_patchFor != entity.Id || !ImGui.IsAnyItemActive())
        {
            _patchFor = entity.Id;
            _fixtureId = fixture.FixtureId;
            var addresses = fixture.Addresses ?? new List<DmxAddress>();
            for (int i = 0; i < _patchUniverse.Length; i++)
            {
                _patchUniverse[i] = i < addresses.Count ? addresses[i].Universe : 1;
                _patchAddress[i] = i < addresses.Count ? addresses[i].Address : 1;
            }
        }

        var profile = _controls.Profile;
        if (!PropertyGrid.Begin("##patch", labelWidth))
            return;
        PropertyGrid.Row(StageLoc.Get(StageStr.PatchFixtureId));
        ImGui.InputInt("##fixture-id", ref _fixtureId);
        if (ImGui.IsItemDeactivatedAfterEdit() && _fixtureId != fixture.FixtureId && _fixtureId >= 0)
            SetFixtureId(entity, fixture, _fixtureId);

        PropertyGrid.Row(StageLoc.Get(StageStr.PatchMode));
        string mode = profile?.Mode.Name ?? fixture.GdtfMode ?? "—";
        if (ImGui.BeginCombo("##mode", mode))
        {
            if (profile is not null)
            {
                foreach (var m in profile.FixtureType.DmxModes)
                {
                    if (ImGui.Selectable(m.Name, m.Name == profile.Mode.Name) && m.Name != profile.Mode.Name)
                        SetMode(entity, fixture, m.Name);
                }
            }
            else
            {
                ImGui.TextDisabled(StageLoc.Get(StageStr.PatchNoModes));
            }

            ImGui.EndCombo();
        }

        int breaks = Math.Clamp(Math.Max(profile?.Breaks.Count ?? 1, fixture.Addresses?.Count ?? 0), 1, _patchUniverse.Length);
        for (int b = 0; b < breaks; b++)
        {
            ImGui.PushID(b);
            PropertyGrid.Row(breaks > 1 ? StageLoc.Format(StageStr.PatchAddressBreak, b + 1) : StageLoc.Get(StageStr.PatchAddress));
            var style = ImGui.GetStyle();
            float half = (ImGui.GetContentRegionAvail().X - style.ItemInnerSpacing.X) * 0.5f;
            ImGui.SetNextItemWidth(half);
            ImGui.InputInt("##universe", ref _patchUniverse[b], 1, 10);
            bool done = ImGui.IsItemDeactivatedAfterEdit();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StageLoc.Get(StageStr.PatchUniverse));
            ImGui.SameLine(0f, style.ItemInnerSpacing.X);
            ImGui.SetNextItemWidth(half);
            ImGui.InputInt("##address", ref _patchAddress[b], 1, 10);
            done |= ImGui.IsItemDeactivatedAfterEdit();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StageLoc.Get(StageStr.PatchAddressHint));
            if (done)
                ApplyAddress(entity, fixture, b, profile?.Breaks.Count > b ? profile.Breaks[b].Break : b + 1);
            ImGui.PopID();
        }

        if (engine is not null)
        {
            PropertyGrid.Row(StageLoc.Get(StageStr.PatchRange));
            ImGui.TextUnformatted(PatchRangeText(entity));
        }

        PropertyGrid.End();
        if (engine is not null)
            PatchIssues(engine, entity);
    }

    /// <summary>Sets the fixture id as one undo step of its own (two edits in a row are two steps).</summary>
    internal void SetFixtureId(Entity entity, FixtureComponent fixture, int fixtureId)
    {
        if (fixtureId < 0 || fixtureId == fixture.FixtureId)
            return;
        _stage.Undo.Execute(ModifyComponentCommand.Create(fixture, f => f.FixtureId = fixtureId, $"Set fixture id of {entity.Name}"));
        _stage.Undo.BreakMerge();
    }

    /// <summary>Changes the DMX mode as one undo step of its own (two mode changes in a row are two steps).</summary>
    internal void SetMode(Entity entity, FixtureComponent fixture, string mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(mode);
        if (string.Equals(fixture.GdtfMode, mode, StringComparison.Ordinal))
            return;
        _stage.Undo.Execute(ModifyComponentCommand.Create(fixture, f => f.GdtfMode = mode, $"Change mode of {entity.Name}"));
        _stage.Undo.BreakMerge();
    }

    private void ApplyAddress(Entity entity, FixtureComponent fixture, int index, int dmxBreak)
    {
        _patchUniverse[index] = Math.Clamp(_patchUniverse[index], 1, 63999);
        _patchAddress[index] = Math.Clamp(_patchAddress[index], 1, 512);
        PatchTo(entity, fixture, index, dmxBreak, _patchUniverse[index], _patchAddress[index]);
    }

    /// <summary>
    /// Patches DMX break <paramref name="index"/> (0-based in the address list; GDTF break number
    /// <paramref name="dmxBreak"/>) of the fixture to <paramref name="universe"/>.<paramref name="address"/> as one undo
    /// step; values out of range are clamped, an unchanged address records nothing. Returns true when it changed.
    /// </summary>
    internal bool PatchTo(Entity entity, FixtureComponent fixture, int index, int dmxBreak, int universe, int address)
    {
        if (index < 0 || index >= _patchUniverse.Length)
            return false;
        universe = Math.Clamp(universe, 1, 63999);
        address = Math.Clamp(address, 1, 512);
        var current = fixture.Addresses ?? new List<DmxAddress>();
        if (index < current.Count && current[index].Universe == universe && current[index].Address == address)
            return false;
        var list = new List<DmxAddress>(current);
        while (list.Count <= index)
            list.Add(new DmxAddress(universe, address, list.Count + 1));
        list[index] = new DmxAddress(universe, address, Math.Max(1, dmxBreak));
        _stage.Undo.Execute(ModifyComponentCommand.Create(fixture, f => f.Addresses = list, $"Patch {entity.Name} to {universe}.{address:D3}"));
        _stage.Undo.BreakMerge();
        return true;
    }

    private string PatchRangeText(Entity entity)
    {
        var patch = _stage.Tools.External.Patch;
        if (ReferenceEquals(patch, _patchRangeOf) && entity.Id == _patchRangeFor && _patchRange.Length > 0 &&
            string.Equals(_patchRangeLanguage, Loc.Language, StringComparison.Ordinal))
            return _patchRange;
        _patchRangeOf = patch;
        _patchRangeFor = entity.Id;
        _patchRangeLanguage = Loc.Language;
        var entry = patch?.Find(entity.Id);
        _patchRange = entry is null || entry.Ranges.Count == 0
            ? _controls.Profile is { } p ? StageLoc.Format(StageStr.PatchFootprint, p.Footprint) : "—"
            : string.Join(", ", entry.Ranges.Select(r => r.ToString())) + " · " + StageLoc.Format(StageStr.PatchFootprint, entry.Ranges.Sum(r => r.Length));
        return _patchRange;
    }

    private void PatchIssues(StageEngine engine, Entity entity)
    {
        var current = engine.Issues.Current;
        if (!ReferenceEquals(current, _issuesOf) || _issuesFor != entity.Id)
        {
            _issuesOf = current;
            _issuesFor = entity.Id;
            _issues.Clear();
            foreach (var issue in current)
            {
                if (issue.Category == IssueCategory.Patch && issue.Entities.Contains(entity.Id))
                    _issues.Add(issue);
                if (_issues.Count >= 4)
                    break;
            }
        }

        foreach (var issue in _issues)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, issue.Severity == IssueSeverity.Error ? Theme.Error : issue.Severity == IssueSeverity.Warning ? Theme.Warning : Theme.Note);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(Icons.TriangleAlert + "  " + issue.Message);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
    }

    private static void Muted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }
}
