using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase onto a Location from its road's own end climbing less than a player's height (RoadRockPolicy.PlayerHeight) is not
/// built (RoadStairRuns.ClimbsUnderAPlayer): the road ends where it would have met it, and the Location is reached there.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The ring a mound's Location is kept to here: inside it its body, outside it the road.</summary>
    private const float MoundFootprint = 14f;

    /// <summary>A mound whose top is level at 50 + <paramref name="top"/> out to 16 m from the origin, its side falling
    /// <paramref name="slope"/> to 50: a road arriving on its side from +z ends under the top by the climb its slope gives.</summary>
    private MoundWorld Mound(Heightmap.Biome biome, float slope, float top)
    {
        var world = new MoundWorld { Biome = biome };
        world.Ground = (x, z) => 50f + Mathf.Clamp(top - slope * (new Vector2(x, z).magnitude - 16f), 0f, top);
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        RoadStairRuns.ResetCache();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), MoundFootprint) });
        PlacedLocations.Reset();
        return world;
    }

    /// <summary>A Crypt3 on the mound's top, turned to face +z (its entrance's landing toward the road), placed first: its entrance
    /// and its door fallback, as the game's Crypt3 has them (PlacedLocationShape.DoorLandings: reached only at its door).</summary>
    private PlacedLocation CryptOnAMound(MoundWorld world, float top)
    {
        const string crypt = "Crypt3";
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = crypt, Centre = new Vector2(0f, 0f), Yaw = 180f, Root = 50f + top, RockRadius = 0f,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = world.Ground;
        PlacedLocationShape fallback = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root, crypt);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root,
            crypt, entrance: 0);
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = entrance, Fallback = fallback, Entrances = new[] { entrance } };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return location;
    }

    /// <summary>A road on the mound's ground from 50 m further out along +z to its end <paramref name="end"/> m from the origin,
    /// its waypoints a cell apart, its planned points a metre apart.</summary>
    private static (List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans) DownTheMound(MoundWorld world, float end)
    {
        Vector2 to = new(0f, end), from = new(0f, end + 48f);
        int cells = Mathf.RoundToInt(48f / RoadPathfinder.CellSize);
        var path = Enumerable.Range(0, cells + 1).Select(k => Vector2.Lerp(from, to, k / (float)cells)).ToList();
        var points = Enumerable.Range(0, 49).Select(k => Vector2.Lerp(from, to, k / 48f)).ToList();
        var piece = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(p => world.Ground(p.x, p.y)), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, 48f, false);
        return (path, new List<RoadSpatialGrid.PlannedPath> { piece });
    }

    /// <summary>Where the road ends at the crypt, 18 m out on the mound's side, its entrance's landing 7-8 m in from it.</summary>
    private const float CryptRoadEnd = 18f;

    /// <summary>The way onto the crypt's landing from the road's end at <see cref="CryptRoadEnd"/>, a staircase only (the road's own
    /// way in refused, as at the Crypt3 in game: "switchback: legs too close").</summary>
    private (bool entered, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? span, string why, EntranceArrivals.Arrival? arrival, Vector2 end, float height)
        StairsOntoTheCrypt(MoundWorld world, PlacedLocation crypt, float roadEnd = CryptRoadEnd)
    {
        var (path, plans) = DownTheMound(world, roadEnd);
        // Its end's height as planned, before a staircase's foot is levelled into it (RoadPitches.LandEnd).
        RoadSpatialGrid.PlannedPath last = plans[plans.Count - 1];
        float height = last.Heights[last.Heights.Count - 1];
        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why, out EntranceArrivals.Arrival? arrival,
            only: RoadNetworkGenerator.EntryWays.Stairs);
        return (entered, road, span, why, arrival, path[path.Count - 1], height);
    }

    /// <summary>The Crypt3 in game at (3766.5, -4541.9): its road's own way in refused, a staircase was laid from the road's end
    /// down onto its entrance's landing, its top (3753.6, 36.97, -4539.6) at the road's end and its foot (3755.0, 35.97, -4541.0)
    /// on the landing: one stair, a metre. Climbing less than a player's height, it is not built: the road ends where it would have
    /// met it, on its own end, and the crypt is reached there, at its entrance (its arrival nearby).</summary>
    [Fact]
    public void AStaircaseOntoACryptsLandingClimbingLessThanAPlayerIsNotBuiltAndItsRoadEndsReachingIt()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 0.5f, 3f);
        PlacedLocation crypt = CryptOnAMound(world, 3f);
        var (entered, road, span, why, arrival, end, height) = StairsOntoTheCrypt(world, crypt);

        float climb = crypt.Entrances[0].Ground!(crypt.Entrances[0].Landing.x, crypt.Entrances[0].Landing.y) - height;
        Assert.InRange(climb, StairLayout.StairFitsFrom, RoadRockPolicy.PlayerHeight);
        Assert.True(entered, why);
        Assert.Null(span);
        Assert.Null(road);
        Assert.True(arrival != null && arrival.Target.Entrance == 0, $"arrived at {(arrival == null ? "none" : EntranceArrivals.Name(arrival.Target))}");
        // Its end where it was, on the mound's side, reached at the crypt's entrance as the reached test reads it.
        Assert.Equal(new Vector2(0f, CryptRoadEnd), end);
        Assert.True(crypt.Entrances[0].Arrives(end, height), FormattableString.Invariant($"the road's end at ({end.x:F1}, {height:F2}, {end.y:F1}) does not arrive"));
        Assert.Equal(new Vector2(0f, CryptRoadEnd), RoadNetworkGenerator.EntryEnd(crypt, null, null, new List<Vector2> { new(0f, 66f), end }));
    }

    /// <summary>The same crypt on a mound twice as steep: the staircase from the road's end climbs two stairs, over a player's
    /// height, and is built.</summary>
    [Fact]
    public void AStaircaseOntoACryptsLandingClimbingAPlayersHeightIsBuilt()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 1f, 4f);
        PlacedLocation crypt = CryptOnAMound(world, 4f);
        var (entered, _, span, why, _, end, _) = StairsOntoTheCrypt(world, crypt);

        Assert.True(entered && span != null, why);
        Assert.True(Mathf.Abs(span!.Plan.Rise) >= RoadRockPolicy.PlayerHeight, FormattableString.Invariant($"it climbs {span.Plan.Rise:F2} m"));
        Assert.False(RoadStairRuns.ClimbsUnderAPlayer(span.Plan));
        Assert.Equal(end, span.FromPoint);
    }

    /// <summary>A Dvergr guard tower on the mound, its entrance toward the road, its staircase onto its landing the only way the arrival
    /// ladder tries (<see cref="RoadNetworkGenerator.ArrivalOrder"/>): laid by generation, a road whose staircase onto the landing would
    /// climb one stair is built with none, ending where it would have met it, the tower landed as built and reached; one whose staircase
    /// climbs two is built with it.</summary>
    [Theory]
    [InlineData(0.3f, false)]
    [InlineData(0.6f, true)]
    public void GenerationEndsAtATowerWhoseStaircaseWouldClimbLessThanAPlayer(float slope, bool built)
    {
        PlacedLocation location = TowerOnAMound(slope, out _);
        RoadNetworkGenerator.ArrivalRung[] order = RoadNetworkGenerator.ArrivalOrder;
        RoadNetworkGenerator.ArrivalOrder = new[] { RoadNetworkGenerator.ArrivalRung.WayInStairs };
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower"),
                "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        }
        finally { RoadNetworkGenerator.ArrivalOrder = order; }

        Assert.True(PlacedLocations.Landed(location.Centre));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        Assert.Equal(built ? 1 : 0, StairStructures.Runs.Count);
    }

    /// <summary>A Dvergr guard tower placed first on the mound's top (3 m over its foot, its side falling <paramref name="slope"/>), its
    /// entrance toward +z, as the game's has it.</summary>
    private PlacedLocation TowerOnAMound(float slope, out MoundWorld world)
    {
        const string tower = "Mistlands_GuardTower1_ruined_new";
        world = Mound(Heightmap.Biome.Mistlands, slope, 3f);
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord { Prefab = tower, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = 53f, RockRadius = 0f, Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121 };
        Func<float, float, float> ground = world.Ground;
        PlacedLocationShape fallback = PlacedLocationShape.Of(record.Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root, tower);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root, tower, entrance: 0);
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = entrance, Fallback = fallback, Entrances = new[] { entrance } };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return location;
    }

    /// <summary>The arrival ladder (RoadNetworkGenerator.PlanArrival, its staircase rung the only one) at the tower from a road's end
    /// <see cref="CryptRoadEnd"/> out on the mound's side, a stair under its entrance's landing and within its reach: no road and no
    /// staircase onto the landing, the road ends there, and the rung it arrives on is "nearby".</summary>
    [Fact]
    public void TheArrivalLadderNamesAnEndWhereAStaircaseUnderAPlayerWouldStartNearby()
    {
        PlacedLocation tower = TowerOnAMound(0.5f, out MoundWorld world);
        var (path, plans) = DownTheMound(world, CryptRoadEnd);
        Vector2 end = path[path.Count - 1];
        float height = plans[plans.Count - 1].Heights[plans[plans.Count - 1].Heights.Count - 1];
        var road = new RoadNetworkGenerator.PlannedRoad
        {
            Path = path, Plans = plans, EndCenter = tower.Centre,
            Ends = new RoadNetworkGenerator.LocationEndContext(0f, 10f, false, true, null, tower, RoadNetworkGenerator.RoadWidth, 0f, 0f),
        };
        MethodInfo planArrival = typeof(RoadNetworkGenerator).GetMethod("PlanArrival", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { road, tower, false, null, null, null, null, null, null, null };
        RoadNetworkGenerator.ArrivalRung[] order = RoadNetworkGenerator.ArrivalOrder;
        RoadNetworkGenerator.ArrivalOrder = new[] { RoadNetworkGenerator.ArrivalRung.WayInStairs };
        bool entered;
        try { entered = (bool)planArrival.Invoke(null, args)!; }
        finally { RoadNetworkGenerator.ArrivalOrder = order; }

        float climb = tower.Entrances[0].Ground!(tower.Entrances[0].Landing.x, tower.Entrances[0].Landing.y) - height;
        Assert.InRange(climb, StairLayout.StairFitsFrom, RoadRockPolicy.PlayerHeight);
        Assert.True(tower.Entrances[0].Arrives(end, height));
        Assert.True(entered, (string)args[6]!);
        Assert.Null(args[4]);
        Assert.Null(args[5]);
        Assert.Equal("nearby", (string)args[8]!);
    }

    /// <summary>The crypt on the mound's top, level out to 17 m, its side falling a stair to level ground 19 m out: a road ending
    /// 21 m out on that level ground, a stair under the top, is beyond the reach of its entrance's landing
    /// (RoadNetworkGenerator.LandingReach), and so is every start back along it. Its staircase onto the landing would climb less
    /// than a player's height and is not built, and the crypt is not reached from there: the start is refused with why, and that
    /// refusal is kept for the start (not laid out again).
    /// <para>Two decisions meet here: no staircase climbing less than a player's height onto a Location
    /// (RoadStairRuns.ClimbsUnderAPlayer), and no staircase walking level further than a rest anywhere on it, but a walkway over a
    /// dip (StairLayout.PastARest). With the top level only out to 16 m and the fall to 18 m, the one staircase onto the landing
    /// walked further than a rest on the level and the layout search laid none (its landings "do not meet the raised landing's
    /// lip"), so the climb rule was never asked. The top carried a metre further out, its staircase walks a rest at most and is
    /// laid, and the climb rule refuses it.</para></summary>
    [Fact]
    public void AStaircaseUnderAPlayerOntoACryptFromBeyondItsLandingsReachIsRefusedAndTheCryptNotEntered()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 0.5f, 3f);
        world.Ground = (x, z) => 50f + Mathf.Clamp(3f - 0.5f * (new Vector2(x, z).magnitude - 17f), 2f, 3f);
        PlacedLocation crypt = CryptOnAMound(world, 3f);
        var (entered, road, span, why, _, end, height) = StairsOntoTheCrypt(world, crypt, 21f);

        float climb = crypt.Entrances[0].Ground!(crypt.Entrances[0].Landing.x, crypt.Entrances[0].Landing.y) - height;
        Assert.InRange(climb, StairLayout.StairFitsFrom, RoadRockPolicy.PlayerHeight);
        Assert.False(crypt.Entrances[0].Arrives(end, height));
        Assert.False(entered);
        Assert.Null(span);
        Assert.Null(road);
        string refusal = FormattableString.Invariant($"{RoadStairRuns.UnderAPlayerRefusal}, its road's end at ({end.x:F1}, {end.y:F1}) not on the landing");
        Assert.Contains(refusal, why);

        // The same start again: refused as before, with no staircase laid out again.
        int layouts = RoadNetworkGenerator.EntryStairLayouts;
        var again = StairsOntoTheCrypt(world, crypt, 21f);
        Assert.False(again.entered);
        Assert.Contains(refusal, again.why);
        Assert.Equal(layouts, RoadNetworkGenerator.EntryStairLayouts);
    }

    /// <summary>A road ending 40 m out on level ground, level with the crypt's entrance's landing, a ridge 4 m high between them at
    /// 26 m: the way onto the landing is a staircase up to the crest and one down from it (RoadStairRuns.Join), road end to landing
    /// level, but a player climbs each leg's 4 m. Judged by its legs' climb (StairPlan.LegClimb), not road end to landing, it is
    /// built.</summary>
    [Fact]
    public void AStaircaseOverACrestOntoALandingLevelWithItsRoadsEndIsBuilt()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 0f, 0f);
        world.Ground = (x, z) => 50f + Mathf.Max(0f, 4f - 0.6f * Mathf.Abs(z - 26f));
        PlacedLocation crypt = CryptOnAMound(world, 0f);
        var (entered, _, span, why, _, end, height) = StairsOntoTheCrypt(world, crypt, 40f);

        float landing = crypt.Entrances[0].Ground!(crypt.Entrances[0].Landing.x, crypt.Entrances[0].Landing.y);
        Assert.InRange(landing - height, -0.01f, 0.01f);
        Assert.True(entered && span != null, why);
        Assert.True(Mathf.Abs(span!.Plan.Rise) < RoadRockPolicy.PlayerHeight, FormattableString.Invariant($"it rises {span.Plan.Rise:F2} m road end to landing"));
        Assert.True(span.Plan.LegClimb >= RoadRockPolicy.PlayerHeight, FormattableString.Invariant($"its legs climb {span.Plan.LegClimb:F2} m"));
        Assert.False(RoadStairRuns.ClimbsUnderAPlayer(span.Plan));
        Assert.Equal(end, span.FromPoint);
    }

    /// <summary>Every way a staircase is laid onto a Location gives a staircase climbing one stair the one verdict
    /// (RoadStairRuns.ClimbsUnderAPlayer): onto a placed crypt's landing (RoadNetworkGenerator.PlanEntryFrom) none is laid and the
    /// road ends there; an approach onto a Location not placed first, up to its levelled top (RoadNetworkGenerator.LocationApproaches),
    /// none either. Two stairs, both lay theirs.</summary>
    [Theory]
    [InlineData(0.5f, 3f, 18f, false)]
    [InlineData(1f, 4f, 20f, true)]
    public void EveryWayOntoALocationRefusesAStaircaseClimbingLessThanAPlayer(float cryptSlope, float cryptTop, float approachEnd, bool built)
    {
        // Onto a placed Location's landing.
        MoundWorld world = Mound(Heightmap.Biome.Mountain, cryptSlope, cryptTop);
        PlacedLocation crypt = CryptOnAMound(world, cryptTop);
        var (entered, _, span, why, _, _, _) = StairsOntoTheCrypt(world, crypt);
        Assert.True(entered, why);
        Assert.Equal(built, span != null);

        // An approach onto a Location not placed first, its top levelled 16 m round its centre, in the Mountains, 3 m over the
        // foot of a side falling 0.5: from a road's end 18 m out the approach climbs one stair, from 20 m out two.
        world = Mound(Heightmap.Biome.Mountain, 0.5f, 3f);
        var top16 = new List<LevelOp> { new(0f, 0f, 0f, 16f, false) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? top16 : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        var (path, plans) = DownTheMound(world, approachEnd);
        MethodInfo approaches = typeof(RoadNetworkGenerator).GetMethod("LocationApproaches", BindingFlags.NonPublic | BindingFlags.Static)!;
        var laid = (List<RoadStairSpan>)approaches.Invoke(null, new object[] { path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(),
            path[0], 0f, new Vector2(0f, 0f), 10f, false, true, false })!;
        Assert.Equal(built ? 1 : 0, laid.Count);
        Assert.All(laid, s => Assert.False(RoadStairRuns.ClimbsUnderAPlayer(s.Plan)));
    }

    /// <summary>A road ending on the top of its own staircase, two stairs up the crypt's mound from 22 m out to 18 m, a stair under
    /// the crypt's entrance's landing: the staircase carried on from that top onto the landing climbs one stair, less than a
    /// player's height, and is not built either. The road ends on its own staircase's top, and the crypt is reached there.</summary>
    [Fact]
    public void AStaircaseCarriedOnFromItsRoadsOwnStaircaseOntoALocationClimbingLessThanAPlayerIsNotBuilt()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 0.5f, 3f);
        PlacedLocation crypt = CryptOnAMound(world, 3f);
        var (path, plans) = DownTheMound(world, 22f);
        Vector2 foot = path[path.Count - 1], top = new(0f, CryptRoadEnd);
        path.Add(top);
        float footHeight = world.Ground(foot.x, foot.y), topHeight = world.Ground(top.x, top.y);
        // Its two stairs: each a rise up from its origin, climbing toward its local -z (toward the crypt), a run apart.
        var plan = new StairPlan { Kit = "stone", Reached = true, Rise = topHeight - footHeight, End = new Vector3(top.x, topHeight, top.y) };
        for (int k = 0; k < 2; k++)
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair",
                Position = new Vector3(0f, footHeight + k * StairLayout.PieceRise, foot.y - (k + 0.5f) * StairLayout.PieceRun),
                YawDegrees = StairLayout.Bearing(top, foot) });
        var own = new RoadStairSpan { FromIndex = path.Count - 2, ToIndex = path.Count - 1, FromPoint = foot, FromHeight = footHeight,
            ToPoint = top, ToHeight = topHeight, Kit = StairKit.Stone, Plan = plan };
        var stairs = new List<RoadStairSpan> { own };

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, stairs, new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why, out EntranceArrivals.Arrival? arrival,
            only: RoadNetworkGenerator.EntryWays.Stairs);

        float climb = crypt.Entrances[0].Ground!(crypt.Entrances[0].Landing.x, crypt.Entrances[0].Landing.y) - topHeight;
        Assert.InRange(climb, StairLayout.StairFitsFrom, RoadRockPolicy.PlayerHeight);
        Assert.True(entered, why);
        Assert.True(span == null, span == null ? "" : FormattableString.Invariant($"a staircase climbing {span.Plan.Rise:F2} m from ({span.FromPoint.x:F1}, {span.FromPoint.y:F1})"));
        Assert.Null(road);
        Assert.True(arrival != null && arrival.Target.Entrance == 0, $"arrived at {(arrival == null ? "none" : EntranceArrivals.Name(arrival.Target))}");
        Assert.Equal(top, path[path.Count - 1]);
        Assert.Single(stairs);
    }

    /// <summary>
    /// A manual road ends "nearby" the way generation does (ManualRoads' LocationEnds.Of, RoadNetworkGenerator.EntryEnd): the arrival
    /// ladder lays no road and no staircase onto the tower's landing where its staircase would climb under a player's height
    /// (PlanArrival: "nearby"), and the entry carrying that, with neither, is recorded at the road's own end on its path, as
    /// generation records it (GenerateRoad's EntryEnd of the path), not at the Location's centre.
    /// </summary>
    [Fact]
    public void AManualRoadEndsNearbyAtItsOwnEndAsGenerationDoes()
    {
        PlacedLocation tower = TowerOnAMound(0.5f, out MoundWorld world);
        var (path, plans) = DownTheMound(world, CryptRoadEnd);
        var road = new RoadNetworkGenerator.PlannedRoad
        {
            Path = path, Plans = plans, EndCenter = tower.Centre,
            Ends = new RoadNetworkGenerator.LocationEndContext(0f, 10f, false, true, null, tower, RoadNetworkGenerator.RoadWidth, 0f, 0f),
        };
        MethodInfo planArrival = typeof(RoadNetworkGenerator).GetMethod("PlanArrival", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { road, tower, false, null, null, null, null, null, null, null };
        RoadNetworkGenerator.ArrivalRung[] order = RoadNetworkGenerator.ArrivalOrder;
        RoadNetworkGenerator.ArrivalOrder = new[] { RoadNetworkGenerator.ArrivalRung.WayInStairs };
        try { Assert.True((bool)planArrival.Invoke(null, args)!, (string)args[6]!); }
        finally { RoadNetworkGenerator.ArrivalOrder = order; }
        Assert.Equal("nearby", (string)args[8]!);
        Assert.Null(args[4]);
        Assert.Null(args[5]);
        road.Entries.Add((tower, null, null, (EntranceArrivals.Arrival?)args[7]));

        // What generation records for it (GenerateRoad's entry with neither road nor staircase).
        Vector2 generated = RoadNetworkGenerator.EntryEnd(tower, null, null, road.Path);
        Assert.Equal(path[path.Count - 1], generated);
        Assert.NotEqual(tower.Centre, generated);

        var (location, _, manual) = Assert.Single(RoadNetworkGenerator.LocationEnds.Of(road).Arrivals);
        Assert.Same(tower, location);
        Assert.Equal(generated, manual);

        RoadNetworkGenerator.ManualAppend append = RoadNetworkGenerator.AppendOf(new List<RoadSpatialGrid.PlannedPath>(), new List<RoadCrossing>(),
            new List<RoadStairSpan>(), RoadNetworkGenerator.LocationEnds.Of(road));
        Assert.Equal(generated, Assert.Single(append.Arrivals).end);
    }
}
