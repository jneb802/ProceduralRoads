using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One box of space a walker needs clear, standing on a walking
/// surface at <see cref="Surface"/>: a road's clearance at a road point, or a
/// stretch of a staircase.</summary>
public readonly struct RockClearance
{
    public readonly Vector3 Centre;
    public readonly Vector3 Half;
    public readonly Quaternion Turn;
    public readonly float Surface;

    public RockClearance(Vector3 centre, Vector3 half, Quaternion turn, float surface)
    {
        Centre = centre; Half = half; Turn = turn; Surface = surface;
    }
}

/// <summary>
/// Carving: remove only the chunks of a rock that stand in the road, the way a
/// pickaxe does, instead of the whole rock, and leave no materials behind.
///
/// The game's own model, read from the 1.0 decompilation: an untouched boulder
/// is a Destructible whose first hit swaps it for its fractured MineRock5
/// (m_spawnWhenDestroyed, same position, rotation and scale). A MineRock5 is a
/// set of chunks, one collider each, whose healths live in one ZDO string
/// (ZDOVars.s_health); a chunk at zero health is hidden and loses its collider
/// on every peer through RPC_SetAreaHealth. Stone drops come ONLY from
/// MineRock5.DamageArea (and Destructible.Destroy's DropOnDestroyed hook), and
/// this class calls neither: the swap instantiates and destroys directly, the
/// chunks are zeroed and saved directly. A chunk does not fall when the one
/// under it goes, so what the carve cuts off would hang over the road: every
/// chunk left touching neither the ground nor a chunk of the rock that stays
/// is removed with the road's, quietly (<see cref="RoadRockPolicy.CutOff"/>).
/// </summary>
public static class RoadRockCarve
{
    public enum Outcome { Carved, Removed, Untouched, NotCarvable }

    /// <summary>The widest road's half-width: how far past a rock's footprint a road can touch it.</summary>
    internal const float WidestRoadHalfWidth = 4f;

    /// <summary>Strips across the clearance the allowance
    /// (<see cref="RoadRockPolicy.EdgeAllowance(float)"/>) is counted in.</summary>
    public const int Strips = 10;
    /// <summary>Chunks hanging over the road up to this height are taken too:
    /// the game calls a chunk supported if its bounding box meets a neighbour's,
    /// so a carve left pieces floating at head height.</summary>
    public const float Headroom = 4f;
    /// <summary>A chunk whose lowest point is this far over the road hangs; lower, it stands.</summary>
    public const float HangsAbove = 1f;


    public struct Result
    {
        public Outcome outcome;
        public int chunks;        // chunks in the rock
        public int onRoad;        // removed because they stand in the road
        public int unsupported;   // removed because the carve cut them off (RoadRockPolicy.CutOff)
        public int hanging;       // removed because they hung over the road below the headroom
        public bool swapped;      // an intact boulder was swapped for its fractured copy
        public float chunkSize;   // mean bounds diagonal of the removed road chunks, m
        public string note;
        public MineRock5? rock;   // the rock that was carved (the fractured copy when swapped)
    }

    /// <summary>
    /// Carve the rock at <paramref name="root"/> out of the road's clearance
    /// (<see cref="RoadRockProbe.RoadClearance"/>) at every road point within
    /// its reach, whether or not that is in a location's protected area: the
    /// clearance is only paint-wide and 2 m tall, and a chunk left in it blocks
    /// a road that already runs there. The caller has already decided
    /// the rock may be cleared (policy, ownership; parts of a location never).
    /// </summary>
    /// <summary>Set while carving a zone the server is generating for a remote
    /// peer (ghost generation): the zone's list of temporary objects, which the
    /// game destroys once the zone is populated, leaving only their data.</summary>
    internal static List<GameObject>? GhostSpawned;

    public static Result Carve(GameObject root)
    {
        var result = new Result { outcome = Outcome.NotCarvable, note = "" };
        var rock = root.GetComponent<MineRock5>();
        GameObject? original = null;
        if (rock == null)
        {
            var destructible = root.GetComponent<Destructible>();
            var fractured = destructible != null ? destructible.m_spawnWhenDestroyed : null;
            if (fractured == null || fractured.GetComponent<MineRock5>() == null || fractured.GetComponent<ZNetView>() == null)
            {
                result.note = destructible == null ? "no chunks (not a Destructible or MineRock5)" :
                    fractured == null ? "no fractured version" : $"fractured version {fractured.name} is not a networked MineRock5";
                return result;
            }
            // The swap Destructible.Destroy makes, without its effects, noise,
            // drops or the first-hit damage it passes on.
            // In a zone being generated for a remote peer the rock is a ghost,
            // and so is its copy: made under the game's ghost init, handed to
            // the zone's own list of temporary objects, and torn down with it.
            GameObject copy;
            if (GhostSpawned != null)
            {
                ZNetView.StartGhostInit();
                try { copy = Object.Instantiate(fractured, root.transform.position, root.transform.rotation); }
                finally { ZNetView.FinishGhostInit(); }
                GhostSpawned.Add(copy);
            }
            else
                copy = Object.Instantiate(fractured, root.transform.position, root.transform.rotation);
            copy.GetComponent<ZNetView>().SetLocalScale(root.transform.localScale);
            // Out of the way, not yet destroyed: switched off, its colliders
            // leave the physics scene at once (a destroyed object's stay until
            // the frame ends), and if no chunk of the copy is in the road the
            // swap is undone and the rock stays whole.
            root.SetActive(false);
            original = root;
            rock = copy.GetComponent<MineRock5>();
            result.swapped = true;
            Physics.SyncTransforms();
        }
        if (rock.m_nview == null || !rock.m_nview.IsValid() || !rock.m_nview.IsOwner())
        {
            if (original != null) UndoSwap(rock.gameObject, original, result);
            result.note = "not owner"; return result;
        }

        rock.LoadHealth();
        var areas = rock.m_hitAreas;
        result.chunks = areas.Count;
        // The road points the FRACTURED rock reaches, not the ones the intact
        // rock touched: the fractured mesh is not the same shape, and the
        // automatic pass only looks 46 m round a zone's centre (measured: 4 of
        // 24 carved rocks still clipped the road at points outside both).
        var near = RoadClearances(rock);
        var take = ChooseChunks(rock, near, out var hanging);
        var removed = new List<int>();
        float size = 0f;
        foreach (int i in take)
        {
            var area = areas[i];
            if (area.m_health <= 0f || area.m_collider == null) continue;
            if (!hanging.Contains(i)) size += area.m_collider.bounds.size.magnitude;
            area.m_health = 0f;
            removed.Add(i);
        }
        result.hanging = hanging.Count;
        result.onRoad = removed.Count - hanging.Count;
        result.chunkSize = result.onRoad > 0 ? size / result.onRoad : 0f;
        if (removed.Count == 0 && original != null)
        {
            // Nothing of the copy is in the road: a swapped rock would only
            // turn a whole rock into a fractured one (it looked the same, but
            // mined differently and was not the rock the world placed).
            UndoSwap(rock.gameObject, original, result);
            result.outcome = Outcome.Untouched;
            result.note = "no chunk in the road: left whole";
            return result;
        }
        if (original != null) ZNetScene.instance.Destroy(original);
        if (removed.Count == 0)
        {
            // A swapped rock always touched the road before the swap; if no
            // chunk does, say so rather than leave a silent no-op.
            result.outcome = Outcome.Untouched;
            result.note = result.swapped ? "swapped but no chunk in the road" : "no chunk in the road";
            return result;
        }
        // Judged while every chunk still has its collider: what the carve cuts off goes with it.
        foreach (int i in CutOffChunks(rock, removed))
        {
            areas[i].m_health = 0f;
            removed.Add(i);
            result.unsupported++;
        }
        Commit(rock, removed);
        result.rock = rock;

        if (rock.AllDestroyed())
        {
            rock.m_nview.Destroy();
            result.outcome = Outcome.Removed;
            return result;
        }
        result.outcome = Outcome.Carved;
        return result;
    }

    /// <summary>Put the whole rock back: the copy goes (and leaves the ghost list), the original is switched on again.</summary>
    private static void UndoSwap(GameObject copy, GameObject original, Result result)
    {
        GhostSpawned?.Remove(copy);
        ZNetScene.instance.Destroy(copy);
        original.SetActive(true);
        result.swapped = false;
        Physics.SyncTransforms();
    }

    /// <summary>Spacing of the columns a chunk's footprint is sampled in for the ground, m.</summary>
    public const float GroundStep = 0.5f;

    /// <summary>
    /// The chunks taking <paramref name="taken"/> cuts off (<see cref="RoadRockPolicy.CutOff"/>),
    /// read before any of them loses its collider. Two chunks touch when the real shape of
    /// each comes within <see cref="RoadRockPolicy.FragmentTouch"/> of the other's bounds, as
    /// two shapes that touch always do.
    /// </summary>
    private static List<int> CutOffChunks(MineRock5 rock, ICollection<int> taken)
    {
        var areas = rock.m_hitAreas;
        var standing = new List<int>();
        var index = new Dictionary<Collider, int>();
        for (int i = 0; i < areas.Count; i++)
        {
            var c = areas[i].m_collider;
            if (c == null || !c.enabled || (areas[i].m_health <= 0f && !taken.Contains(i))) continue;
            standing.Add(i);
            index[c] = i;
        }
        var near = new Dictionary<int, HashSet<int>>();
        var ground = new Dictionary<int, bool>();
        var grow = Vector3.one * RoadRockPolicy.FragmentTouch;
        foreach (int i in standing)
        {
            Bounds b = areas[i].m_collider.bounds;
            var mine = new HashSet<int>();
            foreach (var c in Physics.OverlapBox(b.center, b.extents + grow, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                if (c != null && index.TryGetValue(c, out int j) && j != i) mine.Add(j);
            near[i] = mine;
            ground[i] = OnGround(areas[i].m_collider);
        }
        return RoadRockPolicy.CutOff(standing, taken, i => ground[i], (i, j) => near[i].Contains(j) && near[j].Contains(i));
    }

    /// <summary>
    /// Whether a chunk stands on the ground: its underside, found by a ray up through
    /// columns <see cref="GroundStep"/> apart over its footprint, comes within
    /// <see cref="RoadRockPolicy.GroundTouch"/> of the ground (the finished ground, road
    /// levelling included) in some column, or reaches under it. A chunk with no ground
    /// read under it at all is not judged and stays.
    /// </summary>
    private static bool OnGround(Collider chunk)
    {
        Bounds b = chunk.bounds;
        int nx = Mathf.Clamp(Mathf.CeilToInt(b.size.x / GroundStep), 2, 8);
        int nz = Mathf.Clamp(Mathf.CeilToInt(b.size.z / GroundStep), 2, 8);
        bool read = false;
        for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                float x = Mathf.Lerp(b.min.x, b.max.x, (ix + 0.5f) / nx);
                float z = Mathf.Lerp(b.min.z, b.max.z, (iz + 0.5f) / nz);
                if (!ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out float g)) continue;
                read = true;
                if (chunk.Raycast(new Ray(new Vector3(x, b.min.y - 1f, z), Vector3.up), out RaycastHit hit, b.size.y + 2f) &&
                    hit.point.y <= g + RoadRockPolicy.GroundTouch) return true;
            }
        return !read;
    }

    /// <summary>Save the healths and hide the zeroed chunks on every peer
    /// (the local call runs at once, so the colliders are off on return).</summary>
    private static void Commit(MineRock5 rock, List<int> zeroed)
    {
        rock.SaveHealth();
        foreach (int i in zeroed)
            rock.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_SetAreaHealth", i, 0f);
        rock.UpdateMesh();
    }

    /// <summary>The road's clearance at every road point the rock reaches, and
    /// every staircase box within its reach (StructurePlans): a boulder in a
    /// stair's way loses the chunks in the stair, as one in a road does.</summary>
    private static List<RockClearance> RoadClearances(MineRock5 rock)
    {
        var (at, all) = PointsNear(rock);
        var boxes = new List<RockClearance>(at.Count);
        foreach (var (point, surface) in at)
        {
            RoadRockProbe.RoadClearance(point, surface, all, out var centre, out var half, out var turn);
            boxes.Add(new RockClearance(centre, half, turn, surface));
        }
        if (RockBounds(rock) is Bounds bounds)
        {
            float reach = new Vector2(bounds.extents.x, bounds.extents.z).magnitude + WidestRoadHalfWidth;
            foreach (StructureClearance s in StructurePlans.ClearancesNear(bounds.center, reach))
                if (s.IsBox) boxes.Add(new RockClearance(s.Centre, s.Half, Quaternion.Euler(0f, s.YawDegrees, 0f), s.Surface));
        }
        return boxes;
    }

    /// <summary>The bounds of a rock's live chunks, or null when none are left.</summary>
    private static Bounds? RockBounds(MineRock5 rock)
    {
        Bounds? bounds = null;
        foreach (var area in rock.m_hitAreas)
            if (area.m_health > 0f && area.m_collider != null)
            {
                if (bounds == null) bounds = area.m_collider.bounds;
                else { var b = bounds.Value; b.Encapsulate(area.m_collider.bounds); bounds = b; }
            }
        return bounds;
    }

    /// <summary>Every solid road point within the rock's reach, with the ground under it.</summary>
    private static (List<(RoadSpatialGrid.RoadPoint point, float surface)> at, List<RoadSpatialGrid.RoadPoint> all) PointsNear(MineRock5 rock)
    {
        var at = new List<(RoadSpatialGrid.RoadPoint, float)>();
        var all = new List<RoadSpatialGrid.RoadPoint>();
        Bounds? bounds = RockBounds(rock);
        if (bounds == null) return (at, all);
        var box = bounds.Value;
        // Reach: the rock's horizontal half-diagonal plus the widest road's half-width.
        float reach = new Vector2(box.extents.x, box.extents.z).magnitude + WidestRoadHalfWidth;
        // Headings need the neighbours just outside the reach too.
        all = RoadSpatialGrid.GetRoadPointsNearPosition(box.center, reach + 3f);
        all.RemoveAll(p => p.cutOnly); // a staircase's cuts are not road: they would turn the headings
        foreach (var point in all)
        {
            if (!point.IsRoad || (point.p - new Vector2(box.center.x, box.center.z)).sqrMagnitude > reach * reach) continue;
            // A zone the server generates for a remote peer may have no ground
            // to read; the road's own stored height is the surface it levels to.
            at.Add((point, ZoneSystem.instance.GetGroundHeight(new Vector3(point.p.x, 0f, point.p.y), out float surface) ? surface : point.h));
        }
        return (at, all);
    }

    /// <summary>The prefab an object was made from, read from its ZDO when it
    /// has one: MineRock5.Awake renames a fractured rock's GameObject.</summary>
    public static string PrefabName(GameObject root, ZNetView? view)
    {
        if (view != null && view.IsValid() && ZNetScene.instance != null)
        {
            var prefab = ZNetScene.instance.GetPrefab(view.GetZDO().GetPrefab());
            if (prefab != null) return prefab.name;
        }
        return Utils.GetPrefabName(root);
    }

    private static Dictionary<string, List<string>>? s_fracturedFrom;
    private static ZNetScene? s_fracturedScene;

    /// <summary>Fractured prefab name -> the known rocks that fracture into it,
    /// read once per ZNetScene from the rocks' Destructible.m_spawnWhenDestroyed.</summary>
    public static IReadOnlyDictionary<string, List<string>> FracturedFrom()
    {
        if (s_fracturedFrom != null && s_fracturedScene == ZNetScene.instance) return s_fracturedFrom;
        var map = new Dictionary<string, List<string>>();
        if (ZNetScene.instance != null)
            foreach (string name in RoadRockPolicy.KnownRocks)
            {
                var fractured = ZNetScene.instance.GetPrefab(name)?.GetComponent<Destructible>()?.m_spawnWhenDestroyed;
                if (fractured == null) continue;
                if (!map.TryGetValue(fractured.name, out var list)) map[fractured.name] = list = new List<string>();
                list.Add(name);
            }
        s_fracturedScene = ZNetScene.instance;
        return s_fracturedFrom = map;
    }

    /// <summary>
    /// The chunks to take: at every road point, the fewest that open a way a
    /// player fits through (<see cref="RoadRockPolicy.ChunksToOpen"/>, strips
    /// across the clearance, player height, <see cref="RoadRockPolicy.MinOpening"/>
    /// wide), then those hanging over that way below the headroom.
    /// <paramref name="hanging"/> is the second kind.
    /// </summary>
    private static HashSet<int> ChooseChunks(MineRock5 rock, List<RockClearance> near, out HashSet<int> hanging)
    {
        var index = new Dictionary<Collider, int>();
        for (int i = 0; i < rock.m_hitAreas.Count; i++)
        {
            var area = rock.m_hitAreas[i];
            if (area.m_health > 0f && area.m_collider != null) index[area.m_collider] = i;
        }
        var removed = new HashSet<int>();
        hanging = new HashSet<int>();
        var blockers = new List<IReadOnlyCollection<int>>(Strips);
        var held = new List<bool>(Strips);
        foreach (var box in near)
        {
            Vector3 centre = box.Centre, half = box.Half;
            Quaternion turn = box.Turn;
            float surface = box.Surface;
            float allowance = RoadRockPolicy.EdgeAllowance(2f * half.x);
            bool ours = false;
            foreach (var c in Physics.OverlapBox(centre, half, turn, ~0, QueryTriggerInteraction.Ignore))
                if (c != null && index.TryGetValue(c, out int i) && !removed.Contains(i)) { ours = true; break; }
            if (ours)
            {
                blockers.Clear(); held.Clear();
                var right = turn * Vector3.right;
                var strip = new Vector3(half.x / Strips, half.y, half.z);
                for (int s = 0; s < Strips; s++)
                {
                    var at = centre + right * (-half.x + (2 * s + 1) * strip.x);
                    var mine = new List<int>(); bool other = false;
                    foreach (var c in Physics.OverlapBox(at, strip, turn, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (c == null) continue;
                        if (index.TryGetValue(c, out int i)) { mine.Add(i); continue; }
                        if (c.GetComponentInParent<Heightmap>() != null || c.GetComponentInParent<Character>() != null ||
                            c.GetComponentInParent<ItemDrop>() != null) continue;
                        if (c.transform.IsChildOf(rock.transform)) continue;   // a chunk already switched off
                        other = true;
                    }
                    blockers.Add(mine); held.Add(other);
                }
                RoadRockPolicy.ChunksToOpen(blockers, held, allowance, removed);
            }
            // Headroom: what of this rock hangs over the road above the
            // clearance and below the headroom, with the same edge allowance
            // as the road itself: the band is cut into the same strips and
            // pieces go centre-first only until one open run is long enough.
            // Without it a rock whose top leaned over the road's edge lost
            // its top while its base stayed at the edge.
            float from = surface + RoadRockProbe.ClearanceLift + 2f * half.y;
            if (Headroom > from - surface)
            {
                var roofHalf = (surface + Headroom - from) * 0.5f;
                var right = turn * Vector3.right;
                var strip = new Vector3(half.x / Strips, roofHalf, half.z);
                var roofBlockers = new List<IReadOnlyCollection<int>>(Strips);
                var roofHeld = new List<bool>(Strips);
                bool any = false;
                for (int s = 0; s < Strips; s++)
                {
                    var at = new Vector3(centre.x, from + roofHalf, centre.z) + right * (-half.x + (2 * s + 1) * strip.x);
                    var mine = new List<int>();
                    foreach (var c in Physics.OverlapBox(at, strip, turn, ~0, QueryTriggerInteraction.Ignore))
                        if (c != null && index.TryGetValue(c, out int i) && !removed.Contains(i) && c.bounds.min.y > surface + HangsAbove)
                        { mine.Add(i); any = true; }
                    roofBlockers.Add(mine); roofHeld.Add(false);
                }
                if (any)
                    foreach (int i in RoadRockPolicy.ChunksToOpen(roofBlockers, roofHeld, allowance, removed))
                        hanging.Add(i);
            }
        }
        return removed;
    }

    /// <summary>Item drops within <paramref name="radius"/> of a point: the
    /// "no materials left behind" check, taken before and after a clearing.</summary>
    public static int DropsNear(Vector3 centre, float radius)
    {
        int count = 0;
        float r2 = radius * radius;
        foreach (var drop in ItemDrop.s_instances)
            if (drop != null)
            {
                var d = drop.transform.position - centre;
                if (d.x * d.x + d.z * d.z <= r2) count++;
            }
        return count;
    }

    /// <summary>What each clearable rock is made of: whether it can be carved,
    /// and into how many chunks (road_rock_kinds).</summary>
    public static List<string> Kinds()
    {
        var lines = new List<string>();
        if (ZNetScene.instance == null) { lines.Add("Error: no ZNetScene"); return lines; }
        foreach (string name in RoadRockPolicy.KnownRocks)
        {
            var prefab = ZNetScene.instance.GetPrefab(name);
            if (prefab == null) { lines.Add($"  {name}: no such prefab"); continue; }
            var s = new StringBuilder($"  {name}:");
            if (prefab.GetComponent<ZNetView>() == null) s.Append(" NOT networked;");
            var direct = prefab.GetComponent<MineRock5>();
            var destructible = prefab.GetComponent<Destructible>();
            if (direct != null) s.Append(" MineRock5 ").Append(Describe(direct));
            else if (destructible != null)
            {
                s.Append(" Destructible");
                if (prefab.GetComponent<DropOnDestroyed>() != null) s.Append(" (drops on destroy)");
                var fractured = destructible.m_spawnWhenDestroyed;
                if (fractured == null) s.Append(", no fractured version -> whole only");
                else
                {
                    s.Append(" -> ").Append(fractured.name);
                    var mr5 = fractured.GetComponent<MineRock5>();
                    s.Append(mr5 != null ? " MineRock5 " + Describe(mr5) : " (not a MineRock5 -> whole only)");
                }
            }
            else if (prefab.GetComponent<MineRock>() != null)
                s.Append($" MineRock (older chunked type, {prefab.GetComponent<MineRock>().m_areaRoot?.GetComponentsInChildren<Collider>(true).Length ?? prefab.GetComponentsInChildren<Collider>(true).Length} areas) -> not carved yet");
            else s.Append(" no Destructible/MineRock -> whole only");
            lines.Add(s.ToString());
        }
        lines.Insert(0, $"road_rock_kinds: {RoadRockPolicy.KnownRocks.Length} clearable rock prefabs");
        return lines;
    }

    private static string Describe(MineRock5 rock) =>
        $"{rock.GetComponentsInChildren<Collider>(true).Length} chunks, health {rock.m_health:F0}, tool tier {rock.m_minToolTier}, support check {(rock.m_supportCheck ? "on" : "off")}";
}
