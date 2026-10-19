using Candela.Core.Settings;

namespace Candela.App.Input;

/// <summary>Action ids of the document session and the stage engine (bindable like every other action).</summary>
internal static class SessionActions
{
    /// <summary>Tools → Pause Simulation: freezes smoke, fire and sound (DMX and moving lights keep running).</summary>
    public const string PauseSimulation = "sim.pause";

    /// <summary>File → Import → GDTF Fixture: adds a fixture file to the library.</summary>
    public const string ImportGdtf = "file.importGdtf";

    /// <summary>Tools → Open White: every fixture at full in open white, moving heads aimed at the stage (one undo step).</summary>
    public const string OpenWhite = "look.openWhite";

    /// <summary>View → Work Light → Off: the preview shows the fixtures' light only.</summary>
    public const string WorkLightOff = "view.workLightOff";

    /// <summary>View → Work Light → Auto: a dim work light while no fixture emits (the default).</summary>
    public const string WorkLightAuto = "view.workLightAuto";

    /// <summary>View → Work Light → On: the work light always, on top of the fixtures' light.</summary>
    public const string WorkLightOn = "view.workLightOn";

    /// <summary>Default chords of the session actions (merged into the default set with the other app actions).</summary>
    public static void AddDefaults(KeyBindingSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (var id in new[] { WorkLightOff, WorkLightAuto, WorkLightOn })
            if (!set.Bindings.ContainsKey(id))
                set.Set(id);
        if (!set.Bindings.ContainsKey(PauseSimulation))
            set.Set(PauseSimulation, new KeyChord("P", KeyModifiers.Alt));
        if (!set.Bindings.ContainsKey(ImportGdtf))
            set.Set(ImportGdtf);
        if (!set.Bindings.ContainsKey(OpenWhite))
            set.Set(OpenWhite);
    }
}
