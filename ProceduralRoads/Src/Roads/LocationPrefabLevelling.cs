using System;
using System.Collections.Generic;
using UnityEngine;
using SoftReferenceableAssets;

namespace ProceduralRoads;

/// <summary>
/// Reads a location's levelling off its prefab, for LocationLevelling.
///
/// This is the engine half and is deliberately kept out of the pure sources:
/// it loads assets and walks Unity components, neither of which the test suite
/// can stand up. Everything it decides is a fact about the prefab; what that
/// means for a road is decided in LocationLevelling, which is tested.
///
/// A prefab is read once; only its copied operation values are kept. A world
/// has thousands of location instances and only dozens of distinct prefabs. Anything that goes wrong is
/// recorded as "this prefab levels nothing", which puts the road back on the
/// natural terrain under its own end - the behaviour before any of this.
/// </summary>
public static class LocationPrefabLevelling
{
    private static readonly Dictionary<string, IReadOnlyList<LevelOp>> m_byPrefab = new();

    private static readonly Dictionary<string, float> m_terrainRadius = new();
    private static readonly Dictionary<Vector2, float> m_placedHeights = new();
    private static bool m_placementsRead;

    /// <summary>Wire this up as LocationLevelling's source. Called from the plugin.</summary>
    public static void Install()
    {
        LocationLevelling.Source = OpsAt;
        LocationLevelling.PlacementHeightSource = PlacementHeightAt;
        LocationLevelling.ResetPlacements = () => { m_placedHeights.Clear(); m_placementsRead = false; };
        RoadSiteProtection.Source = Footprints;
    }

    private static IEnumerable<RoadSiteProtection.Footprint>? Footprints()
    {
        if (ZoneSystem.instance == null) return null;
        var locations = ZoneSystem.instance.GetLocationList();
        if (locations == null || locations.Count == 0) return null;
        var result = new List<RoadSiteProtection.Footprint>();
        foreach (var inst in locations)
        {
            ForPrefab(inst.m_location);
            m_terrainRadius.TryGetValue(inst.m_location.m_prefab.Name, out float terrainRadius);
            float radius = Mathf.Max(inst.m_location.m_exteriorRadius, terrainRadius);
            result.Add(new RoadSiteProtection.Footprint(new Vector2(inst.m_position.x, inst.m_position.z), radius));
        }
        return result;
    }

    public static void Reset() { m_byPrefab.Clear(); m_terrainRadius.Clear(); m_placedHeights.Clear(); m_placementsRead = false; RoadSiteProtection.Reset(); }

    private static float? PlacementHeightAt(Vector2 centre)
    {
        if (!m_placementsRead && ZDOMan.instance != null)
        {
            int proxyHash = "LocationProxy".GetStableHashCode();
            foreach (var entry in ZDOMan.instance.m_objectsByID)
            {
                ZDO zdo = entry.Value;
                if (zdo.GetPrefab() != proxyHash) continue;
                Vector3 position = zdo.GetPosition();
                m_placedHeights[new Vector2(position.x, position.z)] = position.y;
            }
            m_placementsRead = true;
        }
        if (m_placedHeights.TryGetValue(centre, out float height)) return height;
        // Stored and generated coordinates can differ below console precision.
        foreach (var entry in m_placedHeights)
            if ((entry.Key-centre).sqrMagnitude < 0.25f) return entry.Value;
        return null;
    }

    private static IReadOnlyList<LevelOp>? OpsAt(Vector2 centre)
    {
        if (ZoneSystem.instance == null) return null;
        var list = ZoneSystem.instance.GetLocationList();
        if (list == null) return null;
        foreach (ZoneSystem.LocationInstance inst in list)
        {
            if (Mathf.Abs(inst.m_position.x - centre.x) > 0.5f ||
                Mathf.Abs(inst.m_position.z - centre.y) > 0.5f)
                continue;
            return ForPrefab(inst.m_location);
        }
        return null;
    }

    private static IReadOnlyList<LevelOp> ForPrefab(ZoneSystem.ZoneLocation location)
    {
        string name = location.m_prefab.Name;
        if (m_byPrefab.TryGetValue(name, out IReadOnlyList<LevelOp> cached))
            return cached;

        var ops = new List<LevelOp>();
        float terrainRadius = 0f;
        try
        {
            var reference = location.m_prefab;
            var loader = AssetBundleLoader.Instance;
            int index = loader.m_assetIDToLoaderIndex[reference.m_assetID];
            TemporaryAssetRead.Read(
                () => loader.m_assetLoaders[index].ReferenceCount,
                () => reference.Load(),
                () => reference.Release(),
                () =>
            {
                GameObject asset = reference.Asset;
                if (asset == null) return false;
                // Offsets are taken against the prefab's own root rather than
                // assumed to be zero: a prefab authored off its origin would
                // otherwise report every operation as concentric.
                Vector3 root = asset.transform.position;
                foreach (TerrainModifier modifier in asset.GetComponentsInChildren<TerrainModifier>(true))
                {
                    Vector3 local = modifier.transform.position - root;
                    float reach = modifier.m_level ? modifier.m_levelRadius : 0f;
                    if (modifier.m_smooth) reach = Mathf.Max(reach, modifier.m_smoothRadius);
                    if (modifier.m_paintCleared) reach = Mathf.Max(reach, modifier.m_paintRadius);
                    // Rotation-independent enclosure, including square corners
                    // and off-centre smoothing/paint beyond the exterior radius.
                    if (modifier.m_square) reach *= Mathf.Sqrt(2f);
                    terrainRadius = Mathf.Max(terrainRadius, new Vector2(local.x, local.z).magnitude + reach);
                    if (!modifier.m_level) continue;
                    ops.Add(new LevelOp(local.x, local.z, local.y + modifier.m_levelOffset,
                        modifier.m_levelRadius, modifier.m_square));
                }
                return true;
            });
        }
        catch (Exception e)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Could not read levelling for {name}: {e.GetType().Name}; road ends there meet natural terrain");
            ops.Clear();
        }

        m_terrainRadius[name] = terrainRadius;
        m_byPrefab[name] = ops;
        return ops;
    }
}
