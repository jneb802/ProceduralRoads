using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ProceduralRoads;

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
/// (sharing a corner with it: <see cref="RoadRockPolicy.TouchingFragments"/>)
/// is removed with the road's, quietly (<see cref="RoadRockPolicy.CutOff"/>).
/// </summary>
public static partial class RoadRockCarve
{
    /// <summary>What a carve did. <see cref="Refused"/>: this peer may not change the rock (another owns it): never a reason
    /// to remove it whole, unlike <see cref="NotCarvable"/> (a rock with no chunks to carve).</summary>
    public enum Outcome { Carved, Removed, Untouched, NotCarvable, Refused }

    /// <summary>How far past a rock's footprint a road or structure can touch it (<see cref="RoadRockPolicy.CarveReach"/>). The one owner of road width, and every rule that follows it (docs/OWNERS.md).</summary>
    internal static float WidestRoadHalfWidth => RoadRockPolicy.CarveReach;

    /// <summary>Strips across the clearance the way
    /// (<see cref="RoadRockPolicy.WayStrips"/>) is counted in: 0.17 m each on a
    /// 4 m road, whose way is then 10 of them (1.7 m).</summary>
    public const int Strips = 20;

    public struct Result
    {
        public Outcome outcome;
        public int chunks;        // chunks in the rock
        public int onRoad;        // removed because they stand in the road
        public int unsupported;   // removed because the carve cut them off (RoadRockPolicy.CutOff)
        public bool swapped;      // an intact boulder was swapped for its fractured copy
        public float chunkSize;   // mean bounds diagonal of the removed road chunks, m
        public string note;
        public MineRock5? rock;   // the rock that was carved (the fractured copy when swapped)
    }

    /// <summary>
    /// Carve the rock at <paramref name="root"/> out of every clearance within
    /// its reach (<see cref="RockClearances"/>: a road's, a staircase's walking box),
    /// whether or not that is in a location's protected area: the
    /// clearance is only paint-wide and 2 m tall, and a chunk left in it blocks
    /// a way that already runs there. The caller has already decided
    /// the rock may be cleared (policy, ownership; parts of a location never).
    /// </summary>
    /// <summary>Set while carving a zone the server is generating for a remote
    /// peer (ghost generation): the zone's list of temporary objects, which the
    /// game destroys once the zone is populated, leaving only their data.</summary>
    internal static List<GameObject>? GhostSpawned;

    /// <summary>The one owner of which rocks a road clears or carves (docs/OWNERS.md).</summary>
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
            result.outcome = Outcome.Refused; result.note = "not owner"; return result;
        }

        rock.LoadHealth();
        var areas = rock.m_hitAreas;
        result.chunks = areas.Count;
        var near = RoadClearances(rock, new GroundReader());
        var take = ChooseChunks(rock, near);
        var removed = new List<int>();
        float size = 0f;
        foreach (int i in take)
        {
            var area = areas[i];
            if (area.m_health <= 0f || area.m_collider == null) continue;
            size += area.m_collider.bounds.size.magnitude;
            area.m_health = 0f;
            removed.Add(i);
        }
        result.onRoad = removed.Count;
        result.chunkSize = result.onRoad > 0 ? size / result.onRoad : 0f;
        if (removed.Count == 0 && original != null)
        {
            // Nothing of the copy is in the way: a swapped rock would only
            // turn a whole rock into a fractured one (it looked the same, but
            // mined differently and was not the rock the world placed) --
            // unless the whole rock's own collider stands in the way.
            original.SetActive(true);
            Physics.SyncTransforms();
            if (RoadRockPolicy.RestoresWholeRock(removed.Count, InTheWay(original.transform, near)))
            {
                UndoSwap(rock.gameObject, original, result);
                result.outcome = Outcome.Untouched;
                result.note = "no chunk in the road: left whole";
                return result;
            }
            original.SetActive(false);
            Physics.SyncTransforms();
            ZNetScene.instance.Destroy(original);
            result.rock = rock;
            result.outcome = Outcome.Carved;
            result.note = "the whole rock stood in the way, none of its chunks does: every chunk kept";
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

    /// <summary>Whether any collider of <paramref name="whole"/> stands in the way
    /// (<see cref="RoadRockPolicy.WayStrips"/>) of any of these clearances.</summary>
    private static bool InTheWay(Transform whole, List<RockClearance> near)
    {
        foreach (var box in near)
        {
            var (first, last) = RoadRockPolicy.WayStrips(Strips, 2f * box.Half.x);
            if (last < first) continue;
            float strip = box.Half.x / Strips;   // half a strip's width
            float from = -box.Half.x + 2 * first * strip, to = -box.Half.x + 2 * (last + 1) * strip;
            var at = box.Centre + box.Turn * Vector3.right * ((from + to) * 0.5f);
            var half = new Vector3((to - from) * 0.5f, box.Half.y, box.Half.z);
            foreach (var c in Physics.OverlapBox(at, half, box.Turn, ~0, QueryTriggerInteraction.Ignore))
                if (c != null && c.transform.IsChildOf(whole)) return true;
        }
        return false;
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
    /// read before any of them loses its collider. Two chunks touch when they share a corner
    /// (<see cref="RoadRockPolicy.TouchingFragments"/>), read from each chunk's own mesh. A chunk
    /// whose shape cannot be read is not judged and stays, as one with no ground read under it does.
    /// </summary>
    private static List<int> CutOffChunks(MineRock5 rock, ICollection<int> taken)
    {
        var areas = rock.m_hitAreas;
        var standing = new List<int>();
        for (int i = 0; i < areas.Count; i++)
        {
            var c = areas[i].m_collider;
            if (c == null || !c.enabled || (areas[i].m_health <= 0f && !taken.Contains(i))) continue;
            standing.Add(i);
        }
        var corners = new Dictionary<int, Vector3[]>();
        var ground = new Dictionary<int, bool>();
        var reader = new GroundReader();
        foreach (int i in standing)
        {
            var shape = Corners(areas[i].m_collider);
            if (shape != null) corners[i] = shape;
            ground[i] = shape == null || OnGround(areas[i].m_collider, reader);
        }
        var touching = RoadRockPolicy.TouchingFragments(corners);
        return RoadRockPolicy.CutOff(standing, taken, i => ground[i], (i, j) => touching.TryGetValue(i, out var t) && t.Contains(j));
    }

    /// <summary>A chunk's corners in world space: the vertices of its collider's mesh (every carvable rock's chunks
    /// are readable mesh colliders, their render meshes, which the game itself combines), or null when they cannot be read.</summary>
    private static Vector3[]? Corners(Collider chunk)
    {
        if (chunk is not MeshCollider mesh || mesh.sharedMesh == null || !mesh.sharedMesh.isReadable) return null;
        var local = mesh.sharedMesh.vertices;
        if (local.Length == 0) return null;
        var toWorld = mesh.transform.localToWorldMatrix;
        var world = new Vector3[local.Length];
        for (int k = 0; k < local.Length; k++) world[k] = toWorld.MultiplyPoint3x4(local[k]);
        return world;
    }

    /// <summary>
    /// Whether a chunk stands on the ground: its underside, found by a ray up through
    /// columns <see cref="GroundStep"/> apart over its footprint, comes within
    /// <see cref="RoadRockPolicy.GroundTouch"/> of the ground (<see cref="RoadRockPolicy.StandsOn"/>; the ground as
    /// <see cref="GroundReader"/> reads it, road levelling included) in some column, or reaches under it. A chunk with no
    /// ground read under it at all is not judged and stays.
    /// </summary>
    private static bool OnGround(Collider chunk, GroundReader ground)
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
                if (!ground.At(x, z, out float g)) continue;
                read = true;
                if (chunk.Raycast(new Ray(new Vector3(x, b.min.y - 1f, z), Vector3.up), out RaycastHit hit, b.size.y + 2f) &&
                    RoadRockPolicy.StandsOn(hit.point.y, g)) return true;
            }
        return !read;
    }

    /// <summary>
    /// The ground a rock is judged on, its clearance boxes' (RockClearances.Near, the probe's and the carve's) and its
    /// fragments' alike (<see cref="RoadRockPolicy.FragmentGround"/>): the loaded terrain in a zone whose terrain is ready,
    /// elsewhere the finished ground (<see cref="StructureGround.At"/>), made only when a column needs it; none where neither
    /// is known. One per pass or carve: each zone is asked once.
    /// </summary>
    internal sealed class GroundReader
    {
        /// <summary><see cref="At(float, float, out float)"/> as the clearances read it (RockClearances.GroundAt).</summary>
        public bool At(Vector3 at, out float ground) => At(at.x, at.z, out ground);

        private readonly Dictionary<Vector2s, bool> m_ready = new Dictionary<Vector2s, bool>();
        private System.Func<Vector2, float>? m_finished;

        public bool At(float x, float z, out float ground)
        {
            var at = new Vector3(x, 0f, z);
            Vector2s zone = ZoneSystem.GetZone(at);
            if (!m_ready.TryGetValue(zone, out bool ready)) m_ready[zone] = ready = TerrainReady(zone);
            float terrain = 0f;
            bool read = ready && ZoneSystem.instance.GetGroundHeight(at, out terrain);
            float finished = float.NaN;
            if (!read && WorldGenerator.instance != null)
                finished = (m_finished ??= StructureGround.At(WorldGenerator.instance))(new Vector2(x, z));
            return RoadRockPolicy.FragmentGround(ready, read, terrain, finished, out ground);
        }

        /// <summary>Whether a zone's loaded terrain is the ground the players will have: a full heightmap is built there
        /// and its road terrain is written, or it has no road for the writer to write
        /// (<see cref="RoadRockClearing.TerrainWritten"/>).</summary>
        private static bool TerrainReady(Vector2s zone)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            foreach (var map in Heightmap.GetAllHeightmaps())
                if (map != null && !map.IsDistantLod && map.IsPointInside(centre))
                    return RoadRockClearing.TerrainWritten(map, centre);
            return false;
        }
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

    /// <summary>Every clearance within the rock's reach (<see cref="RockClearances"/>, the boxes the probe judged it
    /// by): the rock's horizontal half-diagonal plus the widest road's half-width round its live chunks. The FRACTURED
    /// rock's reach, not the one the intact rock touched: the fractured mesh is not the same shape, and the automatic pass
    /// only looks 46 m round a zone's centre (measured: 4 of 24 carved rocks still clipped the road at points outside both).</summary>
    private static List<RockClearance> RoadClearances(MineRock5 rock, GroundReader ground)
    {
        if (RockBounds(rock) is not Bounds bounds) return new List<RockClearance>();
        float reach = new Vector2(bounds.extents.x, bounds.extents.z).magnitude + WidestRoadHalfWidth;
        return RockClearances.Near(bounds.center, reach, ground.At, out _);
    }

    /// <summary>
    /// How big the rock still standing is, for the carve-or-whole rule (<see cref="RoadRockPolicy.CarveRemovesWhole"/>):
    /// the diagonal of the bounds round all of it, not its largest piece. A fractured rock is its live chunks
    /// (<see cref="RockBounds"/>), so a remainder spread wide after a carve is not small because each chunk is; an intact
    /// rock is its enabled colliders. 0 when nothing of it is left.
    /// </summary>
    public static float SurvivingSize(GameObject root)
    {
        var rock = root.GetComponent<MineRock5>();
        Bounds? bounds = rock != null ? RockBounds(rock) : null;
        if (rock == null)
            foreach (var part in root.GetComponentsInChildren<Collider>())
                if (part.enabled)
                {
                    if (bounds == null) bounds = part.bounds;
                    else { var b = bounds.Value; b.Encapsulate(part.bounds); bounds = b; }
                }
        return bounds is Bounds all ? all.size.magnitude : 0f;
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
    /// The chunks to take: at every road point, every chunk in the way
    /// (<see cref="RoadRockPolicy.ChunksInTheWay"/>: strips across the
    /// clearance, player height, <see cref="RoadRockPolicy.MinOpening"/> wide on
    /// the road's centre line). Nothing over the box is taken: a road's box, as a staircase's, is a player's height over its
    /// walk (<see cref="RockClearances"/>), so a rock arching over a road over that stays. It once took what hung over a road
    /// up to 4 m (RoadRockPolicy.Headroom), the game calling a chunk supported if its bounding box meets a neighbour's.
    /// </summary>
    private static HashSet<int> ChooseChunks(MineRock5 rock, List<RockClearance> near)
    {
        var index = new Dictionary<Collider, int>();
        for (int i = 0; i < rock.m_hitAreas.Count; i++)
        {
            var area = rock.m_hitAreas[i];
            if (area.m_health > 0f && area.m_collider != null) index[area.m_collider] = i;
        }
        var removed = new HashSet<int>();
        foreach (var box in near)
        {
            Vector3 centre = box.Centre, half = box.Half;
            Quaternion turn = box.Turn;
            float width = 2f * half.x;
            var hits = Physics.OverlapBox(centre, half, turn, ~0, QueryTriggerInteraction.Ignore);
            bool ours = false;
            foreach (var c in hits)
                if (c != null && index.TryGetValue(c, out int i) && !removed.Contains(i)) { ours = true; break; }
            if (!ours) continue;
            // A box no wider than the way (a stair's corridor) is the way in full: its strips together are the box.
            if (width <= RoadRockPolicy.MinOpening)
            {
                foreach (var c in hits)
                    if (c != null && index.TryGetValue(c, out int i)) removed.Add(i);
            }
            else
                RoadRockPolicy.ChunksInTheWay(StripBlockers(centre, half, turn, width, index, removed), width, removed);
        }
        return removed;
    }

    /// <summary>Per strip across a box, this rock's chunks in it not yet taken; only the way's strips are asked, the rest
    /// are empty.</summary>
    private static List<IReadOnlyCollection<int>> StripBlockers(Vector3 centre, Vector3 half, Quaternion turn, float width,
        Dictionary<Collider, int> index, HashSet<int> removed)
    {
        var blockers = new List<IReadOnlyCollection<int>>(Strips);
        var (first, last) = RoadRockPolicy.WayStrips(Strips, width);
        var right = turn * Vector3.right;
        var strip = new Vector3(half.x / Strips, half.y, half.z);
        for (int s = 0; s < Strips; s++)
        {
            var mine = new List<int>();
            if (s >= first && s <= last)
            {
                var at = centre + right * (-half.x + (2 * s + 1) * strip.x);
                foreach (var c in Physics.OverlapBox(at, strip, turn, ~0, QueryTriggerInteraction.Ignore))
                    if (c != null && index.TryGetValue(c, out int i) && !removed.Contains(i)) mine.Add(i);
            }
            blockers.Add(mine);
        }
        return blockers;
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
