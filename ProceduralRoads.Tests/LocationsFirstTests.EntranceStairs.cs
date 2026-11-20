using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Onto a placed location's measured entrance (<see cref="PlacedLocationShape.DoorLandings"/>) an approach staircase climbs from
/// the road's end at its ring outside the Mountains too (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>), laid and judged
/// as every approach staircase; onto its fallback none is, as before.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A Dvergr guard tower (its entrance 13.5 m out of its front, facing out) on a plateau 10 m over the level ground
    /// out to 16 m from its centre, its edge falling 2 in 1 to the ground 21 m out, its front toward the road's start: its
    /// entrance landing on the plateau's top, its ring (20 m) at the plateau's foot. <paramref name="withFallback"/>: with its
    /// entrance, its fallback landing too, as a placed tower has.</summary>
    private PlacedLocation TowerOnAPlateau(bool withEntrance, bool withFallback = false)
    {
        const string tower = "Mistlands_GuardTower1_ruined_new";
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Reset();
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = tower, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = 60f, RockRadius = 0f,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> plateau = (x, z) => _world.GetHeight(x, z) + Mathf.Clamp(10f - 2f * (new Vector2(x, z).magnitude - 16f), 0f, 10f);
        PlacedLocationShape fallback = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, plateau, false, record.Root, tower);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, plateau, false, record.Root,
            tower, entrance: 0);
        var location = new PlacedLocation
        {
            Record = record, Ground = plateau, Shape = withEntrance ? entrance : fallback, Fallback = withEntrance && !withFallback ? null : fallback,
            Entrances = withEntrance ? new[] { entrance } : Array.Empty<PlacedLocationShape>(),
        };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return location;
    }

    /// <summary>Outside the Mountains, a road whose end at a location's ring stands under its measured entrance too steeply for a
    /// road onto it (as between Dvergr entrances and their rings, 3-35 m over 9-36 m, past the grade cap): an approach staircase
    /// climbs from it onto the entrance's landing, in the biome's own kit, its top level with the landing, and the location is
    /// landed. Onto the same landing as its fallback (not a measured entrance) no staircase is laid: the road climbs onto it as its own
    /// last stretch, its fill judged by the ridge judge as any road's.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Plains)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void AMeasuredEntranceOutsideTheMountainsIsClimbedOnAnApproachStaircase(Heightmap.Biome biome)
    {
        _world.Biome = biome;
        PlacedLocation tower = TowerOnAPlateau(withEntrance: true);
        Vector2 landing = tower.Entrances[0].Landing;

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));

        Assert.True(PlacedLocations.Landed(tower.Centre));
        StairRun run = Assert.Single(StairStructures.Runs);
        Assert.Same(biome == Heightmap.Biome.Mistlands ? StairKit.BlackMarble : StairKit.Stone, run.Kit);
        float top = run.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).Max(p => p.Position.y);
        float level = tower.Ground(landing.x, landing.y);
        Assert.InRange(top, level - StairLayout.ReachHeight - StairLayout.PieceRise, level + StairLayout.ReachHeight);
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);

        tower = TowerOnAPlateau(withEntrance: false);
        RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower");
        // Onto its own landing the road itself climbs where it can (every start's road before any staircase,
        // RoadNetworkGenerator.EntryRoadsFirst), as its own last stretch (RoadNetworkGenerator.PlanWayIn).
        Assert.True(PlacedLocations.Landed(tower.Centre));
    }

    /// <summary>The one owner says so: onto every arrival target of a placed Location (an entrance, its own landing, its fallback) in
    /// every biome; with no target asked, outside the Mountains none.</summary>
    [Fact]
    public void ApproachStairsAreAllowedOntoEveryArrivalTargetInEveryBiome()
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        PlacedLocation with = TowerOnAPlateau(withEntrance: true);
        Assert.True(RoadNetworkGenerator.ApproachStairsAt(with.Centre, with.Record.Prefab, onto: with.Entrances[0]));
        PlacedLocation without = TowerOnAPlateau(withEntrance: false);
        Assert.True(RoadNetworkGenerator.ApproachStairsAt(without.Centre, without.Record.Prefab, onto: without.Shape));
        Assert.False(RoadNetworkGenerator.ApproachStairsAt(without.Centre, without.Record.Prefab));
    }

    /// <summary>A Location whose rule says no staircase serves it (<see cref="LocationRules.Staircase"/>, an exception keyed by its
    /// prefab): the same road to the same entrance that the test above climbs on an approach staircase gets none, in every biome,
    /// the Mountains included, on every rung (the entrance and the fallback); a road may still land on the fallback, but no staircase is built, and the refusal names
    /// the rule. The same tower without the exception still climbs on its staircase.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Plains)]
    [InlineData(Heightmap.Biome.Mountain)]
    public void ALocationWhoseRuleSaysNoStaircaseGetsNoneOnAnyRung(Heightmap.Biome biome)
    {
        _world.Biome = biome;
        PlacedLocation tower = TowerOnAPlateau(withEntrance: true, withFallback: true);
        LocationRules.TestExceptions = new Dictionary<string, LocationRules.Rules> { [tower.Record.Prefab] = new(staircase: false) };
        try
        {
            RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower");
            Assert.Empty(StairStructures.Runs);
            Assert.Contains($"no staircase to {tower.Record.Prefab} (rule)", EntranceArrivals.FailedAt(tower.Centre) ?? "");
        }
        finally { LocationRules.TestExceptions = null; }

        tower = TowerOnAPlateau(withEntrance: true, withFallback: true);
        RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower");
        Assert.NotEmpty(StairStructures.Runs);
    }

    /// <summary>Ground rising 28.8 m, at 1.2, west of x = -26 (a scarp 26 m from the cave, too steep for road even along it), so any road
    /// from the west climbs down a staircase whose ends are within <see cref="RoadStairRuns.LocationReach"/> of the cave.</summary>
    private sealed class ScarpWestOfACave : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 50f + Mathf.Clamp((-26f - wx) * 1.2f, 0f, 28.8f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A troll cave (<see cref="CaveAt"/>) below the scarp (<see cref="ScarpWestOfACave"/>) as placed first, the ground and shape as a placed
    /// Location has them.</summary>
    /// <summary>Where the cave stands: 36 m south, so the flights the road buys down the scarp end within its reach.</summary>
    private static readonly Vector2 CaveAt = new(0f, -36f);

    private void CaveBelowAScarp(string prefab)
    {
        var world = new ScarpWestOfACave();
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedLocations.Reset();
        var record = new PlacedLocationRecord
        {
            Prefab = prefab, Centre = CaveAt, Yaw = 90f, Root = 50f, RockRadius = 0f,
            Ops = new List<LevelOp>(CaveFloor), X0 = (int)CaveAt.x - 60, Z0 = (int)CaveAt.y - 60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        PlacedLocations.Remember(record);
        PlacedLocations.Ready(new PlacedLocation { Record = record, Ground = ground, Shape = PlacedLocationShape.Of(record.Centre, record.Yaw, CaveFloor[0], 0f, null, ground, false, record.Root) });
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(record.Centre, Footprint) });
        RoadStairRuns.ForgetGround();
        LocationLevelling.Source = c => (c - CaveAt).magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
    }

    /// <summary>A road that ends at a troll cave or a bear cave, whose searched staircase down the scarp ends within the reach of the
    /// cave, gets no staircase: RoadRefusal refuses it as no staircase to the cave (the rule), through GenerateRoad. The same road to a
    /// Location the rule does not name keeps its staircase (no reading of the prefab but the rule's).</summary>
    [Theory]
    [InlineData("TrollCave02")]
    [InlineData("BearCave")]
    public void ARoadEndingAtACaveGetsNoStaircaseOfItsOwnWithinReachOfIt(string prefab)
    {
        try
        {
            CaveBelowAScarp("MountainCave02");
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, CaveAt, 10f, 4f, "to the cave"),
                "control, last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
            Assert.NotEmpty(StairStructures.Runs);

            CaveBelowAScarp(prefab);
            // Every road the planner judges, as it judged it (RoadRefusal's first call), judged again by the one list afterwards.
            var judged = new List<RoadNetworkGenerator.PlannedRoad>();
            RoadNetworkGenerator.RefusedFirst = road => { judged.Add(road); return null; };
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, CaveAt, 10f, 4f, "to the cave");
            RoadNetworkGenerator.RefusedFirst = null;
            Assert.Empty(StairStructures.Runs);
            Assert.False(built, "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
            Assert.Contains($"no staircase to {prefab} (rule)", judged.ToList().Select(r => RoadNetworkGenerator.RoadRefusal(r, out _)?.Reason));
        }
        finally { RoadNetworkGenerator.RefusedFirst = null; PlacedLocations.Reset(); }
    }

    /// <summary>A through road to another destination, down the same scarp past the cave, keeps its staircase whose ends are within
    /// the cave's reach: the rule bans a road's own staircase only on a road whose end is that cave.</summary>
    [Fact]
    public void AThroughRoadToAnotherDestinationMayClimbPastACave()
    {
        try
        {
            CaveBelowAScarp("TrollCave02");
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(26f, -60f), 10f, 4f, "past the cave"),
                "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
            StairRun run = Assert.Single(StairStructures.Runs);
            Assert.True(Math.Min((run.From - CaveAt).magnitude, (run.To - CaveAt).magnitude) <= RoadStairRuns.LocationReach, run.ToString());
        }
        finally { PlacedLocations.Reset(); }
    }

    /// <summary>Every staircase path asks the one owner (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>, which reads
    /// <see cref="LocationRules.For"/>), and for the troll cave and the bear cave it says no on every one: onto each arrival target
    /// of a placed one (its entrance, its fallback; the ladder's way-in and fallback staircases), from a road's end at any height,
    /// and with no target (the staircase from a road's end at a Location not placed first, LocationApproaches), in every biome; a
    /// tower beside them keeps its staircases.</summary>
    [Theory]
    [InlineData("TrollCave02")]
    [InlineData("BearCave")]
    public void NoStaircasePathServesATrollCaveOrABearCave(string cave)
    {
        foreach (Heightmap.Biome biome in new[] { Heightmap.Biome.BlackForest, Heightmap.Biome.Mountain, Heightmap.Biome.Meadows })
        {
            _world.Biome = biome;
            PlacedLocation with = TowerOnAPlateau(withEntrance: true, withFallback: true);
            Assert.True(RoadNetworkGenerator.ApproachStairsAt(with.Centre, with.Record.Prefab, 40f, with.Entrances[0]));
            Assert.True(RoadNetworkGenerator.ApproachStairsAt(with.Centre, with.Record.Prefab, 40f, with.Fallback));
            foreach (PlacedLocationShape? onto in new[] { with.Entrances[0], with.Fallback, null })
                foreach (float? from in new float?[] { null, 40f, 70f })
                    Assert.False(RoadNetworkGenerator.ApproachStairsAt(with.Centre, cave, from, onto), $"{cave} in {biome} from {from}");
            Assert.Equal(biome == Heightmap.Biome.Mountain, RoadNetworkGenerator.ApproachStairsAt(with.Centre, "StoneTower1"));
        }
    }
}
