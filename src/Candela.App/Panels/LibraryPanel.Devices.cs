using System.Globalization;
using System.Numerics;
using Candela.App.Thumbnails;
using Candela.App.Ui;
using Candela.Electrical;
using Candela.Gdtf;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The Devices section of the library (requirement "Кабели": the devices cables connect): consoles, Art-Net / sACN
/// nodes and DMX splitters, mains supplies, power distros, dimmers and PSUs — the built-in devices of
/// <see cref="DeviceCatalog"/> and the power devices of the fixture library. Group chips filter them, the search field
/// matches name and manufacturer; each tile shows the device (its 3D thumbnail for GDTF devices, its icon and
/// connectors otherwise) with its name and its outputs; the tooltip lists every connector. Tiles are dragged into a
/// viewport like fixtures.
/// </summary>
internal sealed partial class LibraryPanel
{
    private static readonly DeviceGroup[] DeviceGroups = Enum.GetValues<DeviceGroup>();
    private readonly List<DevicePreset> _devices = new();
    private readonly List<DevicePreset> _filteredDevices = new();
    private readonly Dictionary<DevicePreset, DeviceTexts> _deviceTexts = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<FixtureLibraryEntry>? _devicesOf;
    private string _deviceSearch = "";
    private string _filteredDeviceSearch = "\0";
    private int _deviceGroup = -1;
    private int _filteredDeviceGroup = int.MinValue;
    private bool _devicesDirty = true;

    /// <summary>The devices the Devices section shows now (tests and scripts).</summary>
    internal IReadOnlyList<DevicePreset> VisibleDevices
    {
        get
        {
            RefreshDevices();
            return _filteredDevices;
        }
    }

    /// <summary>Selected group of the Devices section (−1 = all).</summary>
    internal int DeviceGroupIndex
    {
        get => _deviceGroup;
        set => _deviceGroup = Math.Clamp(value, -1, DeviceGroups.Length - 1);
    }

    /// <summary>Search text of the Devices section.</summary>
    internal string DeviceSearch
    {
        get => _deviceSearch;
        set => _deviceSearch = value ?? "";
    }

    private void DrawDevices()
    {
        float scale = UiScale.Effective(_ctx.Settings.Settings.UiScale, ImGui.GetStyle().FontScaleDpi);
        var style = ImGui.GetStyle();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##device-search", Labels.With(Icons.Search, Str.LibSearch), ref _deviceSearch, 128);
        ImGui.Spacing();
        float avail = ImGui.GetContentRegionAvail().X;
        float x = 0f;
        if (Chip(Loc.Get(Str.LibAll), _deviceGroup < 0, ref x, avail, style))
            _deviceGroup = -1;
        for (int i = 0; i < DeviceGroups.Length; i++)
        {
            if (Chip(CableLoc.GroupName(DeviceGroups[i]), _deviceGroup == i, ref x, avail, style))
                _deviceGroup = i;
        }

        ImGui.NewLine();
        ImGui.Separator();
        RefreshDevices();
        ImGui.TextDisabled(CableLoc.Format(CableStr.DevicesCount, _filteredDevices.Count));
        if (_filteredDevices.Count == 0)
        {
            EmptyState(Icons.Search, CableLoc.Get(CableStr.DevicesNoMatches));
            return;
        }

        EnsureWorker();
        DrawDeviceGrid(scale);
    }

    /// <summary>Rebuilds the device list when the library changed, and the filtered list when the search or group changed.</summary>
    private void RefreshDevices()
    {
        var entries = _ctx.Session.Engine?.Library.Entries;
        if (_devicesDirty || !ReferenceEquals(entries, _devicesOf))
        {
            _devicesDirty = false;
            _devicesOf = entries;
            _devices.Clear();
            _devices.AddRange(DeviceCatalog.BuiltIn);
            _devices.AddRange(DeviceCatalog.LibraryDevices(_ctx.Session.Engine?.Library));
            _filteredDeviceSearch = "\0";
        }

        if (_filteredDeviceSearch == _deviceSearch && _filteredDeviceGroup == _deviceGroup)
            return;
        _filteredDeviceSearch = _deviceSearch;
        _filteredDeviceGroup = _deviceGroup;
        _filteredDevices.Clear();
        string term = _deviceSearch.Trim();
        foreach (var d in _devices)
        {
            if (_deviceGroup >= 0 && d.Group != DeviceGroups[_deviceGroup])
                continue;
            if (term.Length > 0 && !d.Name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !CableLoc.DeviceName(d).Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !d.Manufacturer.Contains(term, StringComparison.OrdinalIgnoreCase) && !d.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
                continue;
            _filteredDevices.Add(d);
        }
    }

    private void DrawDeviceGrid(float scale)
    {
        if (!ImGui.BeginChild("##device-grid", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        float spacing = 8f * scale;
        float avail = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)((avail + spacing) / (104f * scale + spacing)));
        float tile = MathF.Floor((avail - (columns - 1) * spacing) / columns);
        float tileHeight = tile + 2f * ImGui.GetTextLineHeight() + 10f * scale;
        float framebuffer = ImGui.GetWindowViewport().FramebufferScale.X is > 0 and var fb ? fb : 1f;
        int size = Bucket((int)MathF.Ceiling((tile - 8f * scale) * framebuffer));
        var drawList = ImGui.GetWindowDrawList();
        int rows = (_filteredDevices.Count + columns - 1) / columns;
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows, tileHeight + spacing);
        while (clipper.Step())
        {
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var rowStart = ImGui.GetCursorScreenPos();
                for (int c = 0; c < columns; c++)
                {
                    int index = row * columns + c;
                    if (index >= _filteredDevices.Count)
                        break;
                    var device = _filteredDevices[index];
                    var min = rowStart + new Vector2(c * (tile + spacing), 0f);
                    ImGui.SetCursorScreenPos(min);
                    ImGui.PushID(10000 + index);
                    ImGui.InvisibleButton("##device", new Vector2(tile, tileHeight));
                    bool hovered = ImGui.IsItemHovered();
                    var texts = DeviceTextsOf(device);
                    if (hovered)
                        DeviceTooltip(device, texts, scale);
                    DragSource(null, null, size, device);
                    var entry = DeviceEntry(device);
                    UiTexture? texture = entry is not null ? Want(KeysOf(entry, size).Still, ThumbnailKind.FixtureStill, entry, null, size, 0f) : null;
                    DrawTile(drawList, device, min, tile, tileHeight, texture, hovered, scale, DeviceIcons.Glyph(device.IconKey), texts.Name, texts.Detail);
                    if (texture is null)
                        DrawDeviceConnectors(drawList, texts, min, tile, scale);
                    ImGui.PopID();
                }

                ImGui.SetCursorScreenPos(rowStart + new Vector2(0f, tileHeight + spacing));
                ImGui.Dummy(new Vector2(avail, 0f));
            }
        }

        clipper.End();
        ImGui.EndChild();
    }

    /// <summary>The library entry of a GDTF device (thumbnails), or null.</summary>
    private FixtureLibraryEntry? DeviceEntry(DevicePreset device) =>
        device.GdtfFile is { } file ? _ctx.Session.Engine?.Library.FindByFileName(file) : null;

    /// <summary>Up to three connector drawings of the device's outputs along the bottom of its icon tile.</summary>
    private static void DrawDeviceConnectors(ImDrawListPtr drawList, DeviceTexts texts, Vector2 min, float tile, float scale)
    {
        if (texts.ConnectorKeys.Length == 0)
            return;
        float pad = 4f * scale;
        float side = MathF.Min(28f * scale, (tile - 2f * pad) / 4f);
        float total = texts.ConnectorKeys.Length * side + (texts.ConnectorKeys.Length - 1) * 4f * scale;
        float x = min.X + (tile - total) * 0.5f;
        float y = min.Y + tile - pad - side - 4f * scale;
        foreach (var key in texts.ConnectorKeys)
        {
            ConnectorIcons.Draw(drawList, new Vector2(x, y), side, key);
            x += side + 4f * scale;
        }
    }

    private void DeviceTooltip(DevicePreset device, DeviceTexts texts, float scale)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360f * scale);
        ImGui.TextColored(Theme.Accent, DeviceIcons.Glyph(device.IconKey) + "  " + texts.FullName);
        ImGui.TextDisabled(device.FromLibrary ? $"{device.Manufacturer} · {CableLoc.Get(CableStr.DevicesFromLibrary)}" : CableLoc.GroupName(device.Group));
        ImGui.TextUnformatted(texts.Description);
        ImGui.PopTextWrapPos();
        if (texts.Ports.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(CableLoc.Get(CableStr.DevicesPorts));
            float icon = ImGui.GetTextLineHeight() * 1.3f;
            foreach (var (key, line) in texts.Ports)
            {
                var p = ImGui.GetCursorScreenPos();
                ConnectorIcons.Draw(ImGui.GetWindowDrawList(), p, icon, key);
                ImGui.Dummy(new Vector2(icon));
                ImGui.SameLine();
                ImGui.TextUnformatted(line);
            }
        }

        ImGui.EndTooltip();
    }

    /// <summary>Texts of a device tile (name, outputs, ports), built once per language.</summary>
    private DeviceTexts DeviceTextsOf(DevicePreset device)
    {
        if (_deviceTexts.TryGetValue(device, out var texts) && ReferenceEquals(texts.Language, Loc.Language))
            return texts;
        var ports = device.Ports(_ctx.Session.Engine?.ElectricalProfiles);
        var outputs = ports.Where(p => p.Direction != PortDirection.Input).ToList();
        string detail = Summary(outputs.Count > 0 ? outputs : ports);
        var keys = outputs.Select(p => p.Connector.IllustrationKey).Where(k => k is { Length: > 0 }).Distinct().Take(3).ToArray();
        var lines = ports.Select(p => (p.Connector.IllustrationKey, $"{p.Name} · {PortPicker.Detail(new Editing.PortCandidate(p, 0, default, null, false))}")).ToArray();
        string name = CableLoc.DeviceName(device);
        texts = new DeviceTexts(Loc.Language, name, name, detail.Length > 0 ? detail : device.Manufacturer, CableLoc.DeviceDescription(device), keys, lines);
        _deviceTexts[device] = texts;
        return texts;
    }

    /// <summary>"4 × XLR 5-pin", "6 × Schuko · 16 A": outputs grouped by connector.</summary>
    internal static string Summary(IReadOnlyList<Port> ports)
    {
        var parts = new List<string>();
        foreach (var group in ports.GroupBy(p => p.Connector.DisplayName))
        {
            int n = group.Count();
            var breaker = group.Select(p => p.BreakerA).FirstOrDefault(b => b is not null);
            string text = n > 1 ? $"{n} × {group.Key}" : group.Key;
            if (breaker is { } a)
                text += string.Create(CultureInfo.InvariantCulture, $" {a:0} {StageUnits.Current.Ampere}");
            parts.Add(text);
        }

        return string.Join(" + ", parts);
    }

    private sealed record DeviceTexts(string Language, string Name, string FullName, string Detail, string Description, string[] ConnectorKeys,
        (string Key, string Line)[] Ports);
}
