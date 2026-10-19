namespace Candela.App.Session;

/// <summary>Outcome of a session operation as shown to the user.</summary>
internal enum SessionOutcome
{
    /// <summary>The operation happened (possibly with warnings in the issues panel).</summary>
    Done,

    /// <summary>The operation was attempted and failed; nothing changed.</summary>
    Failed,

    /// <summary>The operation needs a part of the program that is not in this build (the stage engine).</summary>
    NotAvailable,
}

/// <summary>Result of an <see cref="EditorSession"/> operation (open, save, import, export, render…).</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">One line for the status bar / message box.</param>
internal readonly record struct SessionResult(SessionOutcome Outcome, string Message)
{
    /// <summary>True when the operation happened.</summary>
    public bool Succeeded => Outcome == SessionOutcome.Done;

    /// <summary>A successful result.</summary>
    public static SessionResult Done(string message) => new(SessionOutcome.Done, message);

    /// <summary>A failed result.</summary>
    public static SessionResult Failed(string message) => new(SessionOutcome.Failed, message);

    /// <summary>The operation is not available in this build.</summary>
    public static SessionResult NotAvailable(string message) => new(SessionOutcome.NotAvailable, message);
}

/// <summary>What an import brings into the open scene (requirement: another project, a 3D model, an MVR, an image).</summary>
internal enum ImportKind
{
    /// <summary>Another Candela project merged into this one.</summary>
    Project,

    /// <summary>A textured 3D model (OBJ, FBX, DAE, 3DS, glTF/GLB, STL, PLY).</summary>
    Model,

    /// <summary>An MVR scene (fixtures, trusses, geometry).</summary>
    Mvr,

    /// <summary>An image used as a reference drawing or poster.</summary>
    Image,
}

/// <summary>Export formats.</summary>
internal enum ExportKind
{
    /// <summary>MVR (My Virtual Rig) exchange file.</summary>
    Mvr,
}
