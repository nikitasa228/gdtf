using Candela.App.Session;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.App.Panels;

/// <summary>
/// The engine-backed sections of the properties panel, drawn below the name and flags of the selected object:
/// for a fixture its current state, manual controls, DMX patch, manufacturer data and connectors
/// (<see cref="FixtureInspectorView"/>); for a console or power source its connectors; for walls, floors and models
/// their dimensions and materials (<see cref="SurfaceInspectorView"/>).
/// </summary>
internal sealed class StagePropertiesSection
{
    private readonly StagePanelContext _stage;
    private readonly FixtureInspectorView _fixture;
    private readonly SurfaceInspectorView _surface;
    private readonly DeviceFactsView _device;

    /// <summary>Creates the section.</summary>
    public StagePropertiesSection(StagePanelContext stage)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _fixture = new FixtureInspectorView(stage);
        _surface = new SurfaceInspectorView(stage);
        _device = new DeviceFactsView(stage);
    }

    /// <summary>The fixture view (tests).</summary>
    internal FixtureInspectorView Fixture => _fixture;

    /// <summary>The wall/model view (tests).</summary>
    internal SurfaceInspectorView Surface => _surface;

    /// <summary>Draws the sections that apply to <paramref name="entity"/> (the primary selection).</summary>
    public void Draw(Entity entity, float labelWidth)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Get<FixtureComponent>() is { } fixture)
            _fixture.Draw(entity, fixture, labelWidth);
        else if (DeviceFactsView.HasPorts(entity) && _stage.Engine is { } engine)
            _device.Draw(engine, entity, labelWidth);
        _surface.Draw(entity, labelWidth);
    }
}
