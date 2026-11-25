using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Cantilevers (one owner): on a face of 2:1 or steeper with a drop under the outer edge, a piece is held out from the face on
/// pillars clipped into it, arches and a block beside, where that is cheaper than columns; priced by the face's grade and by how
/// far its strip stands out of the face.
/// </summary>
public static partial class StairLayout
{
    /// <summary>A face gentler than this (rise over run across the flight, read over 3 m) is not held
    /// out from: a flight hugs the slope or stands on columns, as before (the support shifts from
    /// supporting pillars to cantilever arches). From 1:1, the layouts up Hildir's cave's 2:1 to 2.6:1
    /// face held out half their rows, took other ways up and stalled at a road
    /// staircase's allowance; from 2:1 they lay as before, and the near-sheer faces cantilever.</summary>
    public const float CantileverFromGrade = 2f;

    /// <summary>A piece is held out only where the ground under its outer edge lies at least
    /// this far under its underside: "where the ground drops away under its outer side".</summary>
    public const float CantileverDrop = 1f;

    /// <summary>
    /// Pricing over the gradient (over a certain grade a piece is more
    /// expensive but still possible: first cheaper at lesser grades, then it moves up to a
    /// maximum, as the support shifts from supporting pillars to cantilever arches). A
    /// cantilevered piece costs <see cref="CantileverBasePrice"/> on a 2:1 face, rising
    /// evenly to <see cref="CantileverMaxPrice"/> on a <see cref="CantileverFullGrade"/>:1
    /// face, and no more however sheer: a sheer face stays possible, the dearest piece there
    /// is. A piece on columns costs <see cref="FloatWeight"/> a metre of mean float (about 11
    /// across a hand-built reference face), and a piece is held out only where that is dearer.
    /// </summary>
    public const float CantileverBasePrice = 2f, CantileverMaxPrice = 6f, CantileverFullGrade = 5f;

    public static float CantileverPrice(float face) =>
        Mathf.Lerp(CantileverBasePrice, CantileverMaxPrice, Mathf.Clamp01((face - CantileverFromGrade) / (CantileverFullGrade - CantileverFromGrade)));

    /// <summary>
    /// Hugging the face (a cantilever onto the mountain cave at 1920,704 worked well, but the stairs should hug the wall
    /// closer if possible so less of the side block shows through): a cantilevered piece costs this much more per metre its strip (the block beside it and
    /// the pillars under that) stands out of the face, read at the strip's middle. Its tread is kept clear as
    /// ever (the wall side's burial is read where its cuts are), so the search sets the flight as close in as
    /// that allows.
    /// </summary>
    public const float HugWeight = 1f;

    /// <summary>How far, on average over its two metres, a strip whose block's top is <paramref name="blockTop"/>
    /// stands out of the ground at its middle (0 where the face covers it).</summary>
    internal static float StripExposed(Vector2 start, Vector2 dir, Vector2 wall, float half, float blockTop, Func<float, float, float> ground)
    {
        float reach = half + CantileverKit.Strip / 2f, exposed = 0f;
        for (int i = 0; i < 2; i++)
        {
            Vector2 p = start + dir * (i + 0.5f) + wall * reach;
            exposed += Mathf.Max(0f, blockTop - ground(p.x, p.y));
        }
        return exposed / 2f;
    }

    /// <summary>
    /// The side (+1 right of <paramref name="dir"/>, -1 left) of the face a piece at
    /// <paramref name="start"/> is held out from, or 0 where it is not: the cantilever
    /// (<see cref="CantileverKit"/>) where the kit has one, the face across the piece is at
    /// least <see cref="CantileverFromGrade"/>, the ground under its outer edge lies
    /// <see cref="CantileverDrop"/> or more under it, and on the wall side each metre's
    /// pillar column, stacked no deeper than <see cref="CantileverKit.MaxDepth"/>, can be sunk <see cref="SupportBury"/>
    /// into the face or the ground under it: the pillars hold the
    /// rest, the arches whether or not they touch the face (read in game, <see cref="CantileverKit"/>).
    /// <paramref name="face"/>: rise over run across the piece, wall strip to a metre past
    /// its outer edge (3 m).
    /// </summary>
    internal static int CantileverSide(Vector2 start, Vector2 dir, Vector2 right, float half, float underside, StairKit kit,
        Func<float, float, float> ground, out float face, float surface = float.PositiveInfinity, float rise = 0f, float maxBuried = StairCover.RoadStaircase)
    {
        face = 0f;
        CantileverKit? ck = kit.Cantilever;
        if (ck == null) return 0;
        Vector2 mid = start + dir * (PieceRun / 2f);
        float reach = half + CantileverKit.Strip / 2f;
        Vector2 r = mid + right * reach, l = mid - right * reach;
        float gr = ground(r.x, r.y), gl = ground(l.x, l.y);
        int side = gr >= gl ? 1 : -1;
        Vector2 wall = right * side;
        face = Mathf.Abs(gr - gl) / (2f * reach);
        if (face < CantileverFromGrade) return 0;
        Vector2 outer = mid - wall * (half * 0.9f);
        if (ground(outer.x, outer.y) > underside - CantileverDrop) return 0;
        for (int i = 0; i < 2; i++)
        {
            Vector2 m = start + dir * (i + 0.5f);
            Vector2 pillar = m + wall * reach;
            if (ground(pillar.x, pillar.y) < underside - ck.MaxDepth + SupportBury) return 0;
        }
        // Against a face, a few centimetres across are a metre of height: the wall side of the walk
        // is read where its cuts are (StairLayout.AddCuts, every half metre), and a piece the face
        // would bury there stays on columns, laid as before. Held out, an approach up Hildir's cave's
        // face was refused for earthwork in the cave's footprint that the coarser fit missed.
        if (float.IsPositiveInfinity(surface)) return side;
        for (float along = CutSpacing / 2f; along < PieceRun; along += CutSpacing)
            for (float across = CutSpacing / 2f; across < half; across += CutSpacing)
            {
                Vector2 p = start + dir * along + wall * across;
                if (ground(p.x, p.y) - (surface + rise * along / PieceRun) > maxBuried) return 0;
            }
        return side;
    }

    /// <summary>
    /// The pieces that hold a piece whose back edge is centred on <paramref name="back"/>, facing
    /// <paramref name="heading"/>, out from the face on its <paramref name="side"/> (as in a hand-built reference):
    /// for each metre along it a column of pillars <see cref="CantileverKit.StripDepth"/> deep in
    /// the strip beside it, their top at the piece's <paramref name="underside"/>, and an arch from
    /// that pillar out under the piece, its tall end against the pillar and its top under the
    /// piece; then a block laid along the strip on the pillars, its top level with the piece's top
    /// edge. Nothing stands in the drop.
    /// </summary>
    internal static void AddCantilever(StairPlan plan, CantileverKit ck, Vector2 back, float heading, float underside, int side, float across,
        Func<float, float, float>? ground = null)
    {
        Vector2 dir = Forward(heading), wall = Right(heading) * side;
        float reach = across / 2f + CantileverKit.Strip / 2f;
        // local +x times the tall end points at the wall: local x is Right(yaw).
        float archYaw = ArchYaw(ck, heading, side);
        for (int i = 0; i < 2; i++)
        {
            Vector2 m = back + dir * (i + 0.5f), p = m + wall * reach;
            AddStripColumn(plan, ck, p, underside, heading, ground);
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Support, Prefab = ck.Arch, Position = new Vector3(m.x, underside - ck.ArchHeight / 2f, m.y), YawDegrees = archYaw, Cantilevered = true });
        }
        // Local x along the flight: Right(heading - 90) is Forward(heading).
        Vector2 b = back + dir * (PieceRun / 2f) + wall * reach;
        if (BesideSunk(b, underside + 1f, ground))
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Support, Prefab = ck.Beside, Position = new Vector3(b.x, underside + 0.5f, b.y), YawDegrees = Wrap(heading - 90f), Cantilevered = true });
    }

    /// <summary>
    /// The block beside a held-out piece is laid only where the face holds most of it: its top no more than
    /// <see cref="BesideShows"/> over the ground at its middle. It is the look of the thing, not its support (the arches
    /// and pillars hold the stair), and standing out of the face it showed as a row of blocks between the stair and the
    /// rock (the stairs should hug the face, not the side wall, so less of the side block shows through).
    /// </summary>
    public const float BesideShows = 0.5f;

    private static bool BesideSunk(Vector2 at, float top, Func<float, float, float>? ground) =>
        ground == null || top - ground(at.x, at.y) <= BesideShows;

    private static float ArchYaw(CantileverKit ck, float heading, int side) => side * ck.ArchTallEnd > 0f ? heading : Wrap(heading + 180f);

    /// <summary>A strip column at <paramref name="p"/>: pillars from <paramref name="top"/> down at least
    /// <see cref="CantileverKit.StripDepth"/>, and on down until the lowest is sunk <see cref="SupportBury"/> into the
    /// ground there, no deeper than <see cref="CantileverKit.MaxDepth"/> (hand-built turns: their stacks went down to
    /// the ground where the top pillars did not reach the face).</summary>
    private static void AddStripColumn(StairPlan plan, CantileverKit ck, Vector2 p, float top, float heading, Func<float, float, float>? ground)
    {
        float g = ground?.Invoke(p.x, p.y) ?? float.PositiveInfinity;
        for (float at = top; at > top - ck.MaxDepth + 0.01f; at -= ck.PillarHeight)
        {
            if (at <= top - CantileverKit.StripDepth + 0.01f && at <= g - SupportBury) break;
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Support, Prefab = ck.Pillar, Position = new Vector3(p.x, at - ck.PillarHeight / 2f, p.y), YawDegrees = heading, Cantilevered = true });
        }
    }

    /// <summary>
    /// What holds a turn's wedge off a cantilevered piece (hand-built turn examples at 1934,164,705: 22.5 and
    /// 45 degree turns both ways): the wedge's filler, laid across the piece's top edge as StairFiller chooses, rests
    /// on an arch where the piece's next metre's would be, with a strip column beside it; turned away from the face,
    /// the piece's strip block is carried on past its top too, level with it, where the next flight's strip turns off.
    /// <paramref name="back"/>, <paramref name="heading"/> and <paramref name="top"/>: the piece before's back edge,
    /// heading and top edge's walking height; <paramref name="turn"/> its signed turn (+ right).
    /// </summary>
    internal static void AddTurnSupport(StairPlan plan, StairKit kit, Vector2 back, float heading, float top, int side, int turn, float across,
        Func<float, float, float>? ground)
    {
        CantileverKit ck = kit.Cantilever!;
        Vector2 dir = Forward(heading), wall = Right(heading) * side;
        float reach = across / 2f + CantileverKit.Strip / 2f;
        float underside = top - kit.FloorThickness; // the filler's underside
        Vector2 m = back + dir * (PieceRun + 0.5f);
        AddStripColumn(plan, ck, m + wall * reach, underside, heading, ground);
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Support, Prefab = ck.Arch, Position = new Vector3(m.x, underside - ck.ArchHeight / 2f, m.y),
            YawDegrees = ArchYaw(ck, heading, side), Cantilevered = true });
        // Turned away from the face (the wedge on the face's side): the strip's block on, 2 m past the top.
        if (turn * side < 0 && BesideSunk(back + dir * (PieceRun + PieceRun / 2f) + wall * reach, top, ground))
        {
            Vector2 b = back + dir * (PieceRun + PieceRun / 2f) + wall * reach;
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Support, Prefab = ck.Beside, Position = new Vector3(b.x, top - 0.5f, b.y),
                YawDegrees = Wrap(heading - 90f), Cantilevered = true });
        }
    }
}
