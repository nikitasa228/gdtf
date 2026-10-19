using System.Numerics;
using Candela.App.Viewport;
using Candela.Engine;
using Candela.Rendering;

namespace Candela.App.Session;

/// <summary>
/// Changes the editor makes to the open project's <c>view</c> settings section (<see cref="StageEngine.View"/>) outside
/// the preset-bound panels: the work light and the saved camera. Each change is made in place and reported with
/// <see cref="StageEngine.MarkViewSettingsChanged"/>, so it counts as an unsaved change and reaches the next autosave;
/// setting a value that is already there reports nothing.
/// </summary>
internal static class ViewSettingsEdits
{
    /// <summary>Positions closer than this (m) and angles or exposures closer than this count as the same saved camera.</summary>
    private const float Tolerance = 1e-4f;

    /// <summary>
    /// Sets the work light of the preview (<see cref="ViewSettings.WorkLight"/>, applied to the render binding by the
    /// viewports). Returns true when it changed. An undefined value is treated as <see cref="WorkLightMode.Auto"/>.
    /// </summary>
    public static bool SetWorkLight(StageEngine engine, WorkLightMode mode)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!Enum.IsDefined(mode))
            mode = WorkLightMode.Auto;
        if (engine.View.WorkLight == mode)
            return false;
        engine.View.WorkLight = mode;
        engine.MarkViewSettingsChanged();
        return true;
    }

    /// <summary>
    /// Stores <paramref name="camera"/> as the project's camera (<see cref="ViewSettings.Camera"/>, the view that comes
    /// back when the project is opened). Returns true when the stored camera changed.
    /// </summary>
    public static bool StoreCamera(StageEngine engine, ViewportCamera camera)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(camera);
        var wanted = ToSaved(camera);
        if (engine.View.Camera is { } stored && Same(stored, wanted))
            return false;
        if (engine.View.Camera is { } existing)
        {
            existing.Eye = wanted.Eye;
            existing.Target = wanted.Target;
            existing.FovYDegrees = wanted.FovYDegrees;
            existing.Orthographic = wanted.Orthographic;
            existing.OrthoHeight = wanted.OrthoHeight;
            existing.Ev100 = wanted.Ev100;
        }
        else
        {
            engine.View.Camera = wanted;
        }

        engine.MarkViewSettingsChanged();
        return true;
    }

    /// <summary>
    /// The saved form of a viewport camera: eye at the orbit distance (also for orthographic views, whose own eye sits
    /// kilometres behind the target), target, vertical field of view in degrees, projection, visible height and EV100.
    /// </summary>
    public static SavedCamera ToSaved(ViewportCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return new SavedCamera
        {
            Eye = camera.Target - camera.Forward * camera.Distance,
            Target = camera.Target,
            FovYDegrees = camera.FovY * 180f / MathF.PI,
            Orthographic = camera.Projection == ProjectionKind.Orthographic,
            OrthoHeight = camera.OrthoHeight,
            Ev100 = camera.Ev100,
        };
    }

    /// <summary>
    /// Points <paramref name="camera"/> like <paramref name="saved"/> (a project's stored view). Values a damaged file
    /// may hold — non-finite numbers, the eye on the target — are ignored; returns false when nothing usable was stored.
    /// </summary>
    public static bool Apply(SavedCamera saved, ViewportCamera camera)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(camera);
        var eye = saved.Eye;
        var target = saved.Target;
        if (!Finite(eye) || !Finite(target))
            return false;
        var offset = target - eye;
        float distance = offset.Length();
        if (!(distance > 1e-4f) || !float.IsFinite(distance))
            return false;
        var direction = offset / distance;
        camera.Target = target;
        camera.Orient(MathF.Atan2(direction.X, direction.Y), MathF.Asin(Math.Clamp(-direction.Z, -1f, 1f)));
        camera.Distance = distance;
        if (float.IsFinite(saved.FovYDegrees))
            camera.FovY = saved.FovYDegrees * MathF.PI / 180f;
        if (float.IsFinite(saved.OrthoHeight) && saved.OrthoHeight > 0f)
            camera.OrthoHeight = saved.OrthoHeight;
        camera.Projection = saved.Orthographic ? ProjectionKind.Orthographic : ProjectionKind.Perspective;
        if (float.IsFinite(saved.Ev100))
            camera.Ev100 = Math.Clamp(saved.Ev100, -6f, 24f);
        return true;
    }

    private static bool Same(SavedCamera a, SavedCamera b) =>
        Vector3.Distance(a.Eye, b.Eye) < Tolerance && Vector3.Distance(a.Target, b.Target) < Tolerance &&
        MathF.Abs(a.FovYDegrees - b.FovYDegrees) < Tolerance && a.Orthographic == b.Orthographic &&
        MathF.Abs(a.OrthoHeight - b.OrthoHeight) < Tolerance && MathF.Abs(a.Ev100 - b.Ev100) < Tolerance;

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
