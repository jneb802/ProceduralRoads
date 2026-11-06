using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A rock standing in a place's footprint: the place's own, read off its saved
/// objects (PlacedSiteReader). Its colliders' box seen from above, turned with the rock
/// about the vertical (<see cref="Yaw"/>, degrees, as Unity turns): a circle out to the box's
/// corner made a long rock half as wide again as it is (rock1_mountain 17.5 m).</summary>
public readonly struct SiteRock
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
    public SiteRock(Vector2 at, float halfX, float halfZ, float yaw, float minY, float maxY, string name = "rock",
        float[]? top = null, int topX0 = 0, int topZ0 = 0, int topW = 0)
    {
        At = at; HalfX = halfX; HalfZ = halfZ; Yaw = yaw; MinY = minY; MaxY = maxY; Name = name;
        Top = top; TopX0 = topX0; TopZ0 = topZ0; TopW = topW;
    }

    /// <summary>
    /// Whether the rock is in the way of something at <paramref name="p"/> that keeps
    /// <paramref name="margin"/> off it, walked on <paramref name="ground"/> (NaN: any height);
    /// <paramref name="d"/>, how near it comes. Only where it reaches into a walker's box there
    /// (<see cref="RockClearances.Bottom"/> to <see cref="RockClearances.Top"/> over the ground, the
    /// one owner of what a walker needs clear): by its real top where known, else by its box.
    /// </summary>
    public bool InTheWay(Vector2 p, float margin, float ground, out float d)
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
                float dist = Mathf.Max(0f, Vector2.Distance(p, new Vector2(x, z)) - 0.5f);
                if (dist < d) d = dist;
            }
        return d < margin;
    }
    /// <summary>A square rock <paramref name="half"/> each way from its middle, of any height.</summary>
    public SiteRock(Vector2 at, float half, string name = "rock")
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
        float ex = Mathf.Max(Mathf.Abs(local.x) - HalfX, 0f), ez = Mathf.Max(Mathf.Abs(local.y) - HalfZ, 0f);
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

    /// <summary>Whether it stands round the mouth of a place at <paramref name="centre"/> whose exterior radius is
    /// <paramref name="rockRadius"/> (none without one): its box's middle inside that radius.</summary>
    public bool RoundItsMouth(Vector2 centre, float rockRadius) => rockRadius > 0f && Vector2.Distance(At, centre) <= rockRadius;

    /// <summary>Whether a place's rock is kept on an approach to the place (the one owner of the rule; each caller measures
    /// the rock its own way): a <paramref name="large"/> one (<see cref="LargeRock"/> or more across), any at a place that
    /// keeps every rock (<paramref name="keepsEveryRock"/>, <see cref="KeepsEveryRock"/>), or any
    /// <paramref name="roundItsMouth"/> (inside its exterior radius). <paramref name="why"/>: the first of these that holds,
    /// in that order ("" when none does).</summary>
    public static bool Kept(bool large, bool keepsEveryRock, bool roundItsMouth, out string why)
    {
        why = large ? "large" : keepsEveryRock ? "a place keeping every rock" : roundItsMouth ? "round its mouth" : "";
        return large || keepsEveryRock || roundItsMouth;
    }

    /// <summary><see cref="Kept(bool, bool, bool, out string)"/>, without the reason.</summary>
    public static bool Kept(bool large, bool keepsEveryRock, bool roundItsMouth) => Kept(large, keepsEveryRock, roundItsMouth, out _);

    /// <summary>
    /// Whether this rock is kept on an approach to the place at <paramref name="centre"/> (<see cref="Kept(bool, bool, bool, out string)"/>):
    /// the one question planning (the shape, the reader) and the rock clearing's probe both ask, of the same box: its oriented
    /// size (<see cref="Large"/>) and its middle (<see cref="RoundItsMouth"/>).
    /// </summary>
    public bool KeptAt(Vector2 centre, float rockRadius, bool keepsEveryRock, out string why) =>
        Kept(Large, keepsEveryRock, RoundItsMouth(centre, rockRadius), out why);

    /// <summary>A rock's box seen from above: the corners of its colliders' box (<paramref name="local"/>, in its root's frame),
    /// scaled and turned as saved, measured along and across its turn about the vertical.</summary>
    public static SiteRock Of(Vector3 position, Quaternion rotation, Vector3 scale, Bounds local, string name)
    {
        float yaw = rotation.eulerAngles.y;
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3(local.center.x + ((i & 1) == 0 ? -1f : 1f) * local.extents.x,
                local.center.y + ((i & 2) == 0 ? -1f : 1f) * local.extents.y, local.center.z + ((i & 4) == 0 ? -1f : 1f) * local.extents.z);
            Vector3 w = rotation * new Vector3(corner.x * scale.x, corner.y * scale.y, corner.z * scale.z);
            Vector2 q = Local(new Vector2(w.x, w.z), yaw);
            minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x);
            minZ = Mathf.Min(minZ, q.y); maxZ = Mathf.Max(maxZ, q.y);
            minY = Mathf.Min(minY, position.y + w.y); maxY = Mathf.Max(maxY, position.y + w.y);
        }
        Vector2 middle = new Vector2(position.x, position.z) + World(new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f), yaw);
        return new SiteRock(middle, (maxX - minX) * 0.5f, (maxZ - minZ) * 0.5f, yaw, minY, maxY, name);
    }

    /// <summary>A place none of whose rocks an approach may clear, small ones included: Hildir's
    /// cave, whose boulder blocking the entrance is not disturbed (the landing near by is still
    /// reached).</summary>
    public static bool KeepsEveryRock(string placeName) => placeName.StartsWith("Hildir", StringComparison.Ordinal);
}

/// <summary>
/// Where an approach to a placed place may go, for a road onto its landing
/// (<see cref="RoadSiteEntry"/>) and a staircase to it
/// (<see cref="RoadStairRuns.PlanPlacedSiteApproach"/>) alike: the landing in front of
/// its floor, the entrance pit (the floor and its smoothing), the rock round it (its
/// exterior radius, but for a cone in front of its floor) and its own boulders. Pure.
/// </summary>
public sealed class PlacedSiteShape
{
    public readonly Vector2 Centre, FlatCentre, Front, Landing;
    public readonly float Pit, KeepOut, RockRadius;
    /// <summary>Whether the place's root height is known (else no landing is held to the place's level, <see cref="Landings"/>).</summary>
    public readonly bool RootKnown;
    /// <summary>How far out from the flat's middle its outer edge is: the pit's edge for a sunk
    /// floor, the flat's own edge for one level with the place. Landings nearest it come first.</summary>
    public readonly float Edge;
    /// <summary>A flat this far or more under the place's root is a floor dug behind the mouth (a frost
    /// cave's, 4.09 m down): a pit to keep out of. Less, it is the ground in front of the mouth (a
    /// BearCave's): the landing itself (a road that wraps around low ends way below
    /// the cave entrance).</summary>
    public const float SunkDepth = 1.5f;
    /// <summary>How far the ground a step outside a landing's edge may rise over it before the edge meets a wall, not a slope
    /// (<see cref="Aims"/>). A landing itself is held to the place's arrival height (<see cref="Landings"/>).</summary>
    public const float LandingRise = 2f;
    public readonly IReadOnlyList<SiteRock> Rocks;
    /// <summary>The boulders standing inside its exterior radius: its body, round its mouth.</summary>
    public readonly IReadOnlyList<SiteRock> Body;
    private readonly float _cosCone;

    private readonly Func<float, float, float>? _ground;

    /// <summary>The place's own door landing, where its stairs meet the ground (<see cref="DoorLandings"/>), if it has one: the
    /// only landing aimed at.</summary>
    public readonly Vector2? Door;

    /// <summary>Whether the place is entered only from its front, its back blocked (<see cref="FrontOnly"/>): network at its
    /// ring reaches it only on its entrance side (<see cref="OnEntranceSide"/>).</summary>
    public readonly bool EnteredFromFront;

    /// <summary>Whether the place is come to from every side (<see cref="OnLevelGround"/>: a place that levels nothing and is
    /// landed on the level ground its smoothing leaves beside it): no front cone, no keep-out round its exterior radius, and
    /// no way led round behind it; its own pieces and boulders (<see cref="Body"/>) are still kept clear of.</summary>
    public readonly bool AllRound;

    /// <summary>The landing found on the place's level ground (<see cref="OnLevelGround"/>), if it was: its landing.</summary>
    private readonly Vector2? _found;

    private PlacedSiteShape(Vector2 centre, Vector2 flatCentre, Vector2 front, float pit, float edge, bool rootKnown, float rockRadius,
        IReadOnlyList<SiteRock> rocks, Func<float, float, float>? ground, bool keepSmallRocks, Vector2? door = null, bool enteredFromFront = false,
        bool allRound = false, Vector2? found = null)
    {
        Door = door;
        EnteredFromFront = enteredFromFront;
        AllRound = allRound;
        _found = found;
        Centre = centre; FlatCentre = flatCentre; Front = front; Pit = pit; Edge = edge; RootKnown = rootKnown; RockRadius = rockRadius; _ground = ground;
        // A boulder standing inside the place's exterior radius is its body, round its mouth:
        // never touched, earthwork included, where it stands at the walking level (seen
        // from above, the box of a frost cave's rock2_mountain 0.9 m from its centre covered
        // the mouth). The large boulders round it are hugged as ground is (the road and stairs hug the
        // larger boulders much like terrain); the small ones are cleared out of the way, but at a place whose every rock
        // is kept (Hildir's cave: a boulder blocking the entrance is not disturbed).
        // One whose box covers the landing itself is the mound the mouth is cut into: the
        // landing in front of the mouth is clear, and its box overstates it there. With its real top known it
        // stays: where it stands is then known.
        KeepOut = rockRadius + RoadStairRuns.RockMargin;
        Landing = LandingFor(0f);
        var kept = new List<SiteRock>();
        var body = new List<SiteRock>();
        foreach (SiteRock rock in rocks)
        {
            if (rock.RoundItsMouth(centre, rockRadius))
            {
                // Come to from every side, every piece of its body stands: the landing is found clear of them.
                if (rock.Top != null || allRound || rock.Distance(Landing) > 0f) body.Add(rock);
            }
            // The rest of its kept rocks (those round its mouth are its body, above).
            else if (SiteRock.Kept(rock.Large, keepSmallRocks, roundItsMouth: false)) kept.Add(rock);
        }
        Rocks = kept;
        Body = body;
        _cosCone = Mathf.Cos(RoadStairRuns.FrontCone * Mathf.Deg2Rad);
    }

    /// <summary>The shape of a place turned to <paramref name="yaw"/>, levelled by <paramref name="flat"/>.
    /// A boulder is in the way only where it reaches into a walker's box over <paramref name="ground"/>
    /// (<see cref="SiteRock.InTheWay"/>; any height without it):
    /// seen from above, a rock2_mountain 12 m down the slope below a frost cave's ledge covered
    /// the ledge.</summary>
    /// <paramref name="root"/>: the place's root height (NaN: unknown, no landing is held to the
    /// entrance's level).</summary>
    public static PlacedSiteShape Of(Vector2 centre, float yaw, LevelOp flat, float rockRadius, IReadOnlyList<SiteRock>? rocks,
        Func<float, float, float>? ground = null, bool keepSmallRocks = false, float root = float.NaN, string? prefab = null)
    {
        bool sunk = flat.Height <= -SunkDepth;
        float pit = sunk ? Mathf.Max(flat.Radius, flat.SmoothRadius) : 0f;
        // A place with a door faces out of it (a crypt's may face its prefab's back), else its prefab's front.
        Vector2 front = prefab != null && DoorLandings.TryGetValue(prefab, out Vector2 door) && door.y < 0f
            ? -StairLayout.Forward(yaw) : StairLayout.Forward(yaw);
        return new(centre, centre + LocationLevelling.OffsetAt(flat, yaw), front, pit,
            sunk ? pit : flat.Radius, !float.IsNaN(root), rockRadius, rocks ?? Array.Empty<SiteRock>(), ground, keepSmallRocks,
            DoorLanding(prefab, centre, yaw), FrontOnly(prefab));
    }

    /// <summary>The flat a place with no levelled flat of its own is shaped round, its centre at its root: one with a door
    /// (a Crypt2 only smooths), whose landing is its door landing (<see cref="DoorLandings"/>), and one landed on the level
    /// ground its smoothing leaves (<see cref="OnLevelGround"/>).</summary>
    public static readonly LevelOp DoorFlat = new(0f, 0f, 0f, 0f, false);

    /// <summary>How far off the place's body (its pieces and boulders, <see cref="Body"/>) the landing on its level ground is
    /// found (<see cref="OnLevelGround"/>): the clearance a staircase onto a landing keeps
    /// (<see cref="RoadStairRuns.PlanPlacedSiteApproach"/>), a road's its own half width and <see cref="BodyGap"/> more.</summary>
    public const float LevelGroundClearance = 1f;

    /// <summary>
    /// The shape of a place that levels no flat but smooths the ground round it (a Mountain tower ruin's five smoothings, a
    /// cabin's one), landed on the level ground that leaves beside it: the one owner of "smoothing level enough to stand on
    /// is a flat". The landing finder's own test (<see cref="Landings"/>: <see cref="LevelTolerance"/> over
    /// <see cref="LevelRadius"/>, out to <see cref="LandingSearch"/>) on the place's finished ground
    /// (<paramref name="ground"/>, <see cref="PlacedSiteGround"/>), all round it (<see cref="AllRound"/>: a ruin has no
    /// mouth and no rock base; its exterior radius is no keep-out), <see cref="LevelGroundClearance"/> off its own pieces and
    /// boulders (<paramref name="rocks"/>, its <see cref="Body"/>), nearest its centre first; the landing is that point, held
    /// to its own ground. Null where none is level (a cabin's one smoothing on a steep slope): the place is then reached at
    /// its ring (<see cref="PlacedSites.FlatlessPlaced"/>). A StoneTowerRuins04 on a Mountain slope rising 1.4 in 1 left a
    /// pad at 192.7 m 4 to 7 m south-east of its centre, raised 1.6 m over its root by its fourth smoothing, where a player
    /// stood beside the tower; its road had ended at its ring, 33 m off.
    /// </summary>
    public static PlacedSiteShape? OnLevelGround(Vector2 centre, float yaw, float rockRadius, IReadOnlyList<SiteRock>? rocks,
        Func<float, float, float> ground, bool keepSmallRocks, float root, string? prefab)
    {
        // A place entered only from its front keeps its front (it levels its flat in any case).
        if (FrontOnly(prefab)) return null;
        IReadOnlyList<SiteRock> all = rocks ?? Array.Empty<SiteRock>();
        Vector2 front = StairLayout.Forward(yaw);
        // Searched at any height (no entrance level yet), then held to the landing's own.
        var search = new PlacedSiteShape(centre, centre, front, 0f, 0f, false, rockRadius, all, ground, keepSmallRocks, allRound: true);
        var found = search.Landings(ground, LevelGroundClearance, 1);
        if (found.Count == 0) return null;
        return new PlacedSiteShape(centre, centre, front, 0f, 0f, !float.IsNaN(root), rockRadius, all, ground, keepSmallRocks,
            allRound: true, found: found[0].at);
    }

    /// <summary>
    /// Where a place's road ends in front of its door, in the prefab's frame (x across, z out of its front; a door on the
    /// prefab's back has z under 0): the one owner of "where a road ends at a place" for a place with a door. Such a place is
    /// placed first wherever it is (its door turns with it, <see cref="PlacedSites.NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/>)
    /// and reached only on this landing (<see cref="RoadNetworkGenerator.Reached"/>). A Dvergr boss entrance's door is up its
    /// own stairs, and its flat's edge lay against the entrance building (at the Queen's entrance 7552,3392, yaw 315,
    /// the road ended at its west wall, 6 m from the stairs' foot at about 7538,3409, going under the
    /// structure). The crypts': their roads stopped at the edge of everything they paint or level, 16 m (Crypt2) to
    /// 38 m (Crypt4) from the door (49 of 49 reached crypts on one test world). Measured from the game's prefabs (their Gateway, the
    /// door's face and the crypt's own stairs, by level rays through their colliders), each landing on the ground the crypt
    /// levels to its root in front of its door, clear of its rubble: Crypt4 and Hildir's crypt face +z, door face at z 7.5,
    /// rubble to z 10.6 at x 2.5 to 3, the door's sinks level the ground to z 15; Crypt3 is entered down its own stairs
    /// from -z, their top at z -8 on ground its sinks level out to z -12.4; Crypt2's door faces -z, its face at z -4.9,
    /// a passage to its Gateway at the centre. Not the swamp's SunkenCrypt4 (entered from -z over its porch, front at z -6,
    /// landing about (0, -9.5)): its road onto that landing failed at 17 of 19 on one test world offline, mostly in water, so it keeps
    /// its ring end. The location prefab names no entrance or door child the offline model reads,
    /// so it is measured.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Vector2> DoorLandings = new Dictionary<string, Vector2>
    {
        ["Mistlands_DvergrBossEntrance1"] = new Vector2(2.1f, 21.9f),
        ["Crypt2"] = new Vector2(0.5f, -8.5f),
        ["Crypt3"] = new Vector2(0f, -10.5f),
        ["Crypt4"] = new Vector2(0f, 11.5f),
        ["Hildir_crypt"] = new Vector2(0f, 11.5f),
    };

    /// <summary>Whether <paramref name="prefab"/> has a door its road ends in front of (<see cref="DoorLandings"/>).</summary>
    public static bool HasDoor(string? prefab) => prefab != null && DoorLandings.ContainsKey(prefab);

    /// <summary>
    /// The places entered only from their front, their back the blocked rock or wall of the building: the Dvergr town
    /// entrances, a mine shaft (Mistlands_DvergrTownEntrance1, its gate on the prefab's +z) and the same building as the
    /// Queen's entrance (Mistlands_DvergrTownEntrance2, its stairs up to the door on +z, their foot at z 22.25 on the flat it
    /// levels there), as their prefabs have them. Their landing is already in front (<see cref="Landing"/>, the flat's edge
    /// along <see cref="Front"/>, the placed turn), but a road that got no further than the ring reached them anywhere round
    /// it: at (1344, 7168) and (5760, 3264) the road ended 55 to 60 m from the stairs on the blocked back, in the rock, and
    /// counted the place reached (off level). Such a place is reached on its landing, or at its ring on its entrance side
    /// (<see cref="OnEntranceSide"/>) only, level or, on the fallback, off level; a road is not kept at its ring behind it.
    /// Not a door (<see cref="DoorLandings"/>): the road need not come onto one point in front of the door, nor level.
    /// </summary>
    public static IReadOnlyCollection<string> FrontOnlyPlaces => s_frontOnly;
    private static readonly HashSet<string> s_frontOnly = new()
    {
        "Mistlands_DvergrTownEntrance1",
        "Mistlands_DvergrTownEntrance2",
    };

    /// <summary>Whether <paramref name="prefab"/> is entered only from its front (<see cref="FrontOnlyPlaces"/>).</summary>
    public static bool FrontOnly(string? prefab) => prefab != null && s_frontOnly.Contains(prefab);

    /// <summary>Whether network at <paramref name="p"/> is on the side the place is entered from: anywhere for most places;
    /// for one entered only from its front (<see cref="EnteredFromFront"/>), in front of its floor (<see cref="InFront"/>:
    /// the whole front side of the line through its floor across its facing).</summary>
    public bool OnEntranceSide(Vector2 p) => !EnteredFromFront || InFront(p);

    /// <summary>The world position of <paramref name="prefab"/>'s door landing (<see cref="DoorLandings"/>) for a place at
    /// <paramref name="centre"/> turned to <paramref name="yaw"/>; null for any other place.</summary>
    public static Vector2? DoorLanding(string? prefab, Vector2 centre, float yaw)
    {
        if (prefab == null || !DoorLandings.TryGetValue(prefab, out Vector2 local)) return null;
        float r = yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return centre + new Vector2(local.x * c + local.y * s, -local.x * s + local.y * c);
    }

    public bool InFront(Vector2 p)
    {
        Vector2 d = p - FlatCentre;
        return d.sqrMagnitude > 1e-6f && (AllRound || Vector2.Dot(d.normalized, Front) >= _cosCone);
    }

    /// <summary>Whether something centred at <paramref name="p"/> keeps out of the place: out of
    /// its pit by <paramref name="margin"/>, out of its rock by as much (but in front of its
    /// floor) and of its body's boulders, and <paramref name="rockMargin"/> clear of each
    /// boulder kept outside that.</summary>
    public bool Allowed(Vector2 p, float margin, float rockMargin) => Refusal(p, margin, rockMargin) == "";

    /// <summary>How far a road's own surface keeps off a mouth boulder whose real top is known
    /// (<see cref="SiteRock.Top"/>): its edge 0.5 m clear. Kept its whole earthwork reach off
    /// it (the reach a box needs, overstated as it is), a frost cave's level ledge beside the
    /// mound its mouth is cut into was no landing at all.</summary>
    public const float BodyGap = 0.5f;

    /// <summary>What <paramref name="p"/> runs into (see <see cref="Allowed"/>), for the log; "" for nothing.</summary>
    /// <param name="bodyMargin">How far off a mouth boulder whose real top is known (<paramref name="margin"/> when not given).</param>
    public string Refusal(Vector2 p, float margin, float rockMargin, float bodyMargin = float.NaN)
    {
        float floor = Vector2.Distance(p, FlatCentre);
        // Come to from every side, it has no entrance: its body is what is kept off.
        if (!AllRound && floor < Pit + margin) return FormattableString.Invariant($"in the entrance ({floor:F1} m from the floor, {Pit + margin:F1} kept)");
        float centre = Vector2.Distance(p, Centre);
        if (RockRadius > 0f && centre < KeepOut + margin && !InFront(p))
            return FormattableString.Invariant($"in the place's rock ({centre:F1} m from its centre, {KeepOut + margin:F1} kept outside the front)");
        float h = _ground?.Invoke(p.x, p.y) ?? 0f;
        float g = _ground != null ? h : float.NaN;
        foreach (SiteRock rock in Body)
        {
            float keep = rock.Top != null && !float.IsNaN(bodyMargin) ? bodyMargin : margin;
            if (rock.InTheWay(p, keep, g, out float d))
                return FormattableString.Invariant($"at its mouth's {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}) ({2f * rock.HalfX:F1} by {2f * rock.HalfZ:F1} m turned {rock.Yaw:F0}; {d:F1} m off it, {keep:F1} kept)");
        }
        foreach (SiteRock rock in Rocks)
        {
            if (rock.InTheWay(p, rockMargin, g, out float d))
                return FormattableString.Invariant($"on its {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}) ({2f * rock.HalfX:F1} by {2f * rock.HalfZ:F1} m turned {rock.Yaw:F0}; {d:F1} m off it, {rockMargin:F1} kept)");
        }
        return "";
    }

    /// <summary>The landing's aim for an approach that keeps <paramref name="clearance"/> off the pit:
    /// <see cref="Landing"/>, or further out in front where that is not enough.</summary>
    public Vector2 LandingFor(float clearance) =>
        Door is Vector2 door ? door : _found is Vector2 found ? found : Pit > 0f ? FlatCentre + Front * (Pit + Mathf.Max(RoadStairRuns.LandingOut, clearance)) : FlatCentre + Front * Edge;

    /// <summary>How far past its pit a landing is looked for, how level it must be (highest less
    /// lowest ground within <see cref="LevelRadius"/>), how far apart the ones tried stand, and how many.</summary>
    public const float LandingSearch = 14f, LevelTolerance = 0.5f, LevelRadius = 2f, LandingApart = 3f;
    /// <summary>How far off the place's front axis a landing may lie, seen from its floor: on the whole
    /// front side, a BearCave's road came in at its side, 64 degrees off; the ledge of a frost cave was 45 off.</summary>
    public const float LandingCone = 50f;
    public const int LandingTries = 3;

    /// <summary>
    /// The level ground beside the place's mouth, nearest the flat's outer edge on its front
    /// axis first, level with the place: within <see cref="RoadNetworkGenerator.ReachRise"/> of the ground at its
    /// <see cref="Landing"/>, the height a road reaches the place at (<see cref="RoadNetworkGenerator.ArrivalHeight"/>), since
    /// a road or staircase carried onto a landing reaches the place (<see cref="PlacedSites.MarkLanded"/>). Held to 2 m of the
    /// place's root before, a road was carried onto a shelf 1.8 m under a Mountain tower's flat and counted it reached; a
    /// cave's root is not the level of the ground in front of its mouth (a frost cave's 2 m above it), its landing point's
    /// ground is. Only where the place's root is known (<see cref="Entrance"/>): every metre within
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
        // No nearer the middle than the flat's edge less a landing's own level ground: a flat level with its place is the place
        // itself. Looked for from just past the clearance, a Dvergr camp's (Mistlands_Excavation3, its flat 17 m, its wall about
        // 14.7 m round it) landings stood 10 to 14 m from its centre, and its road ran on inside the wall line. A sunk floor's
        // edge is its pit's, so its landings stay where they were; so do a road's onto a flat 6.5 m round or less (its
        // clearance is further out). Where none is left, the approach aims at the flat's edge (LandingFor).
        // Come to from every side (AllRound), from its middle out: its body is kept clear of by the refusal below.
        float inner = AllRound ? 0.5f : Mathf.Max(Pit + clearance, Edge - LevelRadius, 0.5f);
        float level = ground(Landing.x, Landing.y);
        var found = new List<(Vector2 at, float height, float distance)>();
        for (int z = Mathf.FloorToInt(FlatCentre.y - outer); z <= Mathf.CeilToInt(FlatCentre.y + outer); z++)
            for (int x = Mathf.FloorToInt(FlatCentre.x - outer); x <= Mathf.CeilToInt(FlatCentre.x + outer); x++)
            {
                var p = new Vector2(x, z);
                float d = Vector2.Distance(p, FlatCentre);
                if (d < inner || d > outer || (!AllRound && Vector2.Dot((p - FlatCentre) * (1f / d), Front) < cosCone)) continue;
                float here = ground(x, z);
                if (RootKnown && Mathf.Abs(here - level) > RoadNetworkGenerator.ReachRise) continue;
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
    /// for the first 4 m), its west edge 2.2; the slope that meets it on the west 0.8.</summary>
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
        /// or step only (not the pit or the place's rock, which no approach comes from), with the way out over it.</summary>
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
                        float dx = Mathf.Max(0f, Mathf.Abs(at.x - col) - 0.5f);
                        float dz = Mathf.Max(0f, Mathf.Abs(at.y - row) - 0.5f);
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
                // Level with the landing it is walked from (RoadNetworkGenerator.ArrivalLevel), not only a step from the
                // last metre: stepping down WalkStep a metre, the edge aimed at at Hildir's cave lay a metre under its
                // landing and 4 m short of it (at -2304,1536).
                if (Mathf.Abs(there - here) > WalkStep || Mathf.Abs(there - level) > RoadNetworkGenerator.ArrivalLevel) continue;
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
    /// <see cref="RoadNetworkGenerator.ArrivalLevel"/>, clear of the pit and rock, out to <see cref="LandingSearch"/> past the flat) is followed out to its edges; an edge is joined where the ground outside it falls no more than
    /// <see cref="JoinGrade"/> over <see cref="JoinReach"/>, and overhangs where it falls further. Aims: up to
    /// <see cref="JoinedEdges"/> joined edges <see cref="JoinApart"/> apart (out of the water) and the landings that are
    /// themselves joined, nearest <paramref name="from"/> first, then the overhanging landings; at most <paramref name="tries"/>. The face below
    /// Hildir's shelf took a staircase 24000 steps of stall to climb onto its front; its west edge,
    /// where the slope meets the shelf, 8000.
    /// </summary>
    public List<(string name, Vector2 at)> Aims(Func<float, float, float> ground, Vector2 from, float clearance, int tries)
    {
        if (Door is Vector2 door) return new List<(string, Vector2)> { ("door landing", door) };
        return Aims(ground, from, clearance, tries, ConnectedLanding(ground, clearance));
    }

    internal List<(string name, Vector2 at)> Aims(Func<float, float, float> ground, Vector2 from, float clearance, int tries, LandingRegion landing)
    {
        if (Door is Vector2 door) return new List<(string, Vector2)> { ("door landing", door) };
        var landings = landing.Landings;
        var aims = new List<(string name, Vector2 at, bool joined, float distance)>();
        if (landings.Count == 0) return new List<(string, Vector2)>();
        // Joined: the ground outside, away from the place and clear of its pit and rock, falls away no steeper than
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

    /// <summary>Whether a road's end is behind the place, where an approach is led round its rock.</summary>
    public bool Behind(Vector2 roadEnd) => !AllRound && RockRadius > 0f && Vector2.Dot(roadEnd - Centre, Front) < 0f;
}
