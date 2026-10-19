using System.Globalization;
using System.Numerics;
using Candela.App.Session;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;

namespace Candela.App.Editing;

/// <summary>What the cable tool would act on under the mouse.</summary>
internal enum CableTargetKind
{
    /// <summary>Nothing (outside the view).</summary>
    None,

    /// <summary>A wall, truss, floor or any other object (anchored), or the ground (free).</summary>
    Surface,

    /// <summary>A device with ports: a fixture, console or power source.</summary>
    Device,

    /// <summary>An existing node of a cable (continue or branch from it, or join the drawn cable to it).</summary>
    CableNode,

    /// <summary>The middle of an existing cable segment (a new node there; branch or join).</summary>
    CableSegment,
}

/// <summary>A resolved target of the cable tool (world positions in metres).</summary>
internal readonly record struct CableTarget
{
    /// <summary>What is under the mouse.</summary>
    public CableTargetKind Kind { get; init; }

    /// <summary>The world point (on the surface, on the cable, or the device's position).</summary>
    public Vector3 Position { get; init; }

    /// <summary>Surface normal (surfaces only; the cable is lifted off the surface along it).</summary>
    public Vector3 Normal { get; init; }

    /// <summary>The surface's entity (Guid.Empty for the ground), the device, or the cable entity.</summary>
    public Guid EntityId { get; init; }

    /// <summary>The node (<see cref="CableTargetKind.CableNode"/>).</summary>
    public Guid NodeId { get; init; }

    /// <summary>The segment's first node (<see cref="CableTargetKind.CableSegment"/>).</summary>
    public Guid SegmentFrom { get; init; }

    /// <summary>The segment's second node.</summary>
    public Guid SegmentTo { get; init; }

    /// <summary>A surface point (anchored to <paramref name="entity"/>, or free on the ground when it is empty).</summary>
    public static CableTarget Surface(Vector3 position, Vector3 normal, Guid entity) =>
        new() { Kind = CableTargetKind.Surface, Position = position, Normal = normal, EntityId = entity };

    /// <summary>A device.</summary>
    public static CableTarget Device(Guid entity, Vector3 position) => new() { Kind = CableTargetKind.Device, Position = position, EntityId = entity };

    /// <summary>An existing node of a cable.</summary>
    public static CableTarget Node(Guid cable, Guid node, Vector3 position) =>
        new() { Kind = CableTargetKind.CableNode, Position = position, EntityId = cable, NodeId = node };

    /// <summary>A point in the middle of a cable segment.</summary>
    public static CableTarget Segment(Guid cable, Guid from, Guid to, Vector3 position) =>
        new() { Kind = CableTargetKind.CableSegment, Position = position, EntityId = cable, SegmentFrom = from, SegmentTo = to };
}

/// <summary>What a click of the cable tool did.</summary>
internal enum CableClickOutcome
{
    /// <summary>Nothing (no target, no stage).</summary>
    Ignored,

    /// <summary>A new cable (or branch) started.</summary>
    Started,

    /// <summary>A route point was added to the cable being drawn.</summary>
    PointAdded,

    /// <summary>The port picker opened (several possible ports, or none usable — the picker explains why).</summary>
    PickPort,

    /// <summary>The cable was plugged into a port and committed (a new cable may continue from the device).</summary>
    Connected,

    /// <summary>The cable was committed into another cable (joined) or as a branch.</summary>
    Committed,

    /// <summary>The click could not be used (the status message says why).</summary>
    Rejected,
}

/// <summary>A point of the cable being drawn.</summary>
/// <param name="NodeId">Node id (an existing node of the cable when <paramref name="Existing"/>).</param>
/// <param name="World">World position in metres.</param>
/// <param name="Anchor">The entity the node is fixed to (wall, truss, device), or null.</param>
/// <param name="Port">The device port the node is plugged into, or null.</param>
/// <param name="Existing">True for the existing node a branch starts from.</param>
internal readonly record struct DraftPoint(Guid NodeId, Vector3 World, CableAnchor? Anchor, PortRef? Port, bool Existing = false);

/// <summary>The cable being drawn: its points, the cable it branches from (if any) and the port it starts at.</summary>
internal sealed class CableDraft
{
    /// <summary>Cable type.</summary>
    public required CableType Type { get; init; }

    /// <summary>The existing cable the draft is a branch of (null: a new cable).</summary>
    public Guid? ExistingCable { get; init; }

    /// <summary>The segment split by the branch's first node (null when the branch starts at an existing node).</summary>
    public (Guid From, Guid To)? Split { get; init; }

    /// <summary>The port the first point is plugged into (null: a free end or a branch).</summary>
    public Port? StartPort { get; init; }

    /// <summary>The device port of the first point, or null.</summary>
    public PortRef? StartPortRef { get; init; }

    /// <summary>The points in drawing order (the first is the start).</summary>
    public List<DraftPoint> Points { get; } = new();

    /// <summary>Routed length in metres of the drawn points plus the rubber band to <paramref name="next"/> (without slack).</summary>
    public float RoutedLength(Vector3? next = null)
    {
        float length = 0f;
        for (int i = 1; i < Points.Count; i++)
            length += Vector3.Distance(Points[i - 1].World, Points[i].World);
        if (next is { } n && Points.Count > 0 && float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z))
            length += Vector3.Distance(Points[^1].World, n);
        return float.IsFinite(length) ? length : 0f;
    }
}

/// <summary>The open port picker: the device clicked and every port judged for the cable.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="DeviceName">Its name.</param>
/// <param name="Candidates">Usable ports first, then the others with their reasons.</param>
internal sealed record PortPick(Guid DeviceId, string DeviceName, IReadOnlyList<PortCandidate> Candidates)
{
    /// <summary>The usable ports in picker order.</summary>
    public IEnumerable<PortCandidate> Usable => Candidates.Where(c => c.Usable);
}

/// <summary>
/// The cable tool (requirement "Кабели"), independent of the UI so scripted flows can drive it: a click on a device
/// plugs the cable into one of its ports (the port picker opens when several fit or none does, and explains why), a
/// click on a wall, truss, floor or any object adds a route point fixed (anchored) to that object, a click on the
/// ground a free point, a click on an existing cable's node or the middle of one of its segments starts a branch there
/// (a new node splits the segment — a Y-split, which the electrical solver warns about on DMX) or, while drawing, joins
/// the drawn cable into it. Plugging the far end into a port commits the cable as one undo step and, on a daisy-chain
/// device, starts the next cable from its thru/link output; <see cref="Finish"/> (right-click or Esc) commits a cable
/// with a free end and drops a start that has no second point. Positions are world metres; new cables are entities at
/// the world origin, their nodes anchored to whatever they were clicked on.
/// </summary>
internal sealed class CableTool
{
    /// <summary>Height a route point is lifted off the surface it is fixed to, in metres (the cable lies on it).</summary>
    public const float SurfaceOffset = 0.02f;

    private readonly StagePanelContext _stage;

    /// <summary>Creates the tool over the stage's scene, undo history and engine.</summary>
    public CableTool(StagePanelContext stage)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
    }

    /// <summary>Raised with a status message (cable added, port connected, why a click was rejected).</summary>
    public event Action<string>? Status;

    /// <summary>The cable type new cables get (<see cref="CableCatalog"/> id).</summary>
    public string CableTypeId { get; set; } = CableCatalog.Ids.DmxXlr5;

    /// <summary>The cable catalogue of the engine.</summary>
    public CableCatalog Catalog => _stage.Engine?.Network.Catalog ?? CableCatalog.Default;

    /// <summary>The selected cable type, or null when the id is unknown.</summary>
    public CableType? Type => Catalog.Find(CableTypeId);

    /// <summary>The cable being drawn, or null.</summary>
    public CableDraft? Draft { get; private set; }

    /// <summary>The open port picker, or null.</summary>
    public PortPick? Pick { get; private set; }

    /// <summary>The cable entity committed last (tests and the UI's status).</summary>
    public Guid? LastCommitted { get; private set; }

    /// <summary>True while a cable is being drawn.</summary>
    public bool IsDrawing => Draft is not null;

    /// <summary>The last status message (tests).</summary>
    public string? LastMessage { get; private set; }

    /// <summary>Handles a left click on <paramref name="target"/>.</summary>
    public CableClickOutcome Click(CableTarget target)
    {
        if (_stage.Engine is null)
            return Reject(CableLoc.Get(CableStr.ToolCableNeedsEngine));
        if (Pick is not null)
            Pick = null; // a click elsewhere closes the picker
        if (!IsFinite(target.Position))
            return CableClickOutcome.Ignored;
        return target.Kind switch
        {
            CableTargetKind.Surface => ClickSurface(target),
            CableTargetKind.Device => ClickDevice(target),
            CableTargetKind.CableNode or CableTargetKind.CableSegment => ClickCable(target),
            _ => CableClickOutcome.Ignored,
        };
    }

    /// <summary>Plugs into the port <paramref name="portName"/> of the open picker's device.</summary>
    public CableClickOutcome ChoosePort(string portName)
    {
        if (Pick is not { } pick || _stage.Scene.Find(pick.DeviceId) is not { } device)
        {
            Pick = null;
            return CableClickOutcome.Ignored;
        }

        var candidate = pick.Candidates.FirstOrDefault(c => string.Equals(c.Port.Name, portName, StringComparison.OrdinalIgnoreCase));
        Pick = null;
        if (candidate is null || !candidate.Usable)
            return CableClickOutcome.Rejected;
        return Connect(device, candidate);
    }

    /// <summary>Plugs into the n-th usable port of the open picker (0-based; the picker's 1–9 keys).</summary>
    public CableClickOutcome ChooseUsable(int index)
    {
        if (Pick is not { } pick)
            return CableClickOutcome.Ignored;
        var usable = pick.Usable.ToList();
        return index >= 0 && index < usable.Count ? ChoosePort(usable[index].Port.Name) : CableClickOutcome.Ignored;
    }

    /// <summary>Closes the port picker without connecting.</summary>
    public void CancelPick() => Pick = null;

    /// <summary>
    /// Ends the cable being drawn (right-click, Esc): with at least two points it is committed with a free end (one
    /// undo step); a start without a second point is dropped. Returns true when something was committed.
    /// </summary>
    public bool Finish()
    {
        Pick = null;
        var draft = Draft;
        Draft = null;
        if (draft is null || _stage.Engine is null)
            return false;
        if (draft.Points.Count < 2)
            return false;
        return draft.ExistingCable is { } existing ? CommitBranch(draft, existing) : CommitNew(draft) is not null;
    }

    /// <summary>Drops the cable being drawn without committing it (scene replaced, tool switched off mid-way by code).</summary>
    public void Cancel()
    {
        Draft = null;
        Pick = null;
    }

    /// <summary>Removes the last route point of the cable being drawn (Backspace); the start point is kept.</summary>
    public bool RemoveLastPoint()
    {
        if (Draft is not { Points.Count: > 1 } draft)
            return false;
        draft.Points.RemoveAt(draft.Points.Count - 1);
        return true;
    }

    /// <summary>
    /// Forgets a draft whose scene objects are gone (undo removed the cable it branches from, or the scene was replaced).
    /// Call once per frame.
    /// </summary>
    public void Validate()
    {
        if (Draft is { ExistingCable: { } cable } draft &&
            (_stage.Scene.Find(cable)?.Get<CableComponent>() is not { } component ||
             (draft.Split is { } split ? !HasSegment(component, split.From, split.To) : component.FindNode(draft.Points[0].NodeId) is null)))
            Cancel();
        if (Draft is { StartPortRef: { } port } && _stage.Scene.Find(port.EntityId) is null)
            Cancel();
        if (Pick is { } pick && _stage.Scene.Find(pick.DeviceId) is null)
            Pick = null;
    }

    /// <summary>
    /// Which ports the cable being drawn may end on: anything for a new cable (outputs first); for a cable that starts
    /// at a port, the other direction; for a branch, the direction the cable it branches from carries (a branch of a
    /// line fed from an output ends on inputs); for a cable with a free start, anything (inputs first).
    /// </summary>
    public PortSide DraftSide()
    {
        if (Draft is not { } draft)
            return PortSide.Start;
        if (draft.StartPort is { } start)
            return CablePorts.FarSide(start);
        if (draft.ExistingCable is { } cableId && _stage.Engine is { } engine && _stage.Scene.Find(cableId)?.Get<CableComponent>() is { } cable)
        {
            bool output = false, input = false;
            foreach (var end in cable.Endpoints)
            {
                if (end is null || _stage.Scene.Find(end.EntityId) is not { } device || engine.Network.Ports.FindPort(device, end.PortName) is not { } port)
                    continue;
                output |= port.Direction == PortDirection.Output;
                input |= port.Direction == PortDirection.Input;
            }

            return output ? PortSide.AfterOutput : input ? PortSide.AfterInput : PortSide.AfterFree;
        }

        return PortSide.AfterFree;
    }

    // ---- clicks -----------------------------------------------------------------------------------------------------

    private CableClickOutcome ClickSurface(CableTarget target)
    {
        var point = SurfacePoint(target);
        if (Draft is null)
        {
            if (Type is not { } type)
                return Reject(CableLoc.Format(CableStr.ToolCableUnknownType, CableTypeId));
            var draft = new CableDraft { Type = type };
            draft.Points.Add(point);
            Draft = draft;
            return CableClickOutcome.Started;
        }

        Draft.Points.Add(point);
        return CableClickOutcome.PointAdded;
    }

    private CableClickOutcome ClickDevice(CableTarget target)
    {
        var engine = _stage.Engine!;
        if (_stage.Scene.Find(target.EntityId) is not { } device)
            return CableClickOutcome.Ignored;
        var type = Draft?.Type ?? Type;
        if (type is null)
            return CableClickOutcome.Ignored;
        var candidates = CablePorts.Candidates(engine, device, type, DraftSide(), Draft?.StartPortRef);
        var usable = candidates.Where(c => c.Usable).ToList();
        if (usable.Count == 1)
            return Connect(device, usable[0]);
        Pick = new PortPick(device.Id, device.Name, candidates);
        if (usable.Count == 0)
            Notify(CableLoc.Format(CableStr.ToolCableNoPort, device.Name, CableLoc.CableName(type), Reasons(candidates)));
        return CableClickOutcome.PickPort;
    }

    private CableClickOutcome ClickCable(CableTarget target)
    {
        if (_stage.Scene.Find(target.EntityId) is not { } cableEntity || cableEntity.Get<CableComponent>() is not { } cable)
            return CableClickOutcome.Ignored;
        var cableType = Catalog.Find(cable.CableTypeId);
        var world = cableEntity.WorldMatrix;
        Matrix4x4.Invert(world, out var toLocal);

        if (Draft is null)
        {
            if (cableType is null)
                return Reject(CableLoc.Format(CableStr.ToolCableUnknownType, cable.CableTypeId));
            // A branch belongs to the cable it starts from: it takes that cable's type.
            CableTypeId = cableType.Id;
            if (target.Kind == CableTargetKind.CableNode)
            {
                if (cable.FindNode(target.NodeId) is null)
                    return CableClickOutcome.Ignored;
                var draft = new CableDraft { Type = cableType, ExistingCable = cableEntity.Id };
                draft.Points.Add(new DraftPoint(target.NodeId, target.Position, null, null, Existing: true));
                Draft = draft;
            }
            else
            {
                if (!HasSegment(cable, target.SegmentFrom, target.SegmentTo))
                    return CableClickOutcome.Ignored;
                var draft = new CableDraft { Type = cableType, ExistingCable = cableEntity.Id, Split = (target.SegmentFrom, target.SegmentTo) };
                draft.Points.Add(new DraftPoint(Guid.NewGuid(), target.Position, null, null));
                Draft = draft;
            }

            return CableClickOutcome.Started;
        }

        // Drawing: join the drawn cable into this one when it is a new cable of the same type; otherwise the click is a
        // route point next to the other cable.
        bool sameType = cableType is not null && string.Equals(cableType.Id, Draft.Type.Id, StringComparison.OrdinalIgnoreCase);
        if (Draft.ExistingCable is null && sameType && Draft.Points.Count >= 1)
        {
            if (target.Kind == CableTargetKind.CableNode && cable.FindNode(target.NodeId) is null)
                return CableClickOutcome.Ignored;
            if (target.Kind == CableTargetKind.CableSegment && !HasSegment(cable, target.SegmentFrom, target.SegmentTo))
                return CableClickOutcome.Ignored;
            var draft = Draft;
            Draft = null;
            return CommitJoin(draft, cableEntity, cable, target, toLocal) ? CableClickOutcome.Committed : CableClickOutcome.Rejected;
        }

        if (Draft.ExistingCable == cableEntity.Id)
            Notify(CableLoc.Get(CableStr.ToolCableOwnBranch));
        Draft.Points.Add(new DraftPoint(Guid.NewGuid(), target.Position, null, null));
        return CableClickOutcome.PointAdded;
    }

    private CableClickOutcome Connect(Entity device, PortCandidate candidate)
    {
        var engine = _stage.Engine!;
        var port = candidate.Port;
        var (_, index, count) = CablePorts.Find(engine, device, port.Name);
        var local = CablePorts.LocalPosition(device, port, index, count);
        var worldPos = Vector3.Transform(local, device.WorldMatrix);
        var point = new DraftPoint(Guid.NewGuid(), worldPos, new CableAnchor(device.Id, local), new PortRef(device.Id, port.Name));

        if (Draft is null)
        {
            if (Type is not { } type)
                return CableClickOutcome.Ignored;
            var draft = new CableDraft { Type = type, StartPort = port, StartPortRef = point.Port };
            draft.Points.Add(point);
            Draft = draft;
            return CableClickOutcome.Started;
        }

        var done = Draft;
        Draft = null;
        done.Points.Add(point);
        bool committed = done.ExistingCable is { } existing ? CommitBranch(done, existing) : CommitNew(done) is not null;
        if (!committed)
            return CableClickOutcome.Rejected;
        Notify(CableLoc.Format(CableStr.ToolCableConnected, device.Name, port.Name));

        // A daisy chain goes on: the next cable starts from the device's thru / link output.
        if (CablePorts.Continuation(engine, device, done.Type, port) is { } next)
        {
            var (_, ni, nc) = CablePorts.Find(engine, device, next.Port.Name);
            var nextLocal = CablePorts.LocalPosition(device, next.Port, ni, nc);
            var draft = new CableDraft { Type = done.Type, StartPort = next.Port, StartPortRef = new PortRef(device.Id, next.Port.Name) };
            draft.Points.Add(new DraftPoint(Guid.NewGuid(), Vector3.Transform(nextLocal, device.WorldMatrix), new CableAnchor(device.Id, nextLocal),
                draft.StartPortRef));
            Draft = draft;
        }

        return CableClickOutcome.Connected;
    }

    // ---- commits ----------------------------------------------------------------------------------------------------

    /// <summary>Adds the draft as a new cable entity (one undo step); returns it.</summary>
    private Entity? CommitNew(CableDraft draft)
    {
        if (draft.Points.Count < 2)
        {
            Notify(CableLoc.Get(CableStr.ToolCableTooShort));
            return null;
        }

        var scene = _stage.Scene;
        var entity = new Entity(NextCableName(scene, draft.Type));
        var cable = new CableComponent { CableTypeId = draft.Type.Id };
        Guid previous = Guid.Empty;
        foreach (var p in draft.Points)
        {
            cable.Nodes.Add(new CableNode { Id = p.NodeId, Position = p.World, Anchor = p.Anchor });
            if (previous != Guid.Empty)
                cable.Segments.Add(new CableSegment(previous, p.NodeId));
            if (p.Port is { } port)
                cable.Endpoints.Add(new CableEndpoint { NodeId = p.NodeId, EntityId = port.EntityId, PortName = port.PortName });
            previous = p.NodeId;
        }

        entity.Add(cable);
        _stage.Undo.Execute(new AddEntityCommand(scene, entity));
        _stage.Undo.BreakMerge();
        LastCommitted = entity.Id;
        float length = draft.RoutedLength() * cable.SlackFactor;
        Notify(CableLoc.Format(CableStr.ToolCableAdded, entity.Name, Metres(length), Kilograms(length * draft.Type.WeightKgPerM)));
        return entity;
    }

    /// <summary>Adds the draft as a branch of the cable it started from (one undo step).</summary>
    private bool CommitBranch(CableDraft draft, Guid cableId)
    {
        if (_stage.Scene.Find(cableId) is not { } cableEntity || cableEntity.Get<CableComponent>() is not { } cable)
            return false;
        if (draft.Points.Count < 2)
            return false;
        Matrix4x4.Invert(cableEntity.WorldMatrix, out var toLocal);
        _stage.Undo.Execute(ModifyComponentCommand.Create(cable, c =>
        {
            var start = draft.Points[0];
            if (draft.Split is { } split)
                SplitSegment(c, split.From, split.To, start.NodeId, Vector3.Transform(start.World, toLocal));
            AppendPoints(c, draft.Points, 1, start.NodeId, toLocal);
        }, "Branch cable"));
        _stage.Undo.BreakMerge();
        LastCommitted = cableEntity.Id;
        var geometry = CableGeometry.Compute(_stage.Scene, cableEntity, cable);
        Notify(CableLoc.Format(CableStr.ToolCableBranched, cableEntity.Name, Metres(geometry.LengthM)));
        return true;
    }

    /// <summary>Merges a new cable's draft into another cable of the same type at a node or a new split node.</summary>
    private bool CommitJoin(CableDraft draft, Entity cableEntity, CableComponent cable, CableTarget target, Matrix4x4 toLocal)
    {
        Guid join = target.Kind == CableTargetKind.CableNode ? target.NodeId : Guid.NewGuid();
        _stage.Undo.Execute(ModifyComponentCommand.Create(cable, c =>
        {
            if (target.Kind == CableTargetKind.CableSegment)
                SplitSegment(c, target.SegmentFrom, target.SegmentTo, join, Vector3.Transform(target.Position, toLocal));
            // The drawn points hang off the joint, last point first, so the tree stays connected.
            Guid previous = join;
            for (int i = draft.Points.Count - 1; i >= 0; i--)
            {
                var p = draft.Points[i];
                c.Nodes.Add(new CableNode { Id = p.NodeId, Position = Vector3.Transform(p.World, toLocal), Anchor = p.Anchor });
                c.Segments.Add(new CableSegment(previous, p.NodeId));
                if (p.Port is { } port)
                    c.Endpoints.Add(new CableEndpoint { NodeId = p.NodeId, EntityId = port.EntityId, PortName = port.PortName });
                previous = p.NodeId;
            }
        }, "Join cable"));
        _stage.Undo.BreakMerge();
        LastCommitted = cableEntity.Id;
        var geometry = CableGeometry.Compute(_stage.Scene, cableEntity, cable);
        Notify(CableLoc.Format(CableStr.ToolCableBranched, cableEntity.Name, Metres(geometry.LengthM)));
        return true;
    }

    private static void AppendPoints(CableComponent c, List<DraftPoint> points, int from, Guid previous, Matrix4x4 toLocal)
    {
        for (int i = from; i < points.Count; i++)
        {
            var p = points[i];
            c.Nodes.Add(new CableNode { Id = p.NodeId, Position = Vector3.Transform(p.World, toLocal), Anchor = p.Anchor });
            c.Segments.Add(new CableSegment(previous, p.NodeId));
            if (p.Port is { } port)
                c.Endpoints.Add(new CableEndpoint { NodeId = p.NodeId, EntityId = port.EntityId, PortName = port.PortName });
            previous = p.NodeId;
        }
    }

    /// <summary>Replaces the segment between <paramref name="a"/> and <paramref name="b"/> by two through a new node.</summary>
    internal static void SplitSegment(CableComponent c, Guid a, Guid b, Guid node, Vector3 localPosition)
    {
        int index = c.Segments.FindIndex(s => (s.FromNode == a && s.ToNode == b) || (s.FromNode == b && s.ToNode == a));
        if (index < 0)
            throw new InvalidOperationException("The cable segment to split no longer exists.");
        var segment = c.Segments[index];
        c.Nodes.Add(new CableNode { Id = node, Position = localPosition });
        c.Segments[index] = new CableSegment(segment.FromNode, node);
        c.Segments.Insert(index + 1, new CableSegment(node, segment.ToNode));
    }

    private static bool HasSegment(CableComponent c, Guid a, Guid b) =>
        c.Segments.Exists(s => (s.FromNode == a && s.ToNode == b) || (s.FromNode == b && s.ToNode == a));

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A route point on a surface: lifted off it along the normal and fixed to its entity (free on the ground).</summary>
    private DraftPoint SurfacePoint(CableTarget target)
    {
        var normal = IsFinite(target.Normal) && target.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(target.Normal) : Vector3.UnitZ;
        var world = target.Position + normal * SurfaceOffset;
        CableAnchor? anchor = null;
        if (target.EntityId != Guid.Empty && _stage.Scene.Find(target.EntityId) is { } entity && Matrix4x4.Invert(entity.WorldMatrix, out var inverse))
            anchor = new CableAnchor(entity.Id, Vector3.Transform(world, inverse));
        return new DraftPoint(Guid.NewGuid(), world, anchor, null);
    }

    /// <summary>"DMX 3", "Power 2": the next free number for the cable's signal in the scene.</summary>
    internal static string NextCableName(Scene scene, CableType type)
    {
        string prefix = CableLoc.Get(type.Signal switch
        {
            SignalKind.Dmx512 => CableStr.NameCableDmx,
            SignalKind.Ethernet => CableStr.NameCableEthernet,
            SignalKind.Multicore => CableStr.NameCableMulticore,
            _ => CableStr.NameCablePower,
        });
        return NextName(scene, prefix);
    }

    /// <summary>"<paramref name="prefix"/> n" with n one more than the highest such number in the scene.</summary>
    internal static string NextName(Scene scene, string prefix)
    {
        ArgumentNullException.ThrowIfNull(scene);
        int max = 0;
        foreach (var e in scene.All())
        {
            string name = e.Name;
            if (name.Length > prefix.Length + 1 && name.StartsWith(prefix, StringComparison.Ordinal) && name[prefix.Length] == ' ' &&
                int.TryParse(name.AsSpan(prefix.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                max = Math.Max(max, n);
        }

        return $"{prefix} {(max + 1).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string Reasons(IReadOnlyList<PortCandidate> candidates)
    {
        if (candidates.Count == 0)
            return "—";
        return string.Join("; ", candidates.Take(4).Select(c => c.Occupant is { } o
            ? $"{c.Port.Name}: {CableLoc.Format(CableStr.PickerConnected, o.DisplayLabel)}"
            : c.WrongDirection ? $"{c.Port.Name}: {CableLoc.Format(CableStr.PickerIncompatible, DirectionReason(c.Port))}"
            : $"{c.Port.Name}: {CableLoc.Format(CableStr.PickerIncompatible, c.Check.Reason)}"));
    }

    /// <summary>Why a port of the same direction as the cable's other end does not fit ("output to output").</summary>
    internal static string DirectionReason(Port port) =>
        CableLoc.Get(port.Direction == PortDirection.Output ? CableStr.PickerOutputToOutput : CableStr.PickerInputToInput);

    private CableClickOutcome Reject(string message)
    {
        Notify(message);
        return CableClickOutcome.Rejected;
    }

    private void Notify(string message)
    {
        LastMessage = message;
        Status?.Invoke(message);
    }

    internal static string Metres(float m) => (float.IsFinite(m) ? m : 0f).ToString("0.0", CultureInfo.InvariantCulture);

    internal static string Kilograms(float kg) => (float.IsFinite(kg) ? kg : 0f).ToString("0.0#", CultureInfo.InvariantCulture);

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
