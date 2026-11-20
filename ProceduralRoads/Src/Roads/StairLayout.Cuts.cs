using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Small cuts where the ground covers a walk (one owner): a row of a piece is cut out where the ground covers more than half of
/// it or any of it deeper than StairCover.TreadEdge, to CutBelowSurface under the walk; the treads near either end are kept clear of
/// other earthwork; where a staircase is kept off earthwork (an approach) a piece's and a turn's wedge's burial is read over its
/// whole walking surface (Cover, the one reading the search, the approach's refusal and the tests share).
/// </summary>
public static partial class StairLayout
{
    /// <summary>Where the ground covers a walking surface it is lowered to this far below
    /// it (a small earthwork to expose it, if required, but at a cost: hugging is preferred). The cost is the search's
    /// burial cost; the cut is what that cost pays for.</summary>
    public const float CutBelowSurface = 0.15f;

    /// <summary>Ground up to this far above the line of a stair's step nosings (or a
    /// floor's top) is flush with it, not covering it: a tread lies a step below the line.</summary>
    public const float CoverTolerance = 0.1f;

    /// <summary>Cut points stand this far apart across and along a piece.</summary>
    public const float CutSpacing = 0.5f;

    /// <summary>How far round each cut point the ground is lowered. Cut points
    /// stand 0.5 m apart and terrain vertices 1 m apart, so this reaches every
    /// vertex under the cut stretch (half the 1 m diagonal is 0.71 m).</summary>
    public const float CutRadius = 0.75f;
    /// <summary>The width a staircase's cut is committed at: twice its radius.</summary>
    public const float CutWidth = CutRadius * 2f;

    internal const float SurfaceSpacing = CutSpacing / 2f;

    /// <summary>The walking height <paramref name="along"/> metres on from a piece's back edge (a stair rises along it), less
    /// <paramref name="below"/>. One expression, rounded once: the game's Mono works floats wider than float and rounds at the
    /// store, so taking <paramref name="below"/> off a rounded walk height can move the last bit.</summary>
    internal static float WalkHeight(bool stair, float height, float along, float below = 0f) => height + (stair ? PieceRise * along / PieceRun : 0f) - below;

    private static Vector3 WalkPoint(bool stair, Vector2 back, Vector2 dir, Vector2 right, float height, float along, float side)
    {
        Vector2 p = back + dir * along + right * side;
        return new Vector3(p.x, WalkHeight(stair, height, along), p.y);
    }

    /// <summary>The complete walked surface, its boundary included, whether or not cuts were emitted.
    /// Uses the same surface transform as AddCuts and the kit's actual filler dimensions.</summary>
    internal static IEnumerable<Vector3> WalkingSurface(StairPiece piece)
    {
        if (piece.Kind == StairPieceKind.Support) yield break;
        var (back, heading, across, depth, height) = Footprint(piece);
        foreach (Vector3 sample in WalkingSurface(piece.Kind == StairPieceKind.Stair, back, heading, height, across, depth)) yield return sample;
    }

    /// <summary>A walked piece's footprint as its walking surface is read (<see cref="WalkingSurface(StairPiece)"/>): its back
    /// edge's centre, the heading it is walked on, its size across and along that heading (a filler's its kit's), and its
    /// walking height at its back edge (a stair's low edge, a floor's top).</summary>
    internal static (Vector2 back, float heading, float across, float depth, float height) Footprint(StairPiece piece)
    {
        StairKit kit = StairKit.OfPrefab(piece.Prefab);
        bool stair = piece.Kind == StairPieceKind.Stair;
        float across = PieceRun, depth = PieceRun;
        if (piece.Kind == StairPieceKind.Filler) { FillerKind filler = kit.FlatOf(piece.Prefab); across = filler.Across; depth = filler.Depth; }
        float heading = stair ? Wrap(piece.YawDegrees + 180f) : piece.YawDegrees;
        Vector2 back = new Vector2(piece.Position.x, piece.Position.z) - Forward(heading) * (depth / 2f);
        return (back, heading, across, depth, piece.Position.y + (stair ? 0f : kit.FloorTop));
    }

    private static IEnumerable<Vector3> WalkingSurface(bool stair, Vector2 back, float heading, float height, float across, float depth)
    {
        Vector2 dir = Forward(heading), right = Right(heading);
        for (float along = 0f; along <= depth + 0.001f; along += SurfaceSpacing)
            for (float side = -across / 2f; side <= across / 2f + 0.001f; side += SurfaceSpacing)
                yield return WalkPoint(stair, back, dir, right, height, along, side);
    }

    /// <summary>
    /// How deep the ground stands over a walked piece, point by point (one owner of "a walked piece is buried in the ground"
    /// where no earthwork uncovers it: an approach's search, its refusal and the tests' oracle all read this): the ground
    /// less the walking height at every point of <see cref="WalkingSurface(StairPiece)"/>, its edges included. Read at the
    /// piece's inside only (the search read 0.5–1.5 m along, 0.9 m across; the refusal its cut rows), a stair just past a
    /// switchback pad at the Mountain tower had its side edge 0.53 m under the tower's raised corner while every inside
    /// point stood clear. The edges are read where they are, with no inset: the edge is where a player sees the slope come
    /// through the piece. Positive where the ground covers the surface.
    /// </summary>
    internal static IEnumerable<(Vector3 at, float depth)> Cover(IEnumerable<Vector3> surface, Func<float, float, float> ground)
    {
        foreach (Vector3 s in surface) yield return (s, ground(s.x, s.z) - s.y);
    }

    /// <summary>The deepest the ground stands over the walking surface <see cref="WalkingSurface(bool, Vector2, float, float, float, float)"/>
    /// lays out (<see cref="Cover"/>), 0 where it stands clear everywhere, read no further once deeper than
    /// <paramref name="enough"/> (the search's refusal: the rest cannot change it); the same lattice point for point, read with plain loops: the search reads every candidate piece and wedge so,
    /// and an iterator per reading was garbage on the worker threads.</summary>
    internal static float DeepestOver(bool stair, Vector2 back, float heading, float height, float across, float depth,
        Func<float, float, float> ground, float enough = float.PositiveInfinity)
    {
        Vector2 dir = Forward(heading), right = Right(heading);
        float deepest = 0f;
        for (float along = 0f; along <= depth + 0.001f; along += SurfaceSpacing)
            for (float side = -across / 2f; side <= across / 2f + 0.001f; side += SurfaceSpacing)
            {
                Vector3 s = WalkPoint(stair, back, dir, right, height, along, side);
                deepest = Mathf.Max(deepest, ground(s.x, s.z) - s.y);
                if (deepest > enough) return deepest;
            }
        return deepest;
    }

    /// <summary>How deep the ground stands over the fillers of the wedge a turn of <paramref name="turn"/> heading steps off a
    /// piece walking <paramref name="from"/> opens, pivoting on <paramref name="pivot"/> (StairFiller.Choose's fillers, as the
    /// kit lays them, read round the origin and moved to the pivot, allocating nothing: StairFiller.Around and At, as
    /// Route.WedgeHeld reads them; level with the piece before, its top <paramref name="height"/>): each over its whole walking
    /// surface, its edges included (<see cref="DeepestOver"/>). Read before at the wedge's middle and its open side only, a
    /// filler at the Mountain tower stood 0.72 m into the slope at its edge.</summary>
    internal static float WedgeBuried(Vector2 pivot, float from, float height, int turn, float width, bool offFloor, FillerKind[]? fillers,
        Func<float, float, float> ground, float enough = float.PositiveInfinity)
    {
        float deepest = 0f;
        foreach (FillerPiece around in StairFiller.Around(from, turn, width, offFloor, fillers))
        {
            FillerPiece filler = StairFiller.At(around, pivot);
            deepest = Mathf.Max(deepest, DeepestOver(false, filler.Back, filler.Heading, height, filler.Across, filler.Depth, ground, enough));
            if (deepest > enough) break;
        }
        return deepest;
    }

    /// <summary>How far from either end a staircase's treads are kept clear of earthwork
    /// that is not its own. A road meeting a staircase is held at the staircase's end height
    /// and runs on from there, its blend and fill reaching several metres past its edge; with
    /// nothing to stop it, it would cover the last steps. Settable for tests of the burial
    /// rule alone (0 is off).</summary>
    internal static float EndGuardReach = DefaultEndGuardReach;

    public const float DefaultEndGuardReach = 8f;

    /// <summary>Cut points under every tread and landing within <see cref="EndGuardReach"/>
    /// of the foot or the top, whether the natural ground covers them or not: the terrain
    /// writer lowers ground (a road's fill included) to a cut, and never raises it.</summary>
    private static void GuardEnds(StairPlan plan, StairKit kit, Vector2 foot, Vector2 top, Func<float, float, float> ground, int sunkPieces)
    {
        if (!(EndGuardReach > 0f)) return;
        int count = plan.Pieces.Count;
        // The sunk foot is buried on purpose (the flight comes up out of the road): left as it is.
        for (int i = sunkPieces; i < count; i++)
        {
            StairPiece piece = plan.Pieces[i];
            bool stair = piece.Kind == StairPieceKind.Stair;
            if (!stair && piece.Kind != StairPieceKind.Landing) continue;
            float heading = stair ? Wrap(piece.YawDegrees + 180f) : piece.YawDegrees;
            float baseHeight = stair ? piece.Position.y : piece.Position.y + kit.FloorTop;
            GuardPiece(plan.Cuts, ref plan.MaxCut, stair, new Vector2(piece.Position.x, piece.Position.z), heading, baseHeight, foot, top, ground);
        }
    }

    /// <summary>One 2 m piece's guard cuts (<see cref="GuardEnds"/>): every row of it, centred on <paramref name="centre"/>,
    /// walked on <paramref name="heading"/>, when it lies within <see cref="EndGuardReach"/> of <paramref name="foot"/> or
    /// <paramref name="top"/>.</summary>
    private static void GuardPiece(List<Vector3> cuts, ref float maxCut, bool stair, Vector2 centre, float heading, float baseHeight,
        Vector2 foot, Vector2 top, Func<float, float, float> ground)
    {
        if (Vector2.Distance(centre, foot) > EndGuardReach && Vector2.Distance(centre, top) > EndGuardReach) return;
        Vector2 dir = Forward(heading);
        AddCuts(cuts, ref maxCut, stair, centre - dir * (PieceRun / 2f), dir, Right(heading), baseHeight, PieceRun, PieceRun, ground, guard: true);
    }

    /// <summary>
    /// The cuts that expose a structure's end step (a Dvergr viaduct's, <see cref="RoadNetworkGenerator.GroundOf"/>), the one
    /// stair excavation a staircase's stairs are cut by: the stair laid at <paramref name="at"/> (its origin, at its foot),
    /// turned <paramref name="yaw"/> (it climbs toward its local -z), 2 m by 2 m. Its walking surface is cut where the ground
    /// covers it (<see cref="AddCuts"/>), and every row of it within <see cref="EndGuardReach"/> of the stairs'
    /// <paramref name="foot"/> or <paramref name="top"/> whatever covers it (<see cref="GuardEnds"/>' rule), each to
    /// <see cref="CutBelowSurface"/> under the walk over it, committed <see cref="CutWidth"/> wide. Into <paramref name="cuts"/>.
    /// </summary>
    internal static void AddStepCuts(List<Vector3> cuts, Vector3 at, float yaw, Vector2 foot, Vector2 top, Func<float, float, float> ground)
    {
        float heading = Wrap(yaw + 180f), maxCut = 0f;
        Vector2 dir = Forward(heading), centre = new(at.x, at.z);
        AddCuts(cuts, ref maxCut, true, centre - dir * (PieceRun / 2f), dir, Right(heading), at.y, PieceRun, PieceRun, ground);
        if (EndGuardReach > 0f) GuardPiece(cuts, ref maxCut, true, centre, heading, at.y, foot, top, ground);
    }

    /// <summary>
    /// Where the ground swallows this piece's walking surface, cut points at the
    /// height that exposes it. The piece is looked at in rows across it, every
    /// <see cref="CutSpacing"/> along; a row is cut, across its whole width,
    /// when the ground covers more than half of it, or buries any part of it
    /// deeper than <see cref="StairCover.TreadEdge"/>. An edge only grazed by the
    /// slope is left alone (it is fine if some of the stairs are covered, just not all of them; a stair 0.5 m into a bank
    /// has too much terrain overlap). The one owner of stair excavation (docs/OWNERS.md): a staircase's pieces
    /// (<see cref="AddPiece"/>, <see cref="GuardEnds"/>) and a structure's end steps (<see cref="AddStepCuts"/>). Into
    /// <paramref name="cuts"/>, the deepest cut kept in <paramref name="maxCut"/>.
    /// </summary>
    private static void AddCuts(List<Vector3> cuts, ref float maxCut, bool stair, Vector2 laneStart, Vector2 dir, Vector2 right, float baseHeight,
        float across, float depth, Func<float, float, float> ground, bool guard = false)
    {
        var row = new List<(Vector2 p, float g)>();
        for (float along = CutSpacing / 2f; along < depth; along += CutSpacing)
        {
            float surface = WalkPoint(stair, laneStart, dir, right, baseHeight, along, 0f).y;
            row.Clear();
            int covered = 0;
            float deepest = float.NegativeInfinity;
            for (float side = -across / 2f + CutSpacing / 2f; side < across / 2f; side += CutSpacing)
            {
                Vector3 walk = WalkPoint(stair, laneStart, dir, right, baseHeight, along, side);
                Vector2 p = new Vector2(walk.x, walk.z);
                float g = ground(p.x, p.y);
                row.Add((p, g));
                if (g > surface + CoverTolerance) covered++;
                deepest = Mathf.Max(deepest, g - surface);
            }
            if (!guard && covered * 2 <= row.Count && deepest <= StairCover.TreadEdge) continue;
            float target = surface - CutBelowSurface;
            foreach (var (p, g) in row)
            {
                // A guard row (GuardEnds) is cut whatever the natural ground: what covers it is another road's earthwork.
                if (g <= target && !guard) continue;
                cuts.Add(new Vector3(p.x, target, p.y));
                if (g > target) maxCut = Mathf.Max(maxCut, g - target);
            }
        }
    }
}
