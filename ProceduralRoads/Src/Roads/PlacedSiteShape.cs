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
    /// <paramref name="margin"/> off it, standing on <paramref name="ground"/> (NaN: any height);
    /// <paramref name="d"/>, how near it comes. By its real top where known: only where the rock
    /// stands within <paramref name="below"/> under the ground there or higher, and its bottom
    /// no more than <paramref name="above"/> over it; else by its box.
    /// </summary>
    public bool InTheWay(Vector2 p, float margin, float ground, float below, float above, out float d)
    {
        d = float.PositiveInfinity;
        if (Top == null)
        {
            d = Distance(p);
            return d < margin && (float.IsNaN(ground) || Spans(ground - below, ground + above));
        }
        if (!float.IsNaN(ground) && MinY > ground + above) return false;
        int topH = Top.Length / Math.Max(1, TopW), r = Mathf.CeilToInt(margin) + 1;
        int cx = Mathf.RoundToInt(p.x), cz = Mathf.RoundToInt(p.y);
        for (int z = cz - r; z <= cz + r; z++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                int i = x - TopX0, k = z - TopZ0;
                if (i < 0 || k < 0 || i >= TopW || k >= topH) continue;
                float top = Top[k * TopW + i];
                if (float.IsNaN(top) || (!float.IsNaN(ground) && top < ground - below)) continue;
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
    /// <summary>The height of the entrance's own level, which a landing must be near (NaN: unknown):
    /// the place's root, or for a flat raised or sunk less than <see cref="SunkDepth"/>, the flat.</summary>
    public readonly float Entrance;
    /// <summary>How far out from the flat's middle its outer edge is: the pit's edge for a sunk
    /// floor, the flat's own edge for one level with the place. Landings nearest it come first.</summary>
    public readonly float Edge;
    /// <summary>A flat this far or more under the place's root is a floor dug behind the mouth (a frost
    /// cave's, 4.09 m down): a pit to keep out of. Less, it is the ground in front of the mouth (a
    /// BearCave's): the landing itself (a road that wraps around low ends way below
    /// the cave entrance).</summary>
    public const float SunkDepth = 1.5f;
    /// <summary>How far above or below the entrance's level a landing may be.</summary>
    public const float LandingRise = 2f;
    public readonly IReadOnlyList<SiteRock> Rocks;
    /// <summary>The boulders standing inside its exterior radius: its body, round its mouth.</summary>
    public readonly IReadOnlyList<SiteRock> Body;
    private readonly float _cosCone;

    private readonly Func<float, float, float>? _ground;

    /// <summary>How far below the ground a rock's top may be and still be in the way (a rock
    /// standing on the slope below a ledge is not), and how far above it its bottom.</summary>
    public const float RockBelow = 0.5f, RockHeadroom = 3f;

    /// <summary>The place's own door landing, where its stairs meet the ground (<see cref="DoorLandings"/>), if it has one: the
    /// only landing aimed at.</summary>
    public readonly Vector2? Door;

    private PlacedSiteShape(Vector2 centre, Vector2 flatCentre, Vector2 front, float pit, float edge, float entrance, float rockRadius,
        IReadOnlyList<SiteRock> rocks, Func<float, float, float>? ground, bool keepSmallRocks, Vector2? door = null)
    {
        Door = door;
        Centre = centre; FlatCentre = flatCentre; Front = front; Pit = pit; Edge = edge; Entrance = entrance; RockRadius = rockRadius; _ground = ground;
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
        Landing = door ?? (pit > 0f ? flatCentre + front * (pit + RoadStairRuns.LandingOut) : flatCentre + front * edge);
        var kept = new List<SiteRock>();
        var body = new List<SiteRock>();
        foreach (SiteRock rock in rocks)
        {
            if (rockRadius > 0f && Vector2.Distance(rock.At, centre) <= rockRadius)
            {
                if (rock.Top != null || rock.Distance(Landing) > 0f) body.Add(rock);
            }
            else if (rock.Large || keepSmallRocks) kept.Add(rock);
        }
        Rocks = kept;
        Body = body;
        _cosCone = Mathf.Cos(RoadStairRuns.FrontCone * Mathf.Deg2Rad);
    }

    /// <summary>The shape of a place turned to <paramref name="yaw"/>, levelled by <paramref name="flat"/>.
    /// A boulder is in the way only where it stands between <see cref="RockBelow"/> under
    /// <paramref name="ground"/> and <see cref="RockHeadroom"/> over it (any height without it):
    /// seen from above, a rock2_mountain 12 m down the slope below a frost cave's ledge covered
    /// the ledge.</summary>
    /// <paramref name="root"/>: the place's root height (NaN: unknown, no landing is held to the
    /// entrance's level).</summary>
    public static PlacedSiteShape Of(Vector2 centre, float yaw, LevelOp flat, float rockRadius, IReadOnlyList<SiteRock>? rocks,
        Func<float, float, float>? ground = null, bool keepSmallRocks = false, float root = float.NaN, string? prefab = null)
    {
        bool sunk = flat.Height <= -SunkDepth;
        float pit = sunk ? Mathf.Max(flat.Radius, flat.SmoothRadius) : 0f;
        float entrance = float.IsNaN(root) ? float.NaN : sunk ? root : root + flat.Height;
        return new(centre, centre + LocationLevelling.OffsetAt(flat, yaw), StairLayout.Forward(yaw), pit,
            sunk ? pit : flat.Radius, entrance, rockRadius, rocks ?? Array.Empty<SiteRock>(), ground, keepSmallRocks,
            DoorLanding(prefab, centre, yaw));
    }

    /// <summary>
    /// Where a place's own stairs meet the ground in front of its door, in the prefab's frame (x across, z out of its front),
    /// for the places whose landing is not their levelled flat's edge: a Dvergr boss entrance's door is up its own stairs,
    /// and its flat's edge lay against the entrance building (at the Queen's entrance 7552,3392, yaw 315,
    /// the road ended at its west wall, 6 m from the stairs' foot at about 7538,3409, going under the
    /// structure). The location prefab names no entrance or door child the offline model reads, so it is measured.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Vector2> DoorLandings = new Dictionary<string, Vector2>
    {
        ["Mistlands_DvergrBossEntrance1"] = new Vector2(2.1f, 21.9f),
    };

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
        return d.sqrMagnitude > 1e-6f && Vector2.Dot(d.normalized, Front) >= _cosCone;
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
        if (floor < Pit + margin) return FormattableString.Invariant($"in the entrance ({floor:F1} m from the floor, {Pit + margin:F1} kept)");
        float centre = Vector2.Distance(p, Centre);
        if (RockRadius > 0f && centre < KeepOut + margin && !InFront(p))
            return FormattableString.Invariant($"in the place's rock ({centre:F1} m from its centre, {KeepOut + margin:F1} kept outside the front)");
        float h = _ground?.Invoke(p.x, p.y) ?? 0f;
        float g = _ground != null ? h : float.NaN;
        foreach (SiteRock rock in Body)
        {
            float keep = rock.Top != null && !float.IsNaN(bodyMargin) ? bodyMargin : margin;
            if (rock.InTheWay(p, keep, g, RockBelow, RockHeadroom, out float d))
                return FormattableString.Invariant($"at its mouth's {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}) ({2f * rock.HalfX:F1} by {2f * rock.HalfZ:F1} m turned {rock.Yaw:F0}; {d:F1} m off it, {keep:F1} kept)");
        }
        foreach (SiteRock rock in Rocks)
        {
            if (rock.InTheWay(p, rockMargin, g, RockBelow, RockHeadroom, out float d))
                return FormattableString.Invariant($"on its {rock.Name} at ({rock.At.x:F1}, {rock.At.y:F1}) ({2f * rock.HalfX:F1} by {2f * rock.HalfZ:F1} m turned {rock.Yaw:F0}; {d:F1} m off it, {rockMargin:F1} kept)");
        }
        return "";
    }

    /// <summary>The landing's aim for an approach that keeps <paramref name="clearance"/> off the pit:
    /// <see cref="Landing"/>, or further out in front where that is not enough.</summary>
    public Vector2 LandingFor(float clearance) =>
        Door is Vector2 door ? door : Pit > 0f ? FlatCentre + Front * (Pit + Mathf.Max(RoadStairRuns.LandingOut, clearance)) : FlatCentre + Front * Edge;

    /// <summary>How far past its pit a landing is looked for, how level it must be (highest less
    /// lowest ground within <see cref="LevelRadius"/>), how far apart the ones tried stand, and how many.</summary>
    public const float LandingSearch = 14f, LevelTolerance = 0.5f, LevelRadius = 2f, LandingApart = 3f;
    /// <summary>How far off the place's front axis a landing may lie, seen from its floor: on the whole
    /// front side, a BearCave's road came in at its side, 64 degrees off; the ledge of a frost cave was 45 off.</summary>
    public const float LandingCone = 50f;
    public const int LandingTries = 3;

    /// <summary>
    /// The level ground beside the place's mouth, nearest the flat's outer edge on its front
    /// axis first, within <see cref="LandingRise"/> of the entrance's level: every metre within
    /// <see cref="LandingCone"/> of its front axis, seen from its floor, <paramref name="clearance"/> clear of its pit, rock and boulders, out to
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
        var found = new List<(Vector2 at, float height, float distance)>();
        for (int z = Mathf.FloorToInt(FlatCentre.y - outer); z <= Mathf.CeilToInt(FlatCentre.y + outer); z++)
            for (int x = Mathf.FloorToInt(FlatCentre.x - outer); x <= Mathf.CeilToInt(FlatCentre.x + outer); x++)
            {
                var p = new Vector2(x, z);
                float d = Vector2.Distance(p, FlatCentre);
                if (d < Mathf.Max(Pit + clearance, 0.5f) || d > outer || Vector2.Dot((p - FlatCentre) * (1f / d), Front) < cosCone) continue;
                float here = ground(x, z);
                if (!float.IsNaN(Entrance) && Mathf.Abs(here - Entrance) > LandingRise) continue;
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
        var landings = Landings(ground, clearance);
        var aims = new List<(string name, Vector2 at, bool joined, float distance)>();
        if (landings.Count == 0) return new List<(string, Vector2)>();
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
        var steps = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (queue.Count > 0)
        {
            var (x, z, level) = queue.Dequeue();
            float here = ground(x, z);
            foreach (var (dx, dz) in steps)
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
        foreach (var (x, z) in region)
        {
            Vector2 outward = new Vector2(0f, 0f);
            foreach (var (dx, dz) in steps)
                // Toward ground left out for its height or step only: not the pit or the rock, which no approach comes from.
                if (!region.Contains((x + dx, z + dz)) && Inside(x + dx, z + dz) && Refusal(new Vector2(x + dx, z + dz), clearance, clearance) == "")
                    outward += new Vector2(dx, dz);
            if (outward.sqrMagnitude < 0.5f) continue;
            var at = new Vector2(x, z);
            if (RoadWater.BuiltDry(ground(x, z)) && Joined(at, outward.normalized))
                aims.Add(("joined edge", at, true, Vector2.Distance(at, from)));
        }
        // At most JoinedEdges edges, JoinApart apart (one per side, not a row along one), then every landing:
        // edges alone crowded out a Dvergr entrance's own landing, which a staircase had reached (at
        // 7872,1920). Joined ones first, nearest the road's end first; overhanging landings last.
        aims.Sort((a, b) => a.distance.CompareTo(b.distance));
        var edges = new List<(string name, Vector2 at, bool joined, float distance)>();
        foreach (var a in aims)
            if (edges.Count < JoinedEdges && !edges.Exists(q => Vector2.Distance(q.at, a.at) < JoinApart)) edges.Add(a);
        foreach (var (at, _) in landings)
            if (!edges.Exists(q => Vector2.Distance(q.at, at) < LandingApart))
                edges.Add(("level landing", at, Joined(at, Front), Vector2.Distance(at, from)));
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
    public bool Behind(Vector2 roadEnd) => RockRadius > 0f && Vector2.Dot(roadEnd - Centre, Front) < 0f;
}
