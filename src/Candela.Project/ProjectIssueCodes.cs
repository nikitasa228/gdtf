namespace Candela.Project;

/// <summary>
/// Stable machine ids (<see cref="Candela.Core.Diagnostics.Issue.Code"/>) of the problems reported while saving, loading,
/// merging and recovering projects. The issues panel filters and silences by these codes, so they never change.
/// </summary>
public static class ProjectIssueCodes
{
    /// <summary><c>manifest.json</c> is missing; the archive was read as the current format.</summary>
    public const string ManifestMissing = "project.manifest-missing";

    /// <summary>A manifest field has a wrong type or value; a default was used.</summary>
    public const string ManifestInvalid = "project.manifest-invalid";

    /// <summary>The project was converted from an older format version (one note per migration step).</summary>
    public const string Migrated = "project.migrated";

    /// <summary>
    /// A JSON document of the archive contained text that cannot be represented (bytes that are not UTF-8, an escaped
    /// UTF-16 surrogate without its pair); those characters were replaced by U+FFFD and the document was read.
    /// </summary>
    public const string TextRepaired = "project.text-repaired";

    /// <summary><c>settings.json</c> is missing, damaged or not an object; the project opens with default settings.</summary>
    public const string SettingsInvalid = "project.settings-invalid";

    /// <summary>The scene's settings could not be read; defaults were used.</summary>
    public const string SceneSettingsInvalid = "project.scene-settings-invalid";

    /// <summary>A layer record could not be read and was skipped.</summary>
    public const string SceneLayerInvalid = "project.scene-layer-invalid";

    /// <summary>Entities referred to layers that do not exist; they were put on their parent's or the default layer.</summary>
    public const string SceneLayerMissing = "project.scene-layer-missing";

    /// <summary>An entity record is not an object and was skipped.</summary>
    public const string SceneEntityInvalid = "project.scene-entity-invalid";

    /// <summary>An entity had no valid id, or its id was used twice; a new id was given.</summary>
    public const string SceneIdInvalid = "project.scene-id-invalid";

    /// <summary>An entity's parent does not exist; the entity was put at the scene root.</summary>
    public const string SceneParentMissing = "project.scene-parent-missing";

    /// <summary>Parent links formed a cycle; the cycle was broken by moving an entity to the scene root.</summary>
    public const string SceneParentCycle = "project.scene-parent-cycle";

    /// <summary>An entity was nested deeper than the reader accepts (1000 levels); it was moved to the root, keeping its world placement.</summary>
    public const string SceneTooDeep = "project.scene-too-deep";

    /// <summary>An entity's transform could not be read or was not finite; identity was used.</summary>
    public const string SceneTransformInvalid = "project.scene-transform-invalid";

    /// <summary>A component could not be read and was dropped (the rest of the entity loads).</summary>
    public const string SceneComponentInvalid = "project.scene-component-invalid";

    /// <summary>
    /// A component of a type this program does not know (written by a newer version): it is kept verbatim and saved again
    /// (<see cref="ProjectData.UnknownComponents"/>); merging does not import it.
    /// </summary>
    public const string SceneComponentUnknown = "project.scene-component-unknown";

    /// <summary>Required values were missing (null) in a component or layer and were replaced by defaults.</summary>
    public const string SceneValueRepaired = "project.scene-value-repaired";

    /// <summary><c>assets/index.json</c> is damaged; assets were recovered from the archive's folder layout.</summary>
    public const string AssetIndexInvalid = "project.asset-index-invalid";

    /// <summary>An asset record is invalid (no id, duplicate id, unknown kind) and was skipped.</summary>
    public const string AssetRecordInvalid = "project.asset-record-invalid";

    /// <summary>An asset's file is missing from the archive.</summary>
    public const string AssetMissing = "project.asset-missing";

    /// <summary>An asset's file is damaged (unreadable entry, wrong checksum, too large); the asset was not loaded.</summary>
    public const string AssetCorrupt = "project.asset-corrupt";

    /// <summary>The asset store rejected an asset's content (unsupported or invalid file).</summary>
    public const string AssetRejected = "project.asset-rejected";

    /// <summary>
    /// An asset with intact files that this version cannot load (a newer kind, content the store does not accept) is kept
    /// verbatim and saved again (<see cref="ProjectData.KeptAssetIds"/>); objects using it show a missing asset; merging
    /// does not import it.
    /// </summary>
    public const string AssetKept = "project.asset-kept";

    /// <summary>A companion file of a model (texture, MTL, buffer) is missing or damaged; the model loads without it.</summary>
    public const string AssetCompanionMissing = "project.asset-companion-missing";

    /// <summary>
    /// <c>fixtures/index.json</c> is damaged (GDTF files were recovered from the <c>fixtures/</c> folder) or lists a name twice.
    /// </summary>
    public const string GdtfIndexInvalid = "project.gdtf-index-invalid";

    /// <summary>A GDTF file listed in the project is missing from the archive.</summary>
    public const string GdtfMissing = "project.gdtf-missing";

    /// <summary>A GDTF file in the project is damaged (wrong checksum, not a zip, no description.xml) and was skipped.</summary>
    public const string GdtfCorrupt = "project.gdtf-corrupt";

    /// <summary>Fixtures use a GDTF that is not stored in the project; it has to come from the fixture library.</summary>
    public const string GdtfNotEmbedded = "project.gdtf-not-embedded";

    /// <summary>The thumbnail is not a PNG image and was ignored.</summary>
    public const string ThumbnailInvalid = "project.thumbnail-invalid";

    /// <summary>Saving: the GDTF of some fixtures was not available, so it is not stored in the project.</summary>
    public const string SaveGdtfUnavailable = "project.save-gdtf-unavailable";

    /// <summary>Saving: the GDTF provided for a fixture type is not a valid GDTF archive and was not stored.</summary>
    public const string SaveGdtfInvalid = "project.save-gdtf-invalid";

    /// <summary>Saving: components refer to assets that are not in the asset store.</summary>
    public const string SaveAssetMissing = "project.save-asset-missing";

    /// <summary>Saving: the thumbnail is not a PNG image and was not stored.</summary>
    public const string SaveThumbnailInvalid = "project.save-thumbnail-invalid";

    /// <summary>Merging: a reference pointed to something that does not exist in the imported project; it was cleared.</summary>
    public const string MergeReferenceDangling = "project.merge-reference-dangling";

    /// <summary>Merging: an imported GDTF has the same name as a different file in this project and was renamed.</summary>
    public const string MergeGdtfRenamed = "project.merge-gdtf-renamed";

    /// <summary>Merging: an asset of the imported project could not be added.</summary>
    public const string MergeAssetFailed = "project.merge-asset-failed";

    /// <summary>Recovery: the newest autosave could not be opened, an older one was used.</summary>
    public const string RecoveredOlder = "project.recovered-older";

    /// <summary>More problems of one kind than are listed individually (the message gives the count).</summary>
    public const string MoreIssues = "project.more-issues";
}
