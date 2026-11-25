using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Manages vegetation clear areas for roads.
/// </summary>
public static class RoadClearAreaManager
{
    private static readonly Dictionary<Vector2s, List<ZoneSystem.ClearArea>> s_roadClearAreasCache =
        new Dictionary<Vector2s, List<ZoneSystem.ClearArea>>();

    public static void ClearCache()
    {
        s_roadClearAreasCache.Clear();
    }

    /// <summary>
    /// Gets or creates road clear areas for a zone. Used to prevent vegetation spawning on roads.
    /// </summary>
    public static List<ZoneSystem.ClearArea> GetOrCreateClearAreas(Vector2s zoneID)
    {
        if (!s_roadClearAreasCache.TryGetValue(zoneID, out List<ZoneSystem.ClearArea>? roadClearAreas))
        {
            roadClearAreas = CreateRoadClearAreas(zoneID);
            s_roadClearAreasCache[zoneID] = roadClearAreas;
        }

        return roadClearAreas;
    }

    private static List<ZoneSystem.ClearArea> CreateRoadClearAreas(Vector2s zoneID)
    {
        List<ZoneSystem.ClearArea> clearAreas = new List<ZoneSystem.ClearArea>();
        List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);

        if (roadPoints.Count == 0)
            return clearAreas;

        // One square per road point, centred on it: 0.6 w reaches past the road's half width at any heading, and the
        // points are close enough that neighbouring squares overlap across the whole width. Squares snapped to a coarser
        // grid stood up to half a cell off the road and left its far side outside every clear area. A point gathered
        // twice gives one square.
        HashSet<Vector2> seen = new HashSet<Vector2>();
        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            if (!seen.Add(roadPoint.p))
                continue;
            float radius = roadPoint.w * RoadConstants.VegetationClearMultiplier;
            clearAreas.Add(new ZoneSystem.ClearArea(new Vector3(roadPoint.p.x, 0f, roadPoint.p.y), radius));
        }

        return clearAreas;
    }
}
