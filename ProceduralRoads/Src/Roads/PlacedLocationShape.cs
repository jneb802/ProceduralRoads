using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A rock standing in a location's footprint: the location's own, read off its saved
/// objects (PlacedLocationReader). Its colliders' box seen from above, turned with the rock
/// about the vertical (<see cref="Yaw"/>, degrees, as Unity turns): a circle out to the box's
/// corner made a long rock half as wide again as it is (rock1_mountain 17.5 m).</summary>
public readonly struct LocationRock
{
    /// <summary>A rock this big or more across its box (corner to corner) is a large one:
    /// kept, and hugged as ground is. Smaller ones may be cleared out of an approach's way.</summary>
    public const float LargeRock = 5f;

    /// <summary>The box's middle.</summary>
    public readonly Vector2 At;
    public readonly float HalfX, HalfZ, Yaw;
    /// <summary>The box's lowest and highest points.</summary>
    public readonly float MinY, MaxY;
    public readonly string Name;
    /// <summary>The rock's real top, where known: the highest point of its colliders over each
    /// metre of a world grid from (<see cref="TopX0"/>, <see cref="TopZ0"/>), <see cref="TopW"/>
    /// cells across, NaN where nothing of it stands. Its box, from its colliders' corners,
    /// overstates a rough or tilted rock.</summary>
    public readonly float[]? Top;
    public readonly int TopX0, TopZ0, TopW;
    /// <summary>Whether it is one of the location's own built pieces (a part with a WearNTear: walls, floors, stairs, railings,
    /// PlacedLocationReader.LocationRocks), not a boulder: kept off by the walker's own body (<see cref="PlacedLocationShape.Refusal"/>).</summary>
    public readonly bool Built;
    public LocationRock(Vector2 at, float halfX, float halfZ, float yaw, float minY, float maxY, string name = "rock",
        float[]? top = null, int topX0 = 0, int topZ0 = 0, int topW = 0, bool built = false)
    {
        At = at; HalfX = halfX; HalfZ = halfZ; Yaw = yaw; MinY = minY; MaxY = maxY; Name = name;
        Top = top; TopX0 = topX0; TopZ0 = topZ0; TopW = topW; Built = built;
    }

    /// <summary>
    /// Whether the rock is in the way of something at <paramref name="p"/> that keeps
    /// <paramref name="margin"/> off it, walked on <paramref name="ground"/> (NaN: any height);
    /// <paramref name="d"/>, how near it comes. Only where it reaches into a walker's box there
    /// (<see cref="RockClearances.Bottom"/> to <see cref="RockClearances.Top"/> over the ground, the
    /// one owner of what a walker needs clear): by its real top where known, else by its box.
    /// </summary>
    /// <param name="walkedFrom">The level a walker steps onto the rock from (NaN: none, every cell of it counts): a cell of its
    /// real top within a step of that level (<see cref="StairLayout.Walkable"/>, the one owner of a walker's step) is walked onto,
    /// not in the way. A rock whose top is not known has no such cells.</param>
    public bool InTheWay(Vector2 p, float margin, float ground, out float d, float walkedFrom = float.NaN)
    {
        d = float.PositiveInfinity;
        float below = float.IsNaN(ground) ? float.NaN : RockClearances.Bottom(ground);
        float above = float.IsNaN(ground) ? float.NaN : RockClearances.Top(ground);
        if (Top == null)
        {
            d = Distance(p);
            return d < margin && (float.IsNaN(ground) || Spans(below, above));
        }
        if (!float.IsNaN(ground) && MinY > above) return false;
        int topH = Top.Length / Math.Max(1, TopW), r = Mathf.CeilToInt(margin) + 1;
        int cx = Mathf.RoundToInt(p.x), cz = Mathf.RoundToInt(p.y);
        for (int z = cz - r; z <= cz + r; z++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                int i = x - TopX0, k = z - TopZ0;
                if (i < 0 || k < 0 || i >= TopW || k >= topH) continue;
                float top = Top[k * TopW + i];
                if (float.IsNaN(top) || (!float.IsNaN(ground) && top < below)) continue;
                if (!float.IsNaN(walkedFrom) && StairLayout.Walkable(top, walkedFrom)) continue;
                float dist = Mathf.Max(0f, Vector2.Distance(p, new Vector2(x, z)) - 0.5f);
                if (dist < d) d = dist;
            }
        return d < margin;
    }
    /// <summary>A square rock <paramref name="half"/> each way from its middle, of any height.</summary>
    public LocationRock(Vector2 at, float half, string name = "rock")
        : this(at, half, half, 0f, float.NegativeInfinity, float.PositiveInfinity, name) { }

    public bool Large
    {
        get
        {
            float height = float.IsInfinity(MaxY - MinY) ? 0f : MaxY - MinY;
            return Mathf.Sqrt(4f * HalfX * HalfX + 4f * HalfZ * HalfZ + height * height) >= LargeRock;
        }
    }

    /// <summary>Whether it stands anywhere between <paramref name="below"/> and <paramref name="above"/>.</summary>
    public bool Spans(float below, float above) => MaxY >= below && MinY <= above;

    /// <summary>How far <paramref name="p"/> is outside the box (0 inside it).</summary>
    public float Distance(Vector2 p)
    {
        Vector2 local = Local(p - At, Yaw);
        float ex = Mathf.Max(FloatBits.Abs(local.x) - HalfX, 0f), ez = Mathf.Max(FloatBits.Abs(local.y) - HalfZ, 0f);
        return Mathf.Sqrt(ex * ex + ez * ez);
    }

    /// <summary>A world offset in the frame of something turned <paramref name="yaw"/> degrees
    /// about the vertical (Unity: local (x, z) lies at (x cos + z sin, z cos - x sin)).</summary>
    public static Vector2 Local(Vector2 world, float yaw)
    {
        float r = yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(world.x * c - world.y * s, world.x * s + world.y * c);
    }

    /// <summary>The inverse of <see cref="Local"/>.</summary>
    public static Vector2 World(Vector2 local, float yaw)
    {
        float r = yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(local.x * c + local.y * s, local.y * c - local.x * s);
    }

    /// <summary>Whether it stands round the mouth of a location at <paramref name="centre"/> whose exterior radius is
    /// <paramref name="rockRadius"/> (none without one): its box's middle inside that radius.</summary>
    public bool RoundItsMouth(Vector2 centre, float rockRadius) => rockRadius > 0f && Vector2.Distance(At, centre) <= rockRadius;

    /// <summary>Whether a location's rock is kept on an approach to the location (the one owner of the rule; each caller measures
    /// the rock its own way): a <paramref name="large"/> one (<see cref="LargeRock"/> or more across), any at a location that
    /// keeps every rock (<paramref name="keepsEveryRock"/>, <see cref="KeepsEveryRock"/>), or any
    /// <paramref name="roundItsMouth"/> (inside its exterior radius). <paramref name="why"/>: the first of these that holds,
    /// in that order ("" when none does). The one owner of which rocks a location keeps (docs/OWNERS.md).</summary>
    public static bool Kept(bool large, bool keepsEveryRock, bool roundItsMouth, out string why)
    {
        why = large ? "large" : keepsEveryRock ? "a location keeping every rock" : roundItsMouth ? "round its mouth" : "";
        return large || keepsEveryRock || roundItsMouth;
    }

    /// <summary><see cref="Kept(bool, bool, bool, out string)"/>, without the reason.</summary>
    public static bool Kept(bool large, bool keepsEveryRock, bool roundItsMouth) => Kept(large, keepsEveryRock, roundItsMouth, out _);

    /// <summary>
    /// Whether this rock is kept on an approach to the location at <paramref name="centre"/> (<see cref="Kept(bool, bool, bool, out string)"/>):
    /// the one question planning (the shape, the reader) and the rock clearing's probe both ask, of the same box: its oriented
    /// size (<see cref="Large"/>) and its middle (<see cref="RoundItsMouth"/>).
    /// The one owner of which rocks a location keeps (docs/OWNERS.md).
    /// </summary>
    public bool KeptAt(Vector2 centre, float rockRadius, bool keepsEveryRock, out string why) =>
        Kept(Large, keepsEveryRock, RoundItsMouth(centre, rockRadius), out why);

    /// <summary>A rock's box seen from above: the corners of its colliders' box (<paramref name="local"/>, in its root's frame),
    /// scaled and turned as saved, measured along and across its turn about the vertical.</summary>
    public static LocationRock Of(Vector3 position, Quaternion rotation, Vector3 scale, Bounds local, string name, bool built = false)
    {
        var offsets = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = Corner(local, i);
            offsets[i] = rotation * new Vector3(corner.x * scale.x, corner.y * scale.y, corner.z * scale.z);
        }
        return Measured(position, offsets, rotation.eulerAngles.y, name, built);
    }

    /// <summary>
    /// One of a Location prefab's own static rocks (a solid collider under no ZNetView, PlacedLocationReader.LocationRocks) as the
    /// game stands it: the Location's root <paramref name="root"/> plus its turn (<paramref name="turn"/> degrees about the
    /// vertical) times the collider's pose in the prefab. <paramref name="corners"/>: its colliders' box's eight corners
    /// (<see cref="Corner"/>) through that pose, in the prefab's root frame; <paramref name="forward"/>: the pose's +z there, the
    /// heading its box is measured along (as a saved rock's turn about the vertical, <see cref="Of"/>). The pose is read whole, its
    /// parents' scales included, so a stone scaled under a scaled parent stands as the game stands it.
    /// </summary>
    public static LocationRock OfPart(Vector3 root, float turn, Vector3 forward, IReadOnlyList<Vector3> corners, string name)
    {
        Quaternion q = Quaternion.Euler(0f, turn, 0f);
        float yaw = turn + Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        yaw = (yaw % 360f + 360f) % 360f;
        var offsets = new Vector3[corners.Count];
        for (int i = 0; i < corners.Count; i++) offsets[i] = q * corners[i];
        return Measured(root, offsets, yaw, name, built: false);
    }

    /// <summary>Corner <paramref name="i"/> (0 to 7) of <paramref name="local"/>.</summary>
    public static Vector3 Corner(Bounds local, int i) =>
        new(local.center.x + ((i & 1) == 0 ? -1f : 1f) * local.extents.x,
            local.center.y + ((i & 2) == 0 ? -1f : 1f) * local.extents.y, local.center.z + ((i & 4) == 0 ? -1f : 1f) * local.extents.z);

    /// <summary>The box seen from above of the points <paramref name="offsets"/> from <paramref name="position"/>, measured along
    /// and across <paramref name="yaw"/>.</summary>
    private static LocationRock Measured(Vector3 position, IReadOnlyList<Vector3> offsets, float yaw, string name, bool built)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        foreach (Vector3 w in offsets)
        {
            Vector2 q = Local(new Vector2(w.x, w.z), yaw);
            minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x);
            minZ = Mathf.Min(minZ, q.y); maxZ = Mathf.Max(maxZ, q.y);
            minY = Mathf.Min(minY, position.y + w.y); maxY = Mathf.Max(maxY, position.y + w.y);
        }
        Vector2 middle = new Vector2(position.x, position.z) + World(new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f), yaw);
        return new LocationRock(middle, (maxX - minX) * 0.5f, (maxZ - minZ) * 0.5f, yaw, minY, maxY, name, built: built);
    }

    /// <summary>
    /// Whether a Location's own built piece named <paramref name="name"/> is one a road or staircase clears out of its way: a Dvergr
    /// stake wall's stakes (dvergrprops_wood_stakewall and the like). The one owner of which Location pieces a way may clear
    /// (docs/OWNERS.md). Planning does not keep off them (PlacedLocationShape.Refusal), and the rock pass removes, whole, every one a
    /// walk's clearance box meets (RoadRockHit.Judged, RoadRockPolicy.HowTaken), the server bake and a client's pass alike, so the
    /// bake takes exactly the stakes the generated network's walks pass through: where a road or staircase goes through the
    /// spikes, the spikes go. Every other piece is kept off by the walker's own body.
    /// </summary>
    public static bool ClearedForAWay(string? name) => LocationRules.ClearedForAWay(name);

    /// <summary>Whether a Location's own part named <paramref name="name"/> (a saved object's prefab, a static collider's object
    /// or mesh) is one of its rocks: the name says rock (Hildir's cave's ice_rock1, a crypt's stones on their Rock_3 mesh). The
    /// one owner of the rule, for its saved rocks and its prefab's static ones (PlacedLocationReader.LocationRocks).</summary>
    public static bool OwnRockName(string? name) => name != null && name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The rock's real top over the metre of its grid nearest <paramref name="p"/> (<see cref="Top"/>); NaN where nothing of
    /// it stands there or its top is not known.</summary>
    public float TopOver(Vector2 p)
    {
        if (Top == null) return float.NaN;
        int i = Mathf.RoundToInt(p.x) - TopX0, k = Mathf.RoundToInt(p.y) - TopZ0, h = Top.Length / Math.Max(1, TopW);
        return i < 0 || k < 0 || i >= TopW || k >= h ? float.NaN : Top[k * TopW + i];
    }

    /// <summary>Whether the straight line from <paramref name="a"/> to <paramref name="b"/> comes onto the box seen from above.</summary>
    public bool Crosses(Vector2 a, Vector2 b)
    {
        Vector2 p = Local(a - At, Yaw), d = Local(b - At, Yaw) - p;
        float t0 = 0f, t1 = 1f;
        float[] from = { p.x, p.y }, along = { d.x, d.y }, half = { HalfX, HalfZ };
        for (int k = 0; k < 2; k++)
        {
            if (FloatBits.Abs(along[k]) < 1e-9f)
            {
                if (FloatBits.Abs(from[k]) > half[k]) return false;
                continue;
            }
            float u = (-half[k] - from[k]) / along[k], v = (half[k] - from[k]) / along[k];
            if (u > v) (u, v) = (v, u);
            t0 = Mathf.Max(t0, u); t1 = Mathf.Min(t1, v);
            if (t0 > t1) return false;
        }
        return true;
    }

    /// <summary>A location none of whose rocks an approach may clear, small ones included: Hildir's
    /// cave, whose boulder blocking the entrance is not disturbed (the landing near by is still
    /// reached).</summary>
    public static bool KeepsEveryRock(string locationName) => locationName.StartsWith("Hildir", StringComparison.Ordinal);
}

/// <summary>One entrance of a location (<see cref="PlacedLocationShape.DoorLandings"/>), in its prefab's frame: the landing its road
/// ends on (<see cref="At"/>), the heading a player walks out along there (<see cref="Facing"/>, degrees clockwise from the prefab's
/// +z), and how near the location's own built pieces the way in may come (<see cref="Clearance"/>, the radius of open ground round
/// the landing it was measured with).</summary>
public readonly struct LocationEntrance
{
    public readonly Vector2 At;
    public readonly float Facing, Clearance;
    public LocationEntrance(Vector2 at, float facing, float clearance) { At = at; Facing = facing; Clearance = clearance; }
}

/// <summary>A location's entrances (<see cref="PlacedLocationShape.DoorLandings"/>), any number, and its fallback: the door landing
/// it had before its entrances were measured (<see cref="Fallback"/>), or, null, the landing or ring it is reached at without
/// one.</summary>
public sealed class LocationEntrances
{
    public readonly IReadOnlyList<LocationEntrance> Entrances;
    public readonly Vector2? Fallback;
    public LocationEntrances(IReadOnlyList<LocationEntrance> entrances, Vector2? fallback = null) { Entrances = entrances; Fallback = fallback; }
}

/// <summary>
/// Where an approach to a placed location may go, for a road onto its landing
/// (<see cref="RoadLocationEntry"/>) and a staircase to it
/// (<see cref="RoadStairRuns.PlanPlacedLocationApproach"/>) alike: the landing in front of
/// its floor, the entrance pit (the floor and its smoothing), the rock round it (its
/// exterior radius, but for a cone in front of its floor) and its own boulders. Pure.
/// </summary>
public sealed class PlacedLocationShape
{
    public readonly Vector2 Centre, FlatCentre, Front, Landing;
    public readonly float Pit, KeepOut, RockRadius;
    /// <summary>Whether the location's root height is known (else no landing is held to the location's level, <see cref="Landings"/>).</summary>
    public readonly bool RootKnown;
    /// <summary>How far out from the flat's middle its outer edge is: the pit's edge for a sunk
    /// floor, the flat's own edge for one level with the location. Landings nearest it come first.</summary>
    public readonly float Edge;
    /// <summary>A flat this far or more under the location's root is a floor dug behind the mouth (a frost
    /// cave's, 4.09 m down): a pit to keep out of. Less, it is the ground in front of the mouth (a
    /// BearCave's): the landing itself (a road that wraps around low ends way below
    /// the cave entrance).</summary>
    public const float SunkDepth = 1.5f;
    /// <summary>How far the ground a step outside a landing's edge may rise over it before the edge meets a wall, not a slope
    /// (<see cref="Aims"/>). A landing itself is held to the location's arrival height (<see cref="Landings"/>).</summary>
    public const float LandingRise = 2f;
    public readonly IReadOnlyList<LocationRock> Rocks;
    /// <summary>The boulders standing inside its exterior radius: its body, round its mouth.</summary>
    public readonly IReadOnlyList<LocationRock> Body;
    /// <summary>The boulders of its <see cref="Body"/> that are its entrance's own stairs (a crypt's stair slabs, its prefab's static
    /// rock): those the walk in from its measured landing (<see cref="Door"/>, back along <see cref="Front"/>) stands on within the
    /// entrance's clearance (<see cref="DoorClearance"/>), their real top there a step (<see cref="StairLayout.Walkable"/>) from the
    /// way in's level (<see cref="_doorLevel"/>, the landing's: the way in is held level with it). On the way in to its door
    /// (<see cref="InDoorCorridor"/>) the cells of their top a step from that level, or under the walk, are walked onto
    /// (<see cref="Refusal"/>); every other part of them is kept off by the walker's own reach (its rockMargin), as a kept rock is.
    /// A rock whose top is not known is never one. Everywhere else, and every other boulder there, keeps its clearance.</summary>
    public readonly IReadOnlyList<LocationRock> DoorStairs;
    private readonly bool[] _doorStair;
    /// <summary>The way in's level at its door: the ground at its landing (<see cref="Door"/>); NaN without one or its ground.</summary>
    private readonly float _doorLevel = float.NaN;
    private readonly float _cosCone;

    private readonly Func<float, float, float>? _ground;

    /// <summary>The location's own door landing, where its stairs meet the ground (<see cref="DoorLandings"/>), if it has one: the
    /// only landing aimed at.</summary>
    public readonly Vector2? Door;

    /// <summary>Which of the location's entrances (<see cref="DoorLandings"/>) this shape's <see cref="Door"/> is; -1 for its fallback
    /// (<see cref="LocationEntrances.Fallback"/>) or a location with none.</summary>
    public readonly int Entrance;

    /// <summary>How near the location's own built pieces the way in to its entrance may come (<see cref="LocationEntrance.Clearance"/>,
    /// <see cref="Refusal"/>); NaN for a door landing with none measured (its built pieces are not kept off in its corridor).</summary>
    public readonly float DoorClearance;

    /// <summary>Whether the location is entered only from its front, its back blocked (<see cref="LocationRules.FrontOnly"/>): network at its
    /// ring reaches it only on its entrance side (<see cref="OnEntranceSide"/>).</summary>
    public readonly bool EnteredFromFront;

    /// <summary>Whether the location is come to from every side (<see cref="OnLevelGround"/>: a location that levels nothing and is
    /// landed on the level ground its smoothing leaves beside it): no front cone, no keep-out round its exterior radius, and
    /// no way led round behind it; its own pieces and boulders (<see cref="Body"/>) are still kept clear of.</summary>
    public readonly bool AllRound;

    /// <summary>Whether the location's rocks and built pieces were read off the Location as the game placed it (<see cref="Body"/>, a
    /// Location placed first): they, not the exterior radius, are what an approach keeps off (<see cref="Refusal"/>). A shape made
    /// without them (a Location not placed first) keeps the radius, which stands in for the mountain a cave is cut into.</summary>
    public readonly bool BodyKnown;

    /// <summary>Whether its way in must come from its entrance side near the landing (<see cref="OnTheWayIn"/>: within LandingReach, outside its door; the name is older than the rule, not a lane): a door landing at a cave cut into rock
    /// (<see cref="LocationRules.CutIntoRock"/>) or at a Location with a large boulder in its body (<see cref="LocationRock.Large"/>, round its
    /// mouth: the rock a cave is cut into). From beside the mouth, a
    /// way in from the side ran under the mouth's overhang to the landing, which stands over a walker's head and so is no
    /// boulder in the way (<see cref="LocationRock.InTheWay"/>).</summary>
    public readonly bool FrontLaneOnly;

    /// <summary>The landing found on the location's level ground (<see cref="OnLevelGround"/>), if it was: its landing.</summary>
    private readonly Vector2? _found;

    /// <summary>The location's prefab name, as its refusals name it (<see cref="Refusal"/>); "the location" where not known.</summary>
    public readonly string Name;

    /// <summary>Whether this target is the Location's levelled ground as a whole (<see cref="Ends"/>: anywhere on its landing region's
    /// lip, nearest the road's end first; its landings looked for all round, <see cref="Landings"/>), not one landing: a Location's
    /// fallback where its ground and root are known (PlacedLocations.PrepareOne).</summary>
    public readonly bool LevelledArea;

    private PlacedLocationShape(Vector2 centre, Vector2 flatCentre, Vector2 front, float pit, float edge, bool rootKnown, float rockRadius,
        IReadOnlyList<LocationRock> rocks, Func<float, float, float>? ground, bool keepSmallRocks, Vector2? door = null, bool enteredFromFront = false,
        bool allRound = false, Vector2? found = null, string? prefab = null, int entrance = -1, float doorClearance = float.NaN, bool bodyKnown = false,
        bool levelledArea = false)
    {
        Name = prefab ?? "the location";
        LevelledArea = levelledArea && ground != null && rootKnown;
        BodyKnown = bodyKnown;
        Door = door;
        Entrance = entrance;
        DoorClearance = doorClearance;
        EnteredFromFront = enteredFromFront;
        AllRound = allRound;
        _found = found;
        Centre = centre; FlatCentre = flatCentre; Front = front; Pit = pit; Edge = edge; RootKnown = rootKnown; RockRadius = rockRadius; _ground = ground;
        // A boulder standing inside the location's exterior radius is its body, round its mouth:
        // never touched, earthwork included, where it stands at the walking level (seen
        // from above, the box of a frost cave's rock2_mountain 0.9 m from its centre covered
        // the mouth). The large boulders round it are hugged as ground is (the road and stairs hug the
        // larger boulders much like terrain); the small ones are cleared out of the way, but at a location whose every rock
        // is kept (Hildir's cave: a boulder blocking the entrance is not disturbed).
        // One boulder whose box covers the landing itself is the mound the mouth is cut into: the
        // landing in front of the mouth is clear, and its box overstates it there. With its real top known it
        // stays: where it stands is then known.
        KeepOut = rockRadius + RockMargin;
        Landing = LandingFor(0f);
        var kept = new List<LocationRock>();
        var body = new List<LocationRock>();
        foreach (LocationRock rock in rocks)
        {
            // Its own built pieces are its body wherever they stand (a Dvergr entrance's railed ring and stairs reach past its
            // exterior radius's front cone), whether or not its box covers the landing: a built piece's box is tight, so leaving it
            // out would let a road keep no gap from it. A landing on a floor or under a roof stays clear all the same
            // (LocationRock.InTheWay reads only what reaches into the walker's box).
            // A Location's stakes (LocationRock.ClearedForAWay) are not: a way goes through them and the rock pass removes those it
            // meets, so nothing of planning keeps off them (its keeps-off test, the ground held under its pieces).
            if (rock.Built) { if (!LocationRock.ClearedForAWay(rock.Name)) body.Add(rock); }
            else if (rock.RoundItsMouth(centre, rockRadius))
            {
                // Come to from every side, every piece of its body stands: the landing is found clear of them.
                if (rock.Top != null || allRound || rock.Distance(Landing) > 0f) body.Add(rock);
            }
            // The rest of its kept rocks (those round its mouth are its body, above).
            else if (LocationRock.Kept(rock.Large, keepSmallRocks, roundItsMouth: false)) kept.Add(rock);
        }
        Rocks = kept;
        Body = body;
        // Its entrance's own stairs of rock (a crypt's stair slabs): each boulder of its body that the walk from its measured landing
        // back in toward its door stands on within the entrance's clearance (DoorClearance), the open ground the landing was
        // measured to have round it, so the stairs it was walked down end within it (StoodOnAtTheDoor).
        var stairs = new List<LocationRock>();
        _doorStair = new bool[body.Count];
        if (door is Vector2 foot && !float.IsNaN(doorClearance) && ground != null)
        {
            _doorLevel = ground(foot.x, foot.y);
            for (int i = 0; i < body.Count; i++)
                if (!body[i].Built && StoodOnAtTheDoor(body[i], foot, -front, doorClearance, _doorLevel)) { _doorStair[i] = true; stairs.Add(body[i]); }
        }
        DoorStairs = stairs;
        FrontLaneOnly = door != null && LocationRules.For(prefab).CutIntoRock;
        foreach (LocationRock rock in rocks)
            if (!rock.Built && rock.Large && rock.RoundItsMouth(centre, rockRadius)) FrontLaneOnly = door != null;
        _cosCone = Mathf.Cos(FrontCone * Mathf.Deg2Rad);
    }

    /// <summary>The shape of a location turned to <paramref name="yaw"/>, levelled by <paramref name="flat"/>.
    /// A boulder is in the way only where it reaches into a walker's box over <paramref name="ground"/>
    /// (<see cref="LocationRock.InTheWay"/>; any height without it):
    /// seen from above, a rock2_mountain 12 m down the slope below a frost cave's ledge covered
    /// the ledge.</summary>
    /// <paramref name="root"/>: the location's root height (NaN: unknown, no landing is held to the
    /// entrance's level).</summary>
    /// <paramref name="levelledArea"/>: the shape is its levelled ground as a whole (<see cref="LevelledArea"/>).
    public static PlacedLocationShape Of(Vector2 centre, float yaw, LevelOp flat, float rockRadius, IReadOnlyList<LocationRock>? rocks,
        Func<float, float, float>? ground = null, bool keepSmallRocks = false, float root = float.NaN, string? prefab = null, int entrance = -1,
        bool bodyKnown = false, bool levelledArea = false)
    {
        bool sunk = flat.Height <= -SunkDepth;
        float pit = sunk ? Mathf.Max(flat.Radius, flat.SmoothRadius) : 0f;
        LocationEntrances? entrances = EntrancesOf(prefab);
        // Shaped for one of its entrances: it faces out along that entrance's facing, turned with the location.
        if (entrances != null && entrance >= 0 && entrance < entrances.Entrances.Count)
        {
            LocationEntrance at = entrances.Entrances[entrance];
            return new(centre, centre + LocationLevelling.OffsetAt(flat, yaw), StairLayout.Forward(yaw + at.Facing), pit,
                sunk ? pit : flat.Radius, !float.IsNaN(root), rockRadius, rocks ?? Array.Empty<LocationRock>(), ground, keepSmallRocks,
                Turned(at.At, centre, yaw), LocationRules.For(prefab).FrontOnly, prefab: prefab, entrance: entrance, doorClearance: at.Clearance, bodyKnown: bodyKnown);
        }
        // Else its fallback: a location with a door landing faces out of it (a crypt's may face its prefab's back), else its
        // prefab's front.
        Vector2? door = entrances?.Fallback;
        Vector2 front = door is Vector2 d && d.y < 0f ? -StairLayout.Forward(yaw) : StairLayout.Forward(yaw);
        return new(centre, centre + LocationLevelling.OffsetAt(flat, yaw), front, pit,
            sunk ? pit : flat.Radius, !float.IsNaN(root), rockRadius, rocks ?? Array.Empty<LocationRock>(), ground, keepSmallRocks,
            levelledArea ? null : DoorLanding(prefab, centre, yaw), LocationRules.For(prefab).FrontOnly, prefab: prefab, bodyKnown: bodyKnown,
            levelledArea: levelledArea);
    }

    /// <summary>Whether the walk in from an entrance's landing <paramref name="foot"/>, along <paramref name="inward"/> for
    /// <paramref name="reach"/>, stands on <paramref name="rock"/>: its real top over some metre of the walk is a step
    /// (<see cref="StairLayout.Walkable"/>, the one owner of a walker's step) from the way in's <paramref name="level"/>, so it is a
    /// step the walker takes, not a rock over or beside the way: a crypt's stair slab, never a stone, a cliff or a mountain whose box
    /// covers the landing. A rock whose top is not known is never stood on: its box says nothing of a step.</summary>
    private static bool StoodOnAtTheDoor(LocationRock rock, Vector2 foot, Vector2 inward, float reach, float level)
    {
        if (rock.Top == null) return false;
        int steps = Mathf.Max(1, Mathf.CeilToInt(reach));
        for (int k = 0; k <= steps; k++)
        {
            Vector2 q = foot + inward * (reach * k / steps);
            float top = rock.TopOver(q);
            if (StairLayout.Walkable(top, level)) return true;
        }
        return false;
    }

    /// <summary>The flat a location with no levelled flat of its own is shaped round, its centre at its root: one with a door
    /// (a Crypt2 only smooths), whose landing is its door landing (<see cref="DoorLandings"/>), and one landed on the level
    /// ground its smoothing leaves (<see cref="OnLevelGround"/>).</summary>
    public static readonly LevelOp DoorFlat = new(0f, 0f, 0f, 0f, false);

    /// <summary>How far off the location's body (its pieces and boulders, <see cref="Body"/>) the landing on its level ground is
    /// found (<see cref="OnLevelGround"/>): the clearance a staircase onto a landing keeps
    /// (<see cref="RoadStairRuns.PlanPlacedLocationApproach"/>), a road's its own half width and <see cref="BodyGap"/> more.</summary>
    public const float LevelGroundClearance = 1f;

    /// <summary>
    /// The shape of a location that levels no flat but smooths the ground round it (a Mountain tower ruin's five smoothings, a
    /// cabin's one), landed on the level ground that leaves beside it: the one owner of "smoothing level enough to stand on
    /// is a flat". The landing finder's own test (<see cref="Landings"/>: <see cref="LevelTolerance"/> over
    /// <see cref="LevelRadius"/>, out to <see cref="LandingSearch"/>) on the location's finished ground
    /// (<paramref name="ground"/>, <see cref="PlacedLocationGround"/>), all round it (<see cref="AllRound"/>: a ruin has no
    /// mouth and no rock base; its exterior radius is no keep-out), <see cref="LevelGroundClearance"/> off its own pieces and
    /// boulders (<paramref name="rocks"/>, its <see cref="Body"/>), nearest its centre first; the landing is that point, held
    /// to its own ground. Null where none is level (a cabin's one smoothing on a steep slope): the location is then reached at
    /// its ring (<see cref="PlacedLocations.FlatlessPlaced"/>). A StoneTowerRuins04 on a Mountain slope rising 1.4 in 1 left a
    /// pad at 192.7 m 4 to 7 m south-east of its centre, raised 1.6 m over its root by its fourth smoothing, where a player
    /// stood beside the tower; its road had ended at its ring, 33 m off.
    /// </summary>
    public static PlacedLocationShape? OnLevelGround(Vector2 centre, float yaw, float rockRadius, IReadOnlyList<LocationRock>? rocks,
        Func<float, float, float> ground, bool keepSmallRocks, float root, string? prefab, bool bodyKnown = false)
    {
        // A location entered only from its front keeps its front (it levels its flat in any case).
        if (LocationRules.For(prefab).FrontOnly) return null;
        IReadOnlyList<LocationRock> all = rocks ?? Array.Empty<LocationRock>();
        Vector2 front = StairLayout.Forward(yaw);
        // Searched at any height (no entrance level yet), then held to the landing's own.
        var search = new PlacedLocationShape(centre, centre, front, 0f, 0f, false, rockRadius, all, ground, keepSmallRocks, allRound: true);
        var found = search.Landings(ground, LevelGroundClearance, 1);
        if (found.Count == 0) return null;
        return new PlacedLocationShape(centre, centre, front, 0f, 0f, !float.IsNaN(root), rockRadius, all, ground, keepSmallRocks,
            allRound: true, found: found[0].at, prefab: prefab, bodyKnown: bodyKnown);
    }

    /// <summary>
    /// Where a location's road ends at its entrances, in the prefab's frame (x across, z out of its front; an entrance on the
    /// prefab's back has z under 0): the one owner of "where a road ends at a location" for a location with an entrance (a door or
    /// a dungeon's Gateway, or the foot of the stairs up to one). Such a location is placed first wherever it is (its entrances
    /// turn with it, <see cref="PlacedLocations.NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/>). Its
    /// road aims for the entrance its search reaches best (<see cref="RoadNetworkGenerator.PlanEntry"/>, nearest the road's end
    /// first), and, where none is reached within the rules, for the landing it had before the entrances were measured
    /// (<see cref="LocationEntrances.Fallback"/>: a door landing the location kept, else its own landing or ring), a second target
    /// held to every rule as the first. Measured from the game's prefabs in an editor scene: each entrance walked from its door
    /// or Gateway down its own stairs to open ground (the foot), then straight out to the first point whose disc of the
    /// entrance's clearance (<see cref="LocationEntrance.Clearance"/>) is open ground; the facing is the heading a player walks
    /// out along there. A Dvergr boss entrance's measured landing is on its stairs' centre line, 2 m out from their foot, where
    /// the one read in game (2.1, 21.9) stood on their east edge, on the last step. The crypts' landings moved 0.5 to 1.25 m from
    /// the ones read in game before; the swamp's SunkenCrypt4 is aimed at its entrance first and keeps its ring end as its
    /// fallback (its road onto a landing there failed at 17 of 19 on one test world offline, mostly in water).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, LocationEntrances> DoorLandings = new Dictionary<string, LocationEntrances>
    {
        ["Crypt2"] = new(new[] { new LocationEntrance(new Vector2(0.5f, -7.25f), 180f, 2f) }, fallback: new Vector2(0.5f, -8.5f)),
        ["Ruin2"] = new(new[] { new LocationEntrance(new Vector2(5.49f, 1.71f), 85f, 2f) }),
        ["WoodHouse1"] = new(new[] { new LocationEntrance(new Vector2(0f, 6.5f), 0f, 2f) }),
        ["WoodHouse2"] = new(new[] { new LocationEntrance(new Vector2(0.1f, 6.5f), 3f, 2f) }),
        ["WoodHouse11"] = new(new[] { new LocationEntrance(new Vector2(6.75f, -0.61f), 92f, 2f) }),
        ["WoodHouse12"] = new(new[] { new LocationEntrance(new Vector2(0.14f, 3.25f), 4f, 2f) }),
        ["WoodHouse13"] = new(new[] { new LocationEntrance(new Vector2(6.5f, -0.5f), 90f, 2f) }),
        ["TrollCave02"] = new(new[] { new LocationEntrance(new Vector2(0.54f, 0.75f), 1f, 2f) }),
        ["SunkenCrypt4"] = new(new[] { new LocationEntrance(new Vector2(0f, -8.5f), 180f, 2f) }),
        ["Crypt3"] = new(new[] { new LocationEntrance(new Vector2(-1.25f, -10.5f), 180f, 2f) }, fallback: new Vector2(0f, -10.5f)),
        ["Crypt4"] = new(new[] { new LocationEntrance(new Vector2(0f, 11f), 0f, 2f) }, fallback: new Vector2(0f, 11.5f)),
        ["SwampHut1"] = new(new[] { new LocationEntrance(new Vector2(6.22f, 4.63f), 14f, 2f) }),
        ["SwampHut2"] = new(new[] { new LocationEntrance(new Vector2(6.5f, 3f), 63f, 2f) }),
        ["AbandonedLogCabin02"] = new(new[] { new LocationEntrance(new Vector2(-7.75f, 2.08f), -88f, 2f) }),
        ["AbandonedLogCabin03"] = new(new[] { new LocationEntrance(new Vector2(-5.25f, -0.42f), -88f, 2f) }),
        ["AbandonedLogCabin04"] = new(new[] { new LocationEntrance(new Vector2(-5.5f, 0f), -90f, 2f) }),
        ["MorgenHole1"] = new(new[] { new LocationEntrance(new Vector2(0.61f, 8.73f), -6f, 2f) }),
        ["MorgenHole2"] = new(new[] { new LocationEntrance(new Vector2(0.61f, 8.73f), -6f, 2f) }),
        ["MorgenHole3"] = new(new[] { new LocationEntrance(new Vector2(0.61f, 8.73f), -6f, 2f) }),
        ["MountainCave02"] = new(new[] { new LocationEntrance(new Vector2(-3.82f, 4.42f), -17f, 2f) }),
        ["Hildir_cave"] = new(new[] { new LocationEntrance(new Vector2(0f, 4.5f), 0f, 2f) }),
        ["Hildir_crypt"] = new(new[] { new LocationEntrance(new Vector2(0f, 11f), 0f, 2f) }, fallback: new Vector2(0f, 11.5f)),
        ["Mistlands_GuardTower1_new"] = new(new[] { new LocationEntrance(new Vector2(-0.25f, 13.5f), 0f, 1.5f) }),
        ["Mistlands_GuardTower1_ruined_new"] = new(new[] { new LocationEntrance(new Vector2(-0.25f, 13.5f), 0f, 1.5f) }),
        ["Mistlands_GuardTower1_ruined_new2"] = new(new[] { new LocationEntrance(new Vector2(-0.25f, 14f), 0f, 2f) }),
        ["Mistlands_GuardTower2_new"] = new(new[] { new LocationEntrance(new Vector2(-0.25f, 13.5f), 0f, 1.5f) }),
        ["Mistlands_GuardTower3_new"] = new(new[] { new LocationEntrance(new Vector2(0f, 11f), 0f, 2f) }),
        ["Mistlands_GuardTower3_ruined_new"] = new(new[] { new LocationEntrance(new Vector2(0f, 11f), 0f, 2f) }),
        ["Mistlands_Lighthouse1_new"] = new(new[] { new LocationEntrance(new Vector2(-0.25f, 13.5f), 0f, 1.5f) }),
        ["Mistlands_Harbour1"] = new(new[] { new LocationEntrance(new Vector2(6.45f, -12.09f), 81f, 1.5f) }),
        ["Mistlands_DvergrTownEntrance1"] = new(new[] { new LocationEntrance(new Vector2(-0.3f, 10.24f), 6f, 2f) }),
        ["Mistlands_DvergrTownEntrance2"] = new(new[] { new LocationEntrance(new Vector2(5.13f, 22.4f), 49f, 2f) }),
        ["Mistlands_DvergrBossEntrance1"] = new(new[] { new LocationEntrance(new Vector2(0f, 24.5f), 0f, 2f) }, fallback: new Vector2(2.1f, 21.9f)),
    };

    /// <summary>Whether <paramref name="prefab"/> has an entrance its road aims for (<see cref="DoorLandings"/>).</summary>
    public static bool HasDoor(string? prefab) => prefab != null && DoorLandings.ContainsKey(prefab);

    /// <summary>Whether <paramref name="prefab"/>'s fallback is itself a door landing (<see cref="LocationEntrances.Fallback"/>): such a
    /// location is reached only on a landing in front of its door, never at its ring nor off its level.</summary>
    public static bool ReachedOnlyAtADoor(string? prefab) =>
        EntrancesOf(prefab)?.Fallback != null;

    /// <summary>How many entrances <paramref name="prefab"/> has (<see cref="DoorLandings"/>); 0 for any other location.</summary>
    public static int EntranceCount(string? prefab) => EntrancesOf(prefab)?.Entrances.Count ?? 0;

    /// <summary>The entrances of <paramref name="prefab"/> (<see cref="DoorLandings"/>), null for a location with none.</summary>
    public static LocationEntrances? EntrancesOf(string? prefab) => prefab != null && DoorLandings.TryGetValue(prefab, out var e) ? e : null;

    /// <summary>Whether network at <paramref name="p"/> is on the side the location is entered from: anywhere for most locations;
    /// for one entered only from its front (<see cref="EnteredFromFront"/>), in front of its floor (<see cref="InFront"/>:
    /// the whole front side of the line through its floor across its facing).</summary>
    public bool OnEntranceSide(Vector2 p) => !EnteredFromFront || InFront(p);

    /// <summary>The world position of <paramref name="prefab"/>'s door landing (<see cref="DoorLandings"/>) for a location at
    /// <paramref name="centre"/> turned to <paramref name="yaw"/>; null for any other location.</summary>
    public static Vector2? DoorLanding(string? prefab, Vector2 centre, float yaw) =>
        EntrancesOf(prefab)?.Fallback is Vector2 local ? Turned(local, centre, yaw) : null;

    /// <summary>The world position of <paramref name="prefab"/>'s entrance <paramref name="entrance"/> (<see cref="DoorLandings"/>) for a
    /// location at <paramref name="centre"/> turned to <paramref name="yaw"/>; null where it has no such entrance.</summary>
    public static Vector2? EntranceLanding(string? prefab, int entrance, Vector2 centre, float yaw) =>
        EntrancesOf(prefab) is LocationEntrances e && entrance >= 0 && entrance < e.Entrances.Count
            ? Turned(e.Entrances[entrance].At, centre, yaw) : null;

    /// <summary>A point of a prefab's frame in the world, for a location at <paramref name="centre"/> turned to <paramref name="yaw"/>.</summary>
    private static Vector2 Turned(Vector2 local, Vector2 centre, float yaw)
    {
        float r = yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return centre + new Vector2(local.x * c + local.y * s, -local.x * s + local.y * c);
    }

    /// <summary>Whether <paramref name="p"/> is on the location's entrance side: within the cone of
    /// <see cref="FrontCone"/> about its facing (<see cref="Front"/>), from its placed entrance floor
    /// (<see cref="FlatCentre"/>), never from its centre: a cave's floor stands 4.75 m behind its centre. A point on the
    /// floor itself (no direction) is not in front; come to from every side (<see cref="AllRound"/>), every other point is.
    /// The one owner of the entrance side (docs/OWNERS.md): a road onto the landing and a staircase to it ask it, through
    /// <see cref="Behind"/>, and so do the clearance and reached checks.</summary>
    public bool InFront(Vector2 p) => InFrontOf(FlatCentre, p);

    /// <summary>The entrance side (<see cref="InFront"/>) measured from <paramref name="from"/> instead of the floor.</summary>
    private bool InFrontOf(Vector2 from, Vector2 p)
    {
        Vector2 d = p - from;
        return d.sqrMagnitude > 1e-6f && (AllRound || Vector2.Dot(d.normalized, Front) >= _cosCone);
    }

    /// <summary>
    /// Whether <paramref name="p"/> is on the way in to the location's own door (<see cref="Door"/>): the door landing itself and
    /// the last stretch onto it from the entrance side, the network's reach of the landing (RoadNetworkGenerator.LandingReach, how
    /// near it must come to reach the landing) on the entrance side measured from the landing (<see cref="InFrontOf"/>, the
    /// entrance side's own cone, never nearer the location than the landing). There the location's own built pieces are its
    /// entrance, the stairs the walker steps onto from the landing, and are not kept off (<see cref="Refusal"/>): a Dvergr boss
    /// entrance's door landing stands 1.2 m from the middle of its own bottom stair. Everywhere else they keep
    /// the walker's own body off (<see cref="Refusal"/>); a location without a door has no such way in, and another location's pieces stand inside its
    /// footprint, which the network keeps out of whole (RoadLocationProtection).
    /// The way in is a lane, not the whole half-disc: each side of the line from the landing along <see cref="Front"/> it is no wider
    /// than the walker's own reach (<paramref name="walkerHalf"/>: its half width and margin, the rockMargin <see cref="Refusal"/> is
    /// asked with) plus the entrance's measured clearance; beside the lane the pieces are kept off by the walker's own body.
    /// </summary>
    public bool InDoorCorridor(Vector2 p, float walkerHalf = 0f) =>
        Door is Vector2 door && Vector2.Distance(p, door) <= RoadNetworkGenerator.LandingReach && OutsideItsDoor(p)
        && FloatBits.Abs(Vector2.Dot(p - door, new Vector2(Front.y, -Front.x))) <= walkerHalf + (float.IsNaN(DoorClearance) ? 0f : DoorClearance);

    /// <summary>Whether <paramref name="p"/> may be on the way in to the location's door: anywhere for most locations; for a cave whose
    /// mouth is cut into rock (<see cref="FrontLaneOnly"/>), within the network's reach of the landing (RoadNetworkGenerator.LandingReach)
    /// only on the entrance side measured from the landing (<see cref="OutsideItsDoor"/>), so the last stretch comes from in front of
    /// the mouth, never from beside or behind it. Not a lane: held to the door's lane (<see cref="InDoorCorridor"/>), a way in from the
    /// side was led round to the lane and turned into it, and the road folded back on itself before the mouth.</summary>
    public bool OnTheWayIn(Vector2 p) =>
        !FrontLaneOnly || Door is not Vector2 door || Vector2.Distance(p, door) > RoadNetworkGenerator.LandingReach || OutsideItsDoor(p);

    /// <summary>Whether every metre of the straight line from <paramref name="from"/> to <paramref name="to"/> is on the way in
    /// (<see cref="OnTheWayIn"/>).</summary>
    public bool OnTheWayInAllTheWay(Vector2 from, Vector2 to)
    {
        if (!FrontLaneOnly) return true;
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to)));
        for (int k = 0; k <= steps; k++)
            if (!OnTheWayIn(Vector2.Lerp(from, to, (float)k / steps))) return false;
        return true;
    }

    /// <summary>Whether <paramref name="p"/> is the location's door landing (<see cref="Door"/>) or on the entrance side measured from
    /// it (<see cref="InFrontOf"/>, the entrance side's own cone): outside its entrance pit (<see cref="Refusal"/>) and, within the
    /// network's reach of the landing, on the way in to its door (<see cref="InDoorCorridor"/>). False for a location without one.</summary>
    public bool OutsideItsDoor(Vector2 p) =>
        Door is Vector2 door && ((p - door).sqrMagnitude <= 1e-6f || InFrontOf(door, p));

    /// <summary>Whether something centred at <paramref name="p"/> keeps out of the location: out of
    /// its pit by <paramref name="margin"/>, out of its rock by as much (but in front of its
    /// floor) and of its body's boulders, and <paramref name="rockMargin"/> clear of each
    /// boulder kept outside that.</summary>
    public bool Allowed(Vector2 p, float margin, float rockMargin) => Refusal(p, margin, rockMargin) == "";

    /// <summary>
    /// Who keeps off a Location's edge, and by how much (<see cref="KeepsOff"/>, <see cref="LeadRoundRing(Walker)"/>): the one place the
    /// walker's widths for a way in are named. <see cref="Margin"/> off its pit, its rock (where its body is not known) and its
    /// body's boulders; <see cref="RockMargin"/> off its kept rocks and, by the walker's own body, its built pieces, and the
    /// walker's half of its door's lane (<see cref="InDoorCorridor"/>, <see cref="OnTheWayIn"/>); <see cref="BodyMargin"/> off a
    /// body boulder whose real top is known (NaN: <see cref="Margin"/>); <see cref="RingReach"/>, how far outside
    /// <see cref="KeepOut"/> it is led round the Location's rock. The road and the staircase differ, each for its stated reason:
    /// a road (<see cref="Road"/>) hugs a large boulder (RoadLocationEntry.HugGap: a box overstates a rough rock) and is led round
    /// its earthwork reach and RoadLocationEntry.RingSlack out (its planner rounds bends inside the waypoints); a staircase
    /// (<see cref="Stairs"/>) keeps a turn's fillers clear (RoadStairRuns.FillerReach past its half width) and is led round
    /// RockClearance out (its layout searches its own way and keeps out of the rock regardless).
    /// </summary>
    public readonly struct Walker
    {
        public readonly float Margin, RockMargin, BodyMargin, RingReach;
        public Walker(float margin, float rockMargin, float bodyMargin, float ringReach)
        { Margin = margin; RockMargin = rockMargin; BodyMargin = bodyMargin; RingReach = ringReach; }

        /// <summary>A road of <paramref name="width"/> onto a landing (RoadLocationEntry), and the network that reaches a landing
        /// on its entrance side (RoadNetworkGenerator.ReachedTarget).</summary>
        public static Walker Road(float width)
        {
            float reach = RoadLocationEntry.InsideReach(width);
            return new Walker(reach, width * 0.5f + RoadLocationEntry.HugGap, width * 0.5f + BodyGap, reach + RoadLocationEntry.RingSlack);
        }

        /// <summary>A staircase's piece kept <paramref name="margin"/> further off than its half width (RoadStairRuns.Width): a
        /// turn's fillers (RoadStairRuns.FillerReach) for its layout, none for a laid piece's own centre.</summary>
        public static Walker Stairs(float margin) =>
            new(margin, RoadStairRuns.Width * 0.5f + margin, float.NaN, RockClearance);
    }

    /// <summary>What <paramref name="p"/> runs into for <paramref name="walker"/> (<see cref="Refusal"/> with its margins); "" for
    /// nothing. The one keeps-off test for a road onto a landing, a staircase to it and the landings they aim at.</summary>
    public string KeepsOff(Vector2 p, Walker walker) => Refusal(p, walker.Margin, walker.RockMargin, walker.BodyMargin);

    /// <summary>The ring a way in from <see cref="Behind"/> the Location is led round on, about its <see cref="Centre"/>: its
    /// keep-out (<see cref="KeepOut"/>) and the walker's <see cref="Walker.RingReach"/>. The one owner of the lead-round ring,
    /// read by a road onto the landing (RoadLocationEntry.Route) and a staircase to it (RoadStairRuns.Around).</summary>
    public float LeadRoundRing(Walker walker) => KeepOut + walker.RingReach;

    /// <summary>The lead-round ring (<see cref="LeadRoundRing(Walker)"/>) of a Location of exterior radius <paramref name="rockRadius"/>,
    /// before its shape is made: how far its ground and rocks are read for (PlacedLocationReader.ReadHalf).</summary>
    public static float LeadRoundRing(float rockRadius, Walker walker) => rockRadius + RockMargin + walker.RingReach;

    /// <summary>How far a road's own surface keeps off a mouth boulder whose real top is known
    /// (<see cref="LocationRock.Top"/>): its edge 0.5 m clear. Kept its whole earthwork reach off
    /// it (the reach a box needs, overstated as it is), a frost cave's level ledge beside the
    /// mound its mouth is cut into was no landing at all.</summary>
    public const float BodyGap = 0.5f;

    /// <summary>Half the cone in front of a location's floor where its approach may come in past its
    /// rock: the whole front side of the line through its floor across its facing. At 45 the rock's
    /// ring shut out the natural line from a road's end beside a frost cave to the level ledge
    /// beside its mouth; in front, the boulders round its
    /// mouth keep approaches off it (<see cref="Body"/>).</summary>
    public const float FrontCone = 90f;

    /// <summary>A way in keeps this far outside a location's exterior radius but in front of it (<see cref="KeepOut"/>, where its body is
    /// not known), and a staircase is led round it this much further out again (<see cref="Walker.Stairs"/>, <see cref="LeadRoundRing(Walker)"/>;
    /// the frost cave's own
    /// boulders carved by the loop: the road and stairs go a bit further out when looping to the front).</summary>
    public const float RockMargin = 3f, RockClearance = 3f;


    /// <summary>What <paramref name="p"/> runs into (see <see cref="Allowed"/>), for the log; "" for nothing.</summary>
    /// <param name="bodyMargin">How far off a mouth boulder whose real top is known (<paramref name="margin"/> when not given).</param>
    public string Refusal(Vector2 p, float margin, float rockMargin, float bodyMargin = float.NaN) =>
        Judge(p, margin, rockMargin, bodyMargin, radius: true);

    private string Judge(Vector2 p, float margin, float rockMargin, float bodyMargin, bool radius)
    {
        float floor = Vector2.Distance(p, FlatCentre);
        // Come to from every side, it has no entrance: its body is what is kept off. A location with a door landing keeps its pit
        // (its floor, a sunk floor's whole disc) off on its own side of that landing only (OutsideItsDoor): the landing is where its
        // entrance meets open ground, so the entrance side measured from it is outside the pit. Measured from the floor alone, a
        // Dvergr mine shaft's entrance landing 10 m out stood inside its 20 m sunk yard, and every road onto it was refused.
        if (!AllRound && floor < Pit + margin && !OutsideItsDoor(p)) return FormattableString.Invariant($"in the entrance ({floor:F1} m from the floor, {Pit + margin:F1} kept)");
        float centre = Vector2.Distance(p, Centre);
        if (radius && !BodyKnown && RockRadius > 0f && centre < KeepOut + margin && !InFront(p))
        {
            // What else keeps it off (the body, the gap, the pit), for the log: whether only the radius does, and what stands there.
            string rest = Judge(p, margin, rockMargin, bodyMargin, radius: false);
            float nearest = float.PositiveInfinity;
            foreach (LocationRock piece in Body) nearest = Mathf.Min(nearest, piece.Distance(p));
            foreach (LocationRock piece in Rocks) nearest = Mathf.Min(nearest, piece.Distance(p));
            float over = _ground != null ? _ground(p.x, p.y) - _ground(Landing.x, Landing.y) : float.NaN;
            string there = rest != "" ? "also " + rest : FormattableString.Invariant($"only the radius: ground {over:+0.0;-0.0} m over its landing's, nearest piece or boulder {nearest:F1} m");
            return FormattableString.Invariant($"in the location's rock ({centre:F1} m from its centre, {KeepOut + margin:F1} kept outside the front; {there})");
        }
        float h = _ground?.Invoke(p.x, p.y) ?? 0f;
        float g = _ground != null ? h : float.NaN;
        bool doorWay = InDoorCorridor(p, rockMargin);
        for (int i = 0; i < Body.Count; i++)
        {
            LocationRock rock = Body[i];
            if (rock.Built)
            {
                // A built piece: its colliders' box kept off by the walker's own body, a road's edge its BodyGap clear (bodyMargin,
                // the tight box of a built piece, as of a boulder whose top is known), a staircase's piece its own half width and
                // margin (rockMargin), at any height: an end may stand next to a piece (a Mistlands light post), never on it nor
                // under it. No gap of its own past that: the ground under the piece is the one rule's (PlannedGround.Moved, a step
                // either way). A Location's stakes are not in its body (LocationRock.ClearedForAWay). On the way in to its own door,
                // whose stairs the walker steps onto, an entrance keeps the clearance it was measured with (DoorClearance), a door
                // landing without one none.
                if (doorWay)
                {
                    if (!float.IsNaN(DoorClearance) && rock.InTheWay(p, DoorClearance, g, out float atDoor))
                        return FormattableString.Invariant($"too near {Name}'s built pieces at its entrance ({atDoor:F1} m off its {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}), {DoorClearance:F1} kept)");
                    continue;
                }
                float kept = float.IsNaN(bodyMargin) ? rockMargin : bodyMargin;
                if (rock.InTheWay(p, kept, float.NaN, out float near))
                    return FormattableString.Invariant($"too near {Name}'s built pieces ({near:F1} m off its {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}), {kept:F1} kept)");
                continue;
            }
            // A boulder of its body: the way in's reach off it (its edge off a boulder whose top is known), but on the way in to an
            // entrance measured with its clearance (DoorClearance), which was measured over these same boulders' colliders: there that
            // clearance, as its built pieces keep. By its box, a Dvergr Queen's-building entrance's own cliff_mistlands1 (45 by 75 m)
            // stood 3.8 m off its measured landing's way in, where the road's 4 m reach was kept. Its entrance's own stairs of rock
            // (DoorStairs: a crypt's stair slab 1.6 m by its box behind its landing) are walked onto there, as the stairs of a door
            // landing without a measured clearance are, but only where it is a step: its cells under the walk or a step
            // (StairLayout.Walkable) from the way in's level (_doorLevel), the one walk rule. Every other part of it (a stone that
            // rises past a step, at the point or beside it) is kept off by the walker's own reach (rockMargin), as a kept rock is.
            bool stair = doorWay && _doorStair[i];
            float keep = stair ? rockMargin
                : doorWay && !float.IsNaN(DoorClearance) ? DoorClearance : rock.Top != null && !float.IsNaN(bodyMargin) ? bodyMargin : margin;
            if (rock.InTheWay(p, keep, g, out float d, walkedFrom: stair ? _doorLevel : float.NaN))
                return FormattableString.Invariant($"at its mouth's {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}) ({2f * rock.HalfX:F1} by {2f * rock.HalfZ:F1} m turned {rock.Yaw:F0}; {d:F1} m off it, {keep:F1} kept)");
        }
        foreach (LocationRock rock in Rocks)
        {
            if (rock.InTheWay(p, rockMargin, g, out float d))
                return FormattableString.Invariant($"on its {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}) ({2f * rock.HalfX:F1} by {2f * rock.HalfZ:F1} m turned {rock.Yaw:F0}; {d:F1} m off it, {rockMargin:F1} kept)");
        }
        // A way in from its entrance side only (FrontLaneOnly), the rest clear: beside the mouth near the landing it would come in under its rock.
        if (!OnTheWayIn(p) && Door is Vector2 lane)
            return FormattableString.Invariant($"beside {Name}'s entrance, off its way in ({FloatBits.Abs(Vector2.Dot(p - lane, new Vector2(Front.y, -Front.x))):F1} m across its facing, {Vector2.Distance(p, lane):F1} m from its landing)");
        return "";
    }

    /// <summary>The ground every arrival target reads its landing region on: the Location's own (null where not known).</summary>
    public Func<float, float, float>? Ground => _ground;

    /// <summary>Each landing region worked out on the Location's own ground, by clearance: shapes are shared by the island workers.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<float, LandingRegion> _regions = new();

    /// <summary>The landing region (<see cref="ConnectedLanding"/>) for <paramref name="clearance"/> on <paramref name="ground"/>,
    /// worked out once per clearance on the Location's own ground.</summary>
    public LandingRegion RegionFor(Func<float, float, float> ground, float clearance) =>
        ReferenceEquals(ground, _ground) ? _regions.GetOrAdd(clearance, c => ConnectedLanding(ground, c)) : ConnectedLanding(ground, clearance);

    /// <summary>Whether <paramref name="p"/> is where a way may end on <paramref name="region"/>: on its level ground and within its lip
    /// (<see cref="RoadStairRuns.WithinLip"/> of <see cref="LandingRegion.Depth"/>), as a staircase's top is held. The one test both a
    /// road's end (<see cref="Ends"/>) and a staircase's top (RoadStairRuns.PlanPlacedLocationApproach, PastTheLip) are held to.</summary>
    public static bool OnItsLip(LandingRegion region, Vector2 p) => region.Contains(p) && RoadStairRuns.WithinLip(region.Depth(p));

    /// <summary>
    /// Whether an end at <paramref name="p"/> would stand under the Location's own rock: inside a cave, past its mouth, under its
    /// arch or roof. A rock of the Location (its <see cref="Body"/> or kept <see cref="Rocks"/>, never a built piece) that stands
    /// wholly over a walker's head there (its lowest point over <see cref="RockClearances.Top"/> of the ground at
    /// <paramref name="p"/>, so it is no rock in the way, <see cref="LocationRock.InTheWay"/>) and comes within the walker's own
    /// reach <paramref name="margin"/> of it, by its real top's cells where known, else its box. A road's end and a staircase's top
    /// are kept out of it (<see cref="Ends"/>, RoadStairRuns.PlanPlacedLocationApproach): one that can end only there is refused,
    /// never carried on further onto the landing. The way to the end may pass under the rock; only the end may not stand there.
    /// </summary>
    public bool UnderItsRock(Vector2 p, float margin, Func<float, float, float>? ground = null)
    {
        ground ??= _ground;
        if (ground == null) return false;
        float head = RockClearances.Top(ground(p.x, p.y));
        foreach (LocationRock rock in Body.Concat(Rocks))
            if (!rock.Built && rock.MinY > head && rock.InTheWay(p, margin, float.NaN, out _)) return true;
        return false;
    }

    /// <summary>
    /// Where a road or staircase ends on this target, best first, each with the ground there: the one owner of the arrival point at a
    /// placed Location (docs/OWNERS.md, "Where a road ends at a Location"). The target's landing region as a staircase reads it
    /// (<see cref="ConnectedLanding"/>: the level ground walked onto its landings, level with them within StairLayout.StairFitsFrom,
    /// out to its edges), ended on within its lip (<see cref="OnItsLip"/>: where the levelled ground meets the slope, the same rule
    /// that holds a staircase's top), clear of the Location for the walker (<see cref="Refusal"/> with <paramref name="clearance"/>),
    /// out from under its rock (<see cref="UnderItsRock"/>, the walker's <paramref name="clearance"/> off it) and on a side it may be
    /// come to from (<see cref="OnEntranceSide"/>). Aimed toward the entrance, ended as soon as it is on the landing: onto an entrance
    /// (<see cref="Door"/>), within the network's reach of its measured landing (RoadNetworkGenerator.LandingReach), the least way
    /// round from <paramref name="from"/> (the road's end, where a staircase sets off) through it to the door first (the two
    /// distances together: on the straight way toward the entrance), then nearest <paramref name="from"/>: the way stops at the
    /// landing's rim it comes to on its way toward the door rather than running on up to the door. Onto the levelled ground as a whole (<see cref="LevelledArea"/>): anywhere on its
    /// lip, nearest <paramref name="from"/> first. Onto any other landing (a Location's own), its level landings
    /// (<see cref="Landings"/>), nearest the road's end first, as before. Onto an entrance whose region holds no end, the nearest clear
    /// point level with its landing on its entrance side within reach of it, else the landing itself where it is not under the
    /// Location's rock; else none. At most <paramref name="tries"/>, <see cref="LandingApart"/> apart.
    /// </summary>
    public List<(Vector2 at, float height)> Ends(Func<float, float, float>? ground, Vector2 from, float clearance, int tries,
        float bodyMargin = float.NaN)
    {
        ground ??= _ground;
        if (ground == null) return new List<(Vector2, float)> { (LandingFor(clearance), float.NaN) };
        if (Door == null && !LevelledArea)
        {
            List<(Vector2 at, float height)> landings = Landings(ground, clearance, tries, bodyMargin);
            landings.Sort((a, b) => Vector2.Distance(a.at, from).CompareTo(Vector2.Distance(b.at, from)));
            return landings;
        }
        LandingRegion region = RegionFor(ground, clearance);
        // Out of the water (RoadWater.BuiltDry, as a staircase's joined edge is): stopped at the rim, a way onto a crypt in a pool
        // ended at the levelled ground's wet edge, not on the dry landing past it.
        bool Clear(Vector2 p) => OnEntranceSide(p) && RoadWater.BuiltDry(ground(p.x, p.y)) && Refusal(p, clearance, clearance, bodyMargin) == ""
            && !UnderItsRock(p, clearance, ground);
        bool Fits(Vector2 p) => (LevelledArea || Vector2.Distance(p, Door!.Value) <= RoadNetworkGenerator.LandingReach)
            && OnItsLip(region, p) && Clear(p);
        var found = new List<(Vector2 at, float height, float distance)>();
        if (!LevelledArea && Fits(Door!.Value)) found.Add((Door.Value, ground(Door.Value.x, Door.Value.y), Vector2.Distance(Door.Value, from)));
        foreach (var (x, z) in region.Cells)
        {
            var p = new Vector2(x, z);
            if (Door is Vector2 d && (p - d).sqrMagnitude < 1e-4f || !Fits(p)) continue;
            found.Add((p, ground(x, z), Vector2.Distance(p, from)));
        }
        // Toward the entrance (the way round from the road through the end to the door), then stopped soonest (nearest the road).
        float Toward(Vector2 p) => Door is Vector2 d ? Vector2.Distance(p, from) + Vector2.Distance(p, d) : 0f;
        found.Sort((a, b) => Toward(a.at).CompareTo(Toward(b.at)) is int c && c != 0 ? c : a.distance.CompareTo(b.distance));
        var picked = new List<(Vector2 at, float height)>();
        foreach (var f in found)
        {
            if (picked.Exists(q => Vector2.Distance(q.at, f.at) < LandingApart)) continue;
            picked.Add((f.at, f.height));
            if (picked.Count == tries) break;
        }
        // No end on its region (an entrance whose landing is not clear for the walker, so seeds none: a Dvergr entrance's landing
        // within a cliff's box): the point nearest the landing on its entrance side measured from it (OutsideItsDoor), within the
        // network's reach of it, clear of the Location and level with the landing (StairLayout.StairFitsFrom, the region's own
        // level); else the measured landing itself, as before, but never under the Location's rock.
        if (picked.Count == 0 && Door is Vector2 door)
        {
            float level = ground(door.x, door.y), best = float.PositiveInfinity;
            int r = Mathf.CeilToInt(RoadNetworkGenerator.LandingReach);
            Vector2? nearest = null;
            for (int z = Mathf.FloorToInt(door.y) - r; z <= Mathf.CeilToInt(door.y) + r; z++)
                for (int x = Mathf.FloorToInt(door.x) - r; x <= Mathf.CeilToInt(door.x) + r; x++)
                {
                    var p = new Vector2(x, z);
                    float d = Vector2.Distance(p, door);
                    if (d > RoadNetworkGenerator.LandingReach || d >= best || !OutsideItsDoor(p)
                        || FloatBits.Abs(ground(x, z) - level) > StairLayout.StairFitsFrom || !Clear(p)) continue;
                    best = d; nearest = p;
                }
            if (nearest is Vector2 n) picked.Add((n, ground(n.x, n.y)));
            else if (!UnderItsRock(door, clearance, ground)) picked.Add((door, level));
        }
        return picked;
    }

    /// <summary>Whether network at <paramref name="q"/>, at height <paramref name="h"/>, is on this target: onto the levelled ground as a
    /// whole, on its landing region (as a road's way in reaches it, <see cref="Ends"/>'s region for a road of the default width),
    /// level with the ground there within RoadNetworkGenerator.ReachRise and on a side it may be come to from; onto an entrance or a
    /// landing, within the network's reach of its landing on its way in (<see cref="OnTheWayIn"/>), level with the ground at the
    /// landing. Read by the reached test (RoadNetworkGenerator.ReachedTarget).</summary>
    public bool Arrives(Vector2 q, float h) => ArrivalOff(q, h) is float off && FloatBits.Abs(off) <= RoadNetworkGenerator.ReachRise;

    /// <summary>Whether a road's end at <paramref name="q"/>, at height <paramref name="h"/>, arrives on this target nearby: on it
    /// where <see cref="Arrives"/> reads it, but off its ground there by less than a player's height, the climb a staircase onto
    /// it from there would make and is not built for (RoadStairRuns.ClimbsUnderAPlayer, RoadRockPolicy.PlayerHeight), not
    /// ReachRise. Asked only on the arrival ladder's last resort, every way that arrives level refused (RoadNetworkGenerator.PlanArrival,
    /// PlanEntryFrom); every other reader keeps <see cref="Arrives"/>.</summary>
    public bool ArrivesNearby(Vector2 q, float h) => ArrivalOff(q, h) is float off && RoadStairRuns.ClimbsUnderAPlayer(off);

    /// <summary>How far network at <paramref name="q"/>, at height <paramref name="h"/>, stands over (+) or under (-) this target's
    /// ground where <see cref="Arrives"/> reads it; null where it is not on the target (off its region or its entrance side, or
    /// beyond the network's reach of its landing or off its way in).</summary>
    private float? ArrivalOff(Vector2 q, float h)
    {
        if (LevelledArea && _ground != null)
        {
            LandingRegion region = RegionFor(_ground, RoadLocationEntry.InsideReach(RoadNetworkGenerator.RoadWidth) + RoadLocationEntry.EndSlack);
            return region.Near(q, 0.5f) && OnEntranceSide(q) ? h - _ground(q.x, q.y) : null;
        }
        Vector2 at = Door ?? Landing;
        if (Vector2.Distance(q, at) > RoadNetworkGenerator.LandingReach || !OnTheWayIn(q)) return null;
        return h - (_ground?.Invoke(at.x, at.y) ?? h);
    }

    /// <summary>The disc <see cref="Arrives"/> can hold within: its landing region's search disc (<see cref="ConnectedLanding"/>), or
    /// the network's reach of its landing.</summary>
    public (Vector2 centre, float reach) ArrivalDisc() =>
        LevelledArea ? (FlatCentre, Edge + LandingSearch) : (Door ?? Landing, RoadNetworkGenerator.LandingReach);

    /// <summary>The landing's aim for an approach that keeps <paramref name="clearance"/> off the pit:
    /// <see cref="Landing"/>, or further out in front where that is not enough. The one owner of A location's landing, flat and ground as it will leave it (docs/OWNERS.md).</summary>
    public Vector2 LandingFor(float clearance) =>
        Door is Vector2 door ? door : _found is Vector2 found ? found : Pit > 0f ? FlatCentre + Front * (Pit + Mathf.Max(RoadStairRuns.LandingOut, clearance)) : FlatCentre + Front * Edge;

    /// <summary>How far past its pit a landing is looked for, how level it must be (highest less
    /// lowest ground within <see cref="LevelRadius"/>), how far apart the ones tried stand, and how many.</summary>
    public const float LandingSearch = 14f, LevelTolerance = 0.5f, LevelRadius = 2f, LandingApart = 3f;
    /// <summary>How far off the location's front axis a landing may lie, seen from its floor: on the whole
    /// front side, a BearCave's road came in at its side, 64 degrees off; the ledge of a frost cave was 45 off.</summary>
    public const float LandingCone = 50f;
    public const int LandingTries = 3;

    /// <summary>
    /// The level ground beside the location's mouth, nearest the flat's outer edge on its front
    /// axis first, level with the location: within <see cref="RoadNetworkGenerator.ReachRise"/> of the ground at its
    /// <see cref="Landing"/>, the height a road reaches the location at (<see cref="RoadNetworkGenerator.ArrivalHeight"/>), since
    /// a road or staircase carried onto a landing reaches the location (<see cref="PlacedLocations.MarkLanded"/>). Held to 2 m of the
    /// location's root before, a road was carried onto a shelf 1.8 m under a Mountain tower's flat and counted it reached; a
    /// cave's root is not the level of the ground in front of its mouth (a frost cave's 2 m above it), its landing point's
    /// ground is. Only where the location's root is known (<see cref="Entrance"/>): every metre within
    /// <see cref="LandingCone"/> of its front axis, seen from its floor, <paramref name="clearance"/> clear of its pit, rock and boulders,
    /// no nearer its middle than its <see cref="Edge"/> less <see cref="LevelRadius"/>, out to
    /// <see cref="LandingSearch"/> past the pit, whose ground within <see cref="LevelRadius"/>
    /// varies by <see cref="LevelTolerance"/> or less; at most <see cref="LandingTries"/>,
    /// <see cref="LandingApart"/> apart, each with its ground height, which an approach arrives at. The landing of a
    /// frost cave was a 6 m ledge beside its mouth 10.6 m from its centre, level within 0.3 m;
    /// the point aimed at before, straight out from the pit, was a metre lower on the slope down
    /// into the entrance.
    /// </summary>
    public List<(Vector2 at, float height)> Landings(Func<float, float, float> ground, float clearance, int tries = LandingTries,
        float bodyMargin = float.NaN)
    {
        if (Door is Vector2 door) return new List<(Vector2, float)> { (door, ground(door.x, door.y)) };
        float outer = Edge + LandingSearch;
        Vector2 edge = FlatCentre + Front * Edge;
        float cosCone = Mathf.Cos(LandingCone * Mathf.Deg2Rad);
        // No nearer the middle than the flat's edge less a landing's own level ground: a flat level with its location is the location
        // itself. Looked for from just past the clearance, a Dvergr camp's (Mistlands_Excavation3, its flat 17 m, its wall about
        // 14.7 m round it) landings stood 10 to 14 m from its centre, and its road ran on inside the wall line. A sunk floor's
        // edge is its pit's, so its landings stay where they were; so do a road's onto a flat 6.5 m round or less (its
        // clearance is further out). Where none is left, the approach aims at the flat's edge (LandingFor).
        // Come to from every side (AllRound), from its middle out: its body is kept clear of by the refusal below.
        // The levelled ground as a whole (LevelledArea) is looked for all round as well: its landings seed its region (ConnectedLanding).
        bool round = AllRound || LevelledArea;
        float inner = round ? 0.5f : Mathf.Max(Pit + clearance, Edge - LevelRadius, 0.5f);
        float level = ground(Landing.x, Landing.y);
        var found = new List<(Vector2 at, float height, float distance)>();
        for (int z = Mathf.FloorToInt(FlatCentre.y - outer); z <= Mathf.CeilToInt(FlatCentre.y + outer); z++)
            for (int x = Mathf.FloorToInt(FlatCentre.x - outer); x <= Mathf.CeilToInt(FlatCentre.x + outer); x++)
            {
                var p = new Vector2(x, z);
                float d = Vector2.Distance(p, FlatCentre);
                if (d < inner || d > outer || (!round && Vector2.Dot((p - FlatCentre) * (1f / d), Front) < cosCone)) continue;
                float here = ground(x, z);
                if (RootKnown && FloatBits.Abs(here - level) > RoadNetworkGenerator.ReachRise) continue;
                if (Refusal(p, clearance, clearance, bodyMargin) != "") continue;
                float low = float.PositiveInfinity, high = float.NegativeInfinity;
                int n = 0;
                for (int k = -2; k <= 2; k++)
                    for (int i = -2; i <= 2; i++)
                    {
                        if (i * i + k * k > LevelRadius * LevelRadius) continue;
                        float h = ground(x + i * LevelRadius * 0.5f, z + k * LevelRadius * 0.5f);
                        if (float.IsNaN(h) || float.IsInfinity(h)) { low = float.NegativeInfinity; break; }
                        low = Mathf.Min(low, h); high = Mathf.Max(high, h); n++;
                    }
                if (n == 0 || high - low > LevelTolerance) continue;
                found.Add((p, here, Vector2.Distance(p, edge)));
            }
        found.Sort((a, b) => a.distance.CompareTo(b.distance));
        var picked = new List<(Vector2 at, float height)>();
        foreach (var f in found)
        {
            if (picked.Exists(q => Vector2.Distance(q.at, f.at) < LandingApart)) continue;
            picked.Add((f.at, f.height));
            if (picked.Count == tries) break;
        }
        return picked;
    }

    /// <summary>How far outside an edge of the landing's level ground the ground is read (every
    /// <see cref="JoinStep"/>), and how steeply it may fall away anywhere within that (drop over distance) for the
    /// edge to be joined to the slope: a few switchbacks arrive there at grade. Steeper, the edge overhangs a drop.
    /// Hildir's shelf: its front falls 2.6 in 1 by 8 m out (the levelling's smoothing hides the cliff
    /// for the first 4 m), its west edge 2.2; the slope that meets it on the east is joined. Its south-west corner, where a
    /// landing was finished by hand, falls 1.35 in 1 over its first 2 m on the game's ground (two triangles a cell,
    /// BuriedPieces.BetweenVertices), so it is not joined.</summary>
    public const float JoinReach = 12f, JoinStep = 2f, JoinGrade = 1.25f;
    /// <summary>Most joined edges aimed at, and how far apart.</summary>
    public const int JoinedEdges = 2;
    public const float JoinApart = 8f;
    /// <summary>Most a metre of the landing's level ground may rise or fall to the next and still be walked
    /// onto the landing from: a stair's own grade.</summary>
    public const float WalkStep = 0.5f;

    /// <summary>The existing connected landing-height cells, fixed once for an approach and its ground snapshot.</summary>
    public sealed class LandingRegion
    {
        internal readonly List<(Vector2 at, float height)> Landings;
        internal readonly HashSet<(int x, int z)> Cells;
        /// <summary>The landing's lip, where its level ground meets the slope: each cell beside ground left out for its height
        /// or step only (not the pit or the location's rock, which no approach comes from), with the way out over it.</summary>
        internal readonly List<(Vector2 at, Vector2 outward)> Lip;
        private readonly Func<float, float, float> _ground;
        internal LandingRegion(List<(Vector2 at, float height)> landings, HashSet<(int, int)> cells, List<(Vector2, Vector2)> lip,
            Func<float, float, float> ground)
        { Landings = landings; Cells = cells; Lip = lip; _ground = ground; }

        /// <summary>How far inside the lip <paramref name="at"/> stands on the landing: 0 off it, or where it has no lip.</summary>
        public float Depth(Vector2 at)
        {
            if (Lip.Count == 0 || !Contains(at)) return 0f;
            float nearest = float.PositiveInfinity;
            foreach (var (cell, _) in Lip) nearest = Mathf.Min(nearest, Vector2.Distance(at, cell));
            return nearest;
        }

        /// <summary>
        /// Where an approach aims for <paramref name="aim"/> on the landing: the aim itself within the lip
        /// (<see cref="RoadStairRuns.WithinLip"/>), else the lip cell nearest it. The layout's search keeps every piece within
        /// the lip, so an aim deeper on the landing can never be come near: the level landings of a cave on a knoll stood 9 m
        /// inside its lip, and the staircase up a spur from the road's end at 20,0 found nothing nearer them and was laid from
        /// 24 m further down. Moved to the nearest lip cell each, the landings in front of the mouth stay apart round the lip.
        /// </summary>
        public Vector2 AtLip(Vector2 aim)
        {
            if (RoadStairRuns.WithinLip(Depth(aim))) return aim;
            Vector2 nearest = aim;
            float best = float.PositiveInfinity;
            foreach (var (cell, _) in Lip)
            {
                float d = Vector2.Distance(aim, cell);
                if (d < best) (best, nearest) = (d, cell);
            }
            return nearest;
        }

        // Each visited lattice vertex owns its closed half-metre cell. Contact uses distance to those
        // same cells; neither caller rounds independently nor substitutes the anchor flat's radius.
        public bool Contains(Vector2 at)
        {
            int x = Mathf.FloorToInt(at.x + 0.5f), z = Mathf.FloorToInt(at.y + 0.5f);
            if (Cells.Contains((x, z))) return true;
            bool left = at.x == x - 0.5f, down = at.y == z - 0.5f;
            return left && Cells.Contains((x - 1, z)) || down && Cells.Contains((x, z - 1))
                || left && down && Cells.Contains((x - 1, z - 1));
        }
        public bool Near(Vector2 at, float distance)
        {
            if (Contains(at)) return true;
            int x = Mathf.FloorToInt(at.x), z = Mathf.FloorToInt(at.y);
            int reach = Mathf.CeilToInt(distance + 0.5f);
            for (int row = z - reach; row <= z + reach + 1; row++)
                for (int col = x - reach; col <= x + reach + 1; col++)
                    if (Cells.Contains((col, row)))
                    {
                        float dx = Mathf.Max(0f, FloatBits.Abs(at.x - col) - 0.5f);
                        float dz = Mathf.Max(0f, FloatBits.Abs(at.y - row) - 0.5f);
                        if (dx * dx + dz * dz <= distance * distance) return true;
                    }
            return false;
        }
        public float Height(Vector2 at) => _ground(at.x, at.y);
    }

    private static readonly (int dx, int dz)[] LandingNeighbors = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    public LandingRegion ConnectedLanding(Func<float, float, float> ground, float clearance)
    {
        var landings = Landings(ground, clearance);
        float outer = Edge + LandingSearch;
        bool Inside(int x, int z) => Vector2.Distance(new Vector2(x, z), FlatCentre) <= outer;
        bool Walkable(int x, int z) => Inside(x, z) && Refusal(new Vector2(x, z), clearance, clearance) == "";
        var region = new HashSet<(int, int)>();
        var queue = new Queue<(int x, int z, float level)>();
        foreach (var (at, height) in landings)
        {
            var cell = (Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y));
            if (Walkable(cell.Item1, cell.Item2) && region.Add(cell)) queue.Enqueue((cell.Item1, cell.Item2, height));
        }
        while (queue.Count > 0)
        {
            var (x, z, level) = queue.Dequeue();
            float here = ground(x, z);
            foreach (var (dx, dz) in LandingNeighbors)
            {
                var next = (x + dx, z + dz);
                if (region.Contains(next) || !Walkable(next.Item1, next.Item2)) continue;
                float there = ground(next.Item1, next.Item2);
                // Level with the landing it is walked from (StairLayout.StairFitsFrom), not only a step from the
                // last metre: stepping down WalkStep a metre, the edge aimed at at Hildir's cave lay a metre under its
                // landing and 4 m short of it (at -2304,1536).
                if (FloatBits.Abs(there - here) > WalkStep || FloatBits.Abs(there - level) > StairLayout.StairFitsFrom) continue;
                region.Add(next);
                queue.Enqueue((next.Item1, next.Item2, level));
            }
        }
        var lip = new List<(Vector2, Vector2)>();
        foreach (var (x, z) in region)
        {
            Vector2 outward = new Vector2(0f, 0f);
            foreach (var (dx, dz) in LandingNeighbors)
                if (!region.Contains((x + dx, z + dz)) && Walkable(x + dx, z + dz)) outward += new Vector2(dx, dz);
            if (outward.sqrMagnitude >= 0.5f) lip.Add((new Vector2(x, z), outward.normalized));
        }
        return new LandingRegion(landings, region, lip, ground);
    }

    /// <summary>
    /// Where an approach aims, best first (Hildir's cave: its levelled shelf stands
    /// 10-20 m proud of the face below its mouth; if the landing is a big overhang that part of it cannot be reached, but
    /// coming up where it connects to the mountain it likely can be). The
    /// ground walked onto the landings (<see cref="Landings"/>; a metre to the next rising or falling
    /// <see cref="WalkStep"/> at most and level with the landing it is walked from, within
    /// <see cref="StairLayout.StairFitsFrom"/>, clear of the pit and rock, out to <see cref="LandingSearch"/> past the flat) is followed out to its edges; an edge is joined where the ground outside it falls no more than
    /// <see cref="JoinGrade"/> over <see cref="JoinReach"/>, and overhangs where it falls further. Aims: up to
    /// <see cref="JoinedEdges"/> joined edges <see cref="JoinApart"/> apart (out of the water) and the landings that are
    /// themselves joined, nearest <paramref name="from"/> first, then the overhanging landings; at most <paramref name="tries"/>. The face below
    /// Hildir's shelf took a staircase 24000 steps of stall to climb onto its front; its east edge,
    /// where the slope meets the shelf, 8000.
    /// </summary>
    public List<(string name, Vector2 at)> Aims(Func<float, float, float> ground, Vector2 from, float clearance, int tries)
    {
        if (Door != null) return EntranceAims(ground, from, clearance, tries);
        return Aims(ground, from, clearance, tries, ConnectedLanding(ground, clearance));
    }

    internal List<(string name, Vector2 at)> Aims(Func<float, float, float> ground, Vector2 from, float clearance, int tries, LandingRegion landing)
    {
        if (Door != null) return EntranceAims(ground, from, clearance, tries);
        var landings = landing.Landings;
        var aims = new List<(string name, Vector2 at, bool joined, float distance)>();
        if (landings.Count == 0) return new List<(string, Vector2)>();
        // Joined: the ground outside, away from the location and clear of its pit and rock, falls away no steeper than
        // JoinGrade as far as JoinReach, and does not rise into a wall.
        bool Joined(Vector2 at, Vector2 outward)
        {
            if (Vector2.Dot(outward, at - FlatCentre) <= 0f) return false;
            float h = ground(at.x, at.y);
            for (float d = JoinStep; d <= JoinReach + 0.01f; d += JoinStep)
            {
                Vector2 beyond = at + outward * d;
                if (Refusal(beyond, clearance, clearance) != "") return false;
                float drop = h - ground(beyond.x, beyond.y);
                if (drop > JoinGrade * d || (d <= JoinStep && drop < -LandingRise)) return false;
            }
            return true;
        }
        // Over the landing's lip (LandingRegion.Lip).
        foreach (var (at, outward) in landing.Lip)
            if (RoadWater.BuiltDry(ground(at.x, at.y)) && Joined(at, outward))
                aims.Add(("joined edge", at, true, Vector2.Distance(at, from)));
        // At most JoinedEdges edges, JoinApart apart (one per side, not a row along one), then every landing:
        // edges alone crowded out a Dvergr entrance's own landing, which a staircase had reached (at
        // 7872,1920). Joined ones first, nearest the road's end first; overhanging landings last.
        aims.Sort((a, b) => a.distance.CompareTo(b.distance));
        var edges = new List<(string name, Vector2 at, bool joined, float distance)>();
        foreach (var a in aims)
            if (edges.Count < JoinedEdges && !edges.Exists(q => Vector2.Distance(q.at, a.at) < JoinApart)) edges.Add(a);
        // Each landing aimed at within the lip, where the search may come (LandingRegion.AtLip).
        foreach (var (at, _) in landings)
        {
            Vector2 aim = landing.AtLip(at);
            if (!edges.Exists(q => Vector2.Distance(q.at, aim) < LandingApart))
                edges.Add(("level landing", aim, Joined(at, Front), Vector2.Distance(aim, from)));
        }
        edges.Sort((a, b) => a.joined != b.joined ? (a.joined ? -1 : 1) : a.distance.CompareTo(b.distance));
        var picked = new List<(string name, Vector2 at)>();
        foreach (var a in edges)
        {
            picked.Add((a.name, a.at));
            if (picked.Count == tries) break;
        }
        return picked;
    }

    /// <summary>A staircase onto an entrance aims where a road onto it ends (<see cref="Ends"/>, the one owner): toward the entrance,
    /// on its landing's lip nearest the road's end <paramref name="from"/>, out from under the Location's rock; never past the lip up
    /// to the door. None where no end is clear: the staircase is then refused, never laid on into the entrance.</summary>
    private List<(string name, Vector2 at)> EntranceAims(Func<float, float, float> ground, Vector2 from, float clearance, int tries) =>
        Ends(ground, from, clearance, tries).ConvertAll(e => ("entrance landing", e.at));

    /// <summary>Whether an approach from <paramref name="roadEnd"/> to <paramref name="target"/> is led round the location's rock
    /// first, the road onto its landing (RoadLocationEntry.Route) and a staircase to it (RoadStairRuns.Approach) alike: the
    /// location has a rock to go round (<see cref="RockRadius"/>), is not come to from every side (<see cref="AllRound"/>: no
    /// front and nothing to lead round), and the end is off its entrance side (not <see cref="InFront"/>, the floor's cone; on
    /// the floor itself, no direction, it is led round too). Each then circles the rock about its physical <see cref="Centre"/>
    /// on its lead-round ring (<see cref="LeadRoundRing(Walker)"/>). Where the body is known (<see cref="BodyKnown"/>) the body,
    /// not the radius, keeps a way off (<see cref="Refusal"/>): the ring then only leads round, and only a way whose straight
    /// line in its body refuses for the <paramref name="walker"/> (<see cref="KeepsOffAllTheWay"/>); it never refuses a point.
    /// Before, a staircase asked the line through the centre and a road the floor's cone: an end between the two was led
    /// round by one and not the other.</summary>
    public bool Behind(Vector2 roadEnd, Vector2 target, Walker walker) =>
        !AllRound && RockRadius > 0f && !InFront(roadEnd) && (!BodyKnown || !KeepsOffAllTheWay(roadEnd, target, walker));

    /// <summary>Whether every metre of the straight line from <paramref name="from"/> to <paramref name="to"/> keeps off the
    /// location for <paramref name="walker"/> (<see cref="KeepsOff"/>).</summary>
    public bool KeepsOffAllTheWay(Vector2 from, Vector2 to, Walker walker)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to)));
        for (int k = 0; k <= steps; k++)
            if (KeepsOff(Vector2.Lerp(from, to, (float)k / steps), walker) != "") return false;
        return true;
    }
}
