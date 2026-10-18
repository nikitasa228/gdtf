using Candela.Core.SceneGraph;

namespace Candela.Engine;

public sealed partial class StageEngine
{
    /// <summary>File extension of Candela projects.</summary>
    public const string ProjectExtension = ".candela";

    /// <summary>Saves the project to <see cref="ProjectPath"/> (fails when the scene has never been saved; use <see cref="SaveAs"/>).</summary>
    public EngineResult Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ProjectPath is { } path ? SaveAs(path) : Fail(EngineIssueCodes.SaveFailed, "The scene has not been saved yet; choose a file name (Save as).");
    }

    /// <summary>Saves the project as <paramref name="path"/>, which becomes <see cref="ProjectPath"/>.</summary>
    public EngineResult SaveAs(string path) =>
        Fail(EngineIssueCodes.SaveFailed, $"'{Path.GetFileName(path)}' could not be written: project files are not available in this build.");

    private EngineResult OpenProject(string path) =>
        Fail(EngineIssueCodes.OpenFailed, $"'{Path.GetFileName(path)}' could not be opened: project files are not available in this build.");

    private EngineResult MergeProject(string path, EngineImportOptions options) =>
        Fail(EngineIssueCodes.ImportFailed, $"'{Path.GetFileName(path)}' could not be imported: project files are not available in this build.");

    private static Scene EmptyScene() => new();
}
