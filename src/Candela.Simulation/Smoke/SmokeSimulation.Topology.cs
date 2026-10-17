using System.Numerics;
using Candela.Core.Diagnostics;

namespace Candela.Simulation;

public sealed partial class SmokeSimulation
{
    /// <summary>Face flag: velocity is solved for.</summary>
    private const byte FaceFree = 0;

    /// <summary>Face flag: a wall (solid cell or closed domain face); the normal velocity is zero (free slip).</summary>
    private const byte FaceWall = 1;

    /// <summary>Face flag: an open domain face through which the constant airflow enters; the normal velocity is prescribed.</summary>
    private const byte FaceInflow = 2;

    private byte[] _uFlags = Array.Empty<byte>(), _vFlags = Array.Empty<byte>(), _wFlags = Array.Empty<byte>();
    private float[] _diag = Array.Empty<float>(), _invDiag = Array.Empty<float>();
    private int[] _component = Array.Empty<int>();
    private int[] _componentCells = Array.Empty<int>();
    private bool[] _componentClosed = Array.Empty<bool>();
    private double[] _componentSums = Array.Empty<double>();
    private bool _anyClosedComponent;
    private bool _hasObstacles;
    private DomainFaces _inflowFaces;
    private Vector3 _inflowVelocity;
    private Vector3 _ambientLo, _ambientHi;
    private int _settingsIssueCount;

    /// <summary>
    /// Replaces the solid obstacles. Each box is voxelised conservatively: a cell is solid when its centre lies inside
    /// the box inflated to at least one cell of thickness, so thin walls never leak. Smoke inside new solids is removed.
    /// Invalid boxes (NaN, negative size) are skipped and reported in <see cref="Issues"/>. Cost: proportional to the
    /// cells covered by the boxes' bounding boxes.
    /// </summary>
    /// <param name="boxes">Walls, floors, truss boxes, fixture bodies… in world space.</param>
    public void SetObstacles(ReadOnlySpan<ObstacleBox> boxes)
    {
        _issues.RemoveRange(_settingsIssueCount, _issues.Count - _settingsIssueCount);
        Array.Clear(_solid);
        int invalid = 0, outside = 0, applied = 0;
        float minHalf = 0.5f * _h * 1.001f;
        foreach (var box in boxes)
        {
            if (!box.IsValid)
            {
                invalid++;
                continue;
            }

            var rotation = Quaternion.Normalize(box.Rotation);
            var half = Vector3.Max(box.HalfExtents, new Vector3(minHalf));
            var inflated = box with { HalfExtents = half, Rotation = rotation };
            var world = inflated.WorldBounds();
            if (world.Max.X < _bounds.Min.X || world.Max.Y < _bounds.Min.Y || world.Max.Z < _bounds.Min.Z
                || world.Min.X > _bounds.Max.X || world.Min.Y > _bounds.Max.Y || world.Min.Z > _bounds.Max.Z)
            {
                outside++;
                continue;
            }

            applied++;
            var inverse = Quaternion.Conjugate(rotation);
            GetCellRange(world.Min, world.Max, out var lo, out var hi);
            for (int k = lo.Z; k <= hi.Z; k++)
            {
                for (int j = lo.Y; j <= hi.Y; j++)
                {
                    for (int i = lo.X; i <= hi.X; i++)
                    {
                        var local = Vector3.Transform(CellCenter(i, j, k) - box.Center, inverse);
                        if (MathF.Abs(local.X) <= half.X && MathF.Abs(local.Y) <= half.Y && MathF.Abs(local.Z) <= half.Z)
                            _solid[CellIndex(i, j, k)] = 1;
                    }
                }
            }
        }

        _obstacleCount = applied;
        if (invalid > 0)
        {
            _issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, "simulation.obstacle-invalid",
                $"{invalid} smoke obstacle box(es) with invalid position, size or rotation were ignored."));
        }

        if (outside > 0)
        {
            _issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Scene, "simulation.obstacle-outside",
                $"{outside} smoke obstacle box(es) lie outside the simulation box."));
        }

        for (int c = 0; c < _cellCount; c++)
        {
            if (_solid[c] != 0)
            {
                _density[c] = 0f;
                _temperature[c] = 0f;
                _pressure[c] = 0f;
            }
        }

        RebuildSolverTopology();
    }

    /// <summary>Decides which open faces receive the constant airflow and where outside air counts as ambient.</summary>
    private void InitializeBoundaries()
    {
        _settingsIssueCount = _issues.Count;
        var open = _settings.OpenFaces;
        var air = _settings.Airflow;
        DomainFaces inflow = DomainFaces.None;
        if (air.X > 0f && open.HasFlag(DomainFaces.XMin)) inflow |= DomainFaces.XMin;
        if (air.X < 0f && open.HasFlag(DomainFaces.XMax)) inflow |= DomainFaces.XMax;
        if (air.Y > 0f && open.HasFlag(DomainFaces.YMin)) inflow |= DomainFaces.YMin;
        if (air.Y < 0f && open.HasFlag(DomainFaces.YMax)) inflow |= DomainFaces.YMax;
        if (air.Z > 0f && open.HasFlag(DomainFaces.ZMin)) inflow |= DomainFaces.ZMin;
        if (air.Z < 0f && open.HasFlag(DomainFaces.ZMax)) inflow |= DomainFaces.ZMax;

        // Air blown in must be able to leave: without a remaining pressure-open face the airflow is ignored.
        if (inflow != DomainFaces.None && (open & ~inflow) == DomainFaces.None)
        {
            _issues.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, "simulation.airflow-no-outlet",
                "The smoke simulation airflow is ignored: the box has no open face through which the air can leave."));
            _settingsIssueCount = _issues.Count;
            inflow = DomainFaces.None;
        }
        else if (air != Vector3.Zero && inflow == DomainFaces.None)
        {
            _issues.Add(new Issue(IssueSeverity.Note, IssueCategory.Scene, "simulation.airflow-closed",
                "The smoke simulation airflow has no effect: the faces it would enter through are closed."));
            _settingsIssueCount = _issues.Count;
        }

        _inflowFaces = inflow;
        _inflowVelocity = air;
        float inf = float.PositiveInfinity;
        _ambientLo = new Vector3(open.HasFlag(DomainFaces.XMin) ? 0f : -inf, open.HasFlag(DomainFaces.YMin) ? 0f : -inf,
            open.HasFlag(DomainFaces.ZMin) ? 0f : -inf);
        _ambientHi = new Vector3(open.HasFlag(DomainFaces.XMax) ? _nx : inf, open.HasFlag(DomainFaces.YMax) ? _ny : inf,
            open.HasFlag(DomainFaces.ZMax) ? _nz : inf);
    }

    /// <summary>Recomputes face flags, the pressure matrix diagonal and the connected fluid regions after the solids changed.</summary>
    private void RebuildSolverTopology()
    {
        if (_uFlags.Length != _uCount)
        {
            _uFlags = new byte[_uCount];
            _vFlags = new byte[_vCount];
            _wFlags = new byte[_wCount];
            _diag = new float[_cellCount];
            _invDiag = new float[_cellCount];
            _component = new int[_cellCount];
        }

        _hasObstacles = Array.IndexOf(_solid, (byte)1) >= 0;
        var open = _settings.OpenFaces;
        for (int k = 0; k < _nz; k++)
        {
            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i <= _nx; i++)
                    _uFlags[UIndex(i, j, k)] = FaceFlag(i, _nx, DomainFaces.XMin, DomainFaces.XMax, open,
                        i > 0 && _solid[CellIndex(i - 1, j, k)] != 0, i < _nx && _solid[CellIndex(i, j, k)] != 0);
            }
        }

        for (int k = 0; k < _nz; k++)
        {
            for (int j = 0; j <= _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                    _vFlags[VIndex(i, j, k)] = FaceFlag(j, _ny, DomainFaces.YMin, DomainFaces.YMax, open,
                        j > 0 && _solid[CellIndex(i, j - 1, k)] != 0, j < _ny && _solid[CellIndex(i, j, k)] != 0);
            }
        }

        for (int k = 0; k <= _nz; k++)
        {
            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                    _wFlags[WIndex(i, j, k)] = FaceFlag(k, _nz, DomainFaces.ZMin, DomainFaces.ZMax, open,
                        k > 0 && _solid[CellIndex(i, j, k - 1)] != 0, k < _nz && _solid[CellIndex(i, j, k)] != 0);
            }
        }

        // Diagonal of the pressure matrix: the number of free faces of each fluid cell (an open face couples to the
        // ambient pressure outside, a wall or inflow face to nothing).
        for (int k = 0; k < _nz; k++)
        {
            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                {
                    int c = CellIndex(i, j, k);
                    int free = 0;
                    if (_solid[c] == 0)
                    {
                        free += _uFlags[UIndex(i, j, k)] == FaceFree ? 1 : 0;
                        free += _uFlags[UIndex(i + 1, j, k)] == FaceFree ? 1 : 0;
                        free += _vFlags[VIndex(i, j, k)] == FaceFree ? 1 : 0;
                        free += _vFlags[VIndex(i, j + 1, k)] == FaceFree ? 1 : 0;
                        free += _wFlags[WIndex(i, j, k)] == FaceFree ? 1 : 0;
                        free += _wFlags[WIndex(i, j, k + 1)] == FaceFree ? 1 : 0;
                    }

                    _diag[c] = free;
                    _invDiag[c] = free > 0 ? 1f / free : 0f;
                }
            }
        }

        LabelComponents();
        EnforceBoundaryVelocities(_u, _v, _w);
    }

    private byte FaceFlag(int index, int count, DomainFaces minFace, DomainFaces maxFace, DomainFaces open, bool solidBefore,
        bool solidAfter)
    {
        if (solidBefore || solidAfter)
            return FaceWall;
        if (index == 0)
            return !open.HasFlag(minFace) ? FaceWall : _inflowFaces.HasFlag(minFace) ? FaceInflow : FaceFree;
        if (index == count)
            return !open.HasFlag(maxFace) ? FaceWall : _inflowFaces.HasFlag(maxFace) ? FaceInflow : FaceFree;
        return FaceFree;
    }

    /// <summary>
    /// Labels connected fluid regions. A region that touches no pressure-open face is closed: its pressure is only
    /// defined up to a constant and the divergence it is asked to remove must sum to zero, which the projection enforces
    /// by subtracting the region's mean.
    /// </summary>
    private void LabelComponents()
    {
        Array.Fill(_component, -1);
        var queue = new int[_cellCount];
        var cells = new List<int>();
        var closed = new List<bool>();
        int slab = _nx * _ny;
        for (int seed = 0; seed < _cellCount; seed++)
        {
            if (_solid[seed] != 0 || _component[seed] >= 0)
                continue;
            int id = cells.Count;
            int head = 0, tail = 0, count = 0;
            bool touchesOpen = false;
            queue[tail++] = seed;
            _component[seed] = id;
            while (head < tail)
            {
                int c = queue[head++];
                count++;
                int i = c % _nx, j = c / _nx % _ny, k = c / slab;
                touchesOpen |= i == 0 && _uFlags[UIndex(0, j, k)] == FaceFree;
                touchesOpen |= i == _nx - 1 && _uFlags[UIndex(_nx, j, k)] == FaceFree;
                touchesOpen |= j == 0 && _vFlags[VIndex(i, 0, k)] == FaceFree;
                touchesOpen |= j == _ny - 1 && _vFlags[VIndex(i, _ny, k)] == FaceFree;
                touchesOpen |= k == 0 && _wFlags[WIndex(i, j, 0)] == FaceFree;
                touchesOpen |= k == _nz - 1 && _wFlags[WIndex(i, j, _nz)] == FaceFree;
                if (i > 0) Visit(c - 1);
                if (i < _nx - 1) Visit(c + 1);
                if (j > 0) Visit(c - _nx);
                if (j < _ny - 1) Visit(c + _nx);
                if (k > 0) Visit(c - slab);
                if (k < _nz - 1) Visit(c + slab);
            }

            cells.Add(count);
            closed.Add(!touchesOpen);

            void Visit(int n)
            {
                if (_solid[n] == 0 && _component[n] < 0)
                {
                    _component[n] = id;
                    queue[tail++] = n;
                }
            }
        }

        _componentCells = cells.ToArray();
        _componentClosed = closed.ToArray();
        _componentSums = new double[_componentCells.Length];
        _anyClosedComponent = Array.IndexOf(_componentClosed, true) >= 0;
    }

    /// <summary>Sets wall faces to zero and inflow faces to the airflow's normal component.</summary>
    private void EnforceBoundaryVelocities(float[] u, float[] v, float[] w)
    {
        EnforceFlags(u, _uFlags, _inflowVelocity.X);
        EnforceFlags(v, _vFlags, _inflowVelocity.Y);
        EnforceFlags(w, _wFlags, _inflowVelocity.Z);

        static void EnforceFlags(float[] f, byte[] flags, float inflow)
        {
            for (int n = 0; n < f.Length; n++)
            {
                byte flag = flags[n];
                if (flag != FaceFree)
                    f[n] = flag == FaceWall ? 0f : inflow;
            }
        }
    }

    private float FixedFaceValue(byte flag, float inflow) => flag == FaceWall ? 0f : inflow;
}
