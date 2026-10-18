using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Project.Internal;

/// <summary>
/// Reads a <c>.candela</c> archive. Order: manifest (format and version check, migrations), <c>scene.json</c> (required),
/// <c>settings.json</c>, assets, GDTF files, thumbnail. Everything after the scene is tolerant: one damaged element is an
/// issue, never an exception.
/// </summary>
internal static class ProjectReader
{
    private const string CategoryProgram = IssueCategory.Program;

    public static ProjectData Read(Stream source, ProjectLoadOptions options)
    {
        using var zip = OpenZip(source);
        var access = new ArchiveAccess(zip, options);
        var issues = new IssueCollector(options.MaxIssuesPerCode);

        // 1. Manifest: what the file is and which version wrote it.
        var manifestNode = ReadManifestNode(access, issues, out bool hasManifest);
        if (!hasManifest && !access.Exists(ArchivePaths.Scene))
            throw new ProjectFormatException("The file is not a Candela project: it has neither a manifest nor a scene.");
        var manifest = manifestNode is null ? new ProjectManifest() : ParseManifest(manifestNode, issues);
        CheckFormatId(manifestNode);
        int version = ReadFormatVersion(manifestNode, hasManifest, issues);
        if (version > ProjectFile.FormatVersion)
        {
            var writtenBy = string.IsNullOrEmpty(manifest.AppVersion) ? "a newer version of Candela" : $"Candela {manifest.AppVersion}";
            throw new ProjectVersionException(
                string.Create(CultureInfo.InvariantCulture,
                    $"This project was saved by {writtenBy} in project format {version}. This version of Candela ({ProjectFile.DefaultAppVersion}) reads project format {ProjectFile.FormatVersion} and older; update Candela to open it."),
                version, ProjectFile.FormatVersion, string.IsNullOrEmpty(manifest.AppVersion) ? null : manifest.AppVersion);
        }
        if (version < ProjectFile.FormatVersion && manifestNode is not null)
        {
            Migrate(access, manifestNode, version, manifest.AppVersion, options.Migrations ?? ProjectMigrationRegistry.Default, issues);
            manifest = ParseManifest(manifestNode, null) with { FormatVersion = version };
        }

        // 2. Scene (required).
        byte[] sceneBytes;
        try
        {
            sceneBytes = ReadJson(access, ArchivePaths.Scene, issues)
                         ?? throw new ProjectFormatException("The project has no scene (scene.json); the file is incomplete or not a Candela project.");
        }
        catch (EntryReadException e)
        {
            throw new ProjectFormatException($"The scene (scene.json) cannot be read: {e.Message}.", e);
        }
        var unknownComponents = new Dictionary<Guid, IReadOnlyList<JsonElement>>();
        var scene = SceneJsonReader.Read(sceneBytes, issues, unknownComponents);

        // 3. Everything else: tolerant.
        var store = new AssetStore(options.Materials);
        var project = new ProjectData(scene, store)
        {
            Id = manifest.ProjectId != Guid.Empty ? manifest.ProjectId : scene.Id,
            CreatedUtc = manifest.CreatedUtc != default ? manifest.CreatedUtc : DateTime.UtcNow,
            Manifest = manifest,
        };
        foreach (var (entity, components) in unknownComponents) project.UnknownComponentsMutable[entity] = components;
        ReadSettings(access, project.Settings, hasManifest, issues);
        var assetUsers = CollectAssetUsers(scene);
        ReadAssets(access, store, assetUsers, options, issues);
        ReadFixtures(access, project, options, issues);
        ReadThumbnail(access, project, issues);
        project.LoadIssues = issues.ToList();
        return project;
    }

    public static ProjectManifest ReadManifestOnly(Stream source)
    {
        using var zip = OpenZip(source);
        var access = new ArchiveAccess(zip, ProjectLoadOptions.Default);
        var node = ReadManifestNode(access, null, out bool exists);
        if (!exists) throw new ProjectFormatException("The file is not a Candela project (it has no manifest).");
        if (node is null) throw new ProjectFormatException("The project manifest is damaged.");
        CheckFormatId(node);
        return ParseManifest(node, null);
    }

    public static byte[]? ReadThumbnailOnly(Stream source)
    {
        using var zip = OpenZip(source);
        var access = new ArchiveAccess(zip, ProjectLoadOptions.Default);
        try
        {
            var bytes = access.Read(ArchivePaths.Thumbnail);
            return bytes is not null && ProjectWriter.IsPng(bytes) ? bytes : null;
        }
        catch (EntryReadException)
        {
            return null;
        }
    }

    private static ZipArchive OpenZip(Stream source)
    {
        try
        {
            var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                // The central directory is read lazily; read it now so a damaged directory is reported here.
                _ = zip.Entries.Count;
                return zip;
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new ProjectFormatException("The file is not a Candela project (it is not a zip archive or the archive is damaged).", e);
        }
        catch (IOException e)
        {
            throw new ProjectFormatException($"The project archive cannot be read: {e.Message}", e);
        }
    }

    // ----- manifest -------------------------------------------------------------------------------------------------

    private static JsonObject? ReadManifestNode(ArchiveAccess access, IssueCollector? issues, out bool exists)
    {
        exists = access.Exists(ArchivePaths.Manifest);
        if (!exists)
        {
            issues?.Add(IssueSeverity.Warning, CategoryProgram, ProjectIssueCodes.ManifestMissing,
                "The project has no manifest; it was read as the current format.");
            return null;
        }
        try
        {
            var bytes = ReadJson(access, ArchivePaths.Manifest, issues);
            if (bytes is not null && JsonNode.Parse(bytes, null, ProjectSettings.ReadOptions) is JsonObject obj) return obj;
            issues?.Add(IssueSeverity.Warning, CategoryProgram, ProjectIssueCodes.ManifestInvalid,
                "The project manifest is not a JSON object; it was ignored and the file was read as the current format.");
        }
        catch (Exception e) when (e is EntryReadException or JsonException)
        {
            issues?.Add(IssueSeverity.Warning, CategoryProgram, ProjectIssueCodes.ManifestInvalid,
                $"The project manifest is damaged ({e.Message}); the file was read as the current format.");
        }
        return null;
    }

    private static void CheckFormatId(JsonObject? manifest)
    {
        if (manifest?["format"] is JsonValue v && v.TryGetValue(out string? format) && format != ProjectFile.FormatId)
            throw new ProjectFormatException($"The file is not a Candela project (its manifest describes '{format}').");
    }

    private static int ReadFormatVersion(JsonObject? manifest, bool hasManifest, IssueCollector issues)
    {
        if (manifest is null) return ProjectFile.FormatVersion;
        if (manifest["formatVersion"] is JsonValue v && TryGetInt(v, out int version) && version >= 0) return version;
        if (hasManifest)
        {
            issues.Add(IssueSeverity.Warning, CategoryProgram, ProjectIssueCodes.ManifestInvalid,
                "The project manifest has no valid format version; the file was read as the current format.");
        }
        return ProjectFile.FormatVersion;
    }

    /// <summary>Reads the manifest field by field, so one field of a wrong type costs only that field.</summary>
    private static ProjectManifest ParseManifest(JsonObject obj, IssueCollector? issues)
    {
        var invalid = new List<string>();

        string Str(string name, string fallback)
        {
            var node = obj[name];
            if (node is null) return fallback;
            if (node is JsonValue v && v.TryGetValue(out string? s)) return s;
            invalid.Add(name);
            return fallback;
        }

        int Int(string name)
        {
            var node = obj[name];
            if (node is null) return 0;
            if (node is JsonValue v && TryGetInt(v, out int i) && i >= 0) return i;
            invalid.Add(name);
            return 0;
        }

        DateTime Time(string name)
        {
            var node = obj[name];
            if (node is null) return default;
            if (node is JsonValue v && v.TryGetValue(out string? s) &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
            {
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            }
            invalid.Add(name);
            return default;
        }

        Guid Id(string name)
        {
            var node = obj[name];
            if (node is null) return Guid.Empty;
            if (node is JsonValue v && v.TryGetValue(out string? s) && Guid.TryParse(s, out var g)) return g;
            invalid.Add(name);
            return Guid.Empty;
        }

        bool Bool(string name)
        {
            var node = obj[name];
            if (node is null) return false;
            if (node is JsonValue v && v.TryGetValue(out bool b)) return b;
            invalid.Add(name);
            return false;
        }

        int version = obj["formatVersion"] is JsonValue fv && TryGetInt(fv, out int ver) ? ver : ProjectFile.FormatVersion;
        var manifest = new ProjectManifest
        {
            Format = Str("format", ProjectFile.FormatId),
            FormatVersion = version,
            AppVersion = Str("appVersion", ""),
            CreatedUtc = Time("createdUtc"),
            ModifiedUtc = Time("modifiedUtc"),
            ProjectId = Id("projectId"),
            SceneName = Str("sceneName", ""),
            EntityCount = Int("entityCount"),
            FixtureCount = Int("fixtureCount"),
            AssetCount = Int("assetCount"),
            GdtfCount = Int("gdtfCount"),
            HasThumbnail = Bool("hasThumbnail"),
        };
        if (invalid.Count > 0 && issues is not null)
        {
            issues.Add(IssueSeverity.Note, CategoryProgram, ProjectIssueCodes.ManifestInvalid,
                $"Manifest field(s) {string.Join(", ", invalid)} have invalid values; defaults were used.");
        }
        return manifest;
    }

    private static bool TryGetInt(JsonValue value, out int result)
    {
        result = 0;
        if (value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue(out int i))
        {
            result = i;
            return true;
        }
        if (value.TryGetValue(out double d) && d == System.Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            result = (int)d;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads a JSON entry (null when it does not exist) and repairs text that System.Text.Json parses but cannot turn into
    /// strings (invalid UTF-8, lone surrogate escapes; see <see cref="JsonText"/>), so no string access can throw later.
    /// </summary>
    private static byte[]? ReadJson(ArchiveAccess access, string name, IssueCollector? issues)
    {
        var bytes = access.Read(name);
        if (bytes is null) return null;
        var repaired = JsonText.Repair(bytes, out int repairs);
        if (repairs > 0)
        {
            issues?.Add(IssueSeverity.Note, CategoryProgram, ProjectIssueCodes.TextRepaired,
                string.Create(CultureInfo.InvariantCulture, $"{repairs} invalid character(s) in {name} were replaced by U+FFFD."));
        }
        return repaired;
    }

    // ----- migrations -----------------------------------------------------------------------------------------------

    private static void Migrate(ArchiveAccess access, JsonObject manifest, int version, string writtenBy, ProjectMigrationRegistry registry,
        IssueCollector issues)
    {
        if (!registry.CanMigrate(version)) throw registry.CannotMigrate(version, string.IsNullOrEmpty(writtenBy) ? null : writtenBy);

        var documents = new[] { ArchivePaths.Scene, ArchivePaths.Settings, ArchivePaths.AssetIndex, ArchivePaths.FixtureIndex };
        var before = new JsonNode?[documents.Length];
        for (int i = 0; i < documents.Length; i++) before[i] = TryParseNode(access, documents[i]);
        if (before[0] is null && access.Exists(ArchivePaths.Scene))
            throw new ProjectFormatException("The scene (scene.json) is damaged and the project cannot be converted to the current format.");

        var context = new ProjectMigrationContext(manifest, before[0], before[1], before[2], before[3], access.EntryNames, access.TryRead,
            access.Overlay);
        IReadOnlyList<string> steps;
        try
        {
            steps = registry.Migrate(version, context, string.IsNullOrEmpty(writtenBy) ? null : writtenBy);
        }
        catch (ProjectFormatException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new ProjectFormatException(
                string.Create(CultureInfo.InvariantCulture, $"The project (format {version}) could not be converted to the current format: {e.Message}"), e);
        }

        var after = new[] { context.Scene, context.Settings, context.AssetIndex, context.FixtureIndex };
        for (int i = 0; i < documents.Length; i++)
        {
            if (after[i] is { } node) access.Overlay[documents[i]] = JsonSerializer.SerializeToUtf8Bytes(node, CandelaJson.CompactOptions);
            else if (before[i] is not null) access.Overlay[documents[i]] = null;
        }
        foreach (var step in steps)
            issues.Add(IssueSeverity.Note, CategoryProgram, ProjectIssueCodes.Migrated, $"The project was converted from an older format ({step}).");
        foreach (var note in context.Notes)
            issues.Add(IssueSeverity.Note, CategoryProgram, ProjectIssueCodes.Migrated, note);
    }

    private static JsonNode? TryParseNode(ArchiveAccess access, string name)
    {
        try
        {
            var bytes = ReadJson(access, name, null);
            return bytes is null ? null : JsonNode.Parse(bytes, null, ProjectSettings.ReadOptions);
        }
        catch (Exception e) when (e is EntryReadException or JsonException)
        {
            return null;
        }
    }

    // ----- settings -------------------------------------------------------------------------------------------------

    private static void ReadSettings(ArchiveAccess access, ProjectSettings settings, bool expected, IssueCollector issues)
    {
        byte[]? bytes;
        try
        {
            bytes = ReadJson(access, ArchivePaths.Settings, issues);
        }
        catch (EntryReadException e)
        {
            issues.Add(IssueSeverity.Warning, CategoryProgram, ProjectIssueCodes.SettingsInvalid,
                $"The project settings (settings.json) cannot be read ({e.Message}); default settings are used.");
            return;
        }
        if (bytes is null)
        {
            if (expected)
            {
                issues.Add(IssueSeverity.Note, CategoryProgram, ProjectIssueCodes.SettingsInvalid,
                    "The project has no settings (settings.json); default settings are used.");
            }
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(bytes, ProjectSettings.ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(IssueSeverity.Warning, CategoryProgram, ProjectIssueCodes.SettingsInvalid,
                    "The project settings (settings.json) are not a JSON object; default settings are used.");
                return;
            }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                settings.SetLoaded(p.Name, p.Value.Clone());
            }
        }
        catch (JsonException e)
        {
            issues.Add(IssueSeverity.Warning, CategoryProgram, ProjectIssueCodes.SettingsInvalid,
                $"The project settings (settings.json) are damaged ({e.Message}); default settings are used.");
        }
    }

    // ----- assets ---------------------------------------------------------------------------------------------------

    /// <summary>Asset id → entities that reference it (for issues the user can click).</summary>
    private static Dictionary<Guid, List<Guid>> CollectAssetUsers(Scene scene)
    {
        var users = new Dictionary<Guid, List<Guid>>();
        foreach (var entity in scene.All())
        {
            foreach (var c in entity.Components)
            {
                Guid? id = c switch
                {
                    MeshComponent m => m.ModelAssetId,
                    ImageComponent i => i.ImageAssetId,
                    TrussComponent t => t.ModelAssetId,
                    _ => null,
                };
                if (id is not { } assetId || assetId == Guid.Empty) continue;
                if (!users.TryGetValue(assetId, out var list)) users[assetId] = list = new List<Guid>();
                list.Add(entity.Id);
            }
        }
        return users;
    }

    private static void ReadAssets(ArchiveAccess access, AssetStore store, Dictionary<Guid, List<Guid>> users, ProjectLoadOptions options,
        IssueCollector issues)
    {
        var records = ReadAssetIndex(access, issues);
        var seen = new HashSet<Guid>();
        foreach (var record in records)
        {
            IReadOnlyList<Guid> related = users.TryGetValue(record.Id, out var u) ? u : Array.Empty<Guid>();
            var label = string.IsNullOrEmpty(record.FileName) ? record.Id.ToString("D") : $"'{record.FileName}'";
            if (record.Id == Guid.Empty || !seen.Add(record.Id) || !Enum.IsDefined(record.Kind) || string.IsNullOrEmpty(record.Entry))
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.AssetRecordInvalid,
                    $"The asset record {label} is invalid (missing or repeated id, unknown kind or no file) and was skipped.", related);
                continue;
            }

            byte[]? data;
            try
            {
                data = access.Read(record.Entry);
            }
            catch (EntryReadException e)
            {
                issues.Add(IssueSeverity.Error, IssueCategory.Scene, ProjectIssueCodes.AssetCorrupt,
                    $"The asset {label} is damaged ({e.Message}) and was not loaded.", related);
                continue;
            }
            if (data is null)
            {
                issues.Add(IssueSeverity.Error, IssueCategory.Scene, ProjectIssueCodes.AssetMissing,
                    $"The file of asset {label} is missing from the project.", related);
                continue;
            }
            if (Damaged(data, record.Size, record.Sha256, options) is { } damage)
            {
                issues.Add(IssueSeverity.Error, IssueCategory.Scene, ProjectIssueCodes.AssetCorrupt,
                    $"The asset {label} is damaged ({damage}) and was not loaded.", related);
                continue;
            }

            Dictionary<string, byte[]>? companions = null;
            if (record.Kind == AssetKind.Model && record.Companions is { Count: > 0 })
            {
                companions = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var c in record.Companions)
                {
                    if (c is null || string.IsNullOrEmpty(c.Path) || string.IsNullOrEmpty(c.Entry)) continue;
                    string? problem = null;
                    try
                    {
                        var bytes = access.Read(c.Entry);
                        if (bytes is null) problem = "missing";
                        else if (Damaged(bytes, c.Size, c.Sha256, options) is { } d) problem = d;
                        else companions[c.Path] = bytes;
                    }
                    catch (EntryReadException e)
                    {
                        problem = e.Message;
                    }
                    if (problem is not null)
                    {
                        issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.AssetCompanionMissing,
                            $"The file '{c.Path}' of model {label} is {(problem == "missing" ? "missing" : $"damaged ({problem})")}; the model loads without it.",
                            related);
                    }
                }
            }

            try
            {
                store.Add(record.Kind, record.FileName, data, companions, record.SourcePath, record.Id, record.ImportOptions?.ToOptions());
            }
            catch (AssetImportException e)
            {
                issues.Add(IssueSeverity.Error, IssueCategory.Scene, ProjectIssueCodes.AssetRejected,
                    $"The asset {label} could not be loaded: {e.Message}", related);
            }
            catch (InvalidOperationException e)
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.AssetRecordInvalid,
                    $"The asset {label} could not be added: {e.Message}", related);
            }
        }
    }

    private static List<AssetRecord> ReadAssetIndex(ArchiveAccess access, IssueCollector issues)
    {
        var records = new List<AssetRecord>();
        string? damage = null;
        try
        {
            var bytes = ReadJson(access, ArchivePaths.AssetIndex, issues);
            if (bytes is null)
            {
                damage = "missing";
            }
            else
            {
                using var doc = JsonDocument.Parse(bytes, ProjectSettings.ReadOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("assets", out var array) &&
                    array.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var element in array.EnumerateArray())
                    {
                        index++;
                        try
                        {
                            var record = element.Deserialize<AssetRecord>(CandelaJson.Options);
                            if (record is not null)
                            {
                                NullRepair.Repair(record);
                                records.Add(record);
                                continue;
                            }
                        }
                        catch (Exception e) when (SceneJsonReader.IsDataError(e))
                        {
                            issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.AssetRecordInvalid,
                                $"Asset record #{index} could not be read and was skipped: {e.Message}");
                            continue;
                        }
                        issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.AssetRecordInvalid,
                            $"Asset record #{index} is empty and was skipped.");
                    }
                    return records;
                }
                damage = "not an asset list";
            }
        }
        catch (Exception e) when (e is EntryReadException or JsonException)
        {
            damage = e.Message;
        }

        // No usable index: recover assets from the folder layout assets/{guid}/{file}.
        records = RecoverAssetRecords(access);
        if (records.Count > 0 || damage != "missing")
        {
            issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.AssetIndexInvalid,
                $"The asset list (assets/index.json) is {(damage == "missing" ? "missing" : $"damaged ({damage})")}; {records.Count} asset(s) were recovered from the archive folders (import settings and checks are lost).");
        }
        return records;
    }

    private static List<AssetRecord> RecoverAssetRecords(ArchiveAccess access)
    {
        var folders = new Dictionary<Guid, List<string>>();
        var order = new List<Guid>();
        foreach (var name in access.EntryNames.Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(ArchivePaths.AssetsFolder, StringComparison.Ordinal)) continue;
            var rest = name[ArchivePaths.AssetsFolder.Length..];
            int slash = rest.IndexOf('/');
            if (slash <= 0 || !Guid.TryParse(rest[..slash], out var id) || slash == rest.Length - 1) continue;
            if (!folders.TryGetValue(id, out var files))
            {
                folders[id] = files = new List<string>();
                order.Add(id);
            }
            files.Add(rest[(slash + 1)..]);
        }

        var records = new List<AssetRecord>();
        foreach (var id in order)
        {
            var files = folders[id];
            var folder = ArchivePaths.AssetFolder(id);
            string? main = null;
            AssetKind kind = AssetKind.Image;
            foreach (var preferred in new[] { AssetKind.Model, AssetKind.Image, AssetKind.Gdtf })
            {
                main = files.FirstOrDefault(f => !f.Contains('/') && AssetFormats.GuessKind(f) == preferred &&
                                                 !(preferred == AssetKind.Model && f.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase)));
                if (main is null) continue;
                kind = preferred;
                break;
            }
            if (main is null) continue;
            var record = new AssetRecord { Id = id, Kind = kind, FileName = main, Entry = folder + main };
            if (kind == AssetKind.Model)
            {
                record.Companions = files.Where(f => f != main)
                    .Select(f => new CompanionRecord { Path = f, Entry = folder + f })
                    .ToList();
            }
            records.Add(record);
        }
        return records;
    }

    /// <summary>Null when size and checksum match the record (or are not recorded), otherwise a short reason.</summary>
    private static string? Damaged(byte[] data, long size, string? sha256, ProjectLoadOptions options)
    {
        if (size > 0 && data.LongLength != size)
            return string.Create(CultureInfo.InvariantCulture, $"{data.LongLength:N0} bytes instead of {size:N0}");
        if (options.VerifyChecksums && !string.IsNullOrEmpty(sha256) && !string.Equals(ProjectWriter.Sha256(data), sha256, StringComparison.OrdinalIgnoreCase))
            return "checksum mismatch";
        return null;
    }

    // ----- GDTF files -----------------------------------------------------------------------------------------------

    private static void ReadFixtures(ArchiveAccess access, ProjectData project, ProjectLoadOptions options, IssueCollector issues)
    {
        var usage = ProjectData.CollectGdtfSpecs(project.Scene);
        foreach (var record in ReadFixtureIndex(access, issues))
        {
            var related = usage.EntitiesOf(record.Name);
            if (project.GdtfFiles.ContainsKey(record.Name))
            {
                issues.Add(IssueSeverity.Note, IssueCategory.Fixture, ProjectIssueCodes.GdtfIndexInvalid,
                    $"'{record.Name}' is listed twice in the project's fixture list; the first copy is used.", related);
                continue;
            }
            byte[]? data;
            try
            {
                data = access.Read(record.Entry);
            }
            catch (EntryReadException e)
            {
                issues.Add(IssueSeverity.Error, IssueCategory.Fixture, ProjectIssueCodes.GdtfCorrupt,
                    $"The fixture file '{record.Name}' in the project is damaged ({e.Message}); it has to come from the fixture library.", related);
                continue;
            }
            if (data is null)
            {
                issues.Add(IssueSeverity.Error, IssueCategory.Fixture, ProjectIssueCodes.GdtfMissing,
                    $"The fixture file '{record.Name}' is missing from the project; it has to come from the fixture library.", related);
                continue;
            }
            var problem = Damaged(data, record.Size, record.Sha256, options) ?? GdtfCheck.Validate(data);
            if (problem is not null)
            {
                issues.Add(IssueSeverity.Error, IssueCategory.Fixture, ProjectIssueCodes.GdtfCorrupt,
                    $"The fixture file '{record.Name}' in the project is damaged ({problem}); it has to come from the fixture library.", related);
                continue;
            }
            project.GdtfFiles[record.Name] = data;
        }

        foreach (var name in usage.Keys)
        {
            if (project.FindGdtf(name) is not null) continue;
            var related = usage.EntitiesOf(name);
            issues.Add(IssueSeverity.Note, IssueCategory.Fixture, ProjectIssueCodes.GdtfNotEmbedded,
                $"'{name}' (used by {related.Count} fixture(s)) is not stored in the project; it has to come from the fixture library.", related);
        }
    }

    private static List<FixtureRecord> ReadFixtureIndex(ArchiveAccess access, IssueCollector issues)
    {
        string? damage;
        try
        {
            var bytes = ReadJson(access, ArchivePaths.FixtureIndex, issues);
            if (bytes is null)
            {
                damage = "missing";
            }
            else
            {
                using var doc = JsonDocument.Parse(bytes, ProjectSettings.ReadOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("fixtures", out var array) &&
                    array.ValueKind == JsonValueKind.Array)
                {
                    var records = new List<FixtureRecord>();
                    foreach (var element in array.EnumerateArray())
                    {
                        FixtureRecord? record = null;
                        try
                        {
                            record = element.Deserialize<FixtureRecord>(CandelaJson.Options);
                        }
                        catch (Exception e) when (SceneJsonReader.IsDataError(e))
                        {
                            // Reported below like an empty record.
                        }
                        if (record is null || string.IsNullOrEmpty(record.Name) || string.IsNullOrEmpty(record.Entry))
                        {
                            issues.Add(IssueSeverity.Warning, IssueCategory.Fixture, ProjectIssueCodes.GdtfCorrupt,
                                "A fixture file record in the project is invalid and was skipped.");
                            continue;
                        }
                        records.Add(record);
                    }
                    return records;
                }
                damage = "not a fixture list";
            }
        }
        catch (Exception e) when (e is EntryReadException or JsonException)
        {
            damage = e.Message;
        }

        // No usable index: every fixtures/*.gdtf entry is a GDTF stored under its file name.
        var recovered = new List<FixtureRecord>();
        foreach (var name in access.EntryNames.Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(ArchivePaths.FixturesFolder, StringComparison.Ordinal)) continue;
            var file = name[ArchivePaths.FixturesFolder.Length..];
            if (file.Length == 0 || file.Contains('/') || !file.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase)) continue;
            recovered.Add(new FixtureRecord { Name = file, Entry = name });
        }
        if (recovered.Count > 0 || damage != "missing")
        {
            issues.Add(IssueSeverity.Warning, IssueCategory.Fixture, ProjectIssueCodes.GdtfIndexInvalid,
                $"The fixture file list (fixtures/index.json) is {(damage == "missing" ? "missing" : $"damaged ({damage})")}; {recovered.Count} GDTF file(s) were recovered from the fixtures folder.");
        }
        return recovered;
    }

    // ----- thumbnail ------------------------------------------------------------------------------------------------

    private static void ReadThumbnail(ArchiveAccess access, ProjectData project, IssueCollector issues)
    {
        try
        {
            var bytes = access.Read(ArchivePaths.Thumbnail);
            if (bytes is null) return;
            if (ProjectWriter.IsPng(bytes))
            {
                project.Thumbnail = bytes;
                return;
            }
            issues.Add(IssueSeverity.Note, CategoryProgram, ProjectIssueCodes.ThumbnailInvalid, "The project thumbnail is not a PNG image and was ignored.");
        }
        catch (EntryReadException e)
        {
            issues.Add(IssueSeverity.Note, CategoryProgram, ProjectIssueCodes.ThumbnailInvalid,
                $"The project thumbnail is damaged ({e.Message}) and was ignored.");
        }
    }
}
