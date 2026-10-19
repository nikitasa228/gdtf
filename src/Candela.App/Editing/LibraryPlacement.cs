using Candela.App.Session;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Gdtf;

namespace Candela.App.Editing;

/// <summary>
/// Places fixture types from the library into the scene: a new entity at the surface point (mounted with
/// <see cref="Placement.ForFixture"/>), named after the model with the next free fixture id, in the fixture's default
/// (first) DMX mode, patched by <see cref="AutoPatch"/> to the first free addresses — one undo step, and the new
/// fixture becomes the selection. The mode can be changed afterwards (<see cref="ChangeMode"/>), which patches the
/// fixture again.
/// </summary>
internal static class LibraryPlacement
{
    /// <summary>Adds a fixture of type <paramref name="entry"/> at <paramref name="hit"/>; returns the new entity.</summary>
    public static Entity PlaceFixture(EditorSession session, FixtureLibraryEntry entry, FixtureInfo? info, SurfaceHit hit, string? modeName = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        var engine = session.Engine ?? throw new InvalidOperationException("The stage engine is not running.");
        var scene = session.Scene;
        var mode = info?.Find(modeName);
        var addresses = mode is null ? null : AutoPatch.FindFree(AutoPatch.UsedRanges(scene, engine.Profiles), mode.Breaks);
        int fixtureId = AutoPatch.NextFixtureId(scene);
        var entity = new Entity(fixtureId > 0 ? $"{entry.ModelName} {fixtureId}" : entry.ModelName)
        {
            Local = Placement.ForFixture(hit),
        };
        entity.Add(new FixtureComponent
        {
            GdtfSpec = entry.FileName,
            GdtfMode = mode?.Name,
            FixtureId = fixtureId,
            Addresses = addresses?.ToList() ?? new List<DmxAddress>(),
        });
        session.Undo.Execute(new AddEntityCommand(scene, entity));
        session.Selection.Set(entity.Id);
        return entity;
    }

    /// <summary>
    /// Switches a placed fixture to <paramref name="mode"/> and patches it again to the first free addresses (its own
    /// old channels count as free). One undo step.
    /// </summary>
    public static void ChangeMode(EditorSession session, Entity fixtureEntity, FixtureModeInfo mode)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fixtureEntity);
        ArgumentNullException.ThrowIfNull(mode);
        var engine = session.Engine ?? throw new InvalidOperationException("The stage engine is not running.");
        if (fixtureEntity.Get<FixtureComponent>() is not { } fixture || string.Equals(fixture.GdtfMode, mode.Name, StringComparison.Ordinal))
            return;
        var patch = Patch.Build(session.Scene, engine.Profiles);
        var used = new List<PatchRange>();
        foreach (var e in patch.Entries)
        {
            if (!ReferenceEquals(e.Entity, fixtureEntity))
                used.AddRange(e.Ranges);
        }

        var addresses = AutoPatch.FindFree(used, mode.Breaks)?.ToList() ?? new List<DmxAddress>();
        session.Undo.Execute(ModifyComponentCommand.Create(fixture, f =>
        {
            f.GdtfMode = mode.Name;
            f.Addresses = addresses;
        }, $"Mode {mode.Name}"));
    }

    /// <summary>Patch text of a fixture ("1.001", "1.001 + 2.001", or empty when unpatched).</summary>
    public static string AddressText(FixtureComponent fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return string.Join(" + ", fixture.Addresses.OrderBy(a => a.Break).Select(a => a.ToString()));
    }
}
