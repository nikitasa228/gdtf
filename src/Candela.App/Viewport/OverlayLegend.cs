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
/// for Lux and Noise the gradient bar from the smallest to the largest value with its colour stops — drag, add, remove,
/// recolour — the range limits (outside → grey), linear/log scale and the preset dropdown (Default / Custom / Save as…);
/// for Cables the colour of every cable type used in the scene. The gradients are the shared ones of
/// <see cref="StageToolState"/> (every viewport, the renderer's <see cref="ViewOptions"/> and the project's <c>view</c>
/// settings section use the same objects).
/// </summary>
internal sealed class OverlayLegend
{
    private static readonly ConditionalWeakTable<ViewportPanel, OverlayLegend> s_legends = new();

    private readonly GradientEditor _luxEditor = new("lux", "lx");
    private readonly GradientEditor _noiseEditor = new("noise", "dB");
    private PresetCombo<ColorGradient>? _luxPresets;
    private PresetCombo<ColorGradient>? _noisePresets;
    private StageToolState? _presetsOf;
    private float _height = 120f;
    private int _appliedVersion;
    private bool _expanded;
    private long _cablesVersion = -1;
    private readonly List<(CableType Type, string? Detail)> _cables = new();
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

    /// <summary>Draws the legend window for <paramref name="mode"/> in the corner of the image rectangle; true while hovered.</summary>
    internal bool DrawWindow(StageToolState stage, StageEngine? engine, OverlayMode mode, Vector2 imageMin, Vector2 imageSize)
    {
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        float margin = 8f * scale;
        float width = MathF.Min(310f * scale, imageSize.X - 2f * margin);
        if (width < 120f * scale || imageSize.Y < 80f * scale)
            return false;
        EnsurePresets(stage);
        var pos = new Vector2(imageMin.X + imageSize.X - width - margin, imageMin.Y + imageSize.Y - _height - margin);
        pos.Y = MathF.Max(pos.Y, imageMin.Y + margin);
        ImGui.SetCursorScreenPos(pos);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.OverlayBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f) * scale);
        bool hovered = false;
        if (ImGui.BeginChild(LegendId(mode), new Vector2(width, 0f), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings))
        {
            switch (mode)
            {
                case OverlayMode.Lux:
                    GradientContent(stage.Lux, _luxPresets!, _luxEditor, Icons.Sun, StageStr.LegendLux, StageStr.LegendLuxHint);
                    break;
                case OverlayMode.Noise:
                    GradientContent(stage.Noise, _noisePresets!, _noiseEditor, Icons.AudioLines, StageStr.LegendNoise, StageStr.LegendNoiseHint);
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
        _ => "##legend-cables",
    };

    private void EnsurePresets(StageToolState stage)
    {
        if (ReferenceEquals(_presetsOf, stage))
            return;
        _presetsOf = stage;
        _luxPresets = new PresetCombo<ColorGradient>(stage.Lux, "lux");
        _noisePresets = new PresetCombo<ColorGradient>(stage.Noise, "noise");
    }

    private void GradientContent(PresetBinding<ColorGradient> binding, PresetCombo<ColorGradient> presets, GradientEditor editor, string icon,
        StageStr title, StageStr hint)
    {
        var gradient = binding.Live;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(StageLoc.With(icon, title));
        ImGui.SameLine();
        float button = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - button);
        if (ImGui.Button(_expanded ? Icons.ChevronDown + "##legend-more" : Icons.ChevronRight + "##legend-more", new Vector2(button, 0f)))
            _expanded = !_expanded;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StageLoc.Get(_expanded ? StageStr.LegendLess : StageStr.LegendMore));

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
            if (ImGui.InputFloat("##min", ref min, 0f, 0f, "%.3g") && GradientEditor.SetRange(gradient, true, min))
                binding.MarkEdited();
            PropertyGrid.Row(StageLoc.Get(StageStr.LegendRangeMax));
            float max = gradient.RangeMax;
            if (ImGui.InputFloat("##max", ref max, 0f, 0f, "%.3g") && GradientEditor.SetRange(gradient, false, max))
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
                if (ImGui.InputFloat("##stop-value", ref value, 0f, 0f, "%.3g") && float.IsFinite(value) && (!gradient.Logarithmic || value > 0f))
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
        var dl = ImGui.GetWindowDrawList();
        float swatch = ImGui.GetTextLineHeight();
        if (!_cablesUsed)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(StageLoc.Get(StageStr.LegendNoCables));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        foreach (var (type, detail) in _cables)
        {
            var p = ImGui.GetCursorScreenPos();
            var (r, g, b) = type.ColorSrgb;
            dl.AddRectFilled(p + new Vector2(0f, swatch * 0.35f), p + new Vector2(swatch * 1.6f, swatch * 0.7f), Theme.U32(new Vector4(r, g, b, 1f)), 2f);
            ImGui.Dummy(new Vector2(swatch * 1.8f, swatch));
            ImGui.SameLine();
            ImGui.TextUnformatted(type.DisplayName);
            if (detail is not null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(detail);
            }
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

    /// <summary>Counts the cables of each type (when the scene changed); used types first, in catalogue order.</summary>
    private void RefreshCables(StageEngine? engine)
    {
        long version = engine?.Scene.Version ?? -2;
        if (version == _cablesVersion && string.Equals(_language, Loc.Language, StringComparison.Ordinal))
            return;
        _cablesVersion = version;
        _language = Loc.Language;
        _cables.Clear();
        _cablesUsed = false;
        _cablesSummary = StageLoc.Get(StageStr.LegendCablesHint);
        var catalog = engine?.Network.Catalog ?? CableCatalog.Default;
        if (engine is null)
        {
            foreach (var type in catalog.All)
                _cables.Add((type, null));
            return;
        }

        var counts = new Dictionary<string, (int Count, float Length)>(StringComparer.OrdinalIgnoreCase);
        int unknown = 0;
        try
        {
            foreach (var cable in engine.NetworkSolution.Cables)
            {
                if (cable.Type is null)
                {
                    unknown++;
                    continue;
                }

                var (n, l) = counts.GetValueOrDefault(cable.Type.Id);
                counts[cable.Type.Id] = (n + 1, l + (float.IsFinite(cable.LengthM) ? cable.LengthM : 0f));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            counts.Clear();
        }

        foreach (var type in catalog.All)
        {
            if (counts.TryGetValue(type.Id, out var c))
                _cables.Add((type, string.Create(CultureInfo.InvariantCulture, $"× {c.Count} · {c.Length:0.#} m")));
        }

        _cablesUsed = _cables.Count > 0;
        if (!_cablesUsed)
        {
            foreach (var type in catalog.All)
                _cables.Add((type, null));
        }

        if (unknown > 0)
            _cablesSummary += " " + StageLoc.Format(StageStr.LegendUnknownCables, unknown);
    }
}
