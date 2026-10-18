using System.Text.Json;
using System.Text.Json.Serialization;

namespace Candela.Core.SceneGraph;

/// <summary>
/// Scene-wide settings stored in the project. While the object is the <see cref="Scene.Settings"/> of a scene, every
/// value change raises <see cref="SceneChangeKind.SettingsChanged"/> on that scene (so undo/redo through plain setters
/// is seen by the renderer and marks the project modified).
/// </summary>
public sealed class SceneSettings
{
    private UnitSystem _displayUnits = UnitSystem.Metric;
    private float _baseHazeScattering = 0.02f;
    private float _hazeAnisotropy = 0.7f;
    private float _ambientLuminance = 0.05f;
    private float _gridSpacing = 1f;

    /// <summary>Display units in the UI (the engine itself is always metric).</summary>
    public UnitSystem DisplayUnits
    {
        get => _displayUnits;
        set => Set(ref _displayUnits, value);
    }

    /// <summary>Uniform haze everywhere, as volume scattering coefficient in 1/m (0 = clean air).</summary>
    public float BaseHazeScattering
    {
        get => _baseHazeScattering;
        set => Set(ref _baseHazeScattering, value);
    }

    /// <summary>Henyey–Greenstein anisotropy of haze particles (0 = isotropic, ~0.6–0.85 for stage haze).</summary>
    public float HazeAnisotropy
    {
        get => _hazeAnisotropy;
        set => Set(ref _hazeAnisotropy, value);
    }

    /// <summary>Luminance of the surroundings in cd/m² (a dark venue is close to 0).</summary>
    public float AmbientLuminance
    {
        get => _ambientLuminance;
        set => Set(ref _ambientLuminance, value);
    }

    /// <summary>Viewport grid spacing in metres.</summary>
    public float GridSpacing
    {
        get => _gridSpacing;
        set => Set(ref _gridSpacing, value);
    }

    /// <summary>
    /// JSON properties this version does not know (written by a newer version of Candela), kept so that saving again
    /// does not drop them. Opaque: never interpreted and changing them raises no scene change. Null when there are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }

    /// <summary>The scene these settings belong to (the last one they were assigned to), or null.</summary>
    internal Scene? Owner { get; set; }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Owner?.NotifySettingsChanged();
    }
}

/// <summary>Unit system used to display lengths and masses in the UI.</summary>
public enum UnitSystem
{
    /// <summary>Metres, kilograms.</summary>
    Metric,

    /// <summary>Feet and inches, pounds.</summary>
    Imperial,
}
