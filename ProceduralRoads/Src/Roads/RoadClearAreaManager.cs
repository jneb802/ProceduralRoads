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
    /// Adds to <paramref name="areas"/> (the game's own, a location's, already in it) the clear areas the roads hand
    /// PlaceVegetation for a zone, in its order: the roads' (those added after <paramref name="sinceAppend"/> appends; -1:
    /// every road's), the staircases', then the bridges'; and says where each kind starts. Generation adds them to the
    /// game's list as it generates the zone; the server builds the same list for a zone generated before its roads, so
    /// both judge alike (VegetationClearing.Judge).
    /// </summary>
    public static VegetationClearing.ZoneAreas AddRoadAreas(Vector2s zoneID, List<ZoneSystem.ClearArea> areas, int sinceAppend = -1)
    {
        int firstRoad = areas.Count;
        areas.AddRange(sinceAppend >= 0 ? ClearAreasSince(zoneID, sinceAppend) : GetOrCreateClearAreas(zoneID));
        int firstStructure = areas.Count;
        areas.AddRange(StructurePlacement.GetClearAreas(zoneID, staircases: true));
        int firstRockKeeping = areas.Count;
        areas.AddRange(StructurePlacement.GetClearAreas(zoneID, staircases: false));
        return new VegetationClearing.ZoneAreas(areas, firstRoad, firstStructure, firstRockKeeping);
    }

    /// <summary>
    /// PlaceVegetation's test for one vegetation entry, with the scale it drew for this one: inside one of the game's own
    /// clear areas (a location's, VegetationClearing.InGameArea), as the game tests it without the mod, or what the road
    /// takes (VegetationClearing.Judge, the rule the server clearing applies too). <paramref name="lean"/> is the tilt it drew
    /// and the height its origin will stand at, for armour a road may run through.
    /// </summary>
    public static bool OnRoad(VegetationClearing.ZoneAreas areas, IReadOnlyList<VegetationClearing.RoadCentre> road, Vector3 point,
        string prefab, float scale, System.Func<Vector3, Heightmap.Biome> biomeAt, ref float yaw, VegetationClearing.Lean? lean = null,
        bool turns = true)
    {
        var at = new Vector2(point.x, point.z);
        if (VegetationClearing.InGameArea(areas, at)) return true;
        int hash = prefab.GetStableHashCode();
        VegetationClearing.Lying lying = VegetationClearing.Judge(hash, point, scale, yaw, lean, areas, road, biomeAt, out float turned);
        VegetationClearing.Note(hash, at, lying, yaw, turned);
        // One lying along the ground, turned so the road passes beside it, or through it (VegetationClearing.Lie). Where
        // the candidate's yaw cannot be turned (<paramref name="turns"/> false), one that would be is removed instead.
        bool turn = lying == VegetationClearing.Lying.Turned || lying == VegetationClearing.Lying.Through;
        if (turn && !turns) return true;
        if (turn) yaw = turned;
        return lying == VegetationClearing.Lying.Remove;
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
            centres.Add(new VegetationClearing.RoadCentre(roadPoint.p, roadPoint.w * 0.5f, roadPoint.h));
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
