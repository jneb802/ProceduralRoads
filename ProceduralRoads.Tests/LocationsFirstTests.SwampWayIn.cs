using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing across a swamp's shallows, planned as the road's own last stretch: its water crossed as the
/// road's (RoadCrossingDetector), its fill and earthwork judged as the road's. Offline every SunkenCrypt4's way in was refused as
/// "water" (judged on the bare ground), then at the dry floor; in game a crypt whose road ended on its own wade was refused "a wade
/// on a structure's levelled end".
/// </summary>
public partial class LocationsFirstTests
{
    private const float SwampShallows = 29.6f, CryptDirt = 30.9f;

    /// <summary>The ground round a crypt on a mound in a swamp: the dirt within 12 m, the shallows from 16 to 40 m, dry from 44 m.</summary>
    private static float SwampGround(float x, float z)
    {
        float r = new Vector2(x, z).magnitude;
        return r < 28f ? Mathf.Lerp(CryptDirt, SwampShallows, Mathf.Clamp01((r - 12f) / 4f)) : Mathf.Lerp(SwampShallows, CryptDirt, Mathf.Clamp01((r - 40f) / 4f));
    }

    /// <summary>A SunkenCrypt4 (its entrance 8.5 m out of its front, -z at yaw 0) on a dry mound in a swamp: the mound's dirt at
    /// <see cref="CryptDirt"/> within 12 m of its centre, down to the swamp's waded shallows (<see cref="SwampShallows"/>) by 16 m, the swamp
    /// out to 40 m, dry ground again (the dirt's height) from 44 m (<see cref="SwampGround"/>). <paramref name="groundAt"/> in its place.</summary>
    private PlacedLocation SunkenCryptInASwamp(Func<float, float, float>? groundAt = null, Heightmap.Biome biome = Heightmap.Biome.Swamp)
    {
        const string crypt = "SunkenCrypt4";
        var world = new MoundWorld { Biome = biome };
        world.Ground = groundAt ?? SwampGround;
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Reset();
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = crypt, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = CryptDirt, RockRadius = 0f,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = world.Ground;
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root,
            crypt, entrance: 0, bodyKnown: true);
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = entrance, Entrances = new[] { entrance } };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return location;
    }

    /// <summary>The road in front of the crypt across the swamp to its ring end 22 m out (as offline: 22.1 m), on land at the dry
    /// floor every road on land is built up to: its waypoints a cell apart, its planned points a metre apart.</summary>
    private static (List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans) RoadAcrossTheSwamp()
    {
        Vector2 At(float z) => new(0f, z);
        var path = Enumerable.Range(0, 9).Select(k => At(-22f - RoadPathfinder.CellSize * (8 - k))).ToList();
        float from = path[0].y;
        var points = Enumerable.Range(0, Mathf.RoundToInt(-22f - from) + 1).Select(k => At(from + k)).ToList();
        var piece = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => RoadConstants.DryRoadFloor), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, points.Count - 1, false);
        return (path, new List<RoadSpatialGrid.PlannedPath> { piece });
    }

    /// <summary>A crypt on a mound in a swamp, entered from a ring end in the swamp: the way in, the road's own last stretch over the shallows
    /// (crossed as the swamp's rules choose for any road) and on up onto the dirt in front of the door, ends level with it within a step (StairLayout.StepUp). Judged on
    /// the bare ground, it was refused "water" at its first point.</summary>
    [Fact]
    public void ACryptOnAMoundInASwampIsReachedOnTheDirtInFront()
    {
        PlacedLocation crypt = SunkenCryptInASwamp();
        var (path, plans) = RoadAcrossTheSwamp();

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why);

        Assert.True(entered && road != null, $"entered {entered}, staircase {span != null}: {why}");
        Vector2 last = road!.Points[road.Points.Count - 1];
        // Stopped at the landing's rim on its way toward the door (PlacedLocationShape.Ends), within the network's reach of it.
        Assert.True(OnTheLip(crypt.Entrances[0], last) && Vector2.Distance(last, crypt.Shape.Landing) <= RoadNetworkGenerator.LandingReach,
            $"the way in ends at {last}, the landing {crypt.Shape.Landing}");
        float dirt = crypt.Ground(last.x, last.y);
        Assert.True(Mathf.Abs(road.Heights[road.Heights.Count - 1] - dirt) <= StairLayout.StepUp,
            FormattableString.Invariant($"the way in ends at {road.Heights[road.Heights.Count - 1]:F2}, the dirt at {dirt:F2}"));
    }

    /// <summary>The road across the swamp whose last stretch to its ring end is its own wade over the shallows (its last three
    /// waypoints, the bed at <see cref="SwampShallows"/>).</summary>
    private static RoadCrossing WadeToTheEnd(List<Vector2> path) => new()
    {
        Kind = CrossingKind.Ford, Style = FordStyle.Wade, Shallow = true, FromIndex = path.Count - 4, ToIndex = path.Count - 1,
        FromBank = path[path.Count - 4], ToBank = path[path.Count - 1], RiverbedHeight = SwampShallows,
    };

    /// <summary>The road and its way in as one line in travel order: the road's pieces, then the way in.</summary>
    private static (List<Vector2> points, List<float> heights) RoadAndWayIn(List<RoadSpatialGrid.PlannedPath> plans, RoadSpatialGrid.PlannedPath wayIn)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        foreach (RoadSpatialGrid.PlannedPath p in RoadNetworkGenerator.InTravelOrder(plans, new[] { wayIn }))
            for (int i = 0; i < p.Points.Count; i++)
            {
                if (points.Count > 0 && (points[points.Count - 1] - p.Points[i]).sqrMagnitude < 0.01f) continue;
                points.Add(p.Points[i]);
                heights.Add(p.Heights[i]);
            }
        return (points, heights);
    }

    /// <summary>A crypt on a mound in a swamp whose road ends on its own wade at its ring (as a SunkenCrypt4 in game, its ring end
    /// on the wade 0.6 m under the dirt): the way in is the road's own last stretch, so its wade goes on under the water (the
    /// road's own wade, the bed's own surface) out of the water and on up onto the dirt to the porch with no earthwork: no fill over the
    /// shallows, no cut. A wade that runs onto the landing is the way in whole, from the wade's far bank over the stretch of road it
    /// replaces (not back over its own road). It ends on the dirt at its first end, within a step of the ground there (StairLayout.StepUp). It was refused as "a wade on a
    /// structure's levelled end": the way in was a levelled end built at the dry floor, a step over the wade's bed.</summary>
    [Fact]
    public void ACryptsRoadEndingOnAWadeIsFordedUpOntoTheDirtWithNoEarthwork()
    {
        PlacedLocation crypt = SunkenCryptInASwamp();
        var (path, plans) = RoadAcrossTheSwamp();
        var crossings = new List<RoadCrossing> { WadeToTheEnd(path) };

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), crossings, RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why);

        Assert.True(entered && road != null, $"entered {entered}, staircase {span != null}: {why}");
        // The road's wade goes on past its old ring end under the water and up onto the dirt to the landing: a wade that runs
        // onto the landing is the way in whole (RoadNetworkGenerator.PlanWayIn), the road ending at the wade's far bank. The way
        // in runs from there over the stretch of the road it replaces, which is not back over its own road.
        Vector2 ringEnd = new(0f, -22f), farBank = new(0f, -43f);
        Assert.DoesNotContain(crossings, c => c.Kind == CrossingKind.Ford && c.Style == FordStyle.Wade);
        Assert.True(road!.FollowTerrain, "the way in is the road's own wade, the bed's own surface");
        Assert.True(Vector2.Distance(path[path.Count - 1], farBank) < 0.5f && Vector2.Distance(path[path.Count - 1], road.Points[0]) < 0.5f,
            $"the road ends at {path[path.Count - 1]}, its way in starts at {road.Points[0]}");
        Assert.Contains(road.Points, p => p.magnitude < ringEnd.magnitude && RoadWater.AtOrOverSea(crypt.Ground(p.x, p.y)));
        // On up onto the dirt to the porch, ending at the first of its ends from the ring (PlacedLocationShape.Ends, the lip of the
        // landing's level ground), within a step of the ground there.
        Vector2 last = road.Points[road.Points.Count - 1];
        Vector2 end = RoadLocationEntry.WayInEnd(crypt.Entrances[0], ringEnd, PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth));
        Assert.True(OnTheLip(crypt.Entrances[0], last) && Vector2.Distance(last, crypt.Shape.Landing) <= RoadNetworkGenerator.LandingReach,
            $"the way in ends at {last}, off the lip (the first end from the ring: {end})");
        Assert.True(Vector2.Distance(last, end) < 0.5f, $"the way in ends at {last}, the first end from the ring at {end}");
        float landing = crypt.Ground(end.x, end.y);
        Assert.True(StairLayout.Walkable(road.Heights[road.Heights.Count - 1] - landing, 0f),
            FormattableString.Invariant($"the way in ends at {road.Heights[road.Heights.Count - 1]:F2}, the landing at {landing:F2}"));
        // No earthwork: the way in on the dirt within a step of it everywhere (no fill over the shallows, no cut).
        for (int i = 0; i < road.Points.Count; i++)
            Assert.True(StairLayout.Walkable(road.Heights[i] - crypt.Ground(road.Points[i].x, road.Points[i].y), 0f),
                FormattableString.Invariant($"at ({road.Points[i].x:F1}, {road.Points[i].y:F1}) the way in stands at {road.Heights[i]:F2} over ground {crypt.Ground(road.Points[i].x, road.Points[i].y):F2}"));
    }

    /// <summary>A crypt on a mound with a pool in front of it, deeper than a wade, the road ending on the dry ground beyond (cut back
    /// along its last stretch, 50 m out): the way in over the pool takes the crossing the road's own detector chooses on that line
    /// (RoadCrossingDetector.Detect: a raised ford or a bridge, as the terrain gives), its ends met as the road's are, never fill
    /// nor a wade of its own; on the dirt beyond it the way in runs on to the landing.</summary>
    [Fact]
    public void AWayInOverAPoolTakesTheRoadsOwnCrossing()
    {
        // The dirt to 30 m, a pool from there to 40 m out (longer than a puddle, RoadCrossingDetector.ShallowMinLength) 0.6 m under
        // the sea, dry ground beyond.
        PlacedLocation crypt = SunkenCryptInASwamp((x, z) =>
        {
            float r = new Vector2(x, z).magnitude;
            return r < 30f ? CryptDirt : r < 40f ? RoadConstants.SeaLevel - 0.6f : CryptDirt;
        }, Heightmap.Biome.Meadows);
        WorldGenerator world = WorldGenerator.instance!;
        Vector2 At(float z) => new(0f, z);
        var path = Enumerable.Range(0, 9).Select(k => At(-50f - RoadPathfinder.CellSize * (8 - k))).ToList();
        var points = Enumerable.Range(0, Mathf.RoundToInt(RoadPathfinder.CellSize * 8) + 1).Select(k => At(path[0].y + k)).ToList();
        var plans = new List<RoadSpatialGrid.PlannedPath> { new(points, points.ConvertAll(_ => CryptDirt), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, points.Count - 1, false) };
        var crossings = new List<RoadCrossing>();
        Vector2 roadEnd = path[path.Count - 1];
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, crypt.Aim, RoadLocationEntry.WayInEnd(crypt.Aim, roadEnd, PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth)),
            PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth));
        var dense = new List<Vector2>(path);
        for (int k = 1; k < line.Count; k++)
        {
            int cells = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(line[k - 1], line[k]) / RoadPathfinder.CellSize));
            for (int c = 1; c <= cells; c++) dense.Add(Vector2.Lerp(line[k - 1], line[k], c / (float)cells));
        }
        List<RoadCrossing> found = RoadCrossingDetector.Detect(dense, world, true, true);
        RoadCrossing chosen = Assert.Single(found);
        Assert.True(StructureEnds.MeetEnds(dense, found, world, RoadNetworkGenerator.EarthworkLimit, null, RoadNetworkGenerator.RoadWidth) == null, $"the road's own ends do not meet its {chosen.Kind} {chosen.Style} from {chosen.FromBank} to {chosen.ToBank}");

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), crossings, RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out _, out string why);

        Assert.True(entered && road != null, why);
        RoadCrossing over = Assert.Single(crossings);
        Assert.Equal(chosen.Kind, over.Kind);
        Assert.Equal(chosen.Style, over.Style);
        Assert.True(Vector2.Distance(over.ToBank, chosen.ToBank) < RoadPathfinder.CellSize, $"its bank at {over.ToBank}, the detector's at {chosen.ToBank}");
        Assert.False(road!.FollowTerrain);
        Assert.True(road.Points[0].magnitude <= over.ToBank.magnitude + 0.5f, $"the way in starts at {road.Points[0]}, over the pool");
    }

    /// <summary>A road coming in at an angle to its way in: the road and its way in are planned as one road, so its joint turns no
    /// tighter than any of the road's own bends (RoadSwitchbacks.MinBendRadius): no corner where the road ends and the way in starts.</summary>
    [Fact]
    public void TheJointBetweenARoadAndItsWayInIsNoSharperThanTheRoadsOwnBends()
    {
        PlacedLocation crypt = SunkenCryptInASwamp((_, _) => CryptDirt, Heightmap.Biome.Meadows);
        // The road from the south-west to a ring end 22 m out in front of the crypt: its way in turns 40 degrees there.
        Vector2 ringEnd = new(0f, -22f), along = new Vector2(Mathf.Sin(40f * Mathf.Deg2Rad), Mathf.Cos(40f * Mathf.Deg2Rad));
        var path = Enumerable.Range(0, 9).Select(k => ringEnd - along * (RoadPathfinder.CellSize * (8 - k))).ToList();
        var points = Enumerable.Range(0, Mathf.RoundToInt(RoadPathfinder.CellSize * 8) + 1).Select(k => path[0] + along * k).ToList();
        var piece = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => CryptDirt), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, points.Count - 1, false);
        var plans = new List<RoadSpatialGrid.PlannedPath> { piece };

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out _, out string why);

        Assert.True(entered && road != null, why);
        var (line, _) = RoadAndWayIn(plans, road!);
        // Each point's turn over the metres either side of it, as a radius: no tighter than the road's least bend.
        for (int i = 1; i < line.Count - 1; i++)
        {
            Vector2 a = line[i] - line[i - 1], b = line[i + 1] - line[i];
            if (a.magnitude < 0.05f || b.magnitude < 0.05f) continue;
            float turn = (float)Math.Acos(Mathf.Clamp(Vector2.Dot(a.normalized, b.normalized), -1f, 1f));
            float step = 0.5f * (a.magnitude + b.magnitude);
            Assert.True(turn <= step / RoadSwitchbacks.MinBendRadius + 1e-3f,
                FormattableString.Invariant($"at ({line[i].x:F1}, {line[i].y:F1}) the road turns {turn * Mathf.Rad2Deg:F1} degrees over {step:F2} m: a radius of {step / Mathf.Max(turn, 1e-6f):F2} m, under {RoadSwitchbacks.MinBendRadius:F1}"));
        }
    }

    /// <summary>One water and fill verdict for a road and a way in (rule 13): the same line over a swamp's shallows onto a crypt's
    /// landing, planned as a way in (RoadLocationEntry.Plan, its road the end alone) and as an ordinary road by the road's own
    /// planner (RoadCrossingDetector.Detect, RoadNetworkGenerator.PlanRoadPathWithCrossings, the one list's earthwork cap and ridge
    /// judge), gives the same crossings, the same heights and the same ridge verdict.</summary>
    [Fact]
    public void TheWayInAndTheRoadGiveTheSameWaterAndFillVerdict()
    {
        PlacedLocation crypt = SunkenCryptInASwamp();
        Vector2 roadEnd = new(0f, -22f);
        var request = new LocationApproachRequest { RoadEnd = roadEnd, RoadHeight = SwampShallows, Centre = crypt.Centre, RoadTail = new List<Vector2> { roadEnd }, Reach = 64f };
        PlacedLocationShape door = crypt.Entrances[0];

        RoadSpatialGrid.PlannedPath? wayIn = RoadLocationEntry.Plan(request, door, RoadNetworkGenerator.RoadWidth, crypt.Ground, out string why);

        Assert.True(wayIn != null, why);
        Vector2 end = RoadLocationEntry.WayInEnd(door, roadEnd, PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth));
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth));
        var dense = new List<Vector2> { roadEnd };
        for (int k = 1; k < line.Count; k++)
        {
            int cells = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(line[k - 1], line[k]) / RoadPathfinder.CellSize));
            for (int c = 1; c <= cells; c++) dense.Add(Vector2.Lerp(line[k - 1], line[k], c / (float)cells));
        }
        WorldGenerator world = WorldGenerator.instance!;
        List<RoadCrossing> crossings = RoadCrossingDetector.Detect(dense, world, true, true);
        float landing = door.Landings(crypt.Ground, RoadLocationEntry.InsideReach(RoadNetworkGenerator.RoadWidth) + RoadLocationEntry.EndSlack,
            RoadLocationEntry.RoadLandingTries, PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth).BodyMargin)[0].height;
        List<RoadSpatialGrid.PlannedPath>? asRoad = RoadGrade.Trial(() => RoadNetworkGenerator.PlanRoadPathWithCrossings(dense, crossings, RoadNetworkGenerator.RoadWidth,
            SwampShallows, landing, null, default, new RoadLocationProtection.Targets(null, crypt.Centre, crypt.Centre)));

        Assert.NotNull(asRoad);
        RoadSpatialGrid.PlannedPath last = asRoad![asRoad.Count - 1];
        Assert.Equal(last.FollowTerrain, wayIn!.FollowTerrain);
        Assert.Equal(last.Points[last.Points.Count - 1], wayIn.Points[wayIn.Points.Count - 1]);
        Assert.Equal(last.Heights[last.Heights.Count - 1], wayIn.Heights[wayIn.Heights.Count - 1]);
        bool roadRidge = RoadNetworkGenerator.AlongARidge(asRoad, crossings, out _, out _) != null;
        bool wayInRidge = RoadNetworkGenerator.AlongARidge(new List<RoadSpatialGrid.PlannedPath> { wayIn }, crossings, out _, out _) != null;
        Assert.Equal(roadRidge, wayInRidge);
        Assert.Equal(RoadNetworkGenerator.PastTerrainLimit(asRoad, RoadNetworkGenerator.EarthworkLimit, new List<Vector2>()) > 0,
            RoadNetworkGenerator.PastTerrainLimit(new List<RoadSpatialGrid.PlannedPath> { wayIn }, RoadNetworkGenerator.EarthworkLimit, new List<Vector2>()) > 0);
    }
}
