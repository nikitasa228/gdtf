using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// One label-column width for every property grid of a panel, so the editors of all its sections line up: the widest
/// of the panel's labels in the current language and UI scale, returned in pixels at 100 % (the unit
/// <see cref="PropertyGrid.Begin"/> takes). Measured again only when the language or the scale changes.
/// </summary>
internal sealed class LabelColumn
{
    private readonly Func<IEnumerable<string>> _labels;
    private readonly float _minimum;
    private string _language = "";
    private float _scale = -1f;
    private float _width;

    /// <summary>Creates the column for the labels <paramref name="labels"/> yields (read in the current language).</summary>
    public LabelColumn(Func<IEnumerable<string>> labels, float minimum = 90f)
    {
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));
        _minimum = minimum;
    }

    /// <summary>The label column width at 100 %, for <see cref="PropertyGrid.Begin"/>.</summary>
    public float Width
    {
        get
        {
            var style = ImGui.GetStyle();
            float scale = style.FontScaleMain * style.FontScaleDpi;
            if (scale <= 0f || !float.IsFinite(scale))
                scale = 1f;
            if (!string.Equals(_language, Loc.Language, StringComparison.Ordinal) || scale != _scale)
            {
                _language = Loc.Language;
                _scale = scale;
                float widest = 0f;
                foreach (string label in _labels())
                    widest = MathF.Max(widest, ImGui.CalcTextSize(label).X);
                _width = MathF.Max(_minimum, widest / scale + 2f * style.CellPadding.X / scale + 6f);
            }

            return _width;
        }
    }
}
