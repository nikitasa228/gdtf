using System.Numerics;
using Candela.App.Docking;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.SceneGraph.Components;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The cable link window (requirement "Окно визуализации связей кабеля"): opened by clicking a cable in a viewport or
/// the cable next to a connector in the Properties panel, it draws a strip that links every device of the cable's line
/// in signal order — the console (or power source), then each device with the port the line enters through, its
/// address, and the cables between them with their labels, indices ("DMX A · cable 3") and lengths; the cable the
/// window was opened for is highlighted. Clicking a device selects it (the Properties panel shows it), clicking a
/// cable follows it. The panel also follows the selection when a cable is selected.
/// </summary>
internal sealed class CableLinksPanel : Panel
{
    private readonly StagePanelContext _stage;
    private readonly Action? _showOverlay;
    private Guid? _cable;
    private Guid? _lastPrimary;
    private CableLinkModel? _model;
    private object? _modelSolution;
    private Guid? _modelCable;
    private string _modelLanguage = "";
    private float _textScale = float.NaN;
    private string[] _names = Array.Empty<string>();
    private string[] _details = Array.Empty<string>();
    private string[] _inPorts = Array.Empty<string>();
    private string _header = "";
    private string _counts = "";

    /// <summary>Creates the panel; <paramref name="showOverlay"/> switches the active viewport to the cable overlay.</summary>
    public CableLinksPanel(StagePanelContext stage, Action? showOverlay = null)
        : base("CableLinks", Icons.Cable)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _showOverlay = showOverlay;
    }

    /// <inheritdoc/>
    public override string Title => CableLoc.Get(CableStr.PanelCableLinks);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(720, 260);

    /// <summary>The cable shown, or null.</summary>
    public Guid? Cable => _cable;

    /// <summary>The model of the strip shown (tests).</summary>
    internal CableLinkModel? Model => _model;

    /// <summary>Shows <paramref name="cableId"/>.</summary>
    public void Show(Guid cableId) => _cable = cableId;

    /// <summary>Selects the device of card <paramref name="index"/> (the Properties panel shows it).</summary>
    internal void SelectCard(int index)
    {
        if (_model is not { } model || index < 0 || index >= model.Cards.Count)
            return;
        var id = model.Cards[index].EntityId;
        if (_stage.Scene.Find(id) is not null)
        {
            _stage.Selection.Set(id);
            _lastPrimary = id;
        }
    }

    /// <summary>Shows the cable of link <paramref name="index"/>.</summary>
    internal void FollowLink(int index)
    {
        if (_model is { } model && index >= 0 && index < model.Links.Count)
            _cable = model.Links[index].CableId;
    }

    /// <summary>Rebuilds the model when the cable, the network or the language changed (also called by tests).</summary>
    internal void Refresh()
    {
        // Follows the selection when the user selects a cable elsewhere (outliner, script, viewport).
        var primary = _stage.Selection.Primary;
        if (primary != _lastPrimary)
        {
            _lastPrimary = primary;
            if (primary is { } id && _stage.Scene.Find(id)?.Has<CableComponent>() == true)
                _cable = id;
        }

        var engine = _stage.Engine;
        if (engine is null || _cable is not { } cable)
        {
            _model = null;
            return;
        }

        object solution = engine.NetworkSolution;
        if (_model is not null && ReferenceEquals(solution, _modelSolution) && _modelCable == cable && _modelLanguage == Loc.Language)
            return;
        _modelSolution = solution;
        _modelCable = cable;
        _modelLanguage = Loc.Language;
        _model = CableLinkModel.Build(engine, cable);
        _textScale = float.NaN;
        if (_model is { } m)
        {
            _header = $"{m.Title}  ·  {m.TypeName}  ·  {m.Summary}";
            _counts = CableLoc.Format(CableStr.LinksDevices, m.Cards.Count) + (m.FreeEnds > 0 ? "  ·  " + CableLoc.Format(CableStr.LinksFreeEnds, m.FreeEnds) : "");
        }
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        Refresh();
        if (_stage.Engine is null)
        {
            PropertiesPanel.EmptyState(Icons.Cable, StageLoc.Get(StageStr.PropsNoEngine));
            return;
        }

        if (_model is not { } model)
        {
            PropertiesPanel.EmptyState(Icons.Cable, CableLoc.Get(_cable is null ? CableStr.LinksEmpty : CableStr.LinksCableGone));
            return;
        }

        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        Header(model, scale);
        ImGui.Separator();
        Strip(model, scale);
    }

    private void Header(CableLinkModel model, float scale)
    {
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddRectFilled(p + new Vector2(0f, h * 0.3f), p + new Vector2(h * 1.6f, h * 0.7f), Theme.U32(model.Color), 2f);
        ImGui.Dummy(new Vector2(h * 1.7f, h));
        ImGui.SameLine();
        ImGui.TextUnformatted(_header);
        if (model.Source is { } source)
        {
            ImGui.TextDisabled(CableLoc.Get(CableStr.LinksSource) + ":");
            ImGui.SameLine();
            ImGui.TextUnformatted(source);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Note);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(CableLoc.Get(CableStr.LinksUnfed));
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        ImGui.TextDisabled(_counts);
        ImGui.SameLine();
        if (ImGui.SmallButton(Icons.Pointer + "##select-cable"))
            _stage.Selection.Set(model.CableId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(CableLoc.Get(CableStr.PropsCable));
        if (_showOverlay is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Icons.Layers + "##cable-overlay"))
                _showOverlay();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(CableLoc.Get(CableStr.LinksOverlay));
        }

        _ = scale;
    }

    private void Strip(CableLinkModel model, float scale)
    {
        float line = ImGui.GetTextLineHeight();
        float cardW = 176f * scale, cardH = 3f * line + 18f * scale, gap = 112f * scale;
        float top = 2f * line + 16f * scale; // room for arcs and labels above the cards
        int n = model.Cards.Count;
        float contentW = n * cardW + Math.Max(0, n - 1) * gap + 16f * scale;
        float hintH = ImGui.GetTextLineHeightWithSpacing();
        var avail = ImGui.GetContentRegionAvail();
        if (!ImGui.BeginChild("##strip", new Vector2(0f, MathF.Max(cardH + top + 3f * line, avail.Y - hintH)), ImGuiChildFlags.None,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        PrepareTexts(model, cardW - 16f * scale - line * 1.4f, scale);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(8f * scale, top);
        var dl = ImGui.GetWindowDrawList();
        Vector2 CardMin(int i) => origin + new Vector2(i * (cardW + gap), 0f);

        // Cables first, under the cards.
        for (int i = 0; i < model.Links.Count; i++)
        {
            var link = model.Links[i];
            if (link.From < 0 || link.To < 0 || link.From >= n || link.To >= n)
                continue;
            float thickness = (link.Selected ? 5f : 3f) * scale;
            uint color = Theme.U32(link.Color);
            Vector2 labelAt;
            if (link.To == link.From + 1)
            {
                var a = CardMin(link.From) + new Vector2(cardW, cardH * 0.5f);
                var b = CardMin(link.To) + new Vector2(0f, cardH * 0.5f);
                if (link.Selected)
                    dl.AddLine(a, b, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.9f)), thickness + 4f * scale);
                dl.AddLine(a, b, color, thickness);
                labelAt = new Vector2((a.X + b.X) * 0.5f, a.Y);
            }
            else
            {
                // A branch from an earlier device: an arc over the cards between them.
                float rise = top - (6f + (i % 3) * 6f) * scale;
                var a = CardMin(link.From) + new Vector2(cardW * 0.5f, 0f);
                var b = CardMin(link.To) + new Vector2(cardW * 0.5f, 0f);
                var a2 = a - new Vector2(0f, rise);
                var b2 = b - new Vector2(0f, rise);
                if (link.Selected)
                {
                    uint hl = Theme.U32(Theme.WithAlpha(Theme.Accent, 0.9f));
                    dl.AddLine(a, a2, hl, thickness + 4f * scale);
                    dl.AddLine(a2, b2, hl, thickness + 4f * scale);
                    dl.AddLine(b2, b, hl, thickness + 4f * scale);
                }

                dl.AddLine(a, a2, color, thickness);
                dl.AddLine(a2, b2, color, thickness);
                dl.AddLine(b2, b, color, thickness);
                labelAt = new Vector2(b.X - cardW * 0.5f - gap * 0.5f, a2.Y + line * 0.6f);
            }

            // Label above the cable, index and length below; a click on them follows the cable.
            string label = link.Label;
            string sub = link.Index.Length > 0 ? link.Index : link.Length;
            var labelSize = ImGui.CalcTextSize(label);
            var subSize = ImGui.CalcTextSize(sub);
            var labelPos = labelAt - new Vector2(labelSize.X * 0.5f, labelSize.Y + 3f * scale);
            var subPos = labelAt + new Vector2(-subSize.X * 0.5f, 3f * scale);
            dl.AddText(labelPos, Theme.U32(link.Selected ? Theme.Accent : Theme.Text), label);
            dl.AddText(subPos, Theme.U32(Theme.TextMuted), sub);
            var hitMin = Vector2.Min(labelPos, subPos) - new Vector2(4f * scale);
            var hitMax = Vector2.Max(labelPos + labelSize, subPos + subSize) + new Vector2(4f * scale);
            ImGui.SetCursorScreenPos(hitMin);
            ImGui.PushID(1000 + i);
            if (ImGui.InvisibleButton("##link", Vector2.Max(hitMax - hitMin, new Vector2(1f))))
                FollowLink(i);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{link.Label}\n{link.Ports}\n{link.Length}{(link.Index.Length > 0 ? " · " + link.Index : "")}");
            ImGui.PopID();
        }

        // Device cards.
        var selected = _stage.Selection.Primary;
        for (int i = 0; i < n; i++)
        {
            var card = model.Cards[i];
            var min = CardMin(i);
            var max = min + new Vector2(cardW, cardH);
            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(i);
            bool clicked = ImGui.InvisibleButton("##card", new Vector2(cardW, cardH));
            bool hovered = ImGui.IsItemHovered();
            ImGui.PopID();
            if (clicked)
                SelectCard(i);
            if (hovered)
                ImGui.SetTooltip(string.Join("\n", new[] { card.Name, card.Detail, card.InPort }.Where(s => s.Length > 0)));
            bool isSelected = selected == card.EntityId;
            dl.AddRectFilled(min, max, Theme.U32(hovered ? Theme.Rgb(0x30333A) : Theme.Rgb(0x24262B)), 6f * scale);
            var border = isSelected ? Theme.Accent : card.OnCable ? model.Color : Theme.Rgb(0x3A3D44);
            dl.AddRect(min, max, Theme.U32(border), 6f * scale, ImDrawFlags.None, (isSelected || card.OnCable ? 2f : 1f) * scale);
            var text = min + new Vector2(8f * scale, 6f * scale);
            dl.AddText(text, Theme.U32(card.Depth == 0 ? Theme.Accent : Theme.TextMuted), card.Icon);
            dl.AddText(text + new Vector2(line * 1.4f, 0f), Theme.U32(Theme.Text), _names[i]);
            dl.AddText(text + new Vector2(0f, line + 3f * scale), Theme.U32(Theme.TextMuted), _details[i]);
            dl.AddText(text + new Vector2(0f, 2f * (line + 3f * scale)), Theme.U32(Theme.WithAlpha(Theme.TextMuted, 0.8f)), _inPorts[i]);
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, cardH + line));
        ImGui.Dummy(new Vector2(contentW, 1f));
        ImGui.EndChild();
        ImGui.TextDisabled(CableLoc.Get(CableStr.LinksClickHint));
    }

    /// <summary>Card texts fitted to the card width, rebuilt when the model or the UI scale changes.</summary>
    private void PrepareTexts(CableLinkModel model, float width, float scale)
    {
        if (_textScale == scale && _names.Length == model.Cards.Count)
            return;
        _textScale = scale;
        int n = model.Cards.Count;
        _names = new string[n];
        _details = new string[n];
        _inPorts = new string[n];
        static float Measure(string s) => ImGui.CalcTextSize(s).X;
        for (int i = 0; i < n; i++)
        {
            var c = model.Cards[i];
            _names[i] = TextFit.Truncate(c.Name, width, Measure);
            _details[i] = TextFit.Truncate(c.Detail, width + ImGui.GetTextLineHeight() * 1.4f, Measure);
            _inPorts[i] = TextFit.Truncate(c.InPort, width + ImGui.GetTextLineHeight() * 1.4f, Measure);
        }
    }
}
