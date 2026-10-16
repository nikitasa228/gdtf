using System.Text.Json.Serialization;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Core.SceneGraph;

/// <summary>
/// Persisted data attached to an <see cref="Entity"/>. Components are plain data: behaviour lives in the module that
/// owns the concern (DMX evaluation, cable solving, rendering…), so every module can read the same scene and the
/// project file is serialised in one place. Every concrete component must be registered below with a stable
/// discriminator — the discriminator is part of the file format and must never change.
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

    /// <summary>Deep copy (used by duplicate, copy/paste and undo snapshots). The copy is detached.</summary>
    public Component Clone() => CandelaJson.DeepClone(this);

    /// <summary>Call after changing a field so views and the renderer pick the change up.</summary>
    public void NotifyChanged() => Owner?.NotifyComponentChanged(this);
}
