using System.Globalization;
using System.Numerics;
using Candela.App.Docking;
using Candela.App.Editing;
using Candela.App.Shell;
using Candela.App.Thumbnails;
using Candela.App.Ui;
using Candela.Assets;
using Candela.Core.Settings;
using Candela.Gdtf;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>Something that can be dragged from the library into a viewport: a fixture type or a model-library file.</summary>
/// <param name="Fixture">The fixture type (GDTF), or null.</param>
/// <param name="Model">The model or image file, or null.</param>
internal sealed record LibraryItem(FixtureLibraryEntry? Fixture, ModelLibraryItem? Model)
{
    /// <summary>Name shown while dragging.</summary>
    public string Name => Fixture?.ModelName ?? Model?.Name ?? "";
}

/// <summary>What the library panel needs from the workspace.</summary>
internal interface ILibraryHost
{
    /// <summary>Opens the file browser to add models and images to the model library.</summary>
    void ShowModelImport();

    /// <summary>Opens the file browser to add a GDTF file to the fixture library.</summary>
    void ShowGdtfImport();
}

/// <summary>
/// The library panel (requirement "окно выбора устройств"): sub-tabs <b>Fixtures</b> — category chips (All, PROFILE …
/// GENERATOR FIRE), search by name or manufacturer, a grid of the engine's fixture library (newest revision of each
/// model) with a 3D thumbnail and the name under it — and <b>Models</b>, the user's custom models and images
/// (<see cref="ModelLibrary"/>; <i>Import…</i> adds files). Hovering a fixture grows its tile upward into a card with a
/// rotating 3D preview; tiles are dragged into a viewport to place them (the viewport shows a ghost and places at the
/// surface under the cursor). Pictures are made by the <see cref="ThumbnailWorker"/> in the background and shown as
/// they arrive; only visible tiles ask for them, and at most <see cref="MaxTextures"/> stay in memory.
/// </summary>
internal sealed unsafe class LibraryPanel : Panel, IDisposable
{
    /// <summary>Drag-and-drop payload type of library items.</summary>
    public const string PayloadType = "CANDELA_LIBRARY_ITEM";

    /// <summary>Thumbnail textures kept alive (least recently drawn are released beyond this).</summary>
    public const int MaxTextures = 240;

    /// <summary>Frames of the rotating hover preview (one every 15°).</summary>
    public const int OrbitFrames = 24;

    /// <summary>Seconds the hover card takes to grow.</summary>
    public const float GrowSeconds = 0.18f;

    /// <summary>Degrees per second the hover preview turns.</summary>
    public const float OrbitDegreesPerSecond = 40f;

    private static readonly int[] OrbitOrder = BuildOrbitOrder();
    private static readonly int[] SizeBuckets = { 96, 128, 160, 192, 256, 320, 384, 512 };

    private readonly WorkspaceContext _ctx;
    private readonly ILibraryHost _host;
    private readonly ModelLibrary _models;
    private readonly UiTextures _textures = new();
    private readonly Dictionary<string, TextureSlot> _slots = new(StringComparer.Ordinal);
    private readonly List<ThumbnailRequest> _wanted = new();
    private readonly List<string> _evict = new();
    private readonly List<FixtureLibraryEntry> _filtered = new();
    private readonly List<ModelLibraryItem> _filteredModels = new();
    private readonly Dictionary<object, TileText> _texts = new(ReferenceEqualityComparer.Instance);
    private readonly int[] _categoryCounts = new int[FixtureCategories.All.Count + 1];
    private readonly Dictionary<FixtureLibraryEntry, PictureKeys> _keys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModelLibraryItem, ModelDetails> _details = new(ReferenceEqualityComparer.Instance);
    private FactsText? _facts;
    private Vector2 _gridMin, _gridMax;
    private ThumbnailWorker? _worker;
    private FixtureInfoCache? _info;
    private FixtureLibrary? _library;
    private IReadOnlyList<FixtureLibraryEntry>? _entries;
    private string _search = "";
    private string _filteredSearch = "\0";
    private int _filteredCategory = int.MinValue;
    private string _modelSearch = "";
    private string _filteredModelSearch = "\0";
    private int _filteredModelVersion = -1;
    private int _category = -1;
    private long _frame;
    private object? _hover;
    private float _hoverGrow;
    private float _hoverTime;
    private Vector2 _hoverTileMin, _hoverTileMax;
    private LibraryItem? _dragged;
    private string _countText = "";
    private int _countValue = -1;
    private string _countLanguage = "";
    private string _pendingText = "";
    private int _pendingValue = -1;
    private string _pendingLanguage = "";
    private bool _disposed;

    /// <summary>Creates the panel; <paramref name="models"/> is the user's model library (loaded by the caller).</summary>
    public LibraryPanel(WorkspaceContext context, ILibraryHost host, ModelLibrary models)
        : base("Library", Icons.Library)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelLibrary);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(340, 560);

    /// <summary>Categories shown as sub-tabs (every category except OTHER).</summary>
    public static IReadOnlyList<FixtureCategory> Categories { get; } =
        FixtureCategories.All.Where(c => c != FixtureCategory.Other).ToArray();

    /// <summary>The item being dragged out of the panel (null when no drag is under way).</summary>
    public LibraryItem? Dragged => ImGui.GetDragDropPayload().IsNull ? null : _dragged;

    /// <summary>The user's model library.</summary>
    public ModelLibrary Models => _models;

    /// <summary>Modes, footprints and size of fixture types (read in the background).</summary>
    public FixtureInfoCache? FixtureInfo => _info;

    /// <summary>The fixture entries the grid shows now (tests and scripts).</summary>
    internal IReadOnlyList<FixtureLibraryEntry> VisibleEntries => _filtered;

    /// <summary>Selected category index into <see cref="Categories"/> (−1 = All).</summary>
    internal int Category
    {
        get => _category;
        set => _category = Math.Clamp(value, -1, Categories.Count - 1);
    }

    /// <summary>Search text of the fixtures tab.</summary>
    internal string Search
    {
        get => _search;
        set => _search = value ?? "";
    }

    /// <summary>Thumbnails waiting to be made.</summary>
    internal int PendingPictures => _worker?.Pending ?? 0;

    /// <summary>Adds a model or image file to the model library (after it was imported into a scene).</summary>
    public void RememberModel(string path) => _models.Add(path);

    /// <summary>
    /// Per-frame work outside the panel's drawing (also while the panel is hidden): finished pictures become textures,
    /// released textures are freed, and the engine's library is followed when it changes (a GDTF was imported).
    /// </summary>
    public void Update()
    {
        if (_disposed)
            return;
        _frame++;
        _textures.Update();
        var engine = _ctx.Session.Engine;
        if (engine is not null && !ReferenceEquals(engine.Library, _library))
        {
            _library = engine.Library;
            _info = new FixtureInfoCache(_library);
            _worker?.Dispose();
            _worker = null;
        }

        if (_worker is null)
            return;
        // A few textures per frame: each is uploaded by the backend in the next frame.
        int created = 0;
        while (created < 6 && _worker.TryTake(out var result))
        {
            if (!_slots.TryGetValue(result.Key, out var slot))
                _slots[result.Key] = slot = new TextureSlot();
            slot.Ready = true;
            slot.LastUsed = _frame;
            if (result.Image is { } image)
            {
                _textures.Release(slot.Texture);
                slot.Texture = _textures.Create(image);
                created++;
            }
        }

        EvictOldTextures();
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        _wanted.Clear();
        if (!ImGui.BeginTabBar("##library-tabs"))
            return;
        if (ImGui.BeginTabItem(Labels.With(Icons.Lightbulb, Str.LibFixtures)))
        {
            DrawFixtures();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(Labels.With(Icons.Box, Str.LibModels)))
        {
            DrawModels();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        _worker?.SetWanted(_wanted);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _worker?.Dispose();
        _worker = null;
        _textures.Dispose();
        _slots.Clear();
    }

    // ---- fixtures ---------------------------------------------------------------------------------------------------

    private void DrawFixtures()
    {
        var engine = _ctx.Session.Engine;
        float scale = UiScale.Effective(_ctx.Settings.Settings.UiScale, ImGui.GetStyle().FontScaleDpi);
        var style = ImGui.GetStyle();
        float buttonWidth = ImGui.CalcTextSize(Icons.FileInput).X + style.FramePadding.X * 2;
        ImGui.SetNextItemWidth(-(buttonWidth + style.ItemSpacing.X));
        ImGui.InputTextWithHint("##search", Labels.With(Icons.Search, Str.LibSearch), ref _search, 128);
        ImGui.SameLine();
        if (ImGui.Button(Icons.FileInput + "##import-gdtf"))
            _host.ShowGdtfImport();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get(Str.FileImportGdtf).TrimEnd('…'));
        ImGui.Spacing();

        RefreshFixtureList();

        // Category chips wrapping to the panel width (the requirement's category sub-tabs).
        float avail = ImGui.GetContentRegionAvail().X;
        float x = 0f;
        if (Chip(Loc.Get(Str.LibAll), _category < 0, ref x, avail, style))
            _category = -1;
        for (int i = 0; i < Categories.Count; i++)
        {
            if (Chip(CategoryNames[i], _category == i, ref x, avail, style))
                _category = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Format(Str.LibCount, _categoryCounts[i]));
        }

        ImGui.NewLine();
        ImGui.Separator();
        ImGui.TextDisabled(CountText(_filtered.Count));
        if (_worker is { Pending: > 0 } w)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, PendingText(w.Pending));
        }

        if (engine is null || engine.Library.Count == 0)
        {
            EmptyState(Icons.Library, Loc.Get(Str.LibNoLibrary));
            return;
        }

        if (_filtered.Count == 0)
        {
            EmptyState(Icons.Search, Loc.Get(Str.LibNoMatches));
            return;
        }

        EnsureWorker();
        DrawFixtureGrid(scale);
    }

    private void RefreshFixtureList()
    {
        var library = _ctx.Session.Engine?.Library;
        var entries = library?.Entries;
        if (ReferenceEquals(entries, _entries) && _filteredSearch == _search && _filteredCategory == _category)
            return;
        bool libraryChanged = !ReferenceEquals(entries, _entries);
        _entries = entries;
        _filteredSearch = _search;
        _filteredCategory = _category;
        _filtered.Clear();
        if (library is null || entries is null)
            return;
        string term = _search.Trim();
        if (libraryChanged)
            Array.Clear(_categoryCounts);
        foreach (var e in entries)
        {
            if (!library.IsLatestRevision(e))
                continue;
            int categoryIndex = CategoryIndex(e.Category);
            if (libraryChanged && categoryIndex >= 0)
                _categoryCounts[categoryIndex]++;
            if (_category >= 0 && categoryIndex != _category)
                continue;
            if (term.Length > 0 && !Matches(e, term))
                continue;
            _filtered.Add(e);
        }
    }

    /// <summary>True when <paramref name="term"/> occurs in the entry's manufacturer, model name or file name (any case).</summary>
    internal static bool Matches(FixtureLibraryEntry entry, string term) =>
        entry.ModelName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        entry.Manufacturer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        entry.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        entry.FileName.Contains(term, StringComparison.OrdinalIgnoreCase);

    private void DrawFixtureGrid(float scale)
    {
        if (!ImGui.BeginChild("##fixture-grid", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        var style = ImGui.GetStyle();
        float spacing = 8f * scale;
        float avail = ImGui.GetContentRegionAvail().X;
        _gridMin = ImGui.GetWindowPos();
        _gridMax = new Vector2(ImGui.GetCursorScreenPos().X + avail, _gridMin.Y + ImGui.GetWindowSize().Y);
        _gridMin.X = ImGui.GetCursorScreenPos().X;
        float minTile = 104f * scale;
        int columns = Math.Max(1, (int)((avail + spacing) / (minTile + spacing)));
        float tile = MathF.Floor((avail - (columns - 1) * spacing) / columns);
        float lineHeight = ImGui.GetTextLineHeight();
        float tileHeight = tile + 2f * lineHeight + 10f * scale;
        int rows = (_filtered.Count + columns - 1) / columns;
        float framebuffer = ImGui.GetWindowViewport().FramebufferScale.X is > 0 and var fb ? fb : 1f;
        int stillSize = Bucket((int)MathF.Ceiling((tile - 8f * scale) * framebuffer));
        var drawList = ImGui.GetWindowDrawList();
        var mouse = ImGui.GetIO().MousePos;
        object? hovered = null;
        Vector2 hoveredMin = default, hoveredMax = default;
        bool cardOwnsMouse = _hover is not null && _hoverGrow > 0.05f && CardRect(out var cardMin, out var cardMax) && Inside(mouse, cardMin, cardMax);

        unsafe
        {
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
                        if (index >= _filtered.Count)
                            break;
                        var entry = _filtered[index];
                        var min = rowStart + new Vector2(c * (tile + spacing), 0f);
                        var max = min + new Vector2(tile, tileHeight);
                        ImGui.SetCursorScreenPos(min);
                        ImGui.PushID(index);
                        ImGui.SetNextItemAllowOverlap();
                        ImGui.InvisibleButton("##tile", new Vector2(tile, tileHeight));
                        bool isHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && (!cardOwnsMouse || ReferenceEquals(entry, _hover));
                        if (isHovered)
                        {
                            hovered = entry;
                            hoveredMin = min;
                            hoveredMax = max;
                        }

                        DragSource(entry, null, stillSize);
                        string key = KeysOf(entry, stillSize).Still;
                        var texture = Want(key, ThumbnailKind.FixtureStill, entry, null, stillSize, 0f);
                        DrawTile(drawList, entry, min, tile, tileHeight, texture, isHovered || ReferenceEquals(entry, _hover), scale, CategoryIcon(entry.Category),
                            entry.ModelName, entry.Manufacturer);
                        ImGui.PopID();
                    }

                    ImGui.SetCursorScreenPos(rowStart + new Vector2(0f, tileHeight + spacing));
                    ImGui.Dummy(new Vector2(avail, 0f));
                }
            }

            clipper.End();
        }

        UpdateHover(hovered, hoveredMin, hoveredMax, cardOwnsMouse);
        if (_hover is FixtureLibraryEntry hoverEntry && _hoverGrow > 0f)
        {
            CardDragSource(hoverEntry, stillSize);
            DrawFixtureCard(drawList, hoverEntry, stillSize, scale, framebuffer);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// The grown hover card is an item of its own over the tiles it covers, so pressing on the card and dragging takes
    /// the fixture the card shows — not the tile that happens to lie under the card's picture. Added without layout
    /// (no scroll extent changes); the tiles allow overlap, so the card wins the mouse where it covers them.
    /// </summary>
    private unsafe void CardDragSource(FixtureLibraryEntry entry, int stillSize)
    {
        if (_hoverGrow <= 0.05f || !CardRect(out var min, out var max))
            return;
        var bb = new ImRect(min, max);
        uint id = ImGui.GetID("##hover-card");
        if (!ImGuiP.ItemAdd(bb, id))
            return;
        bool hovered, held;
        ImGuiP.ButtonBehavior(bb, id, &hovered, &held, ImGuiButtonFlags.None);
        DragSource(entry, null, stillSize);
    }

    private void UpdateHover(object? hovered, Vector2 min, Vector2 max, bool cardOwnsMouse)
    {
        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        if (cardOwnsMouse && _hover is not null)
            hovered = _hover;
        if (hovered is not null && !ReferenceEquals(hovered, _hover))
        {
            // A new tile: the card starts again from the tile's size.
            _hover = hovered;
            _hoverGrow = 0f;
            _hoverTime = 0f;
            _hoverTileMin = min;
            _hoverTileMax = max;
        }
        else if (hovered is not null && !cardOwnsMouse)
        {
            _hoverTileMin = min;
            _hoverTileMax = max;
        }

        if (hovered is not null)
        {
            _hoverTime += dt;
            // A short delay before growing, so moving across the grid does not flash cards.
            if (_hoverTime > 0.12f)
                _hoverGrow = MathF.Min(1f, _hoverGrow + dt / GrowSeconds);
        }
        else if (_hover is not null)
        {
            _hoverGrow = MathF.Max(0f, _hoverGrow - dt / (GrowSeconds * 0.6f));
            if (_hoverGrow <= 0f)
                _hover = null;
        }
    }

    /// <summary>The card's full-size rectangle: twice the tile, its bottom on the tile's bottom, inside the grid window.</summary>
    private bool CardRect(out Vector2 min, out Vector2 max)
    {
        // The grid's visible content area (without the scroll bar).
        var windowMin = _gridMin;
        var windowMax = _gridMax;
        float tileWidth = _hoverTileMax.X - _hoverTileMin.X;
        float width = MathF.Min(tileWidth * 2.1f, windowMax.X - windowMin.X - 8f);
        float textBlock = ImGui.GetTextLineHeightWithSpacing() * 3.4f;
        // A short grid gets a smaller picture, so the whole card — the line of facts at its bottom included — stays
        // inside the grid instead of being cut off; the card never becomes smaller than the tile it grows from.
        float roomForPicture = windowMax.Y - windowMin.Y - 8f - textBlock;
        if (width > roomForPicture)
            width = MathF.Max(MathF.Min(width, tileWidth), roomForPicture);
        float height = width + textBlock;
        float centerX = (_hoverTileMin.X + _hoverTileMax.X) * 0.5f;
        float left = Math.Clamp(centerX - width * 0.5f, windowMin.X + 4f, MathF.Max(windowMin.X + 4f, windowMax.X - 4f - width));
        float bottom = _hoverTileMax.Y;
        float top = bottom - height;
        if (top < windowMin.Y + 4f)
        {
            // Not enough room above: the card hangs down from the window's top instead.
            top = windowMin.Y + 4f;
            bottom = top + height;
        }

        min = new Vector2(left, top);
        max = new Vector2(left + width, bottom);
        return width > 0f && height > 0f;
    }

    private void DrawFixtureCard(ImDrawListPtr drawList, FixtureLibraryEntry entry, int stillSize, float scale, float framebuffer)
    {
        if (!CardRect(out var fullMin, out var fullMax))
            return;
        float t = EaseOutCubic(_hoverGrow);
        var min = Vector2.Lerp(_hoverTileMin, fullMin, t);
        var max = Vector2.Lerp(_hoverTileMax, fullMax, t);
        float rounding = 6f * scale;
        var shadow = new Vector2(0f, 6f * scale);
        drawList.AddRectFilled(min + shadow * 0.5f, max + shadow, Theme.U32(new Vector4(0f, 0f, 0f, 0.35f * t)), rounding);
        drawList.AddRectFilled(min, max, Theme.U32(Theme.Rgb(0x232529)), rounding);
        drawList.AddRect(min, max, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.9f)), rounding, ImDrawFlags.None, 1.5f * scale);

        float pad = 6f * scale;
        float imageSide = max.X - min.X - 2f * pad;
        float textBlock = ImGui.GetTextLineHeightWithSpacing() * 3.4f * t + ImGui.GetTextLineHeightWithSpacing() * 2f * (1f - t);
        imageSide = MathF.Max(8f, MathF.Min(imageSide, max.Y - min.Y - textBlock - pad));
        var imageMin = new Vector2((min.X + max.X - imageSide) * 0.5f, min.Y + pad);
        var imageMax = imageMin + new Vector2(imageSide);

        // Rotating preview: the orbit frames that have arrived, cross-faded; the still picture until then.
        int orbitSize = Bucket((int)MathF.Ceiling((fullMax.X - fullMin.X - 2f * pad) * framebuffer));
        float position = (_hoverTime * OrbitDegreesPerSecond / 360f * OrbitFrames + OrbitFrames * FixtureThumbnailAzimuthFraction) % OrbitFrames;
        int a = (int)MathF.Floor(position) % OrbitFrames, b = (a + 1) % OrbitFrames;
        float blend = position - MathF.Floor(position);
        var orbitKeys = KeysOf(entry, orbitSize).Orbit;
        int orbitInsert = 0;
        for (int i = 0; i < OrbitOrder.Length; i++)
        {
            int frame = OrbitOrder[i];
            // Orbit frames come first: they are what the user is looking at.
            if (NeedsPicture(orbitKeys[frame]))
                _wanted.Insert(orbitInsert++, new ThumbnailRequest(orbitKeys[frame], ThumbnailKind.FixtureOrbit, entry, null, orbitSize, frame * MathF.Tau / OrbitFrames));
        }

        var ta = Nearest(orbitKeys, a, -1);
        var tb = Nearest(orbitKeys, b, +1);
        var still = TextureOf(KeysOf(entry, stillSize).Still);
        if (ta is null && tb is null)
        {
            if (still is not null)
                drawList.AddImageRounded(still.Ref, imageMin, imageMax, Vector2.Zero, Vector2.One, uint.MaxValue, rounding * 0.7f);
            else
                DrawIcon(drawList, CategoryIcon(entry.Category), imageMin, imageMax);
        }
        else
        {
            var first = ta ?? tb!;
            drawList.AddImageRounded(first.Ref, imageMin, imageMax, Vector2.Zero, Vector2.One, uint.MaxValue, rounding * 0.7f);
            if (tb is not null && ta is not null && !ReferenceEquals(ta, tb) && blend > 0.01f)
                drawList.AddImageRounded(tb.Ref, imageMin, imageMax, Vector2.Zero, Vector2.One, Theme.U32(new Vector4(1f, 1f, 1f, blend)), rounding * 0.7f);
        }

        // Name, manufacturer and a line of data under the picture.
        var textPos = new Vector2(min.X + pad, imageMax.Y + 4f * scale);
        float textWidth = max.X - min.X - 2f * pad;
        var texts = TextsOf(entry, textWidth, entry.ModelName, entry.Manufacturer);
        drawList.PushClipRect(min, max, true);
        drawList.AddText(textPos, Theme.U32(Theme.Text), texts.Name);
        textPos.Y += ImGui.GetTextLineHeightWithSpacing();
        drawList.AddText(textPos, Theme.U32(Theme.TextMuted), texts.Detail);
        if (t > 0.6f)
        {
            textPos.Y += ImGui.GetTextLineHeightWithSpacing();
            drawList.AddText(textPos, Theme.U32(Theme.WithAlpha(Theme.Accent, (t - 0.6f) / 0.4f)), FactsOf(entry, textWidth));
        }

        drawList.PopClipRect();
    }

    /// <summary>Azimuth of the still thumbnail as a fraction of a turn, so the preview starts where the tile picture is.</summary>
    private const float FixtureThumbnailAzimuthFraction = 35f / 360f;

    private UiTexture? Nearest(string[] keys, int frame, int direction)
    {
        for (int step = 0; step < OrbitFrames / 2; step++)
        {
            int f = ((frame + direction * step) % OrbitFrames + OrbitFrames) % OrbitFrames;
            if (TextureOf(keys[f]) is { } t)
                return t;
        }

        return null;
    }

    private string FactsOf(FixtureLibraryEntry entry, float width)
    {
        var info = _info?.Get(entry);
        int modes = info?.Modes.Count ?? -1;
        if (_facts is { } cached && ReferenceEquals(cached.Entry, entry) && cached.Modes == modes && MathF.Abs(cached.Width - width) < 0.5f &&
            ReferenceEquals(cached.Language, Loc.Language))
            return cached.Text;
        var parts = new List<string>(4) { entry.Category.DisplayName() };
        if (info is { Modes.Count: > 0 })
            parts.Add(Loc.Format(Str.LibModesCount, info.Modes.Count));
        if (entry.Specs?.WeightKg is { } kg && kg > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{kg:0.#} kg"));
        if (entry.Specs?.PowerConsumptionW is { } w && w > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{w:0} W"));
        string text = TextFit.Truncate(string.Join(" · ", parts), width, static s => ImGui.CalcTextSize(s).X);
        _facts = new FactsText(entry, modes, width, Loc.Language, text);
        return text;
    }

    // ---- models -----------------------------------------------------------------------------------------------------

    private void DrawModels()
    {
        float scale = UiScale.Effective(_ctx.Settings.Settings.UiScale, ImGui.GetStyle().FontScaleDpi);
        var style = ImGui.GetStyle();
        string importLabel = Labels.With(Icons.FileInput, Str.LibImport);
        float buttonWidth = ImGui.CalcTextSize(importLabel).X + style.FramePadding.X * 2;
        ImGui.SetNextItemWidth(-(buttonWidth + style.ItemSpacing.X));
        ImGui.InputTextWithHint("##model-search", Labels.With(Icons.Search, Str.Search), ref _modelSearch, 128);
        ImGui.SameLine();
        if (ImGui.Button(importLabel))
            _host.ShowModelImport();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get(Str.LibImportTooltip));
        ImGui.Separator();

        if (_filteredModelVersion != _models.Version || _filteredModelSearch != _modelSearch)
        {
            _filteredModelVersion = _models.Version;
            _filteredModelSearch = _modelSearch;
            _filteredModels.Clear();
            string term = _modelSearch.Trim();
            foreach (var item in _models.Items)
            {
                if (term.Length == 0 || item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || item.Format.Contains(term, StringComparison.OrdinalIgnoreCase))
                    _filteredModels.Add(item);
            }
        }

        ImGui.TextDisabled(Loc.Format(Str.LibModelCount, _filteredModels.Count));
        if (_models.Items.Count == 0)
        {
            EmptyState(Icons.Package, Loc.Get(Str.LibModelsEmpty));
            return;
        }

        if (_filteredModels.Count == 0)
        {
            EmptyState(Icons.Search, Loc.Get(Str.LibNoMatches));
            return;
        }

        EnsureWorker();
        if (!ImGui.BeginChild("##model-grid", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.None))
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
        string? remove = null;
        int rows = (_filteredModels.Count + columns - 1) / columns;
        unsafe
        {
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
                        if (index >= _filteredModels.Count)
                            break;
                        var item = _filteredModels[index];
                        var min = rowStart + new Vector2(c * (tile + spacing), 0f);
                        ImGui.SetCursorScreenPos(min);
                        ImGui.PushID(index);
                        ImGui.InvisibleButton("##model", new Vector2(tile, tileHeight));
                        bool hovered = ImGui.IsItemHovered();
                        if (hovered)
                            ImGui.SetTooltip(item.Path);
                        if (ImGui.BeginPopupContextItem("##model-menu"))
                        {
                            if (ImGui.MenuItem(Labels.With(Icons.Trash, Str.LibRemove)))
                                remove = item.Path;
                            ImGui.EndPopup();
                        }

                        UiTexture? texture = null;
                        var details = DetailsOf(item, size);
                        if (item.Kind == ModelItemKind.Image && details.Exists)
                            texture = Want(details.ImageKey, ThumbnailKind.Image, null, item.Path, size, 0f);
                        DragSource(null, item, size);
                        DrawTile(drawList, item, min, tile, tileHeight, texture, hovered, scale, item.Kind == ModelItemKind.Image ? Icons.Image : Icons.Box,
                            item.Name, details.Text);
                        ImGui.PopID();
                    }

                    ImGui.SetCursorScreenPos(rowStart + new Vector2(0f, tileHeight + spacing));
                    ImGui.Dummy(new Vector2(avail, 0f));
                }
            }

            clipper.End();
        }

        ImGui.EndChild();
        if (remove is not null)
            _models.Remove(remove);
    }

    // ---- tiles, drag and drop, textures -------------------------------------------------------------------------

    private void DrawTile(ImDrawListPtr drawList, object key, Vector2 min, float tile, float tileHeight, UiTexture? texture, bool highlighted, float scale,
        string icon, string name, string detail)
    {
        float rounding = 5f * scale;
        var max = min + new Vector2(tile, tileHeight);
        drawList.AddRectFilled(min, max, Theme.U32(highlighted ? Theme.Rgb(0x2C2F35) : Theme.Rgb(0x222428)), rounding);
        if (highlighted)
            drawList.AddRect(min, max, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.65f)), rounding);
        float pad = 4f * scale;
        var imageMin = min + new Vector2(pad);
        var imageMax = imageMin + new Vector2(tile - 2f * pad);
        if (texture is not null)
            drawList.AddImageRounded(texture.Ref, imageMin, imageMax, Vector2.Zero, Vector2.One, uint.MaxValue, rounding * 0.8f);
        else
            DrawIcon(drawList, icon, imageMin, imageMax);
        var texts = TextsOf(key, tile - 2f * pad, name, detail);
        float y = imageMax.Y + 3f * scale;
        var nameSize = ImGui.CalcTextSize(texts.Name);
        drawList.AddText(new Vector2(min.X + (tile - nameSize.X) * 0.5f, y), Theme.U32(Theme.Text), texts.Name);
        var detailSize = ImGui.CalcTextSize(texts.Detail);
        drawList.AddText(new Vector2(min.X + (tile - detailSize.X) * 0.5f, y + ImGui.GetTextLineHeight()), Theme.U32(Theme.TextMuted), texts.Detail);
    }

    private static void DrawIcon(ImDrawListPtr drawList, string icon, Vector2 min, Vector2 max)
    {
        drawList.AddRectFilled(min, max, Theme.U32(Theme.Rgb(0x1A1B1E)), 4f);
        float size = MathF.Min(max.X - min.X, max.Y - min.Y) * 0.36f;
        var font = ImGui.GetFont();
        var textSize = ImGui.CalcTextSize(icon) * (size / MathF.Max(1f, ImGui.GetFontSize()));
        drawList.AddText(font, size, (min + max - textSize) * 0.5f, Theme.U32(Theme.WithAlpha(Theme.TextMuted, 0.7f)), icon);
    }

    private void DragSource(FixtureLibraryEntry? entry, ModelLibraryItem? model, int size)
    {
        if (!ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            return;
        if (_dragged is null || !ReferenceEquals(_dragged.Fixture, entry) || !ReferenceEquals(_dragged.Model, model))
            _dragged = new LibraryItem(entry, model);
        var item = _dragged;
        unsafe
        {
            int token = 1;
            ImGui.SetDragDropPayload(PayloadType, &token, sizeof(int), ImGuiCond.Once);
        }

        // The drag tooltip: picture, name and how to place it.
        string stillKey = item.Fixture is { } f ? KeysOf(f, size).Still : item.Model is { Kind: ModelItemKind.Image } m ? DetailsOf(m, size).ImageKey : "";
        if (TextureOf(stillKey) is { } texture)
        {
            float side = 72f * ImGui.GetStyle().FontScaleDpi * ImGui.GetStyle().FontScaleMain;
            ImGui.Image(texture.Ref, new Vector2(side));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted(item.Name);
        ImGui.TextDisabled(Loc.Get(Str.LibDragHint));
        ImGui.EndGroup();
        ImGui.EndDragDropSource();
    }

    /// <summary>The texture of <paramref name="key"/> when made; otherwise asks for it (the request is built only then).</summary>
    private UiTexture? Want(string key, ThumbnailKind kind, FixtureLibraryEntry? entry, string? path, int size, float azimuth)
    {
        if (!NeedsPicture(key))
            return _slots[key].Texture;
        if (_wanted.Count < 64 && _worker is not null && _worker.Error is null | kind == ThumbnailKind.Image)
            _wanted.Add(new ThumbnailRequest(key, kind, entry, path, size, azimuth));
        return null;
    }

    /// <summary>False when the picture of <paramref name="key"/> was made (it is marked as used this frame).</summary>
    private bool NeedsPicture(string key)
    {
        if (!_slots.TryGetValue(key, out var slot))
            return true;
        slot.LastUsed = _frame;
        return !slot.Ready;
    }

    /// <summary>Cache keys of a fixture's pictures at one size (built once, so drawing does not format strings).</summary>
    private PictureKeys KeysOf(FixtureLibraryEntry entry, int size)
    {
        if (!_keys.TryGetValue(entry, out var keys) || keys.Size != size)
        {
            var orbit = new string[OrbitFrames];
            for (int i = 0; i < OrbitFrames; i++)
                orbit[i] = ThumbnailWorker.OrbitKey(entry, size, i);
            keys = new PictureKeys(size, ThumbnailWorker.StillKey(entry, size), orbit);
            if (_keys.Count > 8192)
                _keys.Clear();
            _keys[entry] = keys;
        }

        return keys;
    }

    /// <summary>The detail line and picture key of a model-library item; whether the file exists is checked every few seconds.</summary>
    private ModelDetails DetailsOf(ModelLibraryItem item, int size)
    {
        double now = _ctx.Timer.Elapsed;
        if (!_details.TryGetValue(item, out var d) || d.Size != size || !ReferenceEquals(d.Language, Loc.Language) || now - d.CheckedAt > 3.0)
        {
            bool exists = File.Exists(item.Path);
            string text = exists
                ? $"{item.Format} · {Loc.Get(item.Kind == ModelItemKind.Image ? Str.LibImageModel : Str.LibSceneModel)}"
                : Loc.Get(Str.LibFileMissing);
            d = new ModelDetails(size, Loc.Language, now, exists, text, ThumbnailWorker.ImageKey(item.Path, size));
            if (_details.Count > 4096)
                _details.Clear();
            _details[item] = d;
        }

        return d;
    }

    private UiTexture? TextureOf(string key)
    {
        if (key.Length == 0 || !_slots.TryGetValue(key, out var slot) || !slot.Ready)
            return null;
        slot.LastUsed = _frame;
        return slot.Texture;
    }

    private void EvictOldTextures()
    {
        int alive = 0;
        foreach (var (_, slot) in _slots)
            if (slot.Texture is not null)
                alive++;
        if (alive <= MaxTextures)
            return;
        _evict.Clear();
        foreach (var (key, slot) in _slots.OrderBy(kv => kv.Value.LastUsed))
        {
            if (alive <= MaxTextures * 3 / 4)
                break;
            if (slot.Texture is null || slot.LastUsed >= _frame - 1)
                continue;
            _evict.Add(key);
            alive--;
        }

        foreach (var key in _evict)
        {
            if (_slots.Remove(key, out var slot))
                _textures.Release(slot.Texture);
            _worker?.Forget(key);
        }
    }

    private void EnsureWorker()
    {
        if (_worker is not null || _library is null || _disposed)
            return;
        // Headless runs without a texture-capable backend show icons only.
        if (!UiTextures.IsAvailable)
            return;
        _worker = new ThumbnailWorker(_library, new ThumbnailStore(), _ctx.Log, _ctx.Gpu.Context.Info.DeviceName);
    }

    private TileText TextsOf(object key, float width, string name, string detail)
    {
        float fontSize = ImGui.GetFontSize();
        if (!_texts.TryGetValue(key, out var text) || MathF.Abs(text.Width - width) > 0.5f || text.FontSize != fontSize ||
            !ReferenceEquals(text.Language, Loc.Language) || !ReferenceEquals(text.SourceDetail, detail))
        {
            if (_texts.Count > 4096)
                _texts.Clear();
            text = new TileText(width, fontSize, Loc.Language, detail,
                TextFit.Truncate(name, width, static s => ImGui.CalcTextSize(s).X),
                TextFit.Truncate(detail, width, static s => ImGui.CalcTextSize(s).X));
            _texts[key] = text;
        }

        return text;
    }

    private string CountText(int count)
    {
        if (count != _countValue || !ReferenceEquals(_countLanguage, Loc.Language))
        {
            _countValue = count;
            _countLanguage = Loc.Language;
            _countText = Loc.Format(Str.LibCount, count);
        }

        return _countText;
    }

    private string PendingText(int pending)
    {
        // Shown every frame while pictures are being made: rebuilt only when the number changes.
        if (pending != _pendingValue || !ReferenceEquals(_pendingLanguage, Loc.Language))
        {
            _pendingValue = pending;
            _pendingLanguage = Loc.Language;
            _pendingText = "· " + Loc.Format(Str.LibThumbnailsPending, pending);
        }

        return _pendingText;
    }

    private static int Bucket(int pixels)
    {
        foreach (int b in SizeBuckets)
            if (b >= pixels)
                return b;
        return SizeBuckets[^1];
    }

    private static int[] BuildOrbitOrder()
    {
        // Coarse to fine (0°, 180°, 90°, 270°, 45° …): a whole turn is visible early, later frames make it smoother.
        var order = new List<int>(OrbitFrames);
        var seen = new HashSet<int>();
        for (int step = OrbitFrames; step >= 1; step /= 2)
        {
            for (int f = 0; f < OrbitFrames; f += step)
                if (seen.Add(f))
                    order.Add(f);
            if (step == 1)
                break;
            if (step % 2 == 1)
                step++;
        }

        for (int f = 0; f < OrbitFrames; f++)
            if (seen.Add(f))
                order.Add(f);
        return order.ToArray();
    }

    private static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float u = 1f - t;
        return 1f - u * u * u;
    }

    private static bool Inside(Vector2 p, Vector2 min, Vector2 max) => p.X >= min.X && p.Y >= min.Y && p.X < max.X && p.Y < max.Y;

    private static int CategoryIndex(FixtureCategory category)
    {
        for (int i = 0; i < Categories.Count; i++)
            if (Categories[i] == category)
                return i;
        return -1;
    }

    private static bool Chip(string label, bool selected, ref float x, float avail, ImGuiStylePtr style)
    {
        var size = ImGui.CalcTextSize(label) + style.FramePadding * 2;
        if (x > 0 && x + size.X > avail)
            x = 0;
        else if (x > 0)
            ImGui.SameLine();
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.55f));
        bool clicked = ImGui.SmallButton(label);
        if (selected)
            ImGui.PopStyleColor();
        x += size.X + style.ItemSpacing.X;
        return clicked;
    }

    private static void EmptyState(string icon, string text)
    {
        var avail = ImGui.GetContentRegionAvail();
        float wrap = MathF.Max(80f, MathF.Min(avail.X - 16f, 320f * ImGui.GetStyle().FontScaleMain));
        var iconSize = ImGui.CalcTextSize(icon);
        var textSize = ImGui.CalcTextSize(text, false, wrap);
        float total = iconSize.Y * 2 + textSize.Y;
        var start = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(start.X + (avail.X - iconSize.X) * 0.5f, start.Y + MathF.Max(8f, (avail.Y - total) * 0.4f)));
        ImGui.TextDisabled(icon);
        ImGui.SetCursorPosX(start.X + MathF.Max(0f, (avail.X - wrap) * 0.5f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    private static readonly string[] CategoryNames = Categories.Select(c => c.DisplayName()).ToArray();

    /// <summary>Icon for a fixture category.</summary>
    public static string CategoryIcon(FixtureCategory c) => c switch
    {
        FixtureCategory.Profile => Icons.LampCeiling,
        FixtureCategory.Wash or FixtureCategory.Par => Icons.Sun,
        FixtureCategory.WashBeam or FixtureCategory.Beam or FixtureCategory.Bsw => Icons.Sparkle,
        FixtureCategory.Spot or FixtureCategory.FollowSpot => Icons.Crosshair,
        FixtureCategory.LedBar => Icons.Rows,
        FixtureCategory.Haze or FixtureCategory.Fog => Icons.CloudFog,
        FixtureCategory.Fan => Icons.Fan,
        FixtureCategory.Scanner or FixtureCategory.Laser => Icons.Zap,
        FixtureCategory.Strobe or FixtureCategory.Blinder => Icons.Siren,
        FixtureCategory.Uv => Icons.SunMedium,
        FixtureCategory.GeneratorFire => Icons.Flame,
        _ => Icons.Lightbulb,
    };

    private sealed class TextureSlot
    {
        public UiTexture? Texture { get; set; }

        public bool Ready { get; set; }

        public long LastUsed { get; set; }
    }

    private sealed record TileText(float Width, float FontSize, string Language, string SourceDetail, string Name, string Detail);

    private sealed record PictureKeys(int Size, string Still, string[] Orbit);

    private sealed record ModelDetails(int Size, string Language, double CheckedAt, bool Exists, string Text, string ImageKey);

    private sealed record FactsText(FixtureLibraryEntry Entry, int Modes, float Width, string Language, string Text);
}
