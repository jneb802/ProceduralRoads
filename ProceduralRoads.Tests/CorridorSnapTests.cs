using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road running alongside another in the same corridor, 8-14 m off (beyond the 6 m snap), merges
/// onto it (measured: ~400 m of such pairs on a generated world with every other rule on).
/// </summary>
[Collection("RoadStatics")]
public class CorridorSnapTests
{
    private sealed class Terrace : WorldGenerator
    {
        public float StepZ = float.PositiveInfinity, High = 39f;
        public override float GetHeight(float x, float z) => z > StepZ ? High : 31f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static List<Vector2> Line(float z, float x0 = -100f, float x1 = 100f) =>
        Enumerable.Range(0, (int)((x1 - x0) / 8f) + 1).Select(i => new Vector2(x0 + i * 8f, z)).ToList();

    private static void With(WorldGenerator world, System.Action body)
    {
        using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            Assert.True(RoadSpatialGrid.AddRoadPath(Line(0f, -200f, 200f), 4f, world));
            RoadNetworkGenerator.RoadSnap = (float)PlainRoads.ModDefaults["road snap"];
            RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
            body();
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void ARoadTenMetresBesideAnotherMergesOntoIt()
    {
        With(new Terrace(), () =>
        {
            var snapped = RoadNetworkGenerator.SnapToNetwork(Line(10f))!;
            int onRoad = snapped.Skip(1).Take(snapped.Count - 2).Count(p => Mathf.Abs(p.y) < 1f);
            Assert.True(onRoad >= snapped.Count - 4, $"{onRoad} of {snapped.Count - 2} interior waypoints on the road");
        });
    }

    [Fact]
    public void ACrossingRoadIsNotDraggedAlongTheOther()
    {
        With(new Terrace(), () =>
        {
            var crossing = Enumerable.Range(0, 26).Select(i => new Vector2(3f, -100f + i * 8f)).ToList();
            var snapped = RoadNetworkGenerator.SnapToNetwork(crossing)!;
            foreach (var p in snapped)
                if (Mathf.Abs(p.y) > 6.5f) Assert.Equal(3f, p.x, 3);
        });
    }

    [Fact]
    public void ARoadBesideAnotherButAtADifferentHeightStaysApart()
    {
        // A terrace 8 m up from 6 m out: a switchback leg or a road on the next bench is not the same corridor.
        With(new Terrace { StepZ = 6f }, () =>
        {
            var snapped = RoadNetworkGenerator.SnapToNetwork(Line(10f))!;
            Assert.All(snapped, p => Assert.Equal(10f, p.y, 3));
        });
    }

    [Fact]
    public void ABriefBrushPastIsNotAMerge()
    {
        With(new Terrace(), () =>
        {
            // Diagonal: within 14 m of the road for one waypoint only.
            var diag = Enumerable.Range(0, 12).Select(i => new Vector2(-40f + i * 8f, -60f + i * 11f)).ToList();
            var snapped = RoadNetworkGenerator.SnapToNetwork(diag)!;
            foreach (var p in snapped)
                if (Mathf.Abs(p.y) > 6.5f) Assert.Contains(diag, q => Vector2.Distance(q, p) < 0.01f);
        });
    }
    [Fact]
    public void AHeldWaypointBesideAnotherRoadStaysPut()
    {
        // A structure's end the road was brought to (its Meet, 4 m off another road) is not snapped onto that road,
        // neither by the road snap nor by the corridor snap round it, and is kept in the path.
        With(new Terrace(), () =>
        {
            var line = Line(10f);
            var meet = new Vector2(4f, 4f);
            line.Insert(line.FindIndex(p => p.x > 4f), meet);
            var snapped = RoadNetworkGenerator.SnapToNetwork(line, held: p => (p - meet).sqrMagnitude < 0.25f)!;
            Assert.Contains(snapped, p => Vector2.Distance(p, meet) < 0.01f);
            Assert.True(snapped.Count(p => Mathf.Abs(p.y) < 1f) >= snapped.Count - 5, "the rest still merges");
        });
    }

    /// <summary>A staircase's flight is a line of the network a road may be snapped onto only where its search may take it
    /// (RoadPathfinder.ClosedByAStaircase): a road's own staircase, which a later road walks, but never a flight standing
    /// there that no road climbed (a location's approach), whose cells the search keeps every road out of. Snapped onto
    /// one, the road ran up the approach's flight and was refused under it, where its search had come in beside it.</summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ARoadIsSnappedOntoAStaircaseOnlyWhereItsSearchMayWalkIt(bool standing, bool climbed, bool snapped)
    {
        With(new Terrace(), () =>
        {
            // A flight at ground level along z 40, well away from the road along z 0; the road's waypoints 2 m beside it
            // (within the road snap) and every 8 m along it.
            var run = new StairRun { From = new Vector2(-79f, 40f), To = new Vector2(79f, 40f), Width = 2f };
            for (float x = -79f; x <= 79f; x += 2f)
                run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = new Vector3(x, 31f - StairLayout.StepMiddle, 40f) });
            StairStructures.Reset();
            try
            {
                StairStructures.Add(run);
                if (standing) StairStructures.MarkStanding(run);
                if (climbed) StairStructures.MarkLaid(run.Pieces.Select(p => new Vector2(p.Position.x, p.Position.z)));
                var line = Line(42f, -60f, 60f);
                var result = RoadNetworkGenerator.SnapToNetwork(line)!;
                int onFlight = result.Skip(1).Take(result.Count - 2).Count(p => Mathf.Abs(p.y - 40f) < 0.5f);
                if (snapped) Assert.True(onFlight >= result.Count - 4, $"{onFlight} of {result.Count - 2} interior waypoints on the flight");
                else Assert.All(result, p => Assert.Equal(42f, p.y, 3));
            }
            finally { StairStructures.Reset(); }
        });
    }

    /// <summary>A viaduct's deck is a line of the network a road may be snapped onto only where its search may take it
    /// (RoadPathfinder.ClosedToARoad): the cells under a standing viaduct's deck (DvergrViaduct.Standing) are closed to every
    /// road, which uses a viaduct only by its own jump, bank to bank. A deck whose cells are not closed (a bridge's line, say)
    /// still takes the waypoints beside it.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ARoadIsSnappedOntoAViaductsDeckOnlyWhereItsSearchMayGo(bool standing, bool snapped)
    {
        With(new Terrace(), () =>
        {
            // A deck at the ground's height along z 40, well away from the road along z 0; the road's waypoints 2 m beside it
            // (within the road snap) and every 8 m along it.
            var deck = new RoadCrossing
            {
                FromBank = new Vector2(-79f, 40f), ToBank = new Vector2(79f, 40f), Center = new Vector2(0f, 40f),
                Direction = new Vector2(1f, 0f), Width = 158f, Kind = CrossingKind.Bridge, ValleyJump = true, WaterLevel = 31f,
            };
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var gate = typeof(RoadNetworkGenerator).GetField("m_recordGate", flags)!.GetValue(null)!;
            var crossings = (List<RoadCrossing>)typeof(RoadNetworkGenerator).GetField("m_roadCrossings", flags)!.GetValue(null)!;
            lock (gate) crossings.Add(deck);
            DvergrViaduct.ResetStanding();
            try
            {
                if (standing) DvergrViaduct.MarkStanding(deck);
                var result = RoadNetworkGenerator.SnapToNetwork(Line(42f, -60f, 60f))!;
                int onDeck = result.Skip(1).Take(result.Count - 2).Count(p => Mathf.Abs(p.y - 40f) < 0.5f);
                if (snapped) Assert.True(onDeck >= result.Count - 4, $"{onDeck} of {result.Count - 2} interior waypoints on the deck");
                else Assert.All(result, p => Assert.Equal(42f, p.y, 3));
            }
            finally
            {
                lock (gate) crossings.Remove(deck);
                DvergrViaduct.ResetStanding();
            }
        });
    }

    [Fact]
    public void APlannedRoadKeepsAHeldMeetWithinTheSnapOfAnotherRoad()
    {
        With(new Terrace(), () =>
        {
            var meet = new Vector2(0f, 4f);
            var path = new List<Vector2> { new Vector2(0f, 60f), new Vector2(0f, 30f), new Vector2(0f, 12f), meet, new Vector2(40f, 4f), new Vector2(80f, 40f) };
            RoadSpatialGrid.HeldPoints = new List<Vector2> { meet };
            try
            {
                var plan = RoadSpatialGrid.PlanRoadPath(path, 4f, new Terrace());
                Assert.NotNull(plan);
                float nearest = plan!.Points.Min(p => Vector2.Distance(p, meet));
                Assert.True(nearest < 0.5f, $"the held Meet is {nearest:F2} m off the planned road");
            }
            finally { RoadSpatialGrid.HeldPoints = null; }
        });
    }

    [Fact]
    public void TwoCrossingsOfTheSameWaterSideBySideAreOneSite()
    {
        // Measured: two roads left one place through a pool, each with its own ford 6.5 m apart.
        using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.CorridorSnap).AndKeep(() => RoadNetworkGenerator.RoadSnap);
        using var scope = new ValheimWorldScope().WithWorld(new Terrace());
        RoadNetworkGenerator.RoadSnap = (float)PlainRoads.ModDefaults["road snap"]; RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
        var a = RoadCrossing.Between(new Vector2(0f, 0f), new Vector2(0f, 20f), 29f, new Vector2(0f, 10f), 0f);
        var beside = RoadCrossing.Between(new Vector2(6.5f, 20f), new Vector2(6.5f, 0f), 29f, new Vector2(6.5f, 10f), 0f);
        Assert.True(RoadNetworkGenerator.SameCorridorCrossing(a, beside, out float d), "6.5 m apart, opposite order");
        Assert.InRange(d, 12.9f, 13.1f);
        var across = RoadCrossing.Between(new Vector2(-10f, 10f), new Vector2(10f, 10f), 29f, new Vector2(0f, 10f), 0f);
        Assert.False(RoadNetworkGenerator.SameCorridorCrossing(a, across, out _), "a crossing at right angles");
        var far = RoadCrossing.Between(new Vector2(20f, 0f), new Vector2(20f, 20f), 29f, new Vector2(20f, 10f), 0f);
        Assert.False(RoadNetworkGenerator.SameCorridorCrossing(a, far, out _), "20 m off");
    }
    [Fact]
    public void AChainOfCrossingsBesideABridgeBecomesThatBridge()
    {
        // Measured: a raised pool, a wade and a 38 m bridge ran 6 m beside a 53 m bridge; piece
        // by piece no bank matched, end to end both did.
        using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.CorridorSnap).AndKeep(() => RoadNetworkGenerator.RoadSnap);
        var registry = (List<RoadCrossing>)RoadNetworkGenerator.GetRoadCrossings();
        using var scope = new ValheimWorldScope().WithWorld(new Terrace());
        try
        {
            RoadNetworkGenerator.RoadSnap = (float)PlainRoads.ModDefaults["road snap"]; RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
            var bridge = RoadCrossing.Between(new Vector2(0f, 0f), new Vector2(0f, 50f), 28f, new Vector2(0f, 25f), 20f, CrossingKind.Bridge);
            registry.Add(bridge);
            var chain = new List<RoadCrossing>
            {
                RoadCrossing.Between(new Vector2(-5f, -8f), new Vector2(-5f, 2f), 29.4f, new Vector2(-5f, -3f), 0f, CrossingKind.Ford, FordStyle.Raise),
                RoadCrossing.Between(new Vector2(-5f, 2f), new Vector2(-4.5f, 12f), 29.6f, new Vector2(-4.7f, 7f), 0f, CrossingKind.Ford, FordStyle.Wade),
                RoadCrossing.Between(new Vector2(-4.5f, 12f), new Vector2(-3f, 49f), 29f, new Vector2(-4f, 30f), 10f, CrossingKind.Bridge),
            };
            chain[0].FromIndex = 3; chain[0].ToIndex = 4; chain[1].FromIndex = 4; chain[1].ToIndex = 5; chain[2].FromIndex = 5; chain[2].ToIndex = 9;
            RoadNetworkGenerator.SnapToExistingCrossings(chain);
            var c = Assert.Single(chain);
            Assert.Equal(CrossingKind.Bridge, c.Kind);
            Assert.True(c.Shared);
            Assert.Equal(new Vector2(0f, 0f), c.FromBank);
            Assert.Equal(new Vector2(0f, 50f), c.ToBank);
            Assert.Equal(3, c.FromIndex); Assert.Equal(9, c.ToIndex);
        }
        finally { registry.Clear(); }
    }
}
