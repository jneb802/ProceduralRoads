using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One box of space a walker needs clear of rock, standing on a walking surface at <see cref="Surface"/>: a road's
/// clearance at a road point, a bridge deck's, or a staircase's walking box (a level stretch, or a stair stretch's corridor or tread layer). Made only by
/// <see cref="RockClearances"/>, the one owner of what a walker needs clear; the probe judges the rocks in it and the carve
/// takes the chunks in it, both from the same boxes. Nothing over a box is taken: every box is its own roof.
/// <see cref="Point"/>: the road point it stands at (<see cref="HasPoint"/>), for the probe's partial-rock rule.</summary>
public readonly struct RockClearance
{
    public readonly Vector3 Centre;
    public readonly Vector3 Half;
    public readonly Quaternion Turn;
    public readonly float Surface;
    public readonly bool HasPoint;
    public readonly RoadSpatialGrid.RoadPoint Point;
    /// <summary>A rock here is removed whole, never carved (a bridge deck).</summary>
    public readonly bool WholeOnly;
    /// <summary>The box is an approach to a place: a rock in a place's site area is the place's own.</summary>
    public readonly bool KeepsSiteRocks;

    internal RockClearance(Vector3 centre, Vector3 half, Quaternion turn, float surface, bool hasPoint,
        RoadSpatialGrid.RoadPoint point, bool wholeOnly, bool keepsSiteRocks)
    {
        Centre = centre; Half = half; Turn = turn; Surface = surface;
        HasPoint = hasPoint; Point = point; WholeOnly = wholeOnly; KeepsSiteRocks = keepsSiteRocks;
    }

    /// <summary>The box's top.</summary>
    public float Top => Centre.y + Half.y;
}

/// <summary>
/// The one owner of the space a walker needs clear of rock round a point: every road point's clearance
/// (<see cref="Road"/>, at the ground under it, <see cref="RoadRockPolicy.ClearanceSurface"/>), every
/// bridge deck's, and every staircase's walking box (StairLayout.AddClearance, stored with its run). The road-rock pass
/// judges the rocks in these boxes (RoadRockProbe) and the carve takes the chunks in them (RoadRockCarve), from this one list;
/// nothing else decides what rock a road or a staircase clears (a staircase's clear areas keep only vegetation out of it,
/// its cuts are terrain).
/// </summary>
public static class RockClearances
{
    /// <summary>Every clearance within <paramref name="radius"/> of <paramref name="centre"/>; <paramref name="line"/> is
    /// the road points the headings were read from (a little further out, so a point at the edge has its neighbours).
    /// <paramref name="generating"/>: a zone generated for a remote peer, whose ground may not be there to read (a road
    /// point is then probed at its own stored height). <paramref name="groundMisses"/>: road points skipped for want of
    /// ground; <paramref name="roadHeightPoints"/>: those probed at the road's own height. <paramref name="ground"/>: the
    /// finished ground (the game's ZoneSystem.GetGroundHeight).</summary>
    public static List<RockClearance> Near(Vector3 centre, float radius, bool generating, GroundAt ground, out List<RoadSpatialGrid.RoadPoint> line,
        out int groundMisses, out int roadHeightPoints)
    {
        groundMisses = 0; roadHeightPoints = 0;
        var boxes = new List<RockClearance>();
        line = RoadSpatialGrid.GetRoadPointsNearPosition(centre, radius + HeadingReach);
        // A staircase's cuts are not road: left in the line, one half a metre off a road point turned that point's
        // heading, and so its clearance box, toward it.
        line.RemoveAll(p => p.cutOnly);
        var c = new Vector2(centre.x, centre.z);
        foreach (var point in line)
        {
            if (!point.IsRoad || (point.p - c).sqrMagnitude > radius * radius) continue;
            bool read = ground(new Vector3(point.p.x, 0f, point.p.y), out float surface);
            if (!RoadRockPolicy.ClearanceSurface(read, surface, point.h, generating, out surface)) { groundMisses++; continue; }
            if (!read) roadHeightPoints++;
            boxes.Add(Road(point, surface, line, RoadRockPolicy.ClearanceHeight, wholeOnly: false, keepsSiteRocks: point.intoSite));
        }
        foreach (var s in StructurePlans.ClearancesNear(centre, radius))
            if (s.IsBox) AddBox(s, boxes);
            else boxes.Add(Road(s.Point, s.Surface, s.Line!, s.Height, s.WholeOnly, keepsSiteRocks: false));
        return boxes;
    }

    /// <summary>The lowest a walker's box stands over its walking surface <paramref name="surface"/>: a rock reaching no higher
    /// is under the walk (<see cref="RoadRockPolicy.ClearanceLift"/> over it).</summary>
    public static float Bottom(float surface) => surface + RoadRockPolicy.ClearanceLift;

    /// <summary>The highest a road's box stands over its walking surface <paramref name="surface"/>
    /// (<see cref="RoadRockPolicy.ClearanceHeight"/> over <see cref="Bottom"/>): a rock standing wholly over it is over the
    /// walker's head. A place's rocks are judged in the way of its approach and landing by the same heights
    /// (<see cref="SiteRock.InTheWay"/>).</summary>
    public static float Top(float surface) => Bottom(surface) + RoadRockPolicy.ClearanceHeight;

    /// <summary>The ground under a point, if any answers there.</summary>
    public delegate bool GroundAt(Vector3 at, out float height);

    /// <summary>How much further out than a clearance's reach its headings read road points.</summary>
    public const float HeadingReach = 3f;

    /// <summary>Half a road clearance's length along the road: road points are stored about 1 m apart, so neighbouring
    /// boxes overlap.</summary>
    public const float ClearanceHalfLength = 0.6f;

    /// <summary>A road's (or a deck's) clearance at a point: a box turned to the road's heading, as wide as the solid paint,
    /// from <see cref="RoadRockPolicy.ClearanceLift"/> over the surface <paramref name="height"/> tall, its own roof: a rock
    /// arching over the road over that stays (the carve took what hung over it up to 4 m). It was a capsule as wide as the road; a capsule is never
    /// shorter than its width, so on a 4 m road it was a 4 m ball, reaching 4 m up and leaving arches.</summary>
    internal static RockClearance Road(RoadSpatialGrid.RoadPoint point, float surface, List<RoadSpatialGrid.RoadPoint> line, float height,
        bool wholeOnly, bool keepsSiteRocks)
    {
        var dir = RoadRockPolicy.Heading(point, line);
        var half = new Vector3(RoadProfile.PaintEdge(point.w), height * 0.5f, ClearanceHalfLength);
        return new RockClearance(new Vector3(point.p.x, Bottom(surface) + half.y, point.p.y), half,
            Quaternion.Euler(0f, Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg, 0f), surface, true, point, wholeOnly, keepsSiteRocks);
    }

    /// <summary>
    /// A staircase's walking box as it was stored (StairLayout.AddClearance), as the rock in it that obstructs the walk: a
    /// chunk over the walk above the walker's headroom stays, standing on (or leaning on) the rock beside the way. A level stretch (a floor, a landing, a turn's filler, walked across as well as along) is clear in full.
    /// A stretch of a stair, walked along it, leaves a chunk that only clips its edges: it is two boxes, the walker's
    /// <see cref="Corridor"/> (the stretch less a player capsule's radius, <see cref="RoadRockPolicy.PlayerWidth"/> / 2, at
    /// either side, but never narrower than the way, <see cref="RoadRockPolicy.MinOpening"/>; its full height) and its <see cref="TreadLayer"/> (its full width, from its bottom to
    /// <see cref="StairLayout.StepUp"/> over its highest walk), and a chunk is taken only where it reaches into one of them:
    /// into the corridor, or down onto the treads (in the tread layer, as in a road's box, the carve takes the way of
    /// <see cref="RoadRockPolicy.MinOpening"/> across it, <see cref="RoadRockPolicy.WayStrips"/>). Mountain boulders fracture into chunks 10 to 14 m across, taken whole, and
    /// one touching the box anywhere cut a band beside the flight.
    /// </summary>
    internal static void AddBox(StructureClearance s, List<RockClearance> boxes)
    {
        var turn = Quaternion.Euler(0f, s.YawDegrees, 0f);
        float bottom = s.Centre.y - s.Half.y, top = s.Centre.y + s.Half.y;
        // A stair's stretch rises: its middle walk stands under its highest.
        float highest = StairLayout.HighestWalk(top);
        if (highest <= s.Surface + RisesOver || Corridor(s.Half.x) >= s.Half.x)
        {
            boxes.Add(Of(s, turn, s.Centre, s.Half));
            return;
        }
        boxes.Add(Of(s, turn, s.Centre, new Vector3(Corridor(s.Half.x), s.Half.y, s.Half.z)));
        float layer = (TreadLayer(highest) - bottom) * 0.5f;
        boxes.Add(Of(s, turn, new Vector3(s.Centre.x, bottom + layer, s.Centre.z), new Vector3(s.Half.x, layer, s.Half.z)));
    }

    /// <summary>How much a stretch's highest walk must stand over its middle one for it to be a stair's (a stair's stretch
    /// rises StairLayout.PieceRise / StairLayout.ClearanceStretches, half of that over its middle).</summary>
    private const float RisesOver = 0.01f;

    /// <summary>Half the walker's corridor along a stair's stretch <paramref name="half"/> wide either side of its middle:
    /// less a player capsule's radius at either side, but never narrower than the way a carve leaves
    /// (<see cref="RoadRockPolicy.MinOpening"/>, the capsule and about a quarter metre either side, so walking it never
    /// catches), nor wider than the stretch. The carve clears the corridor itself, so the capsule's whole body must fit in
    /// it, not only its centre. On a 2 m stair 1.5 m: rock left at its edges (0.25 m each side), over the treads, never
    /// rubs a player walking the way.</summary>
    internal static float Corridor(float half) =>
        Mathf.Min(half, Mathf.Max(RoadRockPolicy.MinOpening * 0.5f, half - RoadRockPolicy.PlayerWidth * 0.5f));

    /// <summary>The top of a stair's tread layer over its stretch's highest walk <paramref name="highest"/>: rock up to
    /// <see cref="StairLayout.StepUp"/> over the treads, anywhere across the way, is rock through a tread or a lump on it the
    /// walker cannot step over.</summary>
    internal static float TreadLayer(float highest) => highest + StairLayout.StepUp;

    private static RockClearance Of(StructureClearance s, Quaternion turn, Vector3 centre, Vector3 half)
        => new RockClearance(centre, half, turn, s.Surface, false, default, s.WholeOnly, s.KeepsSiteRocks);
}
