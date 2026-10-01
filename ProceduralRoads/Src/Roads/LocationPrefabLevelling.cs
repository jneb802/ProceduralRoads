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

    /// <summary>Wire this up as LocationLevelling's source. Called from the plugin.</summary>
    public static void Install()
    {
        LocationLevelling.Source = OpsAt;
        LocationLevelling.PlacementHeightSource = PlacementHeightAt;
        // The SAME guarded door as Reset() uses. This was a lambda that
        // cleared the dictionary directly, so guarding Reset() left the
        // callback free to empty it under the island workers.
        LocationLevelling.ResetPlacements = LocationPlacementHeights.Clear;
        LocationPlacementHeights.Source = SavedPlacements;
        LocationLevelling.Prime = () => { LocationPlacementHeights.Read(); RoadSiteProtection.Prime(); };
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

    public static void Reset()
    {
        if (LocationLevelling.Sealed)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(
                "levelling data was asked to clear while roads were being generated; refused");
            return;
        }
        m_byPrefab.Clear(); m_terrainRadius.Clear();
        LocationPlacementHeights.Clear();
        RoadSiteProtection.Reset();
    }

    /// <summary>Every LocationProxy in the saved world, as a centre and the
    /// height it stands at. One pass over the ZDO table; the store decides
    /// when, and holds the result.</summary>
    private static IEnumerable<(Vector2 centre, float height)>? SavedPlacements()
    {
        if (ZDOMan.instance == null) return null;
        var found = new List<(Vector2, float)>();
        int proxyHash = "LocationProxy".GetStableHashCode();
        foreach (var entry in ZDOMan.instance.m_objectsByID)
        {
            ZDO zdo = entry.Value;
            if (zdo.GetPrefab() != proxyHash) continue;
            Vector3 position = zdo.GetPosition();
            found.Add((new Vector2(position.x, position.z), position.y));
        }
        return found;
    }

    private static float? PlacementHeightAt(Vector2 centre) => LocationPlacementHeights.At(centre);

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
        // Past this point lies an AssetBundle read and two writes into shared
        // dictionaries. Neither may happen on an island worker. A prefab that
        // could not be read is remembered as an EMPTY list by the catch below,
        // so a miss here means the prefab was never offered to preparation at
        // all -- not that reading it failed.
        if (LocationLevelling.RefuseAfterSealing(name)) return System.Array.Empty<LevelOp>();

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
