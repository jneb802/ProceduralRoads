using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Small cuts where the ground covers a walk (one owner): a row of a piece is cut out where the ground covers more than half of
/// it or any of it deeper than StairCover.TreadEdge, to CutBelowSurface under the walk; the treads near either end are kept clear of
/// other earthwork; a turn's wedge is read for burial where a staircase is kept off earthwork.
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

    /// <summary>How deep the ground stands over the filler of the wedge a turn at <paramref name="pivot"/>
    /// opens off a piece whose far edge is centred on <paramref name="edge"/>, level with its top
    /// <paramref name="height"/>; the turned piece's back edge is centred on <paramref name="start"/>.
    /// Read at the wedge's middle and at the middle of its open side (between the two outer corners),
    /// a little inside it.</summary>
    internal static float WedgeBuried(Vector2 edge, float height, Vector2 pivot, Vector2 start, Func<float, float, float> ground)
    {
        Vector2 a = edge + (edge - pivot);                        // the piece before's outer corner
        Vector2 b = start + (start - pivot);                      // the new piece's outer back corner
        Vector2 open = (a + b) * 0.5f, middle = (pivot + a + b) * (1f / 3f);
        Vector2 inside = open + (pivot - open).normalized * 0.1f;
        return Mathf.Max(ground(middle.x, middle.y), ground(inside.x, inside.y)) - height;
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
            var centre = new Vector2(piece.Position.x, piece.Position.z);
            if (Vector2.Distance(centre, foot) > EndGuardReach && Vector2.Distance(centre, top) > EndGuardReach) continue;
            float heading = stair ? Wrap(piece.YawDegrees + 180f) : piece.YawDegrees;
            float baseHeight = stair ? piece.Position.y : piece.Position.y + kit.FloorTop;
            Vector2 dir = Forward(heading);
            AddCuts(plan, stair, centre - dir * (PieceRun / 2f), dir, Right(heading), baseHeight, PieceRun, PieceRun, ground, guard: true);
        }
    }

    /// <summary>
    /// Where the ground swallows this piece's walking surface, cut points at the
    /// height that exposes it. The piece is looked at in rows across it, every
    /// <see cref="CutSpacing"/> along; a row is cut, across its whole width,
    /// when the ground covers more than half of it, or buries any part of it
    /// deeper than <see cref="StairCover.TreadEdge"/>. An edge only grazed by the
    /// slope is left alone (it is fine if some of the stairs are covered, just not all of them; a stair 0.5 m into a bank
    /// has too much terrain overlap).
    /// </summary>
    private static void AddCuts(StairPlan plan, bool stair, Vector2 laneStart, Vector2 dir, Vector2 right, float baseHeight, float across, float depth,
        Func<float, float, float> ground, bool guard = false)
    {
        var row = new List<(Vector2 p, float g)>();
        for (float along = CutSpacing / 2f; along < depth; along += CutSpacing)
        {
            float surface = stair ? baseHeight + PieceRise * along / PieceRun : baseHeight;
            row.Clear();
            int covered = 0;
            float deepest = float.NegativeInfinity;
            for (float side = -across / 2f + CutSpacing / 2f; side < across / 2f; side += CutSpacing)
            {
                Vector2 p = laneStart + dir * along + right * side;
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
                plan.Cuts.Add(new Vector3(p.x, target, p.y));
                if (g > target) plan.MaxCut = Mathf.Max(plan.MaxCut, g - target);
            }
        }
    }
}
