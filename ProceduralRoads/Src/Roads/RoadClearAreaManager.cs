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

    private static readonly Dictionary<Vector2s, List<VegetationClearing.RoadCentre>> s_roadCentresCache =
        new Dictionary<Vector2s, List<VegetationClearing.RoadCentre>>();

    public static void ClearCache()
    {
        s_roadClearAreasCache.Clear();
        s_roadCentresCache.Clear();
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

    /// <summary>The clear areas of the road points in a zone added by appends after the first <paramref name="sinceAppend"/>
    /// (a zone cleared for the older roads owes only the newer ones'). Not cached.</summary>
    public static List<ZoneSystem.ClearArea> ClearAreasSince(Vector2s zoneID, int sinceAppend) => CreateRoadClearAreas(zoneID, sinceAppend);

    /// <summary>
    /// The road points a zone's grown trees are tested against (VegetationClearing.TrunkReachesRoad): every road point
    /// whose road, widened by the furthest a grown tree reaches (VegetationClearing.TreeReach), comes into the zone.
    /// </summary>
    public static List<VegetationClearing.RoadCentre> GetOrCreateRoadCentres(Vector2s zoneID)
    {
        if (!s_roadCentresCache.TryGetValue(zoneID, out List<VegetationClearing.RoadCentre>? centres))
        {
            centres = CreateRoadCentres(zoneID);
            s_roadCentresCache[zoneID] = centres;
        }

        return centres;
    }

    /// <summary>The road centres of the points added by appends after the first <paramref name="sinceAppend"/>, as
    /// <see cref="ClearAreasSince"/>. Not cached.</summary>
    public static List<VegetationClearing.RoadCentre> RoadCentresSince(Vector2s zoneID, int sinceAppend) => CreateRoadCentres(zoneID, sinceAppend);

    /// <summary>
    /// PlaceVegetation's test for one vegetation entry, with the scale it drew for this one: inside a clear area
    /// (<see cref="InsideClearArea"/>), or a grown tree whose trunk reaches one of <paramref name="road"/>
    /// (VegetationClearing.OnRoad, the rule the server clearing applies too).
    /// </summary>
    public static bool OnRoad(List<ZoneSystem.ClearArea> areas, int firstRoadArea, int firstStructureArea,
        IReadOnlyList<VegetationClearing.RoadCentre> road, Vector3 point, string prefab, float scale,
        RockClearingMode rockClearing, System.Func<Vector3, Heightmap.Biome> biomeAt) =>
        VegetationClearing.OnRoad(InsideClearArea(areas, firstRoadArea, firstStructureArea, point, prefab, rockClearing, biomeAt),
            prefab, scale, road, new Vector2(point.x, point.z));

    /// <summary>
    /// PlaceVegetation's clear-area test (ZoneSystem.InsideClearArea: strict, and square), asked for one vegetation entry.
    /// The areas before <paramref name="firstRoadArea"/> are the game's own (locations), from there to
    /// <paramref name="firstStructureArea"/> the roads', and from there on the structures'. The game's and the structures'
    /// apply to every entry. The roads' keep vegetation out, but not a large rock the rock clearing takes there
    /// (<see cref="RoadRockPolicy.ClearingTakes"/>, in the biome <paramref name="biomeAt"/> gives): that one is placed as
    /// in a world without roads and Roads/RockClearing carves or removes it. A rock the rock clearing would leave is kept
    /// off the road as vegetation is.
    /// </summary>
    public static bool InsideClearArea(List<ZoneSystem.ClearArea> areas, int firstRoadArea, int firstStructureArea, Vector3 point,
        string prefab, RockClearingMode rockClearing, System.Func<Vector3, Heightmap.Biome> biomeAt)
    {
        int roadStart = Mathf.Min(firstRoadArea, areas.Count);
        int roadEnd = Mathf.Clamp(firstStructureArea, roadStart, areas.Count);
        bool rockKeepsRoad = RoadRockPolicy.IsClearableRock(prefab) && roadStart < roadEnd &&
            RoadRockPolicy.ClearingTakes(prefab, biomeAt(point), rockClearing);
        for (int i = 0; i < areas.Count; i++)
        {
            if (rockKeepsRoad && i >= roadStart && i < roadEnd)
                continue;
            ZoneSystem.ClearArea area = areas[i];
            if (point.x > area.m_center.x - area.m_radius && point.x < area.m_center.x + area.m_radius &&
                point.z > area.m_center.z - area.m_radius && point.z < area.m_center.z + area.m_radius)
                return true;
        }
        return false;
    }

    private static List<VegetationClearing.RoadCentre> CreateRoadCentres(Vector2s zoneID, int sinceAppend = -1)
    {
        var centres = new List<VegetationClearing.RoadCentre>();
        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        float beyond = RoadConstants.HalfZoneSize + VegetationClearing.TreeReach;
        // Gathered in a circle twice the box's half-size, which holds the box widened by any road's half-width.
        HashSet<Vector2> seen = new HashSet<Vector2>();
        foreach (RoadSpatialGrid.RoadPoint roadPoint in RoadSpatialGrid.GetRoadPointsNearPosition(zonePos, 2f * beyond))
        {
            if (sinceAppend >= 0 && roadPoint.addition <= sinceAppend)
                continue;
            float reach = beyond + roadPoint.w * 0.5f;
            if (Mathf.Abs(roadPoint.p.x - zonePos.x) > reach || Mathf.Abs(roadPoint.p.y - zonePos.z) > reach || !seen.Add(roadPoint.p))
                continue;
            centres.Add(new VegetationClearing.RoadCentre(roadPoint.p, roadPoint.w * 0.5f));
        }

        return centres;
    }

    private static List<ZoneSystem.ClearArea> CreateRoadClearAreas(Vector2s zoneID, int sinceAppend = -1)
    {
        List<ZoneSystem.ClearArea> clearAreas = new List<ZoneSystem.ClearArea>();
        List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
        if (sinceAppend >= 0) roadPoints = roadPoints.FindAll(p => p.addition > sinceAppend);

        if (roadPoints.Count == 0)
            return clearAreas;

        // One square per road point, centred on it, of half-size VegetationClearMultiplier w: on a road along an axis
        // its middle 90 % is clear and the outer edge is left to creep; a square reaches further across a diagonal road
        // (0.6 w at 45 degrees, past the edge). The points are close enough that neighbouring squares overlap along the
        // road. Squares snapped to a coarser grid stood up to half a cell off the road and left its far side outside
        // every clear area. A point gathered twice gives one square.
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
