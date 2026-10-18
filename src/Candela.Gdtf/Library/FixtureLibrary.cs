using System.Collections.Concurrent;

namespace Candela.Gdtf;

/// <summary>How a <see cref="FixtureLibrary"/> lookup found its entry, from most to least certain.</summary>
public enum FixtureMatchKind
{
    /// <summary>A library file has exactly the requested file name.</summary>
    ExactFileName,

    /// <summary>Same manufacturer and model after normalising case, separators, punctuation and brand aliases.</summary>
    SameModel,

    /// <summary>The manufacturer is not in the library but exactly one manufacturer has a model of that name.</summary>
    ModelOnly,

    /// <summary>
    /// The model is not in the library; the entry is the most similar model of the same manufacturer (same product
    /// family, e.g. "Rivale Wash" for a requested "Rivale Profile"). Callers should tell the user.
    /// </summary>
    Substitute,
}

/// <summary>Result of <see cref="FixtureLibrary.Find"/> / <see cref="FixtureLibrary.FindBySpec"/>.</summary>
/// <param name="Entry">The library file.</param>
/// <param name="Kind">How certain the match is.</param>
/// <param name="Score">1 for exact matches, the name similarity (0 … 1) for substitutes.</param>
public sealed record FixtureMatch(FixtureLibraryEntry Entry, FixtureMatchKind Kind, double Score)
{
    /// <summary>True for matches that identify the requested model (not a substitute).</summary>
    public bool IsExactModel => Kind is FixtureMatchKind.ExactFileName or FixtureMatchKind.SameModel or FixtureMatchKind.ModelOnly;
}

/// <summary>Entries of one library category, for the library UI.</summary>
/// <param name="Category">The category.</param>
/// <param name="Entries">Its entries sorted by display name, then newest revision first.</param>
public sealed record FixtureCategoryGroup(FixtureCategory Category, IReadOnlyList<FixtureLibraryEntry> Entries)
{
    /// <summary>Tab caption (e.g. "WASH BEAM").</summary>
    public string DisplayName => Category.DisplayName();
}

/// <summary>
/// The fixture library: <c>.gdtf</c> files of one or more folders, indexed from their file names
/// (<c>Manufacturer@Model@Revision.gdtf</c>) and the researched specs (<c>_specs/fixtures_index.json</c>) without
/// opening them, parsed on demand with an in-memory cache.
/// </summary>
/// <remarks>
/// <para>
/// Scanning only lists files, so a folder of thousands of fixtures is indexed in milliseconds. Categories come from the
/// specs index, or are guessed from the model name for files it does not know. <see cref="Find"/> resolves human
/// names as MVR files write them for stub GDTFs ("Martin@Atomic 3000 LED", "Ayrton @Rivale Profile"): case, spaces vs
/// underscores and punctuation are ignored, manufacturer names are reduced to their brand ("Martin" =
/// "Martin_Professional", "Robe" = "Robe_Lighting", "Clay Paky" = "Clay_Paky"), the newest revision wins, and when the
/// model is missing the most similar model of the same family is offered as a <see cref="FixtureMatchKind.Substitute"/>.
/// </para>
/// <para>
/// Thread safety: all members may be called from any thread. Queries see an immutable snapshot of the entries, adding
/// folders swaps the snapshot atomically, and <see cref="GetFixtureType"/> parses each file at most once at a time (other
/// callers wait for the same result). The parse cache keeps the <see cref="CacheCapacity"/> most recently used
/// fixture types.
/// </para>
/// <para>
/// Below the in-memory cache, parsed fixture types are kept on disk by <see cref="DiskCache"/> (by default the shared
/// per-user <see cref="FixtureTypeDiskCache.Default"/>), so re-opening a fixture in a later session loads it in a
/// fraction of the parse time. The disk cache is keyed by file content and reader version and is transparent: a
/// missing, stale or damaged cache file only means the file is parsed again.
/// </para>
/// </remarks>
public sealed class FixtureLibrary
{
    /// <summary>Default number of parsed fixture types kept in memory.</summary>
    public const int DefaultCacheCapacity = 128;

    private readonly object _gate = new();
    private readonly object _trimGate = new();
    private readonly ConcurrentDictionary<string, CacheSlot> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = new();
    private readonly List<string> _folders = new();
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private long _clock;
    private FixtureTypeDiskCache? _diskCache = FixtureTypeDiskCache.Default;

    /// <summary>Creates an empty library; <paramref name="specs"/> supplies categories and physical data (null: none).</summary>
    public FixtureLibrary(FixtureSpecsIndex? specs = null, int cacheCapacity = DefaultCacheCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheCapacity, 1);
        Specs = specs ?? FixtureSpecsIndex.Empty;
        CacheCapacity = cacheCapacity;
    }

    /// <summary>Researched fixture data used for categories and display names.</summary>
    public FixtureSpecsIndex Specs { get; }

    /// <summary>Maximum number of parsed fixture types kept in the cache.</summary>
    public int CacheCapacity { get; }

    /// <summary>
    /// Persistent cache used when a fixture type is parsed (default: <see cref="FixtureTypeDiskCache.Default"/>; null
    /// parses every file from scratch). Changing it affects parses started afterwards.
    /// </summary>
    public FixtureTypeDiskCache? DiskCache
    {
        get => Volatile.Read(ref _diskCache);
        set => Volatile.Write(ref _diskCache, value);
    }

    /// <summary>All entries, sorted by manufacturer, model and newest revision first.</summary>
    public IReadOnlyList<FixtureLibraryEntry> Entries => _snapshot.Entries;

    /// <summary>Number of entries.</summary>
    public int Count => _snapshot.Entries.Count;

    /// <summary>Folders added so far (full paths, in priority order).</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate) return _folders.ToArray();
        }
    }

    /// <summary>Problems found while scanning (missing folders, duplicate file names, unreadable files).</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate) return _warnings.ToArray();
        }
    }

    /// <summary>Number of fixture types currently held in the parse cache.</summary>
    public int CachedFixtureTypeCount => _cache.Count;

    /// <summary>Creates a library from one folder.</summary>
    public static FixtureLibrary Scan(string folder, FixtureSpecsIndex? specs = null, SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
        Scan(new[] { folder }, specs, searchOption);

    /// <summary>Creates a library from several folders; a file name found in an earlier folder wins over later ones.</summary>
    public static FixtureLibrary Scan(IEnumerable<string> folders, FixtureSpecsIndex? specs = null, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var library = new FixtureLibrary(specs);
        foreach (var f in folders) library.AddFolder(f, searchOption);
        return library;
    }

    /// <summary>
    /// Adds the <c>*.gdtf</c> files of a folder (no XML is read). A missing or unreadable folder only produces a
    /// warning. Returns the number of entries added; files whose name is already in the library are skipped.
    /// </summary>
    public int AddFolder(string folder, SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        string full;
        List<string> files;
        try
        {
            full = Path.GetFullPath(folder);
            if (!Directory.Exists(full))
            {
                AddWarning($"Library folder '{folder}' does not exist.");
                return 0;
            }
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            files = Directory.EnumerateFiles(full, "*.gdtf", options).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AddWarning($"Library folder '{folder}' cannot be read: {e.Message}");
            return 0;
        }
        files.Sort(StringComparer.Ordinal);
        lock (_gate)
            if (!_folders.Contains(full, StringComparer.Ordinal)) _folders.Add(full);
        return AddFiles(files);
    }

    /// <summary>Adds individual <c>.gdtf</c> files. Returns the number of entries added.</summary>
    public int AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var created = new List<FixtureLibraryEntry>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    AddWarning($"'{path}' does not exist.");
                    continue;
                }
                created.Add(new FixtureLibraryEntry(info.FullName, info.Length, info.LastWriteTimeUtc, Specs.Find(info.Name)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AddWarning($"'{path}' cannot be read: {e.Message}");
            }
        }

        lock (_gate)
        {
            var current = _snapshot;
            var names = new HashSet<string>(current.Entries.Select(e => e.FileName), StringComparer.OrdinalIgnoreCase);
            var merged = new List<FixtureLibraryEntry>(current.Entries.Count + created.Count);
            merged.AddRange(current.Entries);
            int added = 0;
            foreach (var e in created)
            {
                if (!names.Add(e.FileName))
                {
                    _warnings.Add($"'{e.FilePath}' has the same file name as a file already in the library; ignored.");
                    continue;
                }
                if (!e.FollowsNamingPattern)
                    _warnings.Add($"'{e.FileName}' does not follow the Manufacturer@Model@Revision naming pattern.");
                merged.Add(e);
                added++;
            }
            if (added > 0) _snapshot = new Snapshot(merged);
            return added;
        }
    }

    // ---------------------------------------------------------------- lookups

    /// <summary>Entry by file name (with or without folder and <c>.gdtf</c>, case-insensitive), or null.</summary>
    public FixtureLibraryEntry? FindByFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return _snapshot.ByFileName.TryGetValue(Stem(fileName), out var e) ? e : null;
    }

    /// <summary>Entry whose specs record has this GDTF <c>FixtureTypeID</c> (newest revision when several), or null.</summary>
    public FixtureLibraryEntry? FindByFixtureTypeId(Guid fixtureTypeId) =>
        fixtureTypeId != Guid.Empty && _snapshot.ByTypeId.TryGetValue(fixtureTypeId, out var e) ? e : null;

    /// <summary>
    /// Resolves a GDTF reference as MVR files write it (<c>GDTFSpec</c>: <c>"Manufacturer@Model[@Revision][.gdtf]"</c>,
    /// possibly with a folder): an identical library file name first, then <see cref="Find"/> on the manufacturer and
    /// model parts (preferring the same revision text when it exists). Null when nothing fits.
    /// </summary>
    public FixtureMatch? FindBySpec(string? gdtfSpec)
    {
        if (string.IsNullOrWhiteSpace(gdtfSpec)) return null;
        var exact = FindByFileName(gdtfSpec);
        if (exact is not null) return new FixtureMatch(exact, FixtureMatchKind.ExactFileName, 1);

        var parts = Stem(gdtfSpec).Split('@');
        if (parts.Length < 2) return Find(null, parts[0]);
        var match = Find(parts[0], parts[1]);
        if (match is { Kind: FixtureMatchKind.SameModel } && parts.Length > 2)
        {
            string revision = FixtureNames.Key(string.Join("@", parts, 2, parts.Length - 2));
            foreach (var r in GetRevisions(match.Entry))
                if (FixtureNames.Key(r.RevisionText) == revision) return match with { Entry = r };
        }
        return match;
    }

    /// <summary>
    /// Finds a fixture by manufacturer and model name as a human (or an MVR exporter) wrote them. Order: same
    /// manufacturer and model (newest revision) → same model of the only manufacturer that has it when the
    /// manufacturer is unknown → the most similar model of the manufacturer (<see cref="FixtureMatchKind.Substitute"/>).
    /// Null when nothing fits.
    /// </summary>
    public FixtureMatch? Find(string? manufacturer, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        var snapshot = _snapshot;
        string manufacturerKey = FixtureNames.ManufacturerKey(manufacturer);
        var modelKeys = RequestKeys(modelName, manufacturer, manufacturerKey);
        if (modelKeys.Count == 0) return null;

        var brandEntries = manufacturerKey.Length > 0 ? snapshot.EntriesOfManufacturer(manufacturerKey) : null;
        if (brandEntries is { Count: > 0 })
        {
            if (BestMatch(brandEntries, modelKeys) is { } best) return new FixtureMatch(best, FixtureMatchKind.SameModel, 1);
        }
        else
        {
            if (manufacturerKey.Length == 0 && FindWithBrandPrefix(snapshot, modelKeys[0]) is { } prefixed)
                return new FixtureMatch(prefixed, FixtureMatchKind.SameModel, 1);
            foreach (var key in modelKeys)
            {
                if (!snapshot.ByModelKey.TryGetValue(key, out var list)) continue;
                var brands = list.Select(e => e.ManufacturerKey).Distinct(StringComparer.Ordinal).Count();
                if (brands != 1) continue;
                return new FixtureMatch(BestMatch(list, new List<string> { key })!, FixtureMatchKind.ModelOnly, 1);
            }
            return null;
        }

        // Substitute: most similar model of the same manufacturer.
        FixtureLibraryEntry? candidate = null;
        double bestScore = 0;
        foreach (var e in brandEntries)
        {
            double score = System.Math.Max(
                FixtureNames.ModelSimilarity(modelName, e.FileModel.Replace('_', ' ')),
                FixtureNames.ModelSimilarity(modelName, e.ModelName));
            if (score <= 0) continue;
            if (candidate is null || score > bestScore + 1e-12 || (System.Math.Abs(score - bestScore) <= 1e-12 && IsBetter(e, candidate)))
            {
                candidate = e;
                bestScore = score;
            }
        }
        return candidate is null ? null : new FixtureMatch(candidate, FixtureMatchKind.Substitute, bestScore);
    }

    /// <summary>All revisions of the entry's model (including itself), newest first.</summary>
    public IReadOnlyList<FixtureLibraryEntry> GetRevisions(FixtureLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var list = _snapshot.EntriesOfManufacturer(entry.ManufacturerKey);
        var result = list.Where(e => e.IsSameModel(entry)).ToList();
        if (result.Count == 0) result.Add(entry);
        result.Sort((a, b) => b.Revision.CompareTo(a.Revision));
        return result;
    }

    /// <summary>True when no other revision of the same model is newer.</summary>
    public bool IsLatestRevision(FixtureLibraryEntry entry) => ReferenceEquals(GetRevisions(entry)[0], entry);

    /// <summary>
    /// Entries grouped by category in library tab order (<see cref="FixtureCategories.All"/>). With
    /// <paramref name="latestRevisionsOnly"/> each model appears once (its newest revision); with
    /// <paramref name="includeEmpty"/> categories without entries are listed too.
    /// </summary>
    public IReadOnlyList<FixtureCategoryGroup> GroupByCategory(bool latestRevisionsOnly = false, bool includeEmpty = false)
    {
        var snapshot = _snapshot;
        var latest = latestRevisionsOnly ? snapshot.Latest : null;
        var groups = new List<FixtureCategoryGroup>();
        foreach (var category in FixtureCategories.All)
        {
            var list = snapshot.ByCategory.TryGetValue(category, out var all) ? all : (IReadOnlyList<FixtureLibraryEntry>)Array.Empty<FixtureLibraryEntry>();
            if (latest is not null) list = list.Where(latest.Contains).ToList();
            if (list.Count > 0 || includeEmpty) groups.Add(new FixtureCategoryGroup(category, list));
        }
        return groups;
    }

    /// <summary>Entries of one category, sorted by display name.</summary>
    public IReadOnlyList<FixtureLibraryEntry> GetEntries(FixtureCategory category) =>
        _snapshot.ByCategory.TryGetValue(category, out var list) ? list : Array.Empty<FixtureLibraryEntry>();

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// The parsed fixture type of an entry, from the cache or read now (on the calling thread). Concurrent calls for
    /// the same entry share one parse. Throws <see cref="GdtfFormatException"/> or <see cref="IOException"/> when the
    /// file cannot be read at all; such failures are not cached.
    /// </summary>
    public FixtureType GetFixtureType(FixtureLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var slot = _cache.AddOrUpdate(entry.FilePath,
            static (_, a) => new CacheSlot(a.Entry, a.Disk),
            static (_, existing, a) => existing.IsFor(a.Entry) ? existing : new CacheSlot(a.Entry, a.Disk),
            (Entry: entry, Disk: DiskCache));
        slot.LastAccess = Interlocked.Increment(ref _clock);
        FixtureType result;
        try
        {
            result = slot.Value.Value;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<string, CacheSlot>(entry.FilePath, slot));
            throw;
        }
        if (_cache.Count > CacheCapacity) Trim();
        return result;
    }

    /// <summary>Like <see cref="GetFixtureType"/> but parses on a thread-pool thread (completes synchronously when cached).</summary>
    public Task<FixtureType> GetFixtureTypeAsync(FixtureLibraryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (TryGetCachedFixtureType(entry, out var cached)) return Task.FromResult(cached!);
        return Task.Run(() => GetFixtureType(entry), cancellationToken);
    }

    /// <summary>The cached fixture type of an entry when it has been parsed already.</summary>
    public bool TryGetCachedFixtureType(FixtureLibraryEntry entry, out FixtureType? fixtureType)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_cache.TryGetValue(entry.FilePath, out var slot) && slot.IsFor(entry) && slot.Value.IsValueCreated)
        {
            slot.LastAccess = Interlocked.Increment(ref _clock);
            fixtureType = slot.Value.Value;
            return true;
        }
        fixtureType = null;
        return false;
    }

    /// <summary>Drops one parsed fixture type from the cache (e.g. after the file was edited).</summary>
    public void Evict(FixtureLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _cache.TryRemove(entry.FilePath, out _);
    }

    /// <summary>Drops all parsed fixture types.</summary>
    public void ClearCache() => _cache.Clear();

    // ---------------------------------------------------------------- internals

    private void AddWarning(string message)
    {
        lock (_gate) _warnings.Add(message);
    }

    /// <summary>Removes least recently used, completed entries until the cache fits its capacity.</summary>
    private void Trim()
    {
        lock (_trimGate)
        {
            while (_cache.Count > CacheCapacity)
            {
                KeyValuePair<string, CacheSlot>? oldest = null;
                foreach (var kv in _cache)
                {
                    if (!kv.Value.Value.IsValueCreated) continue; // still being parsed
                    if (oldest is null || kv.Value.LastAccess < oldest.Value.Value.LastAccess) oldest = kv;
                }
                if (oldest is null || !_cache.TryRemove(oldest.Value)) return;
            }
        }
    }

    /// <summary>
    /// For names written without a separate manufacturer ("Martin MAC Aura XB"): the longest brand spelling of the
    /// library that starts the key, then the newest entry of that brand with the remaining model key.
    /// </summary>
    private static FixtureLibraryEntry? FindWithBrandPrefix(Snapshot snapshot, string key)
    {
        foreach (var (prefix, brand) in snapshot.BrandPrefixes)
        {
            if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = new List<string> { key[prefix.Length..] };
            if (BestMatch(snapshot.ByManufacturer[brand], rest) is { } best) return best;
        }
        return null;
    }

    /// <summary>
    /// The entry that answers best to <paramref name="keys"/>: one whose file name gives the model beats one that only
    /// has it as a specs alias (two models can share an alias: "Robin Spikie+" was once indistinguishable from
    /// "Robin Spikie"), then the newest revision. Null when none answers.
    /// </summary>
    private static FixtureLibraryEntry? BestMatch(IEnumerable<FixtureLibraryEntry> entries, List<string> keys)
    {
        FixtureLibraryEntry? best = null;
        int bestRank = int.MaxValue;
        foreach (var e in entries)
        {
            int rank = MatchRank(e, keys);
            if (rank < 0) continue;
            if (best is null || rank < bestRank || (rank == bestRank && IsBetter(e, best)))
            {
                best = e;
                bestRank = rank;
            }
        }
        return best;
    }

    /// <summary>0 when a key matches the entry's file-name model, 1 when only a specs alias matches, −1 for no match.</summary>
    private static int MatchRank(FixtureLibraryEntry e, List<string> keys)
    {
        int rank = -1;
        for (int i = 0; i < e.ModelKeys.Count; i++)
        {
            foreach (var k in keys)
            {
                if (!string.Equals(k, e.ModelKeys[i], StringComparison.Ordinal)) continue;
                if (i < e.FileKeyCount) return 0;
                rank = 1;
            }
        }
        return rank;
    }

    /// <summary>Newer revision wins; equal revisions fall back to the file name so results are deterministic.</summary>
    private static bool IsBetter(FixtureLibraryEntry candidate, FixtureLibraryEntry current)
    {
        int c = candidate.Revision.CompareTo(current.Revision);
        return c > 0 || (c == 0 && string.CompareOrdinal(candidate.FileName, current.FileName) < 0);
    }

    /// <summary>Normalised keys of a requested model name: as written and without a leading brand name.</summary>
    private static List<string> RequestKeys(string modelName, string? manufacturer, string manufacturerKey)
    {
        var keys = new List<string>(3);
        string key = FixtureNames.Key(modelName);
        if (key.Length == 0) return keys;
        keys.Add(key);
        foreach (var prefix in new[] { manufacturerKey, FixtureNames.Key(manufacturer) })
            if (prefix.Length > 0 && key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.Ordinal) && !keys.Contains(key[prefix.Length..]))
                keys.Add(key[prefix.Length..]);
        return keys;
    }

    private static string Stem(string fileName)
    {
        string name = fileName.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        name = name.Trim();
        return name.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    /// <summary>Immutable view of the entries with lookup tables; replaced as a whole when files are added.</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(new List<FixtureLibraryEntry>());

        public Snapshot(List<FixtureLibraryEntry> entries)
        {
            entries.Sort(CompareForListing);
            Entries = entries;
            foreach (var e in entries)
            {
                ByFileName.TryAdd(Stem(e.FileName), e);
                Add(ByManufacturer, e.ManufacturerKey, e);
                foreach (var k in e.ModelKeys) Add(ByModelKey, k, e);
                if (e.Specs?.FixtureTypeId is { } id && Guid.TryParse(id, out var guid) &&
                    (!ByTypeId.TryGetValue(guid, out var other) || IsBetter(e, other)))
                    ByTypeId[guid] = e;
            }

            var byCategory = new Dictionary<FixtureCategory, List<FixtureLibraryEntry>>();
            foreach (var e in entries)
            {
                if (!byCategory.TryGetValue(e.Category, out var list)) byCategory[e.Category] = list = new List<FixtureLibraryEntry>();
                list.Add(e);
            }
            foreach (var (category, list) in byCategory)
            {
                list.Sort((a, b) =>
                {
                    int c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : b.Revision.CompareTo(a.Revision);
                });
                ByCategory[category] = list;
            }

            var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                foreach (var spelling in new[] { e.ManufacturerKey, FixtureNames.Key(e.FileManufacturer), FixtureNames.Key(e.Manufacturer) })
                    if (spelling.Length >= 2) prefixes.TryAdd(spelling, e.ManufacturerKey);
            }
            BrandPrefixes = prefixes.OrderByDescending(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => (p.Key, p.Value)).ToArray();

            foreach (var (_, list) in ByManufacturer)
            {
                var newestPerModel = new Dictionary<string, FixtureLibraryEntry>(StringComparer.Ordinal);
                foreach (var e in list)
                {
                    string model = e.ModelKeys[0];
                    if (!newestPerModel.TryGetValue(model, out var cur) || IsBetter(e, cur)) newestPerModel[model] = e;
                }
                foreach (var e in newestPerModel.Values) Latest.Add(e);
            }
        }

        public List<FixtureLibraryEntry> Entries { get; }
        public Dictionary<string, FixtureLibraryEntry> ByFileName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<FixtureLibraryEntry>> ByManufacturer { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<FixtureLibraryEntry>> ByModelKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, FixtureLibraryEntry> ByTypeId { get; } = new();
        public Dictionary<FixtureCategory, IReadOnlyList<FixtureLibraryEntry>> ByCategory { get; } = new();
        public HashSet<FixtureLibraryEntry> Latest { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Normalised brand spellings (core, file and specs forms), longest first, with their core key.</summary>
        public (string Prefix, string Brand)[] BrandPrefixes { get; }

        /// <summary>
        /// Entries of a manufacturer core key; when no library brand has exactly that key, brands whose key starts with it
        /// or that it starts with (at least three letters, e.g. "chauvetpro" ↔ "chauvet").
        /// </summary>
        public IReadOnlyList<FixtureLibraryEntry> EntriesOfManufacturer(string key)
        {
            if (key.Length == 0) return Array.Empty<FixtureLibraryEntry>();
            if (ByManufacturer.TryGetValue(key, out var list)) return list;
            if (key.Length < 3) return Array.Empty<FixtureLibraryEntry>();
            var result = new List<FixtureLibraryEntry>();
            foreach (var (brand, entries) in ByManufacturer)
                if (brand.Length >= 3 && (brand.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(brand, StringComparison.Ordinal)))
                    result.AddRange(entries);
            return result;
        }

        private static void Add(Dictionary<string, List<FixtureLibraryEntry>> d, string key, FixtureLibraryEntry e)
        {
            if (!d.TryGetValue(key, out var list)) d[key] = list = new List<FixtureLibraryEntry>(1);
            list.Add(e);
        }

        private static int CompareForListing(FixtureLibraryEntry a, FixtureLibraryEntry b)
        {
            int c = string.Compare(a.Manufacturer, b.Manufacturer, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.ModelName, b.ModelName, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = b.Revision.CompareTo(a.Revision);
            return c != 0 ? c : string.CompareOrdinal(a.FileName, b.FileName);
        }
    }

    /// <summary>One cache entry: the lazily parsed fixture type of a file version and its last use.</summary>
    private sealed class CacheSlot
    {
        private readonly DateTime _stamp;
        private readonly long _size;

        public CacheSlot(FixtureLibraryEntry entry, FixtureTypeDiskCache? disk)
        {
            _stamp = entry.LastWriteTimeUtc;
            _size = entry.FileSize;
            string path = entry.FilePath;
            Value = new Lazy<FixtureType>(() => disk is null ? GdtfReader.ReadFile(path) : disk.ReadFile(path), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Lazy<FixtureType> Value { get; }

        public long LastAccess { get; set; }

        public bool IsFor(FixtureLibraryEntry entry) => entry.LastWriteTimeUtc == _stamp && entry.FileSize == _size;
    }
}
