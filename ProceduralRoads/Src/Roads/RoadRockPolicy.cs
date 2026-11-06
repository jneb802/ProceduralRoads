using System.Collections.Generic;
using System.Linq;

namespace ProceduralRoads;

/// <summary>Known natural boulders and copper and silver deposits only, never tin, fragments or arbitrary destructibles.</summary>
public static class RoadRockPolicy
{
    /// <summary>Height of the clearance over a walking surface: a player's
    /// (<see cref="PlayerHeight"/>) with a little room over the head.</summary>
    public const float ClearanceHeight = 2f;

    /// <summary>A clearance starts this far above the walking surface (a road's ground, a staircase's walk), so a chunk whose
    /// top only reaches the surface stays.</summary>
    public const float ClearanceLift = 0.1f;

    /// <summary>The player's collision capsule, read from the Player prefab:
    /// 1.85 m tall, 0.49 m radius, standing on its feet.</summary>
    public const float PlayerHeight = 1.85f;
    public const float PlayerWidth = 0.98f;
    /// <summary>The narrowest way a carve leaves through a rock: the player's
    /// width and about a quarter metre either side, so walking it never catches
    /// (the smallest possible hole in the rock the player can
    /// reliably fit through).</summary>
    public const float MinOpening = 1.5f;

    /// <summary>
    /// The way a carve opens through a rock, as the strips of a clearance
    /// <paramref name="width"/> metres wide cut into <paramref name="strips"/>
    /// across the road: every strip reaching within half <see cref="MinOpening"/>
    /// of the road's centre line, so the way runs along the road where a player
    /// walks it, the same from one road point to the next. A clearance no wider
    /// than <see cref="MinOpening"/> is the way in full. The rest of the
    /// clearance keeps what stands in it.
    /// </summary>
    public static (int first, int last) WayStrips(int strips, float width)
    {
        if (strips <= 0) return (0, -1);
        if (width <= MinOpening) return (0, strips - 1);
        float strip = width / strips, half = width * 0.5f, way = MinOpening * 0.5f;
        int first = System.Math.Max(0, (int)System.Math.Floor((half - way) / strip + 1e-4f));
        int last = System.Math.Min(strips - 1, (int)System.Math.Ceiling((half + way) / strip - 1e-4f) - 1);
        return (first, last);
    }

    public const Heightmap.Biome SupportedBiomes = Heightmap.Biome.Mountain |
        Heightmap.Biome.Plains | Heightmap.Biome.BlackForest | Heightmap.Biome.Meadows | Heightmap.Biome.Swamp;

    /// <summary>
    /// Whether a rock standing here may be carved or removed at all: never in
    /// a zone explored before the road network reached the world
    /// (NetworkCutoff), where the rocks are the players' landscape.
    /// </summary>
    public static bool MayTouch(UnityEngine.Vector3 position) => !NetworkCutoff.Excludes(ZoneSystem.GetZone(position));

    /// <summary>A zone is 64 m across: 46 m from its centre reaches its corners.</summary>
    public const float ZoneCornerReach = 46f;

    /// <summary>
    /// Whether the pass looks at a zone at all: it has road, or ANY structure (a bridge or viaduct deck, wooden or Dvergr,
    /// a staircase, a spanned ford) within the reach of its probe -- the same question the probe then asks
    /// (<see cref="StructurePlans.ClearancesNear"/>), so no structure is left to be "reached from its banks" -- and the
    /// zone is not one NetworkCutoff leaves as the players found it.
    /// </summary>
    public static bool ZoneNeedsLook(Vector2s zone, UnityEngine.Vector3 zoneCentre) =>
        MayTouch(zoneCentre) &&
        (RoadSpatialGrid.GetRoadPointsInZone(zone).Count > 0 || StructurePlans.AnyStructureNear(zoneCentre, ZoneCornerReach));

    /// <summary>How many narrow capsules across the road a count of what blocks it uses (the viaduct parapet's overlap rule).</summary>
    public const int Slots = 5;

    /// <summary>Slot <paramref name="k"/> of <paramref name="slots"/> narrow capsules across a road <paramref name="width"/>
    /// wide, which a count of what blocks uses: its centre's offset across the road from the centre line,
    /// and its radius (the road's width shared out between them).</summary>
    public static (float offset, float radius) Slot(int k, int slots, float width)
    {
        float half = width * 0.5f;
        return ((-1f + (2f * k + 1f) / slots) * half, half / slots);
    }
    /// <summary>A rock smaller than this (bounds diagonal, m) is never carved: one in the road's
    /// clearance is removed whole: a small rock is kept or cleared, never cut (keeping it was
    /// measured to leave the centre of the lane blocked at four of 28 sites).</summary>
    public const float CarveMinSize = 12f;

    // Shared rocks also occur outside these biomes. The instance biome and
    // enabled vegetation registration are checked separately before removal.
    // Copper (Black Forest) and silver (Mountain; rock3_silver is the older silver deposit, off in the world's
    // vegetation) deposits are carved like boulders, with no ore left behind: each is a Destructible that swaps
    // for a networked fractured MineRock5 on its first hit, the thing the carve works on. Tin (MineRock_Tin) has
    // no fractured version and the old MineRock_Copper no MineRock5, so they stay.
    private static readonly HashSet<string> NaturalBoulders = new() { "rock1_mountain", "rock2_mountain", "rock3_mountain",
        "rock3_mountain_1", "rock2_heath", "rock4_heath", "rock4_forest", "rock4_coast", "HeathRockPillar", "Rock_3", "Rock_4",
        "Rock_4_plains", "rock4_copper", "silvervein", "rock3_silver" };
    private static readonly HashSet<string> MistlandsBoulders = new() { "rock_mistlands1", "rock_mistlands2" };
    private static readonly HashSet<string> MistlandsCliffs = new() { "cliff_mistlands1", "cliff_mistlands2" };

    public static bool IsNaturalBoulder(string name) => NaturalBoulders.Contains(name);

    // The deposits among them hold ore: the road takes only their chunks in its way, as a pickaxe would.
    private static readonly HashSet<string> ResourceDeposits = new() { "rock4_copper", "silvervein", "rock3_silver" };

    /// <summary>A copper or silver deposit (judged by its policy name, so a fractured one counts as its deposit).</summary>
    public static bool IsResourceDeposit(string name) => ResourceDeposits.Contains(name);

    /// <summary>Whether carving removes a clearable rock in the road whole rather than cutting it: a small rock is
    /// (<see cref="CarveMinSize"/>), a deposit never, whatever its size.</summary>
    public static bool CarveRemovesWhole(string name, float size) => size < CarveMinSize && !IsResourceDeposit(name);

    /// <summary>Whether a frame applying the carves a pass left for later carves one more: the first always (a carve
    /// cannot be split, and the queue must move), more only while the frame is inside <paramref name="budgetMs"/>.</summary>
    public static bool CarvesMoreThisFrame(int carvedThisFrame, double elapsedMs, double budgetMs) =>
        carvedThisFrame == 0 || elapsedMs < budgetMs;

    /// <summary>
    /// Whether an object round a zone keeps the zone from being finished: it will move on the game's next check
    /// (StaticPhysics: falls to its fall height or is pushed up to the ground) and the game simulates it now. The game
    /// moves such an object only inside the active area round the player (StaticPhysics.SUpdate skips it when
    /// ZNetScene.OutsideActiveArea), so one outside it never moves while the player stays where they are, and waiting
    /// for it on a timer never ends.
    /// </summary>
    public static bool HoldsZone(bool movesOnNextCheck, bool simulatedNow) => movesOnNextCheck && simulatedNow;

    /// <summary>What the automatic pass does with a zone it has just looked at.</summary>
    public enum ZoneVerdict
    {
        /// <summary>Nothing left: the zone is finished until it reloads or the network changes.</summary>
        Done,
        /// <summary>Looked at again on the retry timer: something the game or ownership settles over time.</summary>
        RetryOnTimer,
        /// <summary>Only objects the game does not simulate now are still to move: looked at again only when the
        /// player's zone changes (the active area moves with it), the zone reloads or the network changes.</summary>
        WaitForPlayerZone,
    }

    /// <summary>
    /// The verdict on a zone after its pass: <paramref name="holding"/> objects round it the game will still move
    /// (<see cref="HoldsZone"/>), <paramref name="unsimulated"/> that would move but sit outside the game's active area.
    /// Road points without ground or a rock owned elsewhere are retried on the timer, as are simulated movers; a zone
    /// with only unsimulated movers waits for the player's zone to change rather than being marked done, so a rock
    /// that falls into the road once the player comes close is still found.
    /// </summary>
    public static ZoneVerdict JudgeZone(int groundMisses, bool ownedElsewhere, int holding, int unsimulated)
    {
        if (groundMisses > 0 || ownedElsewhere || holding > 0) return ZoneVerdict.RetryOnTimer;
        return unsimulated > 0 ? ZoneVerdict.WaitForPlayerZone : ZoneVerdict.Done;
    }

    /// <summary>Whether the zones waiting for the player's zone are looked at again: the player's zone
    /// (ZNet's reference position, the one the game simulates round) or the road network changed since they waited.</summary>
    public static bool ReleasesWaiting(Vector2s waitedFrom, int waitedVersion, Vector2s playerZoneNow, int versionNow) =>
        waitedFrom.x != playerZoneNow.x || waitedFrom.y != playerZoneNow.y || waitedVersion != versionNow;

    /// <summary>Whether a rock may be removed whole at all (a rock the carve cannot cut, a rock under a deck): a deposit
    /// may not, so its ore is never lost with it; it is carved or kept.</summary>
    public static bool MayRemoveWhole(string name) => !IsResourceDeposit(name);

    /// <summary>Every rock prefab clearing may touch, for road_rock_kinds: the lists above, written once.</summary>
    public static readonly string[] KnownRocks = NaturalBoulders.Concat(MistlandsBoulders).Concat(MistlandsCliffs).ToArray();

    /// <summary>
    /// The names the clearing rules are asked about for an object. A rock
    /// that has been carved (or mined) is its fractured copy, "rock1_mountain_frac",
    /// and is judged as the rocks that fracture into it: one fractured prefab
    /// can serve several (rock_mistlands2 and rock4_forest share
    /// rock4_forest_frac). Anything else is judged by its own name.
    /// </summary>
    public static IReadOnlyList<string> PolicyNames(string name, IReadOnlyDictionary<string, List<string>> fracturedFrom) =>
        fracturedFrom.TryGetValue(name, out var sources) && sources.Count > 0 ? sources : new[] { name };

    /// <summary>
    /// Which chunks to take at one road point so the road stays passable: the
    /// clearance is cut into strips across the road, and every chunk in a strip
    /// of the way (<see cref="WayStrips"/>) goes, nearest the centre line first.
    /// A chunk only in the strips beside the way stays, so a rock keeps its
    /// edges in the road but never the line a player walks. What else stands in
    /// the way (a tree, a rock that is not ours) is judged on its own and
    /// changes nothing here: the way was once any open run of strips, so a rock
    /// standing on the centre line was kept whenever the road beside it was
    /// open, and the opening wandered from side to side along the road.
    /// </summary>
    /// <param name="blockers">per strip, the removable chunks in it (only the way's strips are read)</param>
    /// <param name="width">the clearance's width, m</param>
    /// <param name="removed">chunks already taken; the ones chosen here are added</param>
    /// <returns>the chunks chosen here, in the order taken</returns>
    public static List<int> ChunksInTheWay(IReadOnlyList<IReadOnlyCollection<int>> blockers, float width, ISet<int> removed)
    {
        var taken = new List<int>();
        var (first, last) = WayStrips(blockers.Count, width);
        var order = new List<int>();
        for (int s = first; s <= last; s++) order.Add(s);
        float mid = (blockers.Count - 1) * 0.5f;
        order.Sort((a, b) => System.Math.Abs(a - mid) != System.Math.Abs(b - mid) ? System.Math.Abs(a - mid).CompareTo(System.Math.Abs(b - mid)) : a.CompareTo(b));
        foreach (int s in order)
            foreach (int c in blockers[s])
                if (removed.Add(c)) taken.Add(c);
        return taken;
    }

    /// <summary>
    /// Whether a carve that swapped a whole rock for its fractured copy puts the
    /// whole rock back: only when no chunk of the copy is taken and the whole
    /// rock does not itself stand in the way. The whole rock's collider is not
    /// the shape of its chunks; where it reaches into the way and no chunk does,
    /// putting it back leaves the road blocked by a rock nobody can see there
    /// (rock4_copper, measured: two deposits stood up to 2 m over a road's
    /// centre line), and the copy with every chunk kept clears it.
    /// </summary>
    public static bool RestoresWholeRock(int chunksTaken, bool wholeRockInTheWay) => chunksTaken == 0 && !wholeRockInTheWay;

    /// <summary>How far apart two fragments of one rock may be and still touch, m: how far a corner of one may be from
    /// a corner of the other (<see cref="TouchingFragments"/>).</summary>
    public const float FragmentTouch = 0.1f;

    /// <summary>
    /// Which fragments of one rock touch, from their real shapes: two touch when a corner of one (a vertex of its
    /// mesh, in world space) lies within <see cref="FragmentTouch"/> of a corner of the other. A fractured rock is cut
    /// into cells that share their faces, so fragments that meet share those faces' corners, and fragments a gap apart
    /// share none. Bounds do not tell: a slanted fragment's box reaches far past its shape, and fragments up to metres
    /// apart, across the fragment a carve took between them, were counted as touching (measured on a fractured
    /// Mistlands cliff: 79 of 547 pairs whose boxes met were 0.3 to 3 m apart), which held a carve's cut-off fragments
    /// up in the air. Measured on fractured cliffs and boulders, shared corners find all but a handful of the pairs
    /// whose shapes come within <see cref="FragmentTouch"/>, those few within a centimetre or two of it.
    /// </summary>
    /// <param name="corners">per fragment, its corners in world space</param>
    /// <returns>per fragment, the fragments touching it (symmetric); a fragment touching none has no entry</returns>
    public static Dictionary<int, HashSet<int>> TouchingFragments(IReadOnlyDictionary<int, UnityEngine.Vector3[]> corners)
    {
        float cell = FragmentTouch, reach = FragmentTouch * FragmentTouch;
        var grid = new Dictionary<(int, int, int), List<(int fragment, UnityEngine.Vector3 at)>>();
        (int, int, int) Cell(UnityEngine.Vector3 v) =>
            ((int)System.Math.Floor(v.x / cell), (int)System.Math.Floor(v.y / cell), (int)System.Math.Floor(v.z / cell));
        foreach (var kv in corners)
            foreach (var v in kv.Value)
            {
                var key = Cell(v);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<(int, UnityEngine.Vector3)>();
                list.Add((kv.Key, v));
            }
        var touching = new Dictionary<int, HashSet<int>>();
        foreach (var kv in grid)
        {
            var (cx, cy, cz) = kv.Key;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var near)) continue;
                        foreach (var (a, va) in kv.Value)
                            foreach (var (b, vb) in near)
                            {
                                float ex = va.x - vb.x, ey = va.y - vb.y, ez = va.z - vb.z;
                                if (a == b || ex * ex + ey * ey + ez * ez > reach) continue;
                                if (!touching.TryGetValue(a, out var ta)) touching[a] = ta = new HashSet<int>();
                                if (!touching.TryGetValue(b, out var tb)) touching[b] = tb = new HashSet<int>();
                                ta.Add(b); tb.Add(a);
                            }
                    }
        }
        return touching;
    }
    /// <summary>How far over the ground a fragment's lowest point may be and still stand on it, m.</summary>
    public const float GroundTouch = 0.3f;

    /// <summary>Whether a fragment whose underside is at <paramref name="underside"/> stands on ground at
    /// <paramref name="ground"/>: within <see cref="GroundTouch"/> over it, or reaching under it.</summary>
    public static bool StandsOn(float underside, float ground) => underside <= ground + GroundTouch;

    /// <summary>
    /// The ground a column under a fragment is judged on, as the players will have it: the loaded terrain where its zone's
    /// terrain is ready (<paramref name="zoneReady"/>: its heightmap built and its road terrain written) and answers there;
    /// anywhere else <paramref name="finished"/>, the finished ground the terrain writer leaves (StructureGround.At). A rock
    /// is carved once its own zone is ready, and a large one reaches over the zones round it: a zone not loaded yet has no
    /// ground to read, and one just loaded may not have its road cut yet. Read so, a fragment over such a zone stood on
    /// nothing anyone had read and held up every fragment touching it, and the carve left them hanging (a mountain boulder
    /// and a Mistlands cliff, each on a zone corner, their next zone loaded after the carve: "+ 0 cut off").
    /// False when neither answers (no world generator, or NaN).
    /// </summary>
    public static bool FragmentGround(bool zoneReady, bool terrainRead, float terrain, float finished, out float ground)
    {
        if (zoneReady && terrainRead) { ground = terrain; return true; }
        ground = finished;
        return !float.IsNaN(finished);
    }

    /// <summary>
    /// The fragments a carve leaves with nothing to hold them: a fragment stays
    /// while it touches the ground, or touches -- from below, the side or above --
    /// another remaining fragment of the same rock that stays, so it goes only
    /// once no chain of touching fragments joins it to one on the ground.
    /// Removing one can cut off others; all of them are returned at once.
    /// Neighbouring rocks, trees and pieces hold nothing up: they are not part
    /// of the rock and may be cleared on their own. A fragment the world placed
    /// with no such chain (a rock perched on another) was not cut off by the
    /// carve and stays as it was.
    /// </summary>
    /// <param name="standing">the rock's fragments standing before the carve</param>
    /// <param name="taken">the fragments the carve takes</param>
    /// <param name="onGround">whether a fragment's lowest point is on the ground (<see cref="GroundTouch"/>)</param>
    /// <param name="touch">whether two fragments touch (<see cref="TouchingFragments"/>); symmetric</param>
    public static List<int> CutOff(IReadOnlyCollection<int> standing, ICollection<int> taken,
        System.Func<int, bool> onGround, System.Func<int, int, bool> touch)
    {
        var before = Held(standing, onGround, touch);
        var left = standing.Where(i => !taken.Contains(i)).ToList();
        var after = Held(left, onGround, touch);
        return left.Where(i => before.Contains(i) && !after.Contains(i)).ToList();
    }

    /// <summary>The fragments joined to the ground, fragment to touching fragment.</summary>
    private static HashSet<int> Held(IReadOnlyCollection<int> fragments, System.Func<int, bool> onGround, System.Func<int, int, bool> touch)
    {
        var held = new HashSet<int>(fragments.Where(onGround));
        var queue = new Queue<int>(held);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach (int j in fragments)
                if (!held.Contains(j) && touch(i, j)) { held.Add(j); queue.Enqueue(j); }
        }
        return held;
    }

    /// <summary>The road's direction at a point: toward the nearest other
    /// stored point, ignoring duplicates (a point is stored once per grid cell
    /// it touches) and anything beyond 3 m.</summary>
    public static UnityEngine.Vector2 Heading(RoadSpatialGrid.RoadPoint point, List<RoadSpatialGrid.RoadPoint> all)
    {
        var dir = new UnityEngine.Vector2(1f, 0f); float best = float.MaxValue;
        foreach (var q in all)
        {
            float d = (q.p - point.p).sqrMagnitude;
            if (d > 0.04f && d < best && d < 9f) { best = d; dir = (q.p - point.p).normalized; }
        }
        return dir;
    }

    /// <summary>
    /// The surface a road point's clearance stands on, and whether the point is
    /// probed at all. A pass over loaded zones skips a point with no ground
    /// under it (the terrain collider is not built yet) and looks again later.
    /// A zone the server generates for a remote peer has only its own terrain,
    /// and nothing looks at its objects again: a rock of that zone reaching over
    /// the road in the zone next door is judged then or never, so the clearance
    /// stands on the road's own stored height, the surface it levels to (as the
    /// carve already does in RoadRockCarve.PointsNear).
    /// </summary>
    public static bool ClearanceSurface(bool haveGround, float ground, float roadHeight, bool generating, out float surface)
    {
        surface = haveGround ? ground : roadHeight;
        return haveGround || generating;
    }

    /// <summary>
    /// How far round a zone's centre a pass over that zone's own objects must
    /// look for road points: <paramref name="zoneReach"/> (its corners), or
    /// further when one of its rocks reaches out of it. A clearance can meet a
    /// rock only at a road point within <paramref name="roadReach"/> of the
    /// rock's horizontal bounds, and the farthest point of the bounds from the
    /// centre is a corner. Bounds are (min, max) in x and z.
    /// </summary>
    public static float OwnObjectsReach(UnityEngine.Vector2 centre,
        IEnumerable<(UnityEngine.Vector2 min, UnityEngine.Vector2 max)> bounds, float zoneReach, float roadReach)
    {
        float reach = zoneReach;
        foreach (var (min, max) in bounds)
        {
            float dx = System.Math.Max(System.Math.Abs(min.x - centre.x), System.Math.Abs(max.x - centre.x));
            float dz = System.Math.Max(System.Math.Abs(min.y - centre.y), System.Math.Abs(max.y - centre.y));
            reach = System.Math.Max(reach, (float)System.Math.Sqrt(dx * dx + dz * dz) + roadReach);
        }
        return reach;
    }

    /// <summary>Whether any clearing rule could take this prefab (every rock in <see cref="KnownRocks"/>).</summary>
    public static bool IsClearableRock(string name) => IsNaturalBoulder(name) || IsMistlandsRock(name, true, true);

    /// <summary><see cref="IsClearableRock(string)"/> for a rock or the fractured copy a carve or a pickaxe left of one
    /// (<see cref="PolicyNames"/>): the one way a rock saved under either name is recognised.</summary>
    public static bool IsClearableRock(string name, IReadOnlyDictionary<string, List<string>> fracturedFrom)
    {
        foreach (string judged in PolicyNames(name, fracturedFrom))
            if (IsClearableRock(judged)) return true;
        return false;
    }

    public static bool CanClear(string name, bool registeredVegetation, Heightmap.Biome biome,
        bool protectedSite, bool playerPiece, bool hasNetworkView, bool validView, bool owned) =>
        BoulderInBiome(name, biome) && registeredVegetation &&
        !protectedSite && !playerPiece && (!hasNetworkView || (validView && owned));

    /// <summary>A natural boulder (or copper or silver deposit) standing in a biome whose boulders are cleared.</summary>
    private static bool BoulderInBiome(string name, Heightmap.Biome biome) => IsNaturalBoulder(name) && (biome & SupportedBiomes) != 0;

    /// <summary>A Mistlands rock (with <paramref name="rocks"/>) or cliff (with <paramref name="cliffs"/>) standing in the Mistlands.</summary>
    private static bool MistlandsRockInBiome(string name, bool rocks, bool cliffs, Heightmap.Biome biome) =>
        IsMistlandsRock(name, rocks, cliffs) && (biome & Heightmap.Biome.Mistlands) != 0;

    /// <summary>The automatic pass (RoadRockClearing) clears Mistlands rocks and cliffs as well as boulders.</summary>
    public const bool PassClearsMistlandsRocks = true;
    public const bool PassClearsMistlandsCliffs = true;

    /// <summary>
    /// Whether the automatic rock clearing takes a rock of the world's own vegetation
    /// standing on a road in <paramref name="biome"/>: the rule of <see cref="CanClear"/> and
    /// <see cref="CanClearMistlands"/> for a rock no player or location holds. A rock it would not take is kept off the
    /// road with the vegetation instead.
    /// </summary>
    public static bool ClearingTakes(string name, Heightmap.Biome biome) =>
        BoulderInBiome(name, biome) || MistlandsRockInBiome(name, PassClearsMistlandsRocks, PassClearsMistlandsCliffs, biome);

    // Mistlands rocks and cliffs are recognised separately: the cliffs are
    // outcrops tens of metres across, not boulders.
    public static bool IsMistlandsBoulder(string name) => MistlandsBoulders.Contains(name);
    public static bool IsMistlandsCliff(string name) => MistlandsCliffs.Contains(name);
    public static bool IsMistlandsRock(string name, bool rocks, bool cliffs) =>
        (rocks && IsMistlandsBoulder(name)) || (cliffs && IsMistlandsCliff(name));

    public static bool CanClearMistlands(string name, bool rocks, bool cliffs, bool registeredVegetation, Heightmap.Biome biome,
        bool protectedSite, bool playerPiece, bool hasNetworkView, bool validView, bool owned) =>
        MistlandsRockInBiome(name, rocks, cliffs, biome) && registeredVegetation &&
        !protectedSite && !playerPiece && (!hasNetworkView || (validView && owned));
}
