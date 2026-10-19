using System.Globalization;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Gdtf;
using Candela.Mvr;

namespace Candela.Engine;

/// <summary>Stand-in GDTFs for models the library lacks: mode choice by patch spacing, patch judgement, switching to the real file.</summary>
public sealed partial class StageEngine
{
    private Dictionary<Guid, SubstitutedFixture>? _substitutes;
    private List<SubstitutedFixture>? _substitutesSource;
    private int _substitutesCount = -1;

    /// <summary>
    /// What fixture <paramref name="entityId"/> stands in for, when an import drew it with a similar model's GDTF because
    /// the library had no file of the model the scene asks for (<see cref="EngineSettings.Substitutes"/>); null otherwise,
    /// and once the fixture's GDTF is no longer that stand-in.
    /// </summary>
    public FixtureSubstitution? SubstitutionOf(Guid entityId)
    {
        var list = Settings.Substitutes;
        if (list.Count == 0) return null;
        if (!ReferenceEquals(list, _substitutesSource) || list.Count != _substitutesCount || _substitutes is null)
        {
            _substitutes = new Dictionary<Guid, SubstitutedFixture>(list.Count);
            foreach (var s in list)
                if (s is not null) _substitutes[s.EntityId] = s;
            _substitutesSource = list;
            _substitutesCount = list.Count;
        }
        if (!_substitutes.TryGetValue(entityId, out var sub)) return null;
        var fixture = _scene.Find(entityId)?.Get<FixtureComponent>();
        if (fixture is null || !string.Equals(fixture.GdtfSpec, sub.StandIn, StringComparison.OrdinalIgnoreCase)) return null;
        return new FixtureSubstitution(sub.RequestedSpec, sub.RequestedMode, sub.StandIn, sub.PatchSpacing);
    }

    /// <summary>For the patch: the stand-in an import chose for a fixture (see <see cref="SubstitutionOf(Guid)"/>).</summary>
    private FixtureSubstitution? SubstitutionOf(Entity entity, FixtureComponent fixture) => SubstitutionOf(entity.Id);

    /// <summary>
    /// After an MVR import: records every fixture the importer drew with a similar model (a substitute) in
    /// <see cref="EngineSettings.Substitutes"/>, with the patch spacing of its type, so the patch judges it as a stand-in
    /// and <see cref="ReloadFixtureTypes"/> switches it to the real GDTF once the library has it. The importer has already
    /// given each stand-in type the mode that best fits its patch spacing (<c>mvr.substitute-mode</c>).
    /// </summary>
    private void RecordMvrSubstitutes(MvrImportResult result)
    {
        var bySpec = new Dictionary<string, List<(MvrDeviceObject Device, Entity Entity, FixtureComponent Fixture)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in result.Document.AllObjects())
        {
            if (obj is not MvrDeviceObject { GdtfSpec: { } spec } device || string.IsNullOrWhiteSpace(spec)) continue;
            if (result.FindEntity(obj.Uuid) is not { } entity || entity.Get<FixtureComponent>() is not { } fixture) continue;
            if (!bySpec.TryGetValue(spec.Trim(), out var list)) bySpec[spec.Trim()] = list = new();
            list.Add((device, entity, fixture));
        }

        foreach (var gdtf in result.Gdtfs)
        {
            if (gdtf.MatchKind != FixtureMatchKind.Substitute || gdtf.FixtureType is null) continue;
            if (!bySpec.TryGetValue(gdtf.SpecName.Trim(), out var members)) continue;
            foreach (var group in members.Where(m => string.Equals(m.Fixture.GdtfSpec, gdtf.FileName, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(m => (m.Device.GdtfMode ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var fixtures = group.ToList();
                int? spacing = PatchSpacing.Infer(fixtures.SelectMany(m => m.Fixture.Addresses.OrderBy(a => a.Break).Take(1)));
                foreach (var m in fixtures)
                {
                    Settings.Substitutes.RemoveAll(s => s.EntityId == m.Entity.Id);
                    Settings.Substitutes.Add(new SubstitutedFixture
                    {
                        EntityId = m.Entity.Id,
                        RequestedSpec = gdtf.SpecName.Trim(),
                        RequestedMode = string.IsNullOrWhiteSpace(m.Device.GdtfMode) ? null : m.Device.GdtfMode.Trim(),
                        StandIn = gdtf.FileName,
                        PatchSpacing = spacing,
                    });
                }
            }
        }
    }

    /// <summary>
    /// After GDTF files were added to the library: every stand-in whose requested model the library now has exactly is
    /// switched to that file, in the requested mode (by name, else the mode that fits the patch spacing), and leaves
    /// <see cref="EngineSettings.Substitutes"/>. Returns the number of fixtures switched.
    /// </summary>
    private int ReplaceStandInsWithRealGdtfs()
    {
        var list = Settings.Substitutes;
        if (list.Count == 0) return 0;
        int switched = 0;
        var done = new List<SubstitutedFixture>();
        var affected = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in list)
        {
            var fixture = _scene.Find(sub.EntityId)?.Get<FixtureComponent>();
            if (fixture is null || !string.Equals(fixture.GdtfSpec, sub.StandIn, StringComparison.OrdinalIgnoreCase))
            {
                done.Add(sub); // removed or changed by hand: nothing to replace any more
                continue;
            }
            if (Library.FindBySpec(sub.RequestedSpec) is not { IsExactModel: true } match) continue;
            FixtureType type;
            try
            {
                type = Library.GetFixtureType(match.Entry);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }
            var mode = (sub.RequestedMode is null ? null : type.FindMode(sub.RequestedMode)) ?? SubstituteModes.Choose(type, sub.RequestedMode, sub.PatchSpacing);
            fixture.GdtfSpec = match.Entry.FileName;
            fixture.GdtfMode = mode?.Name ?? sub.RequestedMode;
            fixture.NotifyChanged();
            done.Add(sub);
            switched++;
            if (!affected.TryGetValue(match.Entry.FileName, out var ids)) affected[match.Entry.FileName] = ids = new List<Guid>();
            ids.Add(sub.EntityId);
        }
        foreach (var d in done) list.Remove(d);
        foreach (var (file, ids) in affected)
        {
            Issues.Report(new Issue(IssueSeverity.Note, IssueCategory.Patch, EngineIssueCodes.SubstituteReplaced,
                $"{ids.Count} fixture(s) that stood in for a missing model now use its GDTF '{file}'.", ids.ToArray()));
        }
        return switched;
    }
}
