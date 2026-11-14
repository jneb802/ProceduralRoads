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
    /// whose road, widened by the furthest a grown tree reaches (VegetationClearing.TreeReach), comes into the zone; and the
    /// structures' walks near it as road centres (VegetationClearing.CentresOf), each only in the way of what reaches it. The
    /// road points' are cached; the walks are read from the structure plans each time, as their clear areas are
    /// (AddRoadAreas), so a plan solved again is never judged by its old walk.
    /// </summary>
    public static List<VegetationClearing.RoadCentre> GetOrCreateRoadCentres(Vector2s zoneID)
    {
        if (!s_roadCentresCache.TryGetValue(zoneID, out List<VegetationClearing.RoadCentre>? centres))
        {
            centres = CreateRoadCentres(zoneID);
            s_roadCentresCache[zoneID] = centres;
        }

        return WithWalks(zoneID, new List<VegetationClearing.RoadCentre>(centres));
    }

    /// <summary>The road centres of the points added by appends after the first <paramref name="sinceAppend"/>, as
    /// <see cref="ClearAreasSince"/>, and the structures' walks. Not cached.</summary>
    public static List<VegetationClearing.RoadCentre> RoadCentresSince(Vector2s zoneID, int sinceAppend) =>
        WithWalks(zoneID, CreateRoadCentres(zoneID, sinceAppend));

    /// <summary><paramref name="centres"/> and the structures' walks near the zone as road centres, whatever the appends: as
    /// their clear areas, every structure's (AddRoadAreas).</summary>
    private static List<VegetationClearing.RoadCentre> WithWalks(Vector2s zoneID, List<VegetationClearing.RoadCentre> centres)
    {
        foreach (StairSupport.Walk walk in StructurePlacement.GetWalks(zoneID))
            VegetationClearing.CentresOf(walk, centres);
        return centres;
    }

    /// <summary>
    /// Adds to <paramref name="areas"/> (the game's own, a location's, already in it) the clear areas the roads hand
    /// PlaceVegetation for a zone, in its order: the roads' (those added after <paramref name="sinceAppend"/> appends; -1:
    /// every road's), then the structures' (the staircases'); and says where each kind starts; with the structures' walks
    /// near the zone (StructurePlacement.GetWalks: a bridge's deck and end steps, a staircase's walking boxes). Generation adds them to the game's list
    /// as it generates the zone; the server builds the same for a zone generated before its roads, so both judge alike
    /// (VegetationClearing.Judge).
    /// </summary>
    public static VegetationClearing.ZoneAreas AddRoadAreas(Vector2s zoneID, List<ZoneSystem.ClearArea> areas, int sinceAppend = -1)
    {
        int firstRoad = areas.Count;
        areas.AddRange(sinceAppend >= 0 ? ClearAreasSince(zoneID, sinceAppend) : GetOrCreateClearAreas(zoneID));
        int firstStructure = areas.Count;
        areas.AddRange(StructurePlacement.GetClearAreas(zoneID));
        return new VegetationClearing.ZoneAreas(areas, firstRoad, firstStructure, StructurePlacement.GetWalks(zoneID));
    }

    /// <summary>
    /// What generation hands PlaceVegetation for a zone (ZoneSystem_PlaceVegetation_Patch's prefix, the spawn adapter's one
    /// seam): the clear areas added to the game's own <paramref name="clearAreas"/> (<see cref="AddRoadAreas"/>), the road
    /// points its grown trees and lying ones are tested against (<paramref name="road"/>, <see cref="GetOrCreateRoadCentres"/>),
    /// and the network version its clearing is recorded at once placement is done (<paramref name="clearedFor"/>, for
    /// VegetationClearing.MarkCleared in the patch's postfix): the version read now where the roads or structures reach the
    /// zone and the game's test is the road's (<paramref name="judged"/>: the patch's transpiler put VegetationClearing.Judge
    /// in PlaceVegetation's InsideClearArea); else 0, nothing recorded. A zone generated without the road's test is left owed,
    /// and its clearing is the server bake's, by the same rule (<see cref="ForBake"/>), as for a zone generated before its
    /// roads: the game kept vegetation out of the clear areas only, and nothing off a bridge's walk, nor a trunk or a log
    /// reaching the road.
    /// </summary>
    internal static VegetationClearing.ZoneAreas ForGeneration(Vector2s zoneID, List<ZoneSystem.ClearArea> clearAreas, bool judged,
        out List<VegetationClearing.RoadCentre> road, out int clearedFor)
    {
        int version = RoadSpatialGrid.RoadNetworkVersion;
        // A zone the network protects is owed no clearing (ServerBakePlanner.VegetationOwed, the bake's own rule): only the
        // game's clear areas, no road, nothing recorded.
        if (!ServerBakePlanner.VegetationOwed(generated: true, cleared: false, excluded: NetworkCutoff.Excludes(zoneID)))
        {
            road = new List<VegetationClearing.RoadCentre>();
            clearedFor = 0;
            return new VegetationClearing.ZoneAreas(clearAreas, clearAreas.Count, clearAreas.Count);
        }
        road = GetOrCreateRoadCentres(zoneID);
        VegetationClearing.ZoneAreas areas = AddRoadAreas(zoneID, clearAreas);
        // Generated with the road's clear areas: its vegetation matches the network from the start, and the server never
        // clears it later.
        clearedFor = judged && (areas.AnyRoadOrStructure || road.Count > 0) ? version : 0;
        return areas;
    }

    /// <summary>
    /// The clearing the server bake judges a zone's saved vegetation by (ServerTerrainBake's pass and road_bake: the bake
    /// adapter's one seam): the clear areas generation handed PlaceVegetation for it, built the same way
    /// (<see cref="AddRoadAreas"/>): the game's own first, the zone's location's where it clears its area
    /// (ZoneSystem.PlaceLocations adds a square of its exterior radius as it places it, so only a placed location's), then the
    /// roads' and the staircases', with the structures' walks near it; and the road points its grown trees are tested against
    /// (<paramref name="road"/>, <see cref="GetOrCreateRoadCentres"/>). A zone cleared for the roads up to
    /// <paramref name="sinceAppend"/> appends owes only the later roads' (-1: every road's): <see cref="ClearAreasSince"/>,
    /// <see cref="RoadCentresSince"/>.
    /// </summary>
    internal static VegetationClearing.ZoneAreas ForBake(Vector2s zoneID, int sinceAppend, out List<VegetationClearing.RoadCentre> road)
    {
        var areas = new List<ZoneSystem.ClearArea>();
        if (ZoneSystem.instance.m_locationInstances.TryGetValue(zoneID, out ZoneSystem.LocationInstance location) &&
            location.m_placed && location.m_location != null && location.m_location.m_clearArea)
            areas.Add(new ZoneSystem.ClearArea(location.m_position, location.m_location.m_exteriorRadius));
        road = sinceAppend >= 0 ? RoadCentresSince(zoneID, sinceAppend) : GetOrCreateRoadCentres(zoneID);
        return AddRoadAreas(zoneID, areas, sinceAppend);
    }

    /// <summary>
    /// PlaceVegetation's test for one vegetation entry, with the scale it drew for this one: inside one of the game's own
    /// clear areas (a location's, VegetationClearing.InGameArea), as the game tests it without the mod, or what the road
    /// takes (VegetationClearing.Judge, the rule the server clearing applies too). <paramref name="lean"/> is the tilt it drew
    /// and the height its origin will stand at, for armour a road may run through. <paramref name="groundTilt"/>: the game
    /// will lay it on the ground's tilt (its entry's m_chanceToUseGroundTilt drawn true: every fallen log and stump), which
    /// turns its length a quarter turn from the drawn <paramref name="yaw"/> (<see cref="GroundTiltTurn"/>), so it is judged
    /// at the heading it will lie at, as the bake judges the saved one by its rotation, and a turn is handed back as the yaw
    /// to draw; its tilt is the ground's, not the drawn one.
    /// </summary>
    public static bool OnRoad(VegetationClearing.ZoneAreas areas, IReadOnlyList<VegetationClearing.RoadCentre> road, Vector3 point,
        string prefab, float scale, System.Func<Vector3, Heightmap.Biome> biomeAt, ref float yaw, VegetationClearing.Lean? lean = null,
        bool turns = true, VegetationClearing.Body? body = null, bool groundTilt = false)
    {
        var at = new Vector2(point.x, point.z);
        if (VegetationClearing.InGameArea(areas, at)) return true;
        int hash = prefab.GetStableHashCode();
        float heading = groundTilt ? Wrap(yaw + GroundTiltTurn) : yaw;
        if (groundTilt && lean is VegetationClearing.Lean drawn) lean = new VegetationClearing.Lean(float.NaN, float.NaN, drawn.Origin);
        VegetationClearing.Lying lying = VegetationClearing.Judge(hash, point, scale, heading, lean, areas, road, biomeAt, out float turned, body: body);
        VegetationClearing.Note(hash, at, lying, heading, turned);
        // One lying along the ground, turned so the road passes beside it, or through it (VegetationClearing.Lie). Where
        // the candidate's yaw cannot be turned (<paramref name="turns"/> false), one that would be is removed instead.
        bool turn = lying == VegetationClearing.Lying.Turned || lying == VegetationClearing.Lying.Through;
        if (turn && !turns) return true;
        if (turn) yaw = groundTilt ? Wrap(turned - GroundTiltTurn) : turned;
        return lying == VegetationClearing.Lying.Remove;
    }

    /// <summary>How far the game's ground tilt turns a vegetation entry's length from its drawn yaw, in degrees:
    /// PlaceVegetation lays it at Quaternion.LookRotation(Vector3.Cross(normal, Quaternion.Euler(0, yaw, 0) * Vector3.forward),
    /// normal), whose forward lies, seen from above, along the drawn yaw's right: yaw + 90 on any slope.</summary>
    public const float GroundTiltTurn = 90f;

    private static float Wrap(float degrees) => (degrees % 360f + 360f) % 360f;

    private static List<VegetationClearing.RoadCentre> CreateRoadCentres(Vector2s zoneID, int sinceAppend = -1)
    {
        var centres = new List<VegetationClearing.RoadCentre>();
        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        // Gathered in a circle twice the box's half-size, which holds the box widened by any road's half-width; each stored
        // point once (RoadSpatialGrid.GetRoadPointsNearPosition).
        foreach (RoadSpatialGrid.RoadPoint roadPoint in RoadSpatialGrid.GetRoadPointsNearPosition(zonePos, 2f * Beyond))
        {
            if (!Gathers(zonePos, roadPoint, sinceAppend))
                continue;
            centres.Add(new VegetationClearing.RoadCentre(roadPoint.p, roadPoint.w * 0.5f, roadPoint.h));
        }

        return centres;
    }

    /// <summary>How far past a zone's half-size its road centres are gathered, before a point's half-width: the furthest any
    /// vegetation reaches (<see cref="VegetationClearing.TreeReach"/>).</summary>
    private static float Beyond => RoadConstants.HalfZoneSize + VegetationClearing.TreeReach;

    /// <summary>Whether the zone centred at <paramref name="zonePos"/> gathers <paramref name="roadPoint"/> for its grown trees
    /// and lying ones (<see cref="GetOrCreateRoadCentres"/>): a walked point (<see cref="RoadSpatialGrid.RoadPoint.Walked"/>:
    /// not a cut nor a mound, which change the ground and are not walked, so a tree beside a cut or on a pier's mound stays
    /// unless it stands in a walk) of an append after the first <paramref name="sinceAppend"/> (-1: any) whose road, widened by <see cref="VegetationClearing.TreeReach"/>, comes into the
    /// zone. The one test of which zones a road's vegetation reach takes in: the road centres and the zones owed for them
    /// (<see cref="ZonesReached"/>, <see cref="Reaches"/>) alike.</summary>
    private static bool Gathers(Vector3 zonePos, RoadSpatialGrid.RoadPoint roadPoint, int sinceAppend)
    {
        if (!roadPoint.Walked || sinceAppend >= 0 && roadPoint.addition <= sinceAppend)
            return false;
        float reach = Beyond + roadPoint.w * 0.5f;
        return !(Mathf.Abs(roadPoint.p.x - zonePos.x) > reach) && !(Mathf.Abs(roadPoint.p.y - zonePos.z) > reach);
    }

    /// <summary>
    /// Every zone whose grown trees and lying ones a road can reach (<see cref="Gathers"/>): the road points of the appends
    /// after the first <paramref name="sinceAppend"/> (-1: every road). A tree rooted in a zone with no road of its own, its
    /// trunk or its length over the road next door, is judged only with its zone (the bake judges a zone's own objects), so
    /// these zones are owed clearing as the zones the roads are in are: the server bake's queue takes them
    /// (ServerTerrainBake), and an append owes them (RoadSpatialGrid.ZonesChangedSince). Candidates from each point's reach
    /// with a zone of slack either way, each kept only where the zone gathers the point, so the two agree zone for zone.
    /// </summary>
    internal static HashSet<Vector2s> ZonesReached(int sinceAppend = -1)
    {
        var zones = new HashSet<Vector2s>();
        foreach (RoadSpatialGrid.RoadPoint roadPoint in RoadSpatialGrid.SnapshotAllRoadPoints())
        {
            if (!roadPoint.Walked || sinceAppend >= 0 && roadPoint.addition <= sinceAppend)
                continue;
            float reach = Beyond + roadPoint.w * 0.5f;
            int x0 = Mathf.FloorToInt((roadPoint.p.x - reach) / RoadConstants.ZoneSize), x1 = Mathf.CeilToInt((roadPoint.p.x + reach) / RoadConstants.ZoneSize);
            int y0 = Mathf.FloorToInt((roadPoint.p.y - reach) / RoadConstants.ZoneSize), y1 = Mathf.CeilToInt((roadPoint.p.y + reach) / RoadConstants.ZoneSize);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var zone = new Vector2s(x, y);
                    if (!zones.Contains(zone) && Gathers(ZoneSystem.GetZonePos(zone), roadPoint, sinceAppend)) zones.Add(zone);
                }
        }
        return zones;
    }

    /// <summary>Whether the zone gathers any road point of the appends after the first <paramref name="sinceAppend"/> (-1: any
    /// road), as <see cref="ZonesReached"/> lists it.</summary>
    internal static bool Reaches(Vector2s zoneID, int sinceAppend)
    {
        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        foreach (RoadSpatialGrid.RoadPoint roadPoint in RoadSpatialGrid.GetRoadPointsNearPosition(zonePos, 2f * Beyond))
            if (Gathers(zonePos, roadPoint, sinceAppend)) return true;
        return false;
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
        // every clear area. Each stored point is gathered once (RoadSpatialGrid.GetRoadPointsInZone); two walks through one
        // place (a narrow road and a wider one, or a structure's end) give one each.
        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            if (!roadPoint.Walked)
                continue;
            float radius = roadPoint.w * RoadConstants.VegetationClearMultiplier;
            clearAreas.Add(new ZoneSystem.ClearArea(new Vector3(roadPoint.p.x, 0f, roadPoint.p.y), radius));
        }

        return clearAreas;
    }
}
