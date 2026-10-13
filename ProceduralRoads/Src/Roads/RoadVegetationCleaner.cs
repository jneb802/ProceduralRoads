using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Removes generated vegetation whose placement point lands inside a road corridor.
/// </summary>
public static class RoadVegetationCleaner
{
    private static readonly HashSet<int> s_vegetationPrefabHashes = new HashSet<int>();
    private static readonly List<ZDO> s_zoneObjects = new List<ZDO>();
    private static bool s_hashesInitialized;

    public static void Reset()
    {
        s_vegetationPrefabHashes.Clear();
        s_hashesInitialized = false;
        s_zoneObjects.Clear();
    }

    public static int RemoveOverlappingVegetation(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        if (roadPoints == null || roadPoints.Count == 0 ||
            ZoneSystem.instance == null || ZDOMan.instance == null ||
            ZNetScene.instance == null || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return 0;
        }

        EnsureVegetationPrefabHashes();
        if (s_vegetationPrefabHashes.Count == 0)
            return 0;

        s_zoneObjects.Clear();
        ZDOMan.instance.FindSectorObjects(zoneID, 0, 0, s_zoneObjects);

        int removed = 0;
        foreach (ZDO zdo in s_zoneObjects)
        {
            if (zdo == null || !zdo.IsValid() || !s_vegetationPrefabHashes.Contains(zdo.GetPrefab()))
                continue;

            Vector3 position = zdo.GetPosition();
            if (!OverlapsRoad(position, roadPoints))
                continue;

            zdo.SetOwner(ZDOMan.GetSessionID());
            GameObject instance = ZNetScene.instance.FindInstance(zdo.m_uid);
            if (instance != null)
                ZNetScene.instance.Destroy(instance);
            else
                ZDOMan.instance.DestroyZDO(zdo);

            removed++;
        }

        s_zoneObjects.Clear();
        if (removed > 0)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: removed {removed} overlapping vegetation object(s)");
        }

        return removed;
    }

    private static void EnsureVegetationPrefabHashes()
    {
        if (s_hashesInitialized)
            return;

        s_vegetationPrefabHashes.Clear();
        if (ZoneSystem.instance != null)
        {
            foreach (ZoneSystem.ZoneVegetation vegetation in ZoneSystem.instance.m_vegetation)
            {
                if (vegetation?.m_prefab == null || vegetation.m_prefab.GetComponent<ZNetView>() == null)
                    continue;

                s_vegetationPrefabHashes.Add(vegetation.m_prefab.name.GetStableHashCode());
            }
        }

        s_hashesInitialized = true;
    }

    private static bool OverlapsRoad(Vector3 position, List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        Vector2 position2D = new Vector2(position.x, position.z);
        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            float radius = RoadConstants.GetVegetationClearRadius(roadPoint.w);
            if ((roadPoint.p - position2D).sqrMagnitude <= radius * radius)
                return true;
        }

        return false;
    }
}
