using Candela.Core.Settings;

namespace Candela.App.Input;

/// <summary>Action ids of the document session and the stage engine (bindable like every other action).</summary>
internal static class SessionActions
{
    /// <summary>Tools → Pause Simulation: freezes smoke, fire and sound (DMX and moving lights keep running).</summary>
    public const string PauseSimulation = "sim.pause";

    /// <summary>File → Import → GDTF Fixture: adds a fixture file to the library.</summary>
    public const string ImportGdtf = "file.importGdtf";

    /// <summary>Default chords of the session actions (merged into the default set with the other app actions).</summary>
    public static void AddDefaults(KeyBindingSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!set.Bindings.ContainsKey(PauseSimulation))
            set.Set(PauseSimulation, new KeyChord("P", KeyModifiers.Alt));
        if (!set.Bindings.ContainsKey(ImportGdtf))
            set.Set(ImportGdtf);
    }
}
