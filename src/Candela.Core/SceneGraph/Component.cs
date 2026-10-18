using System.Text.Json;
using System.Text.Json.Serialization;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Core.SceneGraph;

/// <summary>
/// Persisted data attached to an <see cref="Entity"/>. Components are plain data: behaviour lives in the module that
/// owns the concern (DMX evaluation, cable solving, rendering…), so every module can read the same scene and the
/// project file is serialised in one place. Every concrete component must be registered below with a stable
/// discriminator — the discriminator is part of the file format and must never change.
/// Every <see cref="Guid"/> member (also inside nested data such as cable nodes) must be classified with
/// <see cref="EntityReferenceAttribute"/>, <see cref="AssetReferenceAttribute"/> or <see cref="LocalIdAttribute"/>, and
/// entity and asset references must be rewritten in <see cref="RemapReferences"/>; unit tests reflect over every
/// component type and fail when a member is unclassified or a reference survives a remap.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FixtureComponent), "fixture")]
[JsonDerivedType(typeof(MeshComponent), "mesh")]
[JsonDerivedType(typeof(WallComponent), "wall")]
[JsonDerivedType(typeof(ImageComponent), "image")]
[JsonDerivedType(typeof(TrussComponent), "truss")]
[JsonDerivedType(typeof(RiggingPointComponent), "riggingPoint")]
[JsonDerivedType(typeof(CableComponent), "cable")]
[JsonDerivedType(typeof(ConsoleComponent), "console")]
[JsonDerivedType(typeof(PowerSourceComponent), "powerSource")]
[JsonDerivedType(typeof(AtmosphereVolumeComponent), "atmosphere")]
[JsonDerivedType(typeof(CameraComponent), "camera")]
[JsonDerivedType(typeof(GroupComponent), "group")]
public abstract class Component
{
    /// <summary>The entity this component is attached to (null while detached).</summary>
    [JsonIgnore]
    public Entity? Owner { get; internal set; }

    /// <summary>
    /// JSON properties this version does not know (written by a newer version of Candela), kept so that saving again
    /// does not drop them. Opaque: never interpreted; copies carry them unchanged (entity references inside them cannot be remapped). Null when there are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }

    /// <summary>
    /// Exact deep copy (nulls included), used by duplicate, copy/paste and undo snapshots. The copy is detached and keeps
    /// every id as it is; <see cref="EntityCloner"/> copies whole subtrees and rewrites the references.
    /// </summary>
    public Component Clone() => CandelaJson.DeepClone(this);

    /// <summary>Call after changing a field so views and the renderer pick the change up.</summary>
    public void NotifyChanged() => Owner?.NotifyComponentChanged(this);

    /// <summary>
    /// Rewrites every reference this component holds through <paramref name="remapper"/>: entity references
    /// (<see cref="EntityReferenceAttribute"/> members) with <see cref="EntityReferenceRemapper.TryRemap"/> and its helpers,
    /// asset references (<see cref="AssetReferenceAttribute"/>) with <see cref="EntityReferenceRemapper.RemapAsset(Guid)"/>,
    /// and local ids (<see cref="LocalIdAttribute"/>) regenerated when <see cref="EntityReferenceRemapper.RegenerateLocalIds"/>
    /// is set. Called by <see cref="EntityReferenceRemapper.Apply(Component)"/> on copies; the default does nothing (no
    /// references). Implementations must tolerate malformed data (null lists or entries) and leave it as it is.
    /// </summary>
    protected internal virtual void RemapReferences(EntityReferenceRemapper remapper)
    {
    }
}
