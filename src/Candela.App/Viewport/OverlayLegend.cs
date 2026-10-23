using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Electrical;
using Candela.Engine;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Viewport;

/// <summary>
/// The legend window in the bottom-right corner of a viewport that shows an analysis overlay (requirement "Оверлеи"):
/// for Lux, Noise and Smoke the gradient bar from the smallest to the largest value with its colour stops — drag, add,
/// remove, recolour — the range limits (outside → grey, for smoke: no colour), linear/log scale and the preset dropdown
/// (Default / Custom / Save as…); for Smoke also the haze, the densest air and the emitters with their output, whose
/// names the viewport writes next to their arrows; for Cables the colour of every cable type used in the scene. The
/// gradients are the shared ones of <see cref="StageToolState"/> (every viewport, the renderer's
/// <see cref="ViewOptions"/> and the project's <c>view</c> settings section use the same objects).
/// </summary>
internal sealed class OverlayLegend
{
    private static readonly ConditionalWeakTable<ViewportPanel, OverlayLegend> s_legends = new();

    private readonly GradientEditor _luxEditor = new("lux", "lx");
    private readonly GradientEditor _noiseEditor = new("noise", "dB");
    private readonly GradientEditor _smokeEditor = new("smoke", "/m");
    private readonly SmokeEmitterLabels _emitters = new();
    private PresetCombo<ColorGradient>? _luxPresets;
    private PresetCombo<ColorGradient>? _noisePresets;
    private PresetCombo<ColorGradient>? _smokePresets;
    private StageToolState? _presetsOf;
    private float _height = 120f;
    private int _appliedVersion;
    private bool _expanded;
    private long _cablesVersion = -1;
    private object? _cablesScene;
    private readonly List<(CableType Type, string Name, string? Detail, bool Used)> _cables = new();
    private int _usedCount;
    private string _othersLabel = "";
    private bool _cablesUsed;
    private string _cablesSummary = "";
    private string _language = "";

    /// <summary>True when the range and stop fields are shown below the bar.</summary>
    public bool Expanded
    {
        get => _expanded;
        set => _expanded = value;
    }

    /// <summary>
    /// The viewport hook (called while a viewport is drawn, after its image): points the viewport at the shared overlay
    /// gradients, applies the Preview panel's one-shot viewport settings (EV100, grid), remembers the active viewport for
    /// renders, and draws the legend when an overlay is shown. Returns true while the mouse is over the legend (the
    /// viewport then ignores the mouse).
    /// </summary>
    public static bool Draw(WorkspaceContext context, ViewportPanel viewport, Vector2 imageMin, Vector2 imageSize, bool active)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(viewport);
        var stage = context.Tools.Stage;
        var legend = s_legends.GetValue(viewport, static _ => new OverlayLegend());
        legend.Apply(stage, viewport, active);
        if (viewport.View.Overlay == OverlayMode.None)
            return false;
        if (viewport.View.Overlay == OverlayMode.Smoke && context.Session.Engine is { } engine)
            legend._emitters.DrawLabels(engine, viewport.Camera, imageMin, imageSize, legend.Bounds(imageMin, imageSize));
        return legend.DrawWindow(stage, context.Session.Engine, viewport.View.Overlay, imageMin, imageSize);
    }

    /// <summary>Connects a viewport to the shared state (gradients, one-shot settings, active viewport).</summary>
    internal void Apply(StageToolState stage, ViewportPanel viewport, bool active)
    {
        var view = viewport.View;
        if (!ReferenceEquals(view.LuxGradient, stage.Lux.Live))
            view.LuxGradient = stage.Lux.Live;
        if (!ReferenceEquals(view.NoiseGradient, stage.Noise.Live))
            view.NoiseGradient = stage.Noise.Live;
        if (!ReferenceEquals(view.SmokeGradient, stage.Smoke.Live))
            view.SmokeGradient = stage.Smoke.Live;
        if (_appliedVersion != stage.ViewportApplyVersion)
        {
            _appliedVersion = stage.ViewportApplyVersion;
            if (stage.ViewportEv100 is { } ev)
                viewport.Camera.Ev100 = ev;
            if (stage.ViewportGrid is { } grid)
                view.ShowGrid = grid;
        }

        if (active || stage.ActiveViewport is null || !stage.ActiveViewport.IsOpen)
            stage.ActiveViewport = viewport;
    }

    /// <summary>
    /// Where the legend window goes in the image rectangle (screen pixels: min x, min y, max x, max y; its height from
    /// the last frame), or an empty rectangle when the view is too small for it.
    /// </summary>
    internal Vector4 Bounds(Vector2 imageMin, Vector2 imageSize)
    {
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        float margin = 8f * scale;
        float width = MathF.Min(310f * scale, imageSize.X - 2f * margin);
        if (width < 120f * scale || imageSize.Y < 80f * scale)
            return Vector4.Zero;
        var pos = new Vector2(imageMin.X + imageSize.X - width - margin, imageMin.Y + imageSize.Y - _height - margin);
        pos.Y = MathF.Max(pos.Y, imageMin.Y + margin);
        return new Vector4(pos.X, pos.Y, pos.X + width, pos.Y + _height);
    }

    /// <summary>Draws the legend window for <paramref name="mode"/> in the corner of the image rectangle; true while hovered.</summary>
    internal bool DrawWindow(StageToolState stage, StageEngine? engine, OverlayMode mode, Vector2 imageMin, Vector2 imageSize)
    {
        var bounds = Bounds(imageMin, imageSize);
        if (bounds.Z <= bounds.X)
            return false;
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        float width = bounds.Z - bounds.X;
        EnsurePresets(stage);
        var pos = new Vector2(bounds.X, bounds.Y);
        ImGui.SetCursorScreenPos(pos);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.OverlayBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f) * scale);
        bool hovered = false;
        if (ImGui.BeginChild(LegendId(mode), new Vector2(width, 0f), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings))
        {
            _luxEditor.Unit = StageUnits.Current.Lux;
            _noiseEditor.Unit = StageUnits.Current.Decibel;
            _smokeEditor.Unit = StageUnits.Current.PerMetre;
            switch (mode)
            {
                case OverlayMode.Lux:
                    GradientContent(stage.Lux, _luxPresets!, _luxEditor, Icons.Sun, StageStr.LegendLux, StageStr.LegendLuxHint);
                    break;
                case OverlayMode.Noise:
                    GradientContent(stage.Noise, _noisePresets!, _noiseEditor, Icons.AudioLines, StageStr.LegendNoise, StageStr.LegendNoiseHint);
                    break;
                case OverlayMode.Smoke:
                    GradientContent(stage.Smoke, _smokePresets!, _smokeEditor, Icons.CloudFog, StageStr.LegendSmoke, StageStr.LegendSmokeHint);
                    if (engine is not null)
                        _emitters.DrawList(engine);
                    break;
                default:
                    CablesContent(engine);
                    break;
            }

            hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) ||
                      ImGui.IsAnyItemActive() && ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
            _height = ImGui.GetWindowHeight();
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
        return hovered;
    }

    private static string LegendId(OverlayMode mode) => mode switch
    {
        OverlayMode.Lux => "##legend-lux",
        OverlayMode.Noise => "##legend-noise",
        OverlayMode.Smoke => "##legend-smoke",
        _ => "##legend-cables",
    };

    private void EnsurePresets(StageToolState stage)
    {
        if (ReferenceEquals(_presetsOf, stage))
            return;
        _presetsOf = stage;
        _luxPresets = new PresetCombo<ColorGradient>(stage.Lux, "lux");
        _noisePresets = new PresetCombo<ColorGradient>(stage.Noise, "noise");
        _smokePresets = new PresetCombo<ColorGradient>(stage.Smoke, "smoke");
    }

    private void GradientContent(PresetBinding<ColorGradient> binding, PresetCombo<ColorGradient> presets, GradientEditor editor, string icon,
        StageStr title, StageStr hint)
    {
        var gradient = binding.Live;
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(StageLoc.With(icon, title));
        ImGui.SameLine();
        float button = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(right - button);
        if (ImGui.Button(_expanded ? Icons.ChevronDown + "##legend-more" : Icons.ChevronRight + "##legend-more", new Vector2(button, 0f)))
            _expanded = !_expanded;
        if (ImGui.IsItemHovered())
            SafeText.Tooltip(StageLoc.Get(_expanded ? StageStr.LegendLess : StageStr.LegendMore));

        presets.Draw();
        if (editor.Draw(gradient))
            binding.MarkEdited();
        if (!_expanded)
            return;

        ImGui.Spacing();
        if (PropertyGrid.Begin("##legend-grid", 70f))
        {
            PropertyGrid.Row(StageLoc.Get(StageStr.LegendRangeMin));
            float min = gradient.RangeMin;
            if (ImGui.InputFloat("##min", ref min, 0f, 0f, "%.6g") && GradientEditor.SetRange(gradient, true, min))
                binding.MarkEdited();
            PropertyGrid.Row(StageLoc.Get(StageStr.LegendRangeMax));
            float max = gradient.RangeMax;
            if (ImGui.InputFloat("##max", ref max, 0f, 0f, "%.6g") && GradientEditor.SetRange(gradient, false, max))
                binding.MarkEdited();
            PropertyGrid.Row(StageLoc.Get(StageStr.LegendLogScale));
            bool log = gradient.Logarithmic;
            if (ImGui.Checkbox("##log", ref log))
            {
                gradient.Logarithmic = log;
                if (log)
                {
                    // A log scale needs positive values.
                    gradient.RangeMin = MathF.Max(gradient.RangeMin, 1e-3f);
                    gradient.RangeMax = MathF.Max(gradient.RangeMax, gradient.RangeMin * 10f);
                    for (int i = 0; i < gradient.Stops.Count; i++)
                    {
                        if (gradient.Stops[i].Value <= 0f)
                            gradient.Stops[i] = gradient.Stops[i] with { Value = gradient.RangeMin };
                    }
                }

                binding.MarkEdited();
            }

            int selected = editor.Selected;
            if (selected >= 0 && selected < gradient.Stops.Count)
            {
                PropertyGrid.Row(StageLoc.Get(StageStr.LegendStop));
                var stop = gradient.Stops[selected];
                var color = stop.Color;
                if (ImGui.ColorButton("##stop-color", color, ImGuiColorEditFlags.AlphaPreviewHalf, new Vector2(ImGui.GetFrameHeight())))
                    editor.OpenColor(gradient, selected);
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                float value = stop.Value;
                ImGui.SetNextItemWidth(MathF.Max(40f, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemInnerSpacing.X));
                if (ImGui.InputFloat("##stop-value", ref value, 0f, 0f, "%.6g") && float.IsFinite(value) && (!gradient.Logarithmic || value > 0f))
                {
                    gradient.Stops[selected] = stop with { Value = value };
                    binding.MarkEdited();
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                    GradientEditor.SortStops(gradient, selected);
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.BeginDisabled(gradient.Stops.Count <= 2);
                if (ImGui.Button(Icons.Trash + "##stop-remove", new Vector2(ImGui.GetFrameHeight(), 0f)) && editor.RemoveSelected(gradient))
                    binding.MarkEdited();
                ImGui.EndDisabled();
            }

            PropertyGrid.End();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(StageLoc.Get(hint));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private void CablesContent(StageEngine? engine)
    {
        ImGui.TextUnformatted(StageLoc.With(Icons.Cable, StageStr.LegendCables));
        RefreshCables(engine);
        if (!_cablesUsed)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(StageLoc.Get(StageStr.LegendNoCables));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        // The types in the scene with their count, length and weight; the rest of the catalogue below (folded while
        // the scene has cables, so the legend stays small).
        for (int i = 0; i < _cables.Count; i++)
        {
            if (_cables[i].Used || !_cablesUsed)
                LegendRow(_cables[i].Type, _cables[i].Name, _cables[i].Detail, 1f);
        }

        if (_cablesUsed && _cables.Count > _usedCount && ImGui.TreeNodeEx(_othersLabel, ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            for (int i = 0; i < _cables.Count; i++)
            {
                if (!_cables[i].Used)
                    LegendRow(_cables[i].Type, _cables[i].Name, null, 0.6f);
            }

            ImGui.TreePop();
        }

        if (_cablesSummary.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(_cablesSummary);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
    }

    private static void LegendRow(CableType type, string name, string? detail, float alpha)
    {
        var dl = ImGui.GetWindowDrawList();
        float swatch = ImGui.GetTextLineHeight();
        var p = ImGui.GetCursorScreenPos();
        var (r, g, b) = type.ColorSrgb;
        // The colour of the cable's line in the overlay, and the drawing of its connector.
        dl.AddRectFilled(p + new Vector2(0f, swatch * 0.35f), p + new Vector2(swatch * 1.6f, swatch * 0.7f), Theme.U32(new Vector4(r, g, b, alpha)), 2f);
        ImGui.Dummy(new Vector2(swatch * 1.7f, swatch));
        ImGui.SameLine();
        var icon = ImGui.GetCursorScreenPos();
        ConnectorIcons.Draw(dl, icon, swatch, type.ConnectorA.IllustrationKey);
        ImGui.Dummy(new Vector2(swatch, swatch));
        ImGui.SameLine();
        float textX = ImGui.GetCursorPosX();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.WithAlpha(Theme.Text, alpha));
        ImGui.TextUnformatted(name);
        ImGui.PopStyleColor();
        if (detail is not null)
        {
            // Beside the name when it fits, else on its own line under the name.
            ImGui.SameLine();
            if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(detail).X)
            {
                ImGui.NewLine();
                ImGui.SetCursorPosX(textX);
            }

            SafeText.Disabled(detail);
        }
    }

    /// <summary>Counts the cables of each type (when the scene changed); used types first, in catalogue order.</summary>
    private void RefreshCables(StageEngine? engine)
    {
        long version = engine?.Scene.Version ?? -2;
        // Another scene (a file opened) can have the same version number: the scene itself is part of the key.
        if (version == _cablesVersion && ReferenceEquals(engine?.Scene, _cablesScene) && string.Equals(_language, Loc.Language, StringComparison.Ordinal))
            return;
        _cablesVersion = version;
        _cablesScene = engine?.Scene;
        _language = Loc.Language;
        _cables.Clear();
        _cablesUsed = false;
        _usedCount = 0;
        _othersLabel = CableLoc.Get(CableStr.LegendOthers);
        _cablesSummary = StageLoc.Get(StageStr.LegendCablesHint);
        var catalog = engine?.Network.Catalog ?? CableCatalog.Default;
        var counts = new Dictionary<string, (int Count, float Length, float Weight)>(StringComparer.OrdinalIgnoreCase);
        int unknown = 0;
        if (engine is not null)
        {
            try
            {
                foreach (var cable in CablingNetwork.Current(engine).Cables)
                {
                    if (cable.Type is null)
                    {
                        unknown++;
                        continue;
                    }

                    var (n, l, w) = counts.GetValueOrDefault(cable.Type.Id);
                    float length = float.IsFinite(cable.LengthM) ? cable.LengthM : 0f;
                    counts[cable.Type.Id] = (n + 1, l + length, w + (float.IsFinite(cable.WeightKg) ? cable.WeightKg : 0f));
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                counts.Clear();
            }
        }

        var inv = CultureInfo.InvariantCulture;
        foreach (var type in catalog.All)
        {
            if (counts.TryGetValue(type.Id, out var c))
            {
                _cables.Add((type, CableLoc.CableName(type), CableLoc.Format(CableStr.LegendInScene, c.Count, c.Length.ToString("0.#", inv), c.Weight.ToString("0.#", inv)), true));
                _usedCount++;
            }
        }

        _cablesUsed = _usedCount > 0;
        foreach (var type in catalog.All)
        {
            if (!counts.ContainsKey(type.Id))
                _cables.Add((type, CableLoc.CableName(type), null, false));
        }

        if (unknown > 0)
            _cablesSummary += " " + StageLoc.Format(StageStr.LegendUnknownCables, unknown);
    }
}
