using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Gdtf;

namespace Candela.App.Editing;

/// <summary>
/// Places the cabling devices of the library's Devices section: a console or a power source stands upright on the
/// surface (the engine draws a desk or a distro box), a GDTF device (node, splitter, dimmer, PSU) is mounted like a
/// fixture (hanging under a truss, standing on a floor). The entity gets a short numbered name ("Console 1",
/// "Distro 2"); a device with DMX channels (a dimmer pack) gets the next fixture id and is patched to the first free
/// addresses. One undo step; the new device becomes the selection.
/// </summary>
internal static class DevicePlacement
{
    /// <summary>
    /// Adds <paramref name="preset"/> at <paramref name="hit"/>; <paramref name="info"/> are the modes of a GDTF device
    /// (null while unknown: the device's own mode is kept and it stays unpatched). Returns the new entity.
    /// </summary>
    public static Entity Place(StagePanelContext stage, DevicePreset preset, FixtureInfo? info, SurfaceHit hit)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(preset);
        var engine = stage.Engine ?? throw new InvalidOperationException("The stage engine is not running.");
        var scene = stage.Scene;
        var component = preset.CreateComponent();
        var entity = new Entity(CableTool.NextName(scene, ShortName(preset)))
        {
            Local = preset.Kind == DevicePresetKind.Gdtf ? Placement.ForFixture(hit) : Placement.ForModel(hit),
        };
        if (component is FixtureComponent fixture)
        {
            var mode = info?.Find(fixture.GdtfMode);
            if (mode is not null)
            {
                fixture.GdtfMode = mode.Name;
                if (mode.Footprint > 0)
                {
                    fixture.FixtureId = AutoPatch.NextFixtureId(scene);
                    fixture.Addresses = AutoPatch.FindFree(AutoPatch.UsedRanges(scene, engine.Profiles), mode.Breaks)?.ToList() ?? new List<DmxAddress>();
                }
            }
        }

        entity.Add(component);
        stage.Undo.Execute(new AddEntityCommand(scene, entity));
        stage.Undo.BreakMerge();
        stage.Selection.Set(entity.Id);
        return entity;
    }

    /// <summary>The library entry of a GDTF device, or null (consoles, power sources, a file the library lacks).</summary>
    public static FixtureLibraryEntry? EntryOf(Engine.StageEngine? engine, DevicePreset preset) =>
        preset.GdtfFile is { } file ? engine?.Library.FindByFileName(file) : null;

    /// <summary>The base of a new device's name in the UI language ("Console", "Distro", "Splitter" …; a library device keeps its model name).</summary>
    public static string ShortName(DevicePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.FromLibrary)
            return preset.ShortName;
        return CableLoc.Get(preset.IconKey switch
        {
            "console" or "network-console" => CableStr.NameConsole,
            "node" => CableStr.NameNode,
            "splitter" => CableStr.NameSplitter,
            "mains" => CableStr.NameMains,
            "distro" => CableStr.NameDistro,
            _ => CableStr.NameDimmer,
        });
    }
}
