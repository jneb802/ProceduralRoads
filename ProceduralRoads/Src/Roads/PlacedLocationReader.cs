using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What the game placed, read off the saved world: a location's placement (its LocationProxy: root, turn
/// and seed), the levelling its placement kept (<see cref="LocationPrefabLevelling.PlacedOps"/>, the
/// game's draw replayed), its own rocks, and the ground it leaves. One owner for the locations placed
/// before their roads (<see cref="LocationsFirstLoad"/>) and for the bake's reading of a placed location's parts.
/// Main thread.
/// </summary>
public static partial class PlacedLocationReader
{
    /// <summary>How far past a location's ring (or its rock), and round a road's end, its ground is read
    /// or rebuilt: a road or staircase onto its landing cannot be laid beyond it.</summary>
    public const float Margin = 16f;

    /// <summary>Forget the replayed parts with the network.</summary>
    public static void Install() => RoadNetworkGenerator.OnReset += Reset;

    // The game's draw replayed (LocationPrefabLevelling.PlacedOps): null when the prefab cannot be read. Asked once a location a
    // session (KeptOps).
    private static partial IReadOnlyList<LevelOp>? Replay(Vector2 centre, ZDO proxy) =>
        LocationPrefabLevelling.PlacedOps(centre, proxy.GetInt(ZDOVars.s_seed), proxy.GetPosition(), proxy.GetRotation());

    /// <summary>Whether the location whose identity is <paramref name="centre"/> (LocationPrefabLevelling.LocationOf) keeps every
    /// rock (<see cref="LocationRock.KeepsEveryRock"/>).</summary>
    internal static bool KeepsEveryRock(Vector2 centre) =>
        LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance instance) && LocationRock.KeepsEveryRock(instance.m_location.m_prefab.Name);

    /// <summary>The location's exterior radius (the location whose identity is <paramref name="centre"/>,
    /// LocationPrefabLevelling.LocationOf; 0 for none): its rock, for leading a staircase round it. The one owner of which rocks a location keeps (docs/OWNERS.md).</summary>
    internal static float RockRadius(Vector2 centre) =>
        LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance instance) ? instance.m_location.m_exteriorRadius : 0f;

    private static readonly Dictionary<int, Bounds> s_rockBox = new();

    /// <summary>
    /// The location's own boulders: every natural boulder (or fractured one) saved in the
    /// ground's box and standing in a location's footprint, as its prefab's colliders' box seen
    /// from above, scaled and turned as it is saved. A location's rocks are placed with it (a frost cave's reach about
    /// 20 m, past its exterior radius) and vegetation is kept out of its area, so a boulder
    /// in its footprint is its own. The location's own building pieces too (its parts with a WearNTear: a tower ruin's walls
    /// and floors, a Dvergr entrance's railed ring and stairs), as their boxes, marked built (LocationRock.Built): the body
    /// every approach keeps PlacedLocationShape.StructureGap clear of.
    /// </summary>
    internal static List<LocationRock> LocationRocks(int x0, int z0, int w, int h, Vector2 centre, float root, float yaw, out int world)
    {
        world = 0;
        var rocks = new List<LocationRock>();
        if (ZDOMan.instance == null || ZNetScene.instance == null) return rocks;
        // Only the location's own parts: a boulder of the world's standing in its footprint is any
        // road's to carve (a rock2_mountain that stayed put while the frost cave beside it turned,
        // in game). Where the prefab's parts are not known, every boulder there, as before.
        string? location = LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance instance)
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
            if (!RoadLocationProtection.Contains(at)) continue;
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null) continue;
            bool rock = RoadRockPolicy.IsClearableRock(prefab.name, RoadRockCarve.FracturedFrom());
            // The location's own rocks of kinds no road clears, its ice above all (Hildir's cave:
            // ice_rock1, rock3_ice, caverock_ice_*): kept and kept clear of, as its large rocks
            // are. Left out, a staircase ran up beside and behind its great ice block.
            bool ownRock = !rock && location != null && prefab.name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0
                && LocationPrefabLevelling.IsPart(location, rootAt, turn, p) == true;
            bool piece = !rock && !ownRock && location != null && prefab.GetComponent<WearNTear>() != null
                && LocationPrefabLevelling.IsPart(location, rootAt, turn, p) == true;
            if (piece)
            {
                Vector3 pieceScale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
                rocks.Add(LocationRock.Of(p, zdo.GetRotation(), pieceScale != Vector3.zero ? pieceScale : Vector3.one, LocalBox(prefab), prefab.name, built: true));
                continue;
            }
            if (!rock && !ownRock) continue;
            if (!ownRock && location != null && LocationPrefabLevelling.IsPart(location, rootAt, turn, p) == false) { world++; continue; }
            Vector3? setScale = SavedScale(zdo);
            LocationRock box = BoxOf(zdo, prefab);
            // Its real top where it counts: a rock an approach keeps clear of (large, round the
            // mouth, or at a location keeping every rock); a small one elsewhere is cleared anyway.
            bool kept = ownRock || box.KeptAt(centre, rockRadius, keepSmall, out _);
            rocks.Add(kept ? WithTop(box, prefab, p, zdo.GetRotation(), setScale) : box);
        }
        return rocks;
    }

    /// <summary>The scale a ZDO's object was saved with (a vector, or a scalar), none when it has none.</summary>
    internal static Vector3? SavedScale(ZDO zdo)
    {
        Vector3 saved = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
        float scalar = zdo.GetFloat(ZDOVars.s_scaleScalarHash, 0f);
        return saved != Vector3.zero ? saved : scalar > 0f ? Vector3.one * scalar : (Vector3?)null;
    }

    /// <summary>A saved rock's box (<see cref="LocationRock.Of"/>) as planning reads it: its prefab's colliders' box at the saved
    /// scale and turn. The rock clearing's probe reads a live rock through this too.</summary>
    internal static LocationRock BoxOf(ZDO zdo, GameObject prefab) =>
        LocationRock.Of(zdo.GetPosition(), zdo.GetRotation(), SavedScale(zdo) ?? Vector3.one, LocalBox(prefab), prefab.name);

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
    private static LocationRock WithTop(LocationRock box, GameObject prefab, Vector3 position, Quaternion rotation, Vector3? scale)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        for (int c = 0; c < 4; c++)
        {
            Vector2 corner = box.At + LocationRock.World(new Vector2((c & 1) == 0 ? -box.HalfX : box.HalfX, (c & 2) == 0 ? -box.HalfZ : box.HalfZ), box.Yaw);
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
        return new LocationRock(box.At, box.HalfX, box.HalfZ, box.Yaw, box.MinY, box.MaxY, box.Name, top, x0, z0, w);
    }

    /// <summary>A prefab's solid colliders' box in its root's frame.</summary>
    internal static Bounds LocalBox(GameObject prefab)
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

}
