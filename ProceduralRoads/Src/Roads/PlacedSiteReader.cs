using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What the game placed, read off the saved world: a place's placement (its LocationProxy: root, turn
/// and seed), the levelling its placement kept (<see cref="LocationPrefabLevelling.PlacedOps"/>, the
/// game's draw replayed), its own rocks, and the ground it leaves. One owner for the places placed
/// before their roads (<see cref="SitesFirstLoad"/>), the terrain writer, which levels a road onto a place's
/// landing against the ground the place leaves (<see cref="RoadTerrainModifier.PlacedGround"/>).
/// Main thread.
/// </summary>
public static class PlacedSiteReader
{
    /// <summary>How far past a place's ring (or its rock), and round a road's end, its ground is read
    /// or rebuilt: a road or staircase onto its landing cannot be laid beyond it.</summary>
    public const float Margin = 16f;
    private static readonly int ProxyHash = "LocationProxy".GetStableHashCode();

    /// <summary>Wire the terrain writer to the ground placed places leave, and forget it with the network.</summary>
    public static void Install()
    {
        RoadTerrainModifier.PlacedGround = PlacedGroundAt;
        RoadNetworkGenerator.OnReset += Reset;
    }

    /// <summary>The place's saved placement, its LocationProxy: root, turn and seed.</summary>
    internal static bool Placed(Vector2s zone, Vector2 centre, out ZDO? proxy)
    {
        proxy = null;
        if (ZDOMan.instance == null) return false;
        var objects = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, objects, new HashSet<ZoneSystem.SectorIndex>());
        foreach (ZDO zdo in objects)
        {
            if (zdo.GetPrefab() != ProxyHash) continue;
            Vector3 p = zdo.GetPosition();
            if (Vector2.Distance(new Vector2(p.x, p.z), centre) > 1f) continue;
            proxy = zdo;
            return true;
        }
        return false;
    }

    /// <summary>The parts each placed place kept, by its centre: replayed once a session.</summary>
    private static readonly Dictionary<Vector2, IReadOnlyList<LevelOp>> s_opsBySite = new();
    /// <summary>Each place's ground as it leaves it, rebuilt once a session, by its centre (null: cannot be rebuilt).</summary>
    private static readonly Dictionary<Vector2, Func<float, float, float>?> s_siteGround = new();

    /// <summary>
    /// The terrain modifiers the placement at <paramref name="proxy"/> kept: the game's draw replayed
    /// (<see cref="LocationPrefabLevelling.PlacedOps"/>), once per place a session. Replaying loads the
    /// prefab, up to 387 ms on the main thread; it restores the prefab exactly, so it agrees with
    /// the placement (21 of 21). Null when the prefab cannot be read.
    /// </summary>
    internal static IReadOnlyList<LevelOp>? KeptOps(Vector2 centre, ZDO proxy)
    {
        if (s_opsBySite.TryGetValue(centre, out IReadOnlyList<LevelOp>? known)) return known;
        IReadOnlyList<LevelOp>? ops = LocationPrefabLevelling.PlacedOps(centre, proxy.GetInt(ZDOVars.s_seed), proxy.GetPosition(), proxy.GetRotation());
        if (ops != null) s_opsBySite[centre] = ops;
        return ops;
    }

    /// <summary>
    /// The ground a placed place leaves at <paramref name="p"/>, inside its footprint: the terrain's
    /// natural height with the place's kept terrain modifiers applied (<see cref="PlacedSiteGround"/>),
    /// whether or not the place is in the scene. The road terrain writer measures a road into a site
    /// against it (RoadTerrainModifier.PlacedGround): measured against the terrain it was written on,
    /// the server's temporary terrain without the cave, the road's earthwork came on top of the
    /// cave's own levelling in play, 5 m too high at one frost cave's landing (the road's earthwork
    /// added to the place's). Main thread: a place not yet replayed this
    /// session is replayed here. Null outside a footprint or where it cannot be rebuilt.
    /// </summary>
    public static float? PlacedGroundAt(Vector2 p)
    {
        if (ZoneSystem.instance == null || WorldGenerator.instance == null
            || !RoadSiteProtection.NearestFootprint(p, 0f, out RoadSiteProtection.Footprint site)) return null;
        if (!s_siteGround.TryGetValue(site.Centre, out Func<float, float, float>? ground))
            s_siteGround[site.Centre] = ground = BuildSiteGround(site);
        return ground?.Invoke(p.x, p.y);
    }

    private static Func<float, float, float>? BuildSiteGround(RoadSiteProtection.Footprint site)
    {
        Vector2s zone = ZoneSystem.GetZone(new Vector3(site.Centre.x, 0f, site.Centre.y));
        if (!Placed(zone, site.Centre, out ZDO? proxy)) return null;
        IReadOnlyList<LevelOp>? ops = KeptOps(site.Centre, proxy!);
        if (ops == null) return null;
        WorldGenerator world = WorldGenerator.instance;
        int reach = Mathf.CeilToInt(site.Radius) + 2;
        int x0 = Mathf.FloorToInt(site.Centre.x) - reach, z0 = Mathf.FloorToInt(site.Centre.y) - reach, w = 2 * reach + 2, h = 2 * reach + 2;
        float[] heights = PlacedSiteGround.Build(x0, z0, w, h, site.Centre, proxy!.GetPosition().y, proxy.GetRotation().eulerAngles.y, ops,
            (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        return PlacedSiteGround.Sampler(heights, x0, z0, w, h);
    }

    /// <summary>Whether the place at <paramref name="centre"/> keeps every rock (<see cref="SiteRock.KeepsEveryRock"/>).</summary>
    internal static bool KeepsEveryRock(Vector2 centre)
    {
        Vector2s zone = ZoneSystem.GetZone(new Vector3(centre.x, 0f, centre.y));
        return ZoneSystem.instance != null && ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance)
            && SiteRock.KeepsEveryRock(instance.m_location.m_prefab.Name);
    }

    /// <summary>The place's exterior radius: its rock, for leading a staircase round it.</summary>
    internal static float RockRadius(Vector2 centre)
    {
        Vector2s zone = ZoneSystem.GetZone(new Vector3(centre.x, 0f, centre.y));
        return ZoneSystem.instance != null && ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance)
            ? instance.m_location.m_exteriorRadius : 0f;
    }

    private static readonly Dictionary<int, Bounds> s_rockBox = new();

    /// <summary>
    /// The place's own boulders: every natural boulder (or fractured one) saved in the
    /// ground's box and standing in a place's footprint, as its prefab's colliders' box seen
    /// from above, scaled and turned as it is saved. A place's rocks are placed with it (a frost cave's reach about
    /// 20 m, past its exterior radius) and vegetation is kept out of its area, so a boulder
    /// in its footprint is its own.
    /// </summary>
    internal static List<SiteRock> PlaceRocks(int x0, int z0, int w, int h, Vector2 centre, float root, float yaw, out int world)
    {
        world = 0;
        var rocks = new List<SiteRock>();
        if (ZDOMan.instance == null || ZNetScene.instance == null) return rocks;
        // Only the place's own parts: a boulder of the world's standing in its footprint is any
        // road's to carve (a rock2_mountain that stayed put while the frost cave beside it turned,
        // runs 35-36). Where the prefab's parts are not known, every boulder there, as before.
        Vector2s home = ZoneSystem.GetZone(new Vector3(centre.x, 0f, centre.y));
        string? place = ZoneSystem.instance.m_locationInstances.TryGetValue(home, out ZoneSystem.LocationInstance instance)
            ? instance.m_location.m_prefab.Name : null;
        var rootAt = new Vector3(centre.x, root, centre.y);
        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        float rockRadius = RockRadius(centre);
        bool keepSmall = KeepsEveryRock(centre);
        var zones = new HashSet<Vector2s>();
        for (int z = z0; z <= z0 + h + 64; z += 64)
            for (int x = x0; x <= x0 + w + 64; x += 64)
                zones.Add(ZoneSystem.GetZone(new Vector3(Mathf.Min(x, x0 + w), 0f, Mathf.Min(z, z0 + h))));
        var objects = new List<ZDO>();
        foreach (Vector2s zone in zones)
            ZDOMan.instance.FindObjects(zone, objects, new HashSet<ZoneSystem.SectorIndex>());
        foreach (ZDO zdo in objects)
        {
            Vector3 p = zdo.GetPosition();
            if (p.x < x0 || p.x > x0 + w || p.z < z0 || p.z > z0 + h) continue;
            var at = new Vector2(p.x, p.z);
            if (!RoadSiteProtection.Contains(at)) continue;
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null) continue;
            bool rock = false;
            foreach (string name in RoadRockPolicy.PolicyNames(prefab.name, RoadRockCarve.FracturedFrom()))
                rock |= RoadRockPolicy.IsClearableRock(name);
            // The place's own rocks of kinds no road clears, its ice above all (Hildir's cave:
            // ice_rock1, rock3_ice, caverock_ice_*): kept and kept clear of, as its large rocks
            // are. Left out, a staircase ran up beside and behind its great ice block.
            bool ownRock = !rock && place != null && prefab.name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0
                && LocationPrefabLevelling.IsPart(place, rootAt, turn, p) == true;
            if (!rock && !ownRock) continue;
            if (!ownRock && place != null && LocationPrefabLevelling.IsPart(place, rootAt, turn, p) == false) { world++; continue; }
            Vector3 saved = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            float scalar = zdo.GetFloat(ZDOVars.s_scaleScalarHash, 0f);
            Vector3? setScale = saved != Vector3.zero ? saved : scalar > 0f ? Vector3.one * scalar : (Vector3?)null;
            Vector3 scale = setScale ?? Vector3.one;
            SiteRock box = Box(p, zdo.GetRotation(), scale, LocalBox(prefab), prefab.name);
            // Its real top where it counts: a rock an approach keeps clear of (large, round the
            // mouth, or at a place keeping every rock); a small one elsewhere is cleared anyway.
            bool kept = ownRock || box.Large || keepSmall || (rockRadius > 0f && Vector2.Distance(box.At, centre) <= rockRadius);
            rocks.Add(kept ? WithTop(box, prefab, p, zdo.GetRotation(), setScale) : box);
        }
        return rocks;
    }

    /// <summary>Most cells a rock's top is read over: past it, its box stands for it.</summary>
    private const int MaxTopCells = 6400;

    /// <summary>
    /// The rock's real top over each metre of its box: a copy of the rock placed as saved (turned,
    /// scaled), never networked (ZNetView.m_forceDisableInit, as the game makes previews), each of
    /// its colliders hit by a ray straight down at every metre, the highest hit kept, and the copy
    /// taken down at once. Random's state is put back (the rock's Awake draws from it). Main thread.
    /// The box alone, from the colliders' corners, overstated a rough tilted rock: roads were
    /// refused metres from a rock1_mountain whose box they touched.
    /// </summary>
    private static SiteRock WithTop(SiteRock box, GameObject prefab, Vector3 position, Quaternion rotation, Vector3? scale)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        for (int c = 0; c < 4; c++)
        {
            Vector2 corner = box.At + SiteRock.World(new Vector2((c & 1) == 0 ? -box.HalfX : box.HalfX, (c & 2) == 0 ? -box.HalfZ : box.HalfZ), box.Yaw);
            minX = Mathf.Min(minX, corner.x); maxX = Mathf.Max(maxX, corner.x);
            minZ = Mathf.Min(minZ, corner.y); maxZ = Mathf.Max(maxZ, corner.y);
        }
        int x0 = Mathf.FloorToInt(minX), z0 = Mathf.FloorToInt(minZ);
        int w = Mathf.CeilToInt(maxX) - x0 + 1, h = Mathf.CeilToInt(maxZ) - z0 + 1;
        if (w <= 0 || h <= 0 || w * h > MaxTopCells) return box;
        var top = new float[w * h];
        for (int i = 0; i < top.Length; i++) top[i] = float.NaN;
        UnityEngine.Random.State state = UnityEngine.Random.state;
        GameObject? copy = null;
        ZNetView.m_forceDisableInit = true;
        try
        {
            copy = UnityEngine.Object.Instantiate(prefab, position, rotation);
            if (scale is Vector3 s) copy.transform.localScale = s;
            Physics.SyncTransforms();
            var colliders = new List<Collider>();
            foreach (Collider c in copy.GetComponentsInChildren<Collider>())
                if (c.enabled && !c.isTrigger) colliders.Add(c);
            float from = box.MaxY + 2f, length = box.MaxY - box.MinY + 4f;
            int hits = 0;
            for (int k = 0; k < h; k++)
                for (int i = 0; i < w; i++)
                {
                    var ray = new Ray(new Vector3(x0 + i, from, z0 + k), Vector3.down);
                    foreach (Collider c in colliders)
                    {
                        Bounds b = c.bounds;
                        if (ray.origin.x < b.min.x || ray.origin.x > b.max.x || ray.origin.z < b.min.z || ray.origin.z > b.max.z) continue;
                        if (c.Raycast(ray, out RaycastHit hit, length) && (float.IsNaN(top[k * w + i]) || hit.point.y > top[k * w + i]))
                        { top[k * w + i] = hit.point.y; hits++; }
                    }
                }
            if (hits == 0) return box;
        }
        catch (Exception e)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"[STAIRS] {box.Name}'s top not read: {e.Message}; its box stands for it");
            return box;
        }
        finally
        {
            ZNetView.m_forceDisableInit = false;
            if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
            UnityEngine.Random.state = state;
        }
        return new SiteRock(box.At, box.HalfX, box.HalfZ, box.Yaw, box.MinY, box.MaxY, box.Name, top, x0, z0, w);
    }

    /// <summary>A rock's box seen from above: the corners of its colliders' box, scaled and turned
    /// as saved, measured along and across its turn about the vertical.</summary>
    private static SiteRock Box(Vector3 position, Quaternion rotation, Vector3 scale, Bounds local, string name)
    {
        float yaw = rotation.eulerAngles.y;
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = local.center + Vector3.Scale(local.extents,
                new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
            Vector3 w = rotation * Vector3.Scale(corner, scale);
            Vector2 q = SiteRock.Local(new Vector2(w.x, w.z), yaw);
            minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x);
            minZ = Mathf.Min(minZ, q.y); maxZ = Mathf.Max(maxZ, q.y);
            minY = Mathf.Min(minY, position.y + w.y); maxY = Mathf.Max(maxY, position.y + w.y);
        }
        Vector2 middle = new Vector2(position.x, position.z) + SiteRock.World(new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f), yaw);
        return new SiteRock(middle, (maxX - minX) * 0.5f, (maxZ - minZ) * 0.5f, yaw, minY, maxY, name);
    }

    /// <summary>A prefab's solid colliders' box in its root's frame.</summary>
    private static Bounds LocalBox(GameObject prefab)
    {
        int key = prefab.name.GetStableHashCode();
        if (s_rockBox.TryGetValue(key, out Bounds known)) return known;
        Bounds? box = null;
        Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
        foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
        {
            if (collider.isTrigger) continue;
            Bounds local = collider switch
            {
                BoxCollider b => new Bounds(b.center, b.size),
                SphereCollider s => new Bounds(s.center, Vector3.one * (2f * s.radius)),
                CapsuleCollider c => new Bounds(c.center, Vector3.one * Mathf.Max(2f * c.radius, c.height)),
                MeshCollider m when m.sharedMesh != null => m.sharedMesh.bounds,
                _ => new Bounds(Vector3.zero, Vector3.zero),
            };
            Matrix4x4 m2 = toRoot * collider.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 q = m2.MultiplyPoint3x4(corner);
                if (box == null) box = new Bounds(q, Vector3.zero);
                else { Bounds b = box.Value; b.Encapsulate(q); box = b; }
            }
        }
        return s_rockBox[key] = box ?? new Bounds(Vector3.zero, Vector3.zero);
    }

    public static void Reset()
    {
        s_opsBySite.Clear();
        s_siteGround.Clear();
    }
}
