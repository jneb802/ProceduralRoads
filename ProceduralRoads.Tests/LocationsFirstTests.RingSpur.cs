using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A second road at a placed Location the network already serves, kept at its ring (no way onto its landing laid) and meeting the
/// road already serving it straight away (within its stub's reach, RoadNetworkGenerator.DropStubs). In game a Mistlands guard
/// tower (5886.3, -6020.8) was served by a road onto its landing from the east; a road from it to a cave set off from its
/// north-west ring, (5874.8, -5994.6), and met that road again 60 m out, (5816, -5975), past the serving road's last 40 m: a
/// spur from the tower's ring to its own road. Refused from the junction, the road had been planned once more with that stub
/// and built so, as the Location's own far-side way in. The project owner's decision (18 Nov 2026): "otherwise drop the
/// stretch from that Location's ring to a road already serving it"; 19 Nov 2026: "drop only the duplicate part".
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The tower at the origin on the level Mistlands ground, its levelled flat 8 m round its centre.</summary>
    private PlacedLocation RingTower()
    {
        var flat = new LevelOp(0f, 0f, 0f, 8f, false, 12f, 3f);
        var record = new PlacedLocationRecord
        {
            Prefab = "Mistlands_GuardTower1_ruined_new", Centre = new Vector2(0f, 0f), Yaw = 0f, Root = 50f, RockRadius = 0f,
            Ops = new List<LevelOp> { flat }, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        return new PlacedLocation { Record = record, Ground = ground, Shape = PlacedLocationShape.Of(record.Centre, 0f, flat, 0f, null, ground, false, record.Root) };
    }

    /// <summary>A line through <paramref name="corners"/>, a point every 2 m.</summary>
    private static List<Vector2> Through(params Vector2[] corners)
    {
        var points = new List<Vector2> { corners[0] };
        for (int c = 1; c < corners.Length; c++)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(corners[c - 1], corners[c]) / 2f));
            for (int i = 1; i <= n; i++) points.Add(Vector2.Lerp(corners[c - 1], corners[c], i / (float)n));
        }
        return points;
    }

    private RoadSpatialGrid.PlannedPath Laid(List<Vector2> points) =>
        new(points, points.ConvertAll(p => _world.GetHeight(p.x, p.y)), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f,
            points.Zip(points.Skip(1), Vector2.Distance).Sum(), false);

    /// <summary>The road serving the tower: from the north down its west side (x = -60), round its south and up its east side onto
    /// its east ring at (12, 0), recorded as its way in as generation records a road it stores (its last 40 m, x 12 to 40 and up
    /// to (40, -12), arriving heading west), the tower landed; or (not <paramref name="landed"/>) only the road down its west side,
    /// passing 60 m off: the tower not reached.</summary>
    private void TowerServedFromTheEast(PlacedLocation tower, bool landed)
    {
        if (!landed)
        {
            RoadSpatialGrid.Commit(Laid(Through(new Vector2(-60f, 120f), new Vector2(-60f, -40f))));
            return;
        }
        var points = Through(new Vector2(-60f, 120f), new Vector2(-60f, -40f), new Vector2(40f, -40f), new Vector2(40f, 0f), new Vector2(12f, 0f));
        RoadSpatialGrid.PlannedPath road = Laid(points);
        RoadSpatialGrid.Commit(road);
        LocationWaysIn.Record(tower.Centre, LocationWaysIn.Of(road.Points, road.Heights, tower.Centre, locationEndIsLast: true)!);
        PlacedLocations.MarkLanded(tower.Centre);
    }

    /// <summary>The second road as planned with its stub: from the tower's north-west ring (-8, 12), kept there (no way onto its
    /// landing), west onto the serving road at (-60, 30), 56 m along it, where it ends (a junction, reached).</summary>
    private RoadNetworkGenerator.PlannedRoad SpurFromTheRing(PlacedLocation tower)
    {
        var path = new List<Vector2> { new(-8f, 12f), new(-14f, 16f), new(-20f, 20f), new(-30f, 22.5f), new(-40f, 25f), new(-50f, 27.5f), new(-60f, 30f) };
        return new RoadNetworkGenerator.PlannedRoad
        {
            StartCenter = tower.Centre, EndCenter = path[path.Count - 1], Path = path, Plans = new List<RoadSpatialGrid.PlannedPath> { Laid(Through(path.ToArray())) },
            Ends = new RoadNetworkGenerator.LocationEndContext(10f, 0f, true, false, tower, null, 4f, 0f, 0f),
        };
    }

    /// <summary>The guard tower's spur: a road kept at the tower's ring, the tower reached already, meeting the road serving it 56 m
    /// out (past that road's way in, so not joining the way in itself, and from the far side of the way it arrives, so no twin of
    /// it): a duplicate as on the same side, refused for its stretch at the tower and searched again from that junction
    /// (RoadNetworkGenerator.Refusal.ReEnd, read by GenerateRoad through DuplicateWhole). It was built, a spur from the tower's
    /// ring to its own road. Not reached, the tower keeps it: the network's only road to it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARoadKeptAtTheRingOfALocationAlreadyServedThatMeetsItsRoadIsADuplicate(bool reached)
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        using var corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = 6f;
        RoadNetworkGenerator.CorridorSnap = 14f;
        RoadSpatialGrid.Clear();
        LocationWaysIn.Reset();
        PlacedLocation tower = RingTower();
        PlacedLocations.Ready(tower);
        TowerServedFromTheEast(tower, landed: reached);
        Assert.Equal(reached, RoadNetworkGenerator.EndReached(tower.Centre, 10f));
        RoadNetworkGenerator.PlannedRoad spur = SpurFromTheRing(tower);
        var mine = LocationWaysIn.Of(spur.Plans[0].Points, spur.Plans[0].Heights, tower.Centre)!;
        Assert.DoesNotContain(LocationWaysIn.There(tower.Centre), way => LocationWaysIn.SameWay(mine, way));

        RoadNetworkGenerator.Refusal? refused = RoadNetworkGenerator.RoadRefusal(spur, out _);

        if (!reached)
        {
            Assert.True(refused?.ReEnd == null, $"the only road to a tower not reached is a duplicate: {refused?.Reason}");
            return;
        }
        Assert.True(refused?.ReEnd is (Vector2 location, Vector2 junction) && location == tower.Centre && Vector2.Distance(junction, new Vector2(-60f, 30f)) <= RoadBeside.OnRoad,
            $"the spur from the tower's ring onto its own road is not a duplicate: {refused?.Reason ?? "built"} {refused?.ReEnd}");
    }
}
