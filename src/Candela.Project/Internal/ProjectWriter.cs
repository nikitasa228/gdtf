using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Serialization;

namespace Candela.Project.Internal;

/// <summary>
/// Writes a <see cref="ProjectSnapshot"/> as a <c>.candela</c> zip archive. Entry order: <c>scene.json</c>,
/// <c>settings.json</c>, the assets and <c>assets/index.json</c>, the GDTF files and <c>fixtures/index.json</c>,
/// <c>thumbnail.png</c>, and <c>manifest.json</c> last (its counts are known only then; readers use the zip directory).
/// GDTF files are requested and written one at a time, so their bytes never all sit in memory together.
/// </summary>
internal static class ProjectWriter
{
    private static readonly byte[] s_pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static ProjectSaveResult Write(ProjectSnapshot snapshot, Stream destination, string? path, CompressionLevel? compressionOverride = null)
    {
        var options = snapshot.Options;
        var level = compressionOverride ?? options.CompressionLevel;
        var issues = new IssueCollector();
        var modified = options.TimeProvider.GetUtcNow().UtcDateTime;
        var stamp = ZipTimestamp(modified);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ArchivePaths.Manifest, ArchivePaths.Scene, ArchivePaths.Settings, ArchivePaths.AssetIndex, ArchivePaths.FixtureIndex,
            ArchivePaths.Thumbnail,
        };

        var stored = new List<string>();
        var missing = new List<string>();
        bool hasThumbnail = false;
        ProjectManifest manifest;
        long start = destination.CanSeek ? destination.Position : 0;
        using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
        {
            WriteEntry(zip, ArchivePaths.Scene, snapshot.SceneJson, level, stamp);
            WriteEntry(zip, ArchivePaths.Settings, SerializeSettings(snapshot.Settings), level, stamp);
            WriteAssets(zip, snapshot, level, stamp, used);
            WriteFixtures(zip, snapshot, level, stamp, used, issues, stored, missing);

            if (snapshot.Thumbnail is { Length: > 0 } thumb)
            {
                if (IsPng(thumb))
                {
                    WriteEntry(zip, ArchivePaths.Thumbnail, thumb, CompressionLevel.NoCompression, stamp);
                    hasThumbnail = true;
                }
                else
                {
                    issues.Add(IssueSeverity.Warning, IssueCategory.Program, ProjectIssueCodes.SaveThumbnailInvalid,
                        "The project thumbnail is not a PNG image and was not saved.");
                }
            }

            foreach (var (assetId, users) in snapshot.MissingAssets)
            {
                issues.Add(IssueSeverity.Warning, IssueCategory.Scene, ProjectIssueCodes.SaveAssetMissing,
                    $"{users.Count} object(s) refer to asset {assetId:D}, which is not in the project; they are saved without it.", users);
            }

            manifest = new ProjectManifest
            {
                FormatVersion = ProjectFile.FormatVersion,
                AppVersion = options.AppVersion ?? ProjectFile.DefaultAppVersion,
                CreatedUtc = snapshot.CreatedUtc,
                ModifiedUtc = modified,
                ProjectId = snapshot.ProjectId,
                SceneName = snapshot.SceneName,
                EntityCount = snapshot.EntityCount,
                FixtureCount = snapshot.FixtureCount,
                AssetCount = snapshot.AssetCount,
                GdtfCount = stored.Count,
                HasThumbnail = hasThumbnail,
            };
            WriteEntry(zip, ArchivePaths.Manifest, JsonSerializer.SerializeToUtf8Bytes(manifest, CandelaJson.Options), level, stamp);
        }

        // Disposing the archive writes the central directory; only then is the length final.
        long length = destination.CanSeek ? destination.Position - start : 0;
        return new ProjectSaveResult(manifest, issues.ToList(), stored, missing, length, path);
    }

    private static void WriteAssets(ZipArchive zip, ProjectSnapshot snapshot, CompressionLevel level, DateTimeOffset stamp, HashSet<string> used)
    {
        var index = new AssetIndexFile();
        foreach (var asset in snapshot.Assets)
        {
            var folder = ArchivePaths.AssetFolder(asset.Id);
            var mainEntry = ArchivePaths.Unique(folder + ArchivePaths.SafeSegment(asset.OriginalFileName, "asset"), used);
            WriteEntry(zip, mainEntry, asset.Data, LevelFor(asset.OriginalFileName, level), stamp);
            var record = new AssetRecord
            {
                Id = asset.Id,
                Kind = asset.Kind,
                FileName = asset.OriginalFileName,
                Entry = mainEntry,
                Size = asset.Data.LongLength,
                Sha256 = Sha256(asset.Data),
                ContentHash = asset.ContentHash,
                SourcePath = snapshot.Options.IncludeSourcePaths ? asset.SourcePath : null,
                ImportOptions = asset.ImportOptions is { } o ? ImportOptionsRecord.From(o) : null,
            };
            if (asset.CompanionFiles.Count > 0)
            {
                record.Companions = new List<CompanionRecord>(asset.CompanionFiles.Count);
                foreach (var key in asset.CompanionFiles.Keys.Order(StringComparer.Ordinal))
                {
                    var bytes = asset.CompanionFiles[key];
                    var entry = ArchivePaths.Unique(folder + ArchivePaths.SafeRelativePath(key, "file"), used);
                    WriteEntry(zip, entry, bytes, LevelFor(key, level), stamp);
                    record.Companions.Add(new CompanionRecord { Path = key, Entry = entry, Size = bytes.LongLength, Sha256 = Sha256(bytes) });
                }
            }
            index.Assets.Add(record);
        }
        if (snapshot.KeptAssets.Count == 0)
        {
            WriteEntry(zip, ArchivePaths.AssetIndex, JsonSerializer.SerializeToUtf8Bytes(index, CandelaJson.Options), level, stamp);
            return;
        }

        // Assets this version could not load go back exactly as they were read: every file under its original entry name
        // and the record verbatim (it may hold fields only a newer version understands).
        var root = JsonSerializer.SerializeToNode(index, CandelaJson.Options)!.AsObject();
        var records = root["assets"]!.AsArray();
        foreach (var kept in snapshot.KeptAssets)
        {
            // Its folder assets/{id}/ belongs to it alone (an asset of the same id in the store replaces it at capture).
            foreach (var (entry, data) in kept.Files)
            {
                used.Add(entry);
                WriteEntry(zip, entry, data, LevelFor(entry, level), stamp);
            }
            records.Add(JsonNode.Parse(kept.Record.GetRawText()));
        }
        WriteEntry(zip, ArchivePaths.AssetIndex, JsonSerializer.SerializeToUtf8Bytes(root, CandelaJson.Options), level, stamp);
    }

    private static void WriteFixtures(ZipArchive zip, ProjectSnapshot snapshot, CompressionLevel level, DateTimeOffset stamp,
        HashSet<string> used, IssueCollector issues, List<string> stored, List<string> missing)
    {
        var index = new FixtureIndexFile();
        foreach (var name in snapshot.Gdtfs.Keys)
        {
            // Provider exceptions propagate on purpose: the save is aborted and the previous file stays untouched.
            var bytes = snapshot.ResolveGdtf(name);
            var users = snapshot.Gdtfs.EntitiesOf(name);
            if (bytes is null)
            {
                missing.Add(name);
                issues.Add(IssueSeverity.Warning, IssueCategory.Fixture, ProjectIssueCodes.SaveGdtfUnavailable,
                    $"'{name}' (used by {users.Count} fixture(s)) is not available and is not stored in the project; on another computer these fixtures need it in the fixture library.",
                    users);
                continue;
            }
            if (GdtfCheck.Validate(bytes) is { } problem)
            {
                missing.Add(name);
                issues.Add(IssueSeverity.Warning, IssueCategory.Fixture, ProjectIssueCodes.SaveGdtfInvalid,
                    $"'{name}' is not a valid GDTF file ({problem}) and is not stored in the project.", users);
                continue;
            }
            var fileName = ArchivePaths.SafeSegment(name, "fixture");
            if (!fileName.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase)) fileName += ".gdtf";
            var entry = ArchivePaths.Unique(ArchivePaths.FixturesFolder + fileName, used);
            WriteEntry(zip, entry, bytes, CompressionLevel.NoCompression, stamp);
            index.Fixtures.Add(new FixtureRecord { Name = name, Entry = entry, Size = bytes.LongLength, Sha256 = Sha256(bytes) });
            stored.Add(name);
        }
        WriteEntry(zip, ArchivePaths.FixtureIndex, JsonSerializer.SerializeToUtf8Bytes(index, CandelaJson.Options), level, stamp);
    }

    private static byte[] SerializeSettings(IReadOnlyDictionary<string, JsonElement> sections)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var name in sections.Keys.Order(StringComparer.Ordinal))
            {
                w.WritePropertyName(name);
                sections[name].WriteTo(w);
            }
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, ReadOnlySpan<byte> data, CompressionLevel level, DateTimeOffset stamp)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = stamp;
        using var s = entry.Open();
        s.Write(data);
    }

    private static CompressionLevel LevelFor(string fileName, CompressionLevel level) =>
        ArchivePaths.IsPrecompressed(fileName) ? CompressionLevel.NoCompression : level;

    internal static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    internal static bool IsPng(ReadOnlySpan<byte> data) => data.Length > s_pngSignature.Length && data[..s_pngSignature.Length].SequenceEqual(s_pngSignature);

    /// <summary>Zip stores local DOS time from 1980 to 2107; clamp so any clock value can be written.</summary>
    private static DateTimeOffset ZipTimestamp(DateTime utc)
    {
        var min = new DateTime(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2107, 12, 30, 0, 0, 0, DateTimeKind.Utc);
        var t = utc < min ? min : utc > max ? max : utc;
        return new DateTimeOffset(t, TimeSpan.Zero);
    }
}
