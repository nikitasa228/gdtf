using System.Globalization;
using System.Numerics;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Edits an overlay <see cref="ColorGradient"/> in place (requirement "Оверлеи"): a bar with the gradient from its
/// smallest to its largest value and the value labels under it; colour stops shown as markers that can be dragged
/// (left button), added (click on the bar), recoloured (double-click or the context menu) and removed (right-click →
/// Remove, or Delete while dragging); two range handles above the bar limit what the overlay colours — values outside
/// are shown grey (<see cref="ColorGradient.OutOfRangeColor"/>), and the bar shows it there. Linear or logarithmic
/// scale. The pure value mapping is in static methods (tested without a UI). Main thread only.
/// </summary>
internal sealed class GradientEditor
{
    private const int Segments = 64;
    private string _unit;
    private readonly string _surfaceId;
    private readonly string _menuId;
    private readonly string _colorId;
    private readonly string[] _labelCache = new string[32];
    private readonly float[] _labelValues = new float[32];
    private int _dragStop = -1;
    private int _dragRange;
    private int _selected = -1;
    private int _colorStop = -1;
    private Vector4 _colorEdit;

    /// <summary>Creates an editor; <paramref name="unit"/> is appended to the value labels ("lx", "dB").</summary>
    public GradientEditor(string id, string unit)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _unit = unit ?? "";
        _surfaceId = "##gradient-" + id;
        _menuId = "##stop-menu-" + id;
        _colorId = "##stop-color-" + id;
        Array.Fill(_labelValues, float.NaN);
    }

    /// <summary>Unit appended to the values in tooltips ("lx", "dB"; follows the UI language when the owner sets it).</summary>
    public string Unit
    {
        get => _unit;
        set => _unit = value ?? "";
    }

    /// <summary>Index of the selected stop (−1 = none).</summary>
    public int Selected => _selected;

    /// <summary>
    /// Draws the bar, the stop markers, the range handles and the labels into a block of the given width (−1: the
    /// available width). Returns true when the gradient was changed this frame.
    /// </summary>
    public bool Draw(ColorGradient gradient, float width = -1f)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        gradient.Stops ??= new List<GradientStop>();
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        float w = width > 0f ? width : ImGui.GetContentRegionAvail().X;
        w = MathF.Max(w, 80f * scale);
        float handle = 7f * scale, barHeight = 16f * scale, marker = 9f * scale;
        float labelHeight = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorScreenPos();
        float pad = marker;
        var barMin = origin + new Vector2(pad, handle + 2f * scale);
        var barMax = new Vector2(origin.X + w - pad, barMin.Y + barHeight);
        float markerTop = barMax.Y + 1f * scale;
        float totalHeight = (barMin.Y - origin.Y) + barHeight + marker + 2f * scale + labelHeight + 2f * scale;
        var (lo, hi) = Domain(gradient);
        bool log = gradient.Logarithmic && lo > 0f;
        var dl = ImGui.GetWindowDrawList();
        bool changed = false;

        // Interaction surface first (it decides the hover of the frame).
        ImGui.InvisibleButton(_surfaceId, new Vector2(w, totalHeight), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetIO().MousePos;
        float barWidth = barMax.X - barMin.X;

        // Bar: gradient inside the range, out-of-range colour outside it.
        dl.AddRectFilled(barMin - new Vector2(1f), barMax + new Vector2(1f), Theme.U32(new Vector4(0f, 0f, 0f, 0.6f)));
        for (int i = 0; i < Segments; i++)
        {
            float t0 = i / (float)Segments, t1 = (i + 1) / (float)Segments;
            var c0 = ColorAt(gradient, ValueAt(t0, lo, hi, log));
            var c1 = ColorAt(gradient, ValueAt(t1, lo, hi, log));
            dl.AddRectFilledMultiColor(new Vector2(barMin.X + barWidth * t0, barMin.Y), new Vector2(barMin.X + barWidth * t1, barMax.Y),
                Theme.U32(c0), Theme.U32(c1), Theme.U32(c1), Theme.U32(c0));
        }

        dl.AddRect(barMin - new Vector2(1f), barMax + new Vector2(1f), Theme.U32(Theme.WithAlpha(Theme.Text, 0.35f)));

        // Range handles (brackets above the bar).
        for (int which = 1; which <= 2; which++)
        {
            float v = which == 1 ? gradient.RangeMin : gradient.RangeMax;
            float x = barMin.X + barWidth * PositionOf(v, lo, hi, log);
            bool hot = _dragRange == which || (hovered && _dragStop < 0 && _dragRange == 0 && MathF.Abs(mouse.X - x) <= handle &&
                                                  mouse.Y < barMin.Y + 2f * scale);
            uint color = Theme.U32(hot ? Theme.Accent : Theme.Text);
            float dir = which == 1 ? 1f : -1f;
            dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, barMax.Y + 1f), color, 2f * scale);
            dl.AddLine(new Vector2(x, origin.Y + 1f), new Vector2(x + dir * handle * 0.8f, origin.Y + 1f), color, 2f * scale);
            if (hot && hovered)
                ImGui.SetTooltip(FormatValue(v) + " " + _unit);
            if (hot && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _dragRange == 0)
                _dragRange = which;
        }

        // Stop markers (triangles below the bar, filled with the stop colour).
        int hoveredStop = -1;
        for (int i = 0; i < gradient.Stops.Count; i++)
        {
            var stop = gradient.Stops[i];
            float x = barMin.X + barWidth * PositionOf(stop.Value, lo, hi, log);
            var a = new Vector2(x, markerTop);
            var b = new Vector2(x - marker * 0.6f, markerTop + marker);
            var c = new Vector2(x + marker * 0.6f, markerTop + marker);
            bool hot = _dragStop == i || (hovered && _dragRange == 0 && MathF.Abs(mouse.X - x) <= marker * 0.7f && mouse.Y >= barMax.Y - 1f &&
                                          mouse.Y <= markerTop + marker + 2f * scale);
            if (hot && hoveredStop < 0)
                hoveredStop = i;
            dl.AddTriangleFilled(a, b, c, Theme.U32(stop.Color with { W = 1f }));
            dl.AddTriangle(a, b, c, Theme.U32(i == _selected ? Theme.Accent : hot ? Theme.Text : new Vector4(0f, 0f, 0f, 0.9f)), (i == _selected ? 2f : 1f) * scale);
        }

        // Value labels under the markers, skipping those that would overlap their left neighbour.
        float lastRight = float.NegativeInfinity;
        for (int i = 0; i < gradient.Stops.Count && i < _labelCache.Length; i++)
        {
            float v = gradient.Stops[i].Value;
            string label = Label(i, v);
            float tw = ImGui.CalcTextSize(label).X;
            float x = barMin.X + barWidth * PositionOf(v, lo, hi, log) - tw * 0.5f;
            x = Math.Clamp(x, origin.X, origin.X + w - tw);
            if (x < lastRight + 4f * scale)
                continue;
            dl.AddText(new Vector2(x, markerTop + marker + 1f * scale), Theme.U32(Theme.TextMuted), label);
            lastRight = x + tw;
        }

        // Mouse: start dragging a stop, add a stop on the bar, context menu, drag updates.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _dragRange == 0)
        {
            if (hoveredStop >= 0)
            {
                _dragStop = hoveredStop;
                _selected = hoveredStop;
            }
            else if (mouse.Y >= barMin.Y && mouse.Y <= barMax.Y)
            {
                float t = Math.Clamp((mouse.X - barMin.X) / MathF.Max(barWidth, 1f), 0f, 1f);
                _selected = AddStop(gradient, ValueAt(t, lo, hi, log));
                changed = true;
            }
        }

        if (hovered && hoveredStop >= 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            OpenColor(gradient, hoveredStop);
        if (hovered && hoveredStop >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _selected = hoveredStop;
            ImGui.OpenPopup(_menuId);
        }

        if (hovered && hoveredStop >= 0 && _dragStop < 0)
            ImGui.SetTooltip(FormatValue(gradient.Stops[hoveredStop].Value) + " " + _unit);

        if (_dragStop >= 0 || _dragRange != 0)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_dragStop >= 0)
                    _selected = SortStops(gradient, _dragStop);
                _dragStop = -1;
                _dragRange = 0;
            }
            else
            {
                float t = Math.Clamp((mouse.X - barMin.X) / MathF.Max(barWidth, 1f), 0f, 1f);
                float v = Snap(ValueAt(t, lo, hi, log), log);
                if (_dragStop >= 0 && _dragStop < gradient.Stops.Count)
                {
                    if (gradient.Stops[_dragStop].Value != v)
                    {
                        gradient.Stops[_dragStop] = gradient.Stops[_dragStop] with { Value = v };
                        changed = true;
                    }

                    ImGui.SetTooltip(FormatValue(v) + " " + _unit);
                    if (ImGui.IsKeyPressed(ImGuiKey.Delete) && gradient.Stops.Count > 2)
                    {
                        gradient.Stops.RemoveAt(_dragStop);
                        _dragStop = -1;
                        _selected = -1;
                        changed = true;
                    }
                }
                else if (_dragRange != 0)
                {
                    changed |= SetRange(gradient, _dragRange == 1, v);
                    ImGui.SetTooltip(FormatValue(_dragRange == 1 ? gradient.RangeMin : gradient.RangeMax) + " " + _unit);
                }
            }
        }

        changed |= DrawStopMenu(gradient);
        changed |= DrawColorPopup(gradient);
        return changed;
    }

    /// <summary>Removes the selected stop (at least two stay); true when one was removed.</summary>
    public bool RemoveSelected(ColorGradient gradient)
    {
        if (_selected < 0 || _selected >= gradient.Stops.Count || gradient.Stops.Count <= 2)
            return false;
        gradient.Stops.RemoveAt(_selected);
        _selected = -1;
        return true;
    }

    /// <summary>Opens the colour picker of stop <paramref name="index"/>.</summary>
    public void OpenColor(ColorGradient gradient, int index)
    {
        if (index < 0 || index >= gradient.Stops.Count)
            return;
        _selected = index;
        _colorStop = index;
        _colorEdit = gradient.Stops[index].Color;
        ImGui.OpenPopup(_colorId);
    }

    private bool DrawStopMenu(ColorGradient gradient)
    {
        bool changed = false;
        if (!ImGui.BeginPopup(_menuId))
            return false;
        if (ImGui.MenuItem(StageLoc.Get(StageStr.GradientColor)))
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            OpenColor(gradient, _selected);
            return false;
        }

        if (ImGui.MenuItem(StageLoc.Get(StageStr.GradientRemoveStop), "", false, gradient.Stops.Count > 2))
            changed = RemoveSelected(gradient);
        ImGui.EndPopup();
        return changed;
    }

    private bool DrawColorPopup(ColorGradient gradient)
    {
        if (!ImGui.BeginPopup(_colorId))
            return false;
        bool changed = false;
        if (_colorStop >= 0 && _colorStop < gradient.Stops.Count)
        {
            var c = _colorEdit;
            if (ImGui.ColorPicker4("##picker", ref c, ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.DisplayHex))
            {
                _colorEdit = c;
                gradient.Stops[_colorStop] = gradient.Stops[_colorStop] with { Color = Vector4.Clamp(c, Vector4.Zero, Vector4.One) };
                changed = true;
            }
        }
        else
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
        return changed;
    }

    private string Label(int index, float value)
    {
        if (_labelValues[index] == value && _labelCache[index] is { } cached)
            return cached;
        _labelValues[index] = value;
        return _labelCache[index] = FormatValue(value);
    }

    /// <summary>The value range the bar shows: from the smallest to the largest of the stops and the range limits.</summary>
    internal static (float Min, float Max) Domain(ColorGradient gradient)
    {
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        if (gradient.Stops is { } stops)
        {
            foreach (var s in stops)
            {
                if (!float.IsFinite(s.Value))
                    continue;
                lo = MathF.Min(lo, s.Value);
                hi = MathF.Max(hi, s.Value);
            }
        }

        if (float.IsFinite(gradient.RangeMin))
            lo = MathF.Min(lo, gradient.RangeMin);
        if (float.IsFinite(gradient.RangeMax))
            hi = MathF.Max(hi, gradient.RangeMax);
        if (!float.IsFinite(lo) || !float.IsFinite(hi))
            return (0f, 1f);
        if (gradient.Logarithmic)
            lo = MathF.Max(lo, 1e-3f);
        if (!(hi > lo))
            hi = lo + (gradient.Logarithmic ? lo : 1f);
        return (lo, hi);
    }

    /// <summary>Position 0 … 1 of <paramref name="value"/> on a bar from <paramref name="min"/> to <paramref name="max"/>.</summary>
    internal static float PositionOf(float value, float min, float max, bool logarithmic)
    {
        if (!float.IsFinite(value))
            return 0f;
        float t = logarithmic
            ? (MathF.Log10(MathF.Max(value, 1e-6f)) - MathF.Log10(min)) / (MathF.Log10(max) - MathF.Log10(min))
            : (value - min) / (max - min);
        return float.IsFinite(t) ? Math.Clamp(t, 0f, 1f) : 0f;
    }

    /// <summary>The value at position <paramref name="t"/> (0 … 1) of the bar.</summary>
    internal static float ValueAt(float t, float min, float max, bool logarithmic) =>
        logarithmic ? MathF.Pow(10f, float.Lerp(MathF.Log10(min), MathF.Log10(max), t)) : float.Lerp(min, max, t);

    /// <summary>The colour of the bar at <paramref name="value"/> (out-of-range colour outside the range).</summary>
    internal static Vector4 ColorAt(ColorGradient gradient, float value)
    {
        if (value < gradient.RangeMin || value > gradient.RangeMax || gradient.Stops.Count == 0)
            return gradient.OutOfRangeColor with { W = 1f };
        var c = Evaluate(gradient.Stops, value, gradient.Logarithmic);
        return c with { W = 1f };
    }

    /// <summary>Interpolates sorted or unsorted stops without allocating (the gradient's own Evaluate sorts with LINQ).</summary>
    internal static Vector4 Evaluate(List<GradientStop> stops, float value, bool logarithmic)
    {
        // Neighbours of the value among the stops, whatever their order.
        int below = -1, above = -1;
        for (int i = 0; i < stops.Count; i++)
        {
            float v = stops[i].Value;
            if (v <= value && (below < 0 || v > stops[below].Value))
                below = i;
            if (v >= value && (above < 0 || v < stops[above].Value))
                above = i;
        }

        if (below < 0)
            return stops[above].Color;
        if (above < 0 || above == below)
            return stops[below].Color;
        float a = stops[below].Value, b = stops[above].Value;
        float t = logarithmic && a > 0f && b > 0f
            ? (MathF.Log10(MathF.Max(value, 1e-6f)) - MathF.Log10(a)) / (MathF.Log10(b) - MathF.Log10(a))
            : (value - a) / (b - a);
        return Vector4.Lerp(stops[below].Color, stops[above].Color, float.IsFinite(t) ? Math.Clamp(t, 0f, 1f) : 0f);
    }

    /// <summary>Adds a stop at <paramref name="value"/> with the colour the gradient has there; returns its index (sorted).</summary>
    internal static int AddStop(ColorGradient gradient, float value)
    {
        var color = gradient.Stops.Count > 0 ? Evaluate(gradient.Stops, value, gradient.Logarithmic) : Vector4.One;
        gradient.Stops.Add(new GradientStop(value, color));
        return SortStops(gradient, gradient.Stops.Count - 1);
    }

    /// <summary>Sorts the stops by value; returns the new index of the stop that was at <paramref name="index"/>.</summary>
    internal static int SortStops(ColorGradient gradient, int index)
    {
        if (index < 0 || index >= gradient.Stops.Count)
            return -1;
        var moved = gradient.Stops[index];
        gradient.Stops.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        for (int i = 0; i < gradient.Stops.Count; i++)
        {
            if (gradient.Stops[i] == moved)
                return i;
        }

        return -1;
    }

    /// <summary>Moves the lower or upper range limit, keeping min &lt; max (and min &gt; 0 on a logarithmic scale).</summary>
    internal static bool SetRange(ColorGradient gradient, bool lower, float value)
    {
        if (!float.IsFinite(value))
            return false;
        if (gradient.Logarithmic)
            value = MathF.Max(value, 1e-3f);
        if (lower)
        {
            value = MathF.Min(value, gradient.RangeMax * (gradient.Logarithmic ? 0.999f : 1f) - (gradient.Logarithmic ? 0f : 1e-3f));
            if (value == gradient.RangeMin)
                return false;
            gradient.RangeMin = value;
        }
        else
        {
            value = MathF.Max(value, gradient.RangeMin * (gradient.Logarithmic ? 1.001f : 1f) + (gradient.Logarithmic ? 0f : 1e-3f));
            if (value == gradient.RangeMax)
                return false;
            gradient.RangeMax = value;
        }

        return true;
    }

    /// <summary>Rounds a dragged value to two significant digits (log scale) or to a tidy step (linear).</summary>
    internal static float Snap(float value, bool logarithmic)
    {
        if (!float.IsFinite(value) || value == 0f)
            return value;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(value))) - (logarithmic ? 1 : 1));
        return (float)(Math.Round(value / magnitude) * magnitude);
    }

    /// <summary>A compact value label: "0.5", "50", "300", "1.2k", "5k".</summary>
    internal static string FormatValue(float value)
    {
        var inv = CultureInfo.InvariantCulture;
        float a = MathF.Abs(value);
        if (a >= 10000f)
            return (value / 1000f).ToString("0", inv) + "k";
        if (a >= 1000f)
            return (value / 1000f).ToString("0.#", inv) + "k";
        if (a >= 10f)
            return value.ToString("0", inv);
        if (a >= 1f)
            return value.ToString("0.#", inv);
        return value.ToString("0.##", inv);
    }
}
