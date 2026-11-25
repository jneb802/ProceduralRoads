using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One box of space a walker needs clear of rock, standing on a walking surface at <see cref="Surface"/>: a road's
/// clearance at a road point, a bridge deck's, or a staircase's walking box (a level stretch, or a stair stretch's corridor or tread layer). Made only by
/// <see cref="RockClearances"/>, the one owner of what a walker needs clear; the probe judges the rocks in it and the carve
/// takes the chunks in it, both from the same boxes. Nothing over a box is taken: every box is its own roof.</summary>
public readonly struct RockClearance
{
    public readonly Vector3 Centre;
    public readonly Vector3 Half;
    public readonly Quaternion Turn;
    public readonly float Surface;
    /// <summary>A rock here is removed whole, never carved (a bridge's deck or end step).</summary>
    public readonly bool WholeOnly;
    /// <summary>The box is an approach to a location: a rock in a location's location area is the location's own.</summary>
    public readonly bool KeepsLocationRocks;

    internal RockClearance(Vector3 centre, Vector3 half, Quaternion turn, float surface, bool wholeOnly, bool keepsLocationRocks)
    {
        Centre = centre; Half = half; Turn = turn; Surface = surface;
        WholeOnly = wholeOnly; KeepsLocationRocks = keepsLocationRocks;
    }

    /// <summary>The box's top.</summary>
    public float Top => Centre.y + Half.y;
}

/// <summary>
/// The one owner of the space a walker needs clear of rock round a point: every walked road point's clearance
/// (<see cref="Road"/>, at the ground under it: a waded ford's over its riverbed), every
/// bridge's deck and end step's (BridgeStructures.AddClearances, given as they are), and every staircase's walking box (StairLayout.AddClearance, stored with its run). The road-rock pass
/// judges the rocks in these boxes (RoadRockProbe) and the carve takes the chunks in them (RoadRockCarve), from this one list;
/// nothing else decides what rock a road or a staircase clears (a staircase's clear areas keep only vegetation out of it,
/// its cuts are terrain).
/// </summary>
public static class RockClearances
{
    /// <summary>Every clearance within <paramref name="radius"/> of <paramref name="centre"/> (a road point's heading is read from
    /// the road points a little further out, so a point at the edge has its neighbours). <paramref name="ground"/>: the
    /// ground as the players will have it, the one the carve grounds its fragments on (RoadRockCarve.GroundReader: the
    /// loaded terrain where its zone's terrain is written, else the finished ground the writer leaves).
    /// <paramref name="groundMisses"/>: road points skipped where that ground is not known (inside a location whose ground is
    /// not read yet): their rock waits. The one owner of which rocks a road clears or carves (docs/OWNERS.md).</summary>
    public static List<RockClearance> Near(Vector3 centre, float radius, GroundAt ground, out int groundMisses)
    {
        groundMisses = 0;
        var boxes = new List<RockClearance>();
        List<RoadSpatialGrid.RoadPoint> line = RoadSpatialGrid.GetRoadPointsNearPosition(centre, radius + HeadingReach);
        // Only walked points (RoadSpatialGrid.RoadPoint.Walked, the one test of walking space, as vegetation's): road,
        // unpainted road and a waded ford's paint-only points, whose box stands on the riverbed it is waded on. A staircase's
        // cuts and a structure's mounds are not walked: left in the line, one half a metre off a road point turned that
        // point's heading, and so its clearance box, toward it.
        line.RemoveAll(p => !p.Walked);
        var c = new Vector2(centre.x, centre.z);
        foreach (var point in line)
        {
            if ((point.p - c).sqrMagnitude > radius * radius) continue;
            if (!ground(new Vector3(point.p.x, 0f, point.p.y), out float surface)) { groundMisses++; continue; }
            // A road point into a location, or one in any location's footprint, keeps the rocks of a location's area (unchanged by
            // which location a point may change the ground of: RoadLocationProtection.Admits).
            boxes.Add(Road(point, surface, line, RoadRockPolicy.ClearanceHeight, wholeOnly: false,
                keepsLocationRocks: point.intoLocation || RoadLocationProtection.Contains(point.p)));
        }
        // A deck's box (WholeOnly, BridgeStructures.AddClearances) is the deck's own: given as it is, never read as a stair's.
        foreach (var s in StructurePlans.ClearancesNear(centre, radius))
            if (s.WholeOnly) boxes.Add(Of(s, Quaternion.Euler(0f, s.YawDegrees, 0f), s.Centre, s.Half));
            else AddBox(s, boxes);
        return boxes;
    }

    /// <summary>The lowest a walker's box stands over its walking surface <paramref name="surface"/>: a rock reaching no higher
    /// is under the walk (<see cref="RoadRockPolicy.ClearanceLift"/> over it). The one owner of headroom (docs/OWNERS.md).</summary>
    public static float Bottom(float surface) => surface + RoadRockPolicy.ClearanceLift;

    /// <summary>The highest a road's box stands over its walking surface <paramref name="surface"/>
    /// (<see cref="RoadRockPolicy.ClearanceHeight"/> over <see cref="Bottom"/>): a rock standing wholly over it is over the
    /// walker's head. A location's rocks are judged in the way of its approach and landing by the same heights
    /// (<see cref="LocationRock.InTheWay"/>). The one owner of headroom (docs/OWNERS.md).</summary>
    public static float Top(float surface) => Bottom(surface) + RoadRockPolicy.ClearanceHeight;

    /// <summary>The ground under a point, if any answers there.</summary>
    public delegate bool GroundAt(Vector3 at, out float height);

    /// <summary>How much further out than a clearance's reach its headings read road points.</summary>
    public const float HeadingReach = 3f;

    /// <summary>Half a road clearance's length along the road: road points are stored about 1 m apart, so neighbouring
    /// boxes overlap.</summary>
    public const float ClearanceHalfLength = 0.6f;

    /// <summary>A road's clearance at a point: a box turned to the road's heading, as wide as the solid paint,
    /// from <see cref="RoadRockPolicy.ClearanceLift"/> over the surface <paramref name="height"/> tall, its own roof: a rock
    /// arching over the road over that stays (the carve took what hung over it up to 4 m). It was a capsule as wide as the road; a capsule is never
    /// shorter than its width, so on a 4 m road it was a 4 m ball, reaching 4 m up and leaving arches. The one owner of which rocks a road clears or carves (docs/OWNERS.md).</summary>
    internal static RockClearance Road(RoadSpatialGrid.RoadPoint point, float surface, List<RoadSpatialGrid.RoadPoint> line, float height,
        bool wholeOnly, bool keepsLocationRocks)
    {
        var dir = RoadRockPolicy.Heading(point, line);
        var half = new Vector3(RoadProfile.PaintEdge(point.w), height * 0.5f, ClearanceHalfLength);
        return new RockClearance(new Vector3(point.p.x, Bottom(surface) + half.y, point.p.y), half,
            Quaternion.Euler(0f, Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg, 0f), surface, wholeOnly, keepsLocationRocks);
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
        float bottom = s.Centre.y - s.Half.y;
        // A stair's stretch rises: its middle walk stands under its highest.
        float highest = StairLayout.HighestWalk(s.Centre, s.Half, s.Surface);
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
        => new RockClearance(centre, half, turn, s.Surface, s.WholeOnly, s.KeepsLocationRocks);
}
