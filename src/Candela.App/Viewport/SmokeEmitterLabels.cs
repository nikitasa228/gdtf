using System.Globalization;
using System.Numerics;
using Candela.App.Ui;
using Candela.Engine;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Viewport;

/// <summary>
/// The smoke overlay's words in the editor: next to every emitter arrow of a viewport the device's name with its outputs
/// ("Hazer SL · haze 60 %, fan 40 %"), and in the legend the haze, the densest air and the list of emitters. One label
/// per device (a hazer with a fan has both outputs on one line), at the device's first nozzle. Labels are cached per
/// device and rebuilt only when one of its outputs changes by a whole percent, it is renamed or the language changes,
/// so drawing them allocates nothing in steady state. Main thread only.
/// </summary>
internal sealed class SmokeEmitterLabels
{
    /// <summary>Most emitters the legend lists (the rest are counted).</summary>
    public const int MaxListed = 8;

    /// <summary>Most labels drawn in one viewport (a stage has a handful of emitters; this keeps a huge rig readable).</summary>
    public const int MaxLabels = 64;

    /// <summary>Frames between two scans of the smoke grid for its densest cell.</summary>
    private const int DensestScanInterval = 15;

    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly List<Entry> _order = new();
    private readonly List<Guid> _stale = new();
    private int _stamp;
    private string _language = "";
    private int _lastFrame = -1;
    private long _smokeVersion = -1;
    private int _densestFrame = -DensestScanInterval;
    private float _densest;
    private float _airHaze = float.NaN;
    private float _airDensest = float.NaN;
    private string _airLine = "";
    private string _airLanguage = "";
    private int _moreCount = -1;
    private string _moreLine = "";

    /// <summary>The devices of the last refresh in marker order (tests).</summary>
    internal IReadOnlyList<Entry> Entries => _order;

    /// <summary>The legend's air line of the last refresh ("Haze 0.02 /m, densest 0.4 /m").</summary>
    internal string AirLine => _airLine;

    /// <summary>One emitting device: where its label goes, the label and the colour of its swatch.</summary>
    internal sealed class Entry
    {
        public Guid DeviceId;
        public Vector3 Anchor;
        public string Label = "";
        public string? Name;
        public int Key;
        public int Stamp;
        public Vector4 Color;
    }

    /// <summary>
    /// Brings the labels up to date with the engine's emitters (once per UI frame however many viewports draw them).
    /// </summary>
    public void Refresh(StageEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        int frame = ImGui.GetCurrentContext().IsNull ? -2 : ImGui.GetFrameCount();
        if (frame == _lastFrame && frame >= 0)
            return;
        _lastFrame = frame;
        bool relabel = !string.Equals(_language, Loc.Language, StringComparison.Ordinal);
        _language = Loc.Language;
        _stamp++;
        _order.Clear();

        var markers = engine.Simulation.EmitterMarkers;
        for (int i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            if (!_entries.TryGetValue(m.DeviceId, out var entry))
            {
                entry = new Entry { DeviceId = m.DeviceId };
                _entries.Add(m.DeviceId, entry);
            }

            if (entry.Stamp == _stamp)
                continue; // the device's other outputs: already handled with its first marker
            entry.Stamp = _stamp;
            entry.Anchor = m.Position;
            int key = KeyOf(markers, i, out bool running, out var kind);
            entry.Color = running ? new Vector4(SmokeEmitterMarker.ColorOf(kind), 1f) : new Vector4(SmokeEmitterMarker.OffColor, 1f);
            string? name = engine.Scene.Find(m.DeviceId)?.Name;
            if (relabel || key != entry.Key || !ReferenceEquals(name, entry.Name) || entry.Label.Length == 0)
            {
                entry.Key = key;
                entry.Name = name;
                entry.Label = BuildLabel(name, markers, i);
            }

            _order.Add(entry);
        }

        _stale.Clear();
        foreach (var (id, entry) in _entries)
        {
            if (entry.Stamp != _stamp)
                _stale.Add(id);
        }

        foreach (var id in _stale)
            _entries.Remove(id);
        RefreshAir(engine, frame);
    }

    /// <summary>
    /// Draws the label of every emitter next to its arrow in the viewport image rectangle, leaving out the labels that
    /// would lie under <paramref name="exclude"/> (the legend window: min x, min y, max x, max y; empty for none).
    /// </summary>
    public void DrawLabels(StageEngine engine, ViewportCamera camera, Vector2 imageMin, Vector2 imageSize, Vector4 exclude = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Refresh(engine);
        if (_order.Count == 0)
            return;
        var dl = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        float scale = style.FontScaleMain * style.FontScaleDpi;
        float pad = 3f * scale, offset = 10f * scale;
        var imageMax = imageMin + imageSize;
        dl.PushClipRect(imageMin, imageMax, true);
        int drawn = 0;
        foreach (var entry in _order)
        {
            if (drawn >= MaxLabels)
                break;
            if (!camera.WorldToScreen(entry.Anchor, imageSize, out var p))
                continue;
            p += imageMin;
            if (p.X < imageMin.X || p.Y < imageMin.Y || p.X > imageMax.X || p.Y > imageMax.Y)
                continue;
            var size = ImGui.CalcTextSize(entry.Label);
            var min = new Vector2(p.X + offset, p.Y - 0.5f * size.Y - pad);
            var max = min + size + new Vector2(2f * pad);
            if (exclude.Z > exclude.X && max.X > exclude.X && min.X < exclude.Z && max.Y > exclude.Y && min.Y < exclude.W)
                continue;
            dl.AddRectFilled(min, max, Theme.U32(Theme.OverlayBackground), 3f * scale);
            dl.AddRectFilled(min, new Vector2(min.X + 2f * scale, max.Y), Theme.U32(entry.Color), 1f * scale);
            dl.AddText(min + new Vector2(pad + 1f * scale, pad), Theme.U32(Theme.Text), entry.Label);
            drawn++;
        }

        dl.PopClipRect();
    }

    /// <summary>The legend part: haze and densest air, then the emitters with a swatch in their marker colour.</summary>
    public void DrawList(StageEngine engine)
    {
        Refresh(engine);
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextUnformatted(_airLine);
        ImGui.PopStyleColor();
        ImGui.TextUnformatted(StageLoc.Get(StageStr.LegendEmitters));
        if (_order.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            SafeText.Wrapped(StageLoc.Get(StageStr.LegendNoEmitters));
            ImGui.PopStyleColor();
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        float swatch = ImGui.GetTextLineHeight();
        for (int i = 0; i < _order.Count && i < MaxListed; i++)
        {
            var entry = _order[i];
            var p = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(p + new Vector2(0f, swatch * 0.25f), p + new Vector2(swatch * 0.5f, swatch * 0.75f), Theme.U32(entry.Color), 2f);
            ImGui.Dummy(new Vector2(swatch * 0.7f, swatch));
            ImGui.SameLine();
            SafeText.Wrapped(entry.Label);
        }

        int more = _order.Count - MaxListed;
        if (more > 0)
        {
            if (more != _moreCount)
            {
                _moreCount = more;
                _moreLine = StageLoc.Format(StageStr.LegendEmittersMore, more);
            }

            SafeText.Disabled(_moreLine);
        }
    }

    /// <summary>The outputs of the device whose first marker is at <paramref name="first"/>, as whole percent.</summary>
    private static int KeyOf(IReadOnlyList<SmokeEmitterMarker> markers, int first, out bool running, out SmokeEmitterMarkerKind runningKind)
    {
        var id = markers[first].DeviceId;
        var hash = new HashCode();
        running = false;
        runningKind = markers[first].Kind;
        for (int i = first; i < markers.Count; i++)
        {
            var m = markers[i];
            if (m.DeviceId != id)
                continue;
            int percent = Percent(m.Output);
            hash.Add((int)m.Kind);
            hash.Add(percent);
            if (percent > 0 && !running)
            {
                running = true;
                runningKind = m.Kind;
            }
        }

        return hash.ToHashCode();
    }

    private static string BuildLabel(string? deviceName, IReadOnlyList<SmokeEmitterMarker> markers, int first)
    {
        var id = markers[first].DeviceId;
        var text = new System.Text.StringBuilder(deviceName is { Length: > 0 } ? deviceName : KindName(markers[first].Kind));
        text.Append(" · ");
        bool any = false;
        for (int i = first; i < markers.Count; i++)
        {
            var m = markers[i];
            if (m.DeviceId != id)
                continue;
            if (any)
                text.Append(", ");
            any = true;
            text.Append(KindName(m.Kind)).Append(' ');
            int percent = Percent(m.Output);
            if (percent > 0)
                text.Append(percent.ToString(CultureInfo.InvariantCulture)).Append(" %");
            else
                text.Append(StageLoc.Get(StageStr.EmitterOff));
        }

        return text.ToString();
    }

    private static int Percent(float output) => float.IsFinite(output) ? (int)MathF.Round(Math.Clamp(output, 0f, 1f) * 100f) : 0;

    private static string KindName(SmokeEmitterMarkerKind kind) => StageLoc.Get(kind switch
    {
        SmokeEmitterMarkerKind.Hazer => StageStr.EmitterHaze,
        SmokeEmitterMarkerKind.FogMachine => StageStr.EmitterFog,
        SmokeEmitterMarkerKind.LowFog => StageStr.EmitterLowFog,
        SmokeEmitterMarkerKind.Fan => StageStr.EmitterFan,
        _ => StageStr.EmitterFlame,
    });

    /// <summary>The haze of the scene and the densest air of the smoke grid (scanned every few frames when it changed).</summary>
    private void RefreshAir(StageEngine engine, int frame)
    {
        float haze = engine.Scene.Settings.BaseHazeScattering;
        haze = float.IsFinite(haze) ? Math.Max(haze, 0f) : 0f;
        var smoke = engine.Settings.SimulateSmoke ? engine.Simulation.Smoke : null;
        if (smoke is null)
        {
            _densest = 0f;
            _smokeVersion = -1;
        }
        else if (smoke.Current.Version != _smokeVersion && (frame < 0 || frame - _densestFrame >= DensestScanInterval))
        {
            var current = smoke.Current;
            _smokeVersion = current.Version;
            _densestFrame = frame;
            float densest = 0f;
            foreach (float d in current.Density)
            {
                if (d > densest && float.IsFinite(d))
                    densest = d;
            }

            _densest = densest;
        }

        float peak = haze + _densest * engine.Simulation.SmokeScatteringAtFullDensity;
        // Rounded to what the line shows, so the string is only rebuilt when the text changes.
        float shownHaze = MathF.Round(haze, 3), shownPeak = MathF.Round(peak, 3);
        if (shownHaze == _airHaze && shownPeak == _airDensest && _airLine.Length > 0 &&
            string.Equals(_airLanguage, Loc.Language, StringComparison.Ordinal))
            return;
        _airLanguage = Loc.Language;
        _airHaze = shownHaze;
        _airDensest = shownPeak;
        _airLine = StageLoc.Format(StageStr.LegendSmokeAir, GradientEditor.FormatValue(shownHaze), GradientEditor.FormatValue(shownPeak));
    }
}
