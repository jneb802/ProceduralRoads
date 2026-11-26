using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing whose last stretch is a wade, the road before it running on into the Location's footprint
/// up to the wade's bank (in game a SunkenCrypt4 in the Swamp: the road's last 11 m inside the crypt's footprint, then a 5.6 m wade
/// onto the landing). The wade was stored into the crypt and painted; the road's stretch before it inside the footprint was stored
/// into no Location, so the terrain writer left it unlevelled and unpainted: grass between the painted road outside the footprint
/// and the wade at the porch.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The crypt's footprint here: 28 m round (a crypt's paint reaches past its levelling).</summary>
    private const float WideFootprint = 28f;

    /// <summary>A crypt whose landing stands in a short pool: the crypt's own low ground within 5 m, the swamp's shallows from 5 to
    /// 14 m (the landing, 8.5 m out, in them), up to the dirt by 19 m.</summary>
    private static float ShortPoolAtThePorch(float x, float z)
    {
        float r = new Vector2(x, z).magnitude;
        return r < 5f ? RoadConstants.SeaLevel + 0.2f : r < 14f ? SwampShallows : Mathf.Lerp(SwampShallows, CryptDirt, Mathf.Clamp01((r - 14f) / 5f));
    }

    /// <summary>The way in planned as the road's own last stretch (PlanWayIn) and laid as generation lays it (the road's pieces,
    /// then the way in into its Location): every point of it, the road's run into the footprint up to the wade's bank and the
    /// wade onto the landing alike, is stored into the crypt and painted by the terrain writer, end to end.</summary>
    [Fact]
    public void ALaidWayInIsPaintedEndToEnd()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(ShortPoolAtThePorch);
        // The road runs some 9 m into the footprint before the wade's bank.
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(crypt.Centre, WideFootprint) });
        Vector2 roadEnd = new(0f, -50f);
        PlacedLocationShape door = crypt.Entrances[0];
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        Vector2 end = RoadLocationEntry.WayInEnd(door, roadEnd, walker);
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, walker);
        var request = new LocationApproachRequest { RoadEnd = roadEnd, RoadHeight = CryptDirt, Centre = crypt.Centre, RoadTail = new List<Vector2> { roadEnd }, Reach = 64f };

        RoadSpatialGrid.PlannedPath? wayIn = RoadNetworkGenerator.PlanWayIn(RoadNetworkGenerator.WayInRoad.Alone(request), line, crypt.Ground(end.x, end.y),
            crypt.Centre, RoadNetworkGenerator.RoadWidth, out string why, out RoadNetworkGenerator.WayInPlan? planned);

        Assert.True(wayIn != null && planned != null, why);
        Assert.True(wayIn!.FollowTerrain, "the way in onto the landing in the pool is the road's own wade");
        bool InFootprint(Vector2 p) => p.magnitude <= WideFootprint;
        Assert.Contains(planned!.Plans, p => !p.FollowTerrain && p.Points.Any(InFootprint));

        RoadSpatialGrid.CommitRoad(planned.Plans, ownChecked: true);
        RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { wayIn }, intoLocation: true, ownChecked: true);
        // Stored: every point in the footprint into the crypt.
        Assert.All(RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, WideFootprint).Where(rp => InFootprint(rp.p)),
            rp => Assert.True(rp.intoLocation && rp.location == crypt.Centre,
                FormattableString.Invariant($"the point at ({rp.p.x:F1}, {rp.p.y:F1}) in the footprint is stored into {(rp.intoLocation ? rp.location.ToString() : "no Location")}")));

        // Laid: painted under every point, from the footprint's edge to the landing.
        Vector2s zone = new(0, 0);
        Heightmap hm = Heightmap.CreateForZone(zone);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        int Index(Vector2 p) => Mathf.RoundToInt(p.y / spacing + tc.m_width / 2f) * grid + Mathf.RoundToInt(p.x / spacing + tc.m_width / 2f);
        foreach (Vector2 p in planned.Plans.Append(wayIn).SelectMany(plan => plan.Points).Where(p => Mathf.Abs(p.x) < 30f && Mathf.Abs(p.y) < 30f))
            Assert.True(tc.m_modifiedPaint[Index(p)], FormattableString.Invariant($"the way in is not painted at ({p.x:F1}, {p.y:F1}), {p.magnitude:F1} m from the crypt"));
    }
}
