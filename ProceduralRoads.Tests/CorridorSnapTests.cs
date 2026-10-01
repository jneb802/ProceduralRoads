using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static List<Vector2> Line(float z, float x0 = -100f, float x1 = 100f) =>
        Enumerable.Range(0, (int)((x1 - x0) / 8f) + 1).Select(i => new Vector2(x0 + i * 8f, z)).ToList();

    private static void With(WorldGenerator world, System.Action body)
    {
        float snap = RoadNetworkGenerator.RoadSnap, corridor = RoadNetworkGenerator.CorridorSnap;
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            Assert.True(RoadSpatialGrid.AddRoadPath(Line(0f, -200f, 200f), 4f, world));
            RoadNetworkGenerator.RoadSnap = 6f;
            RoadNetworkGenerator.CorridorSnap = 14f;
            body();
        }
        finally
        {
            RoadNetworkGenerator.RoadSnap = snap; RoadNetworkGenerator.CorridorSnap = corridor;
            RoadSpatialGrid.Clear(); WorldGenerator.instance = null;
        }
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
    public void TwoCrossingsOfTheSameWaterSideBySideAreOneSite()
    {
        // Measured: two roads left one place through a pool, each with its own ford 6.5 m apart.
        float corridor = RoadNetworkGenerator.CorridorSnap, snap = RoadNetworkGenerator.RoadSnap;
        WorldGenerator.instance = new Terrace();
        try
        {
            RoadNetworkGenerator.RoadSnap = 6f; RoadNetworkGenerator.CorridorSnap = 14f;
            var a = RoadCrossing.Between(new Vector2(0f, 0f), new Vector2(0f, 20f), 29f, new Vector2(0f, 10f), 0f);
            var beside = RoadCrossing.Between(new Vector2(6.5f, 20f), new Vector2(6.5f, 0f), 29f, new Vector2(6.5f, 10f), 0f);
            Assert.True(RoadNetworkGenerator.SameCorridorCrossing(a, beside, out float d), "6.5 m apart, opposite order");
            Assert.InRange(d, 12.9f, 13.1f);
            var across = RoadCrossing.Between(new Vector2(-10f, 10f), new Vector2(10f, 10f), 29f, new Vector2(0f, 10f), 0f);
            Assert.False(RoadNetworkGenerator.SameCorridorCrossing(a, across, out _), "a crossing at right angles");
            var far = RoadCrossing.Between(new Vector2(20f, 0f), new Vector2(20f, 20f), 29f, new Vector2(20f, 10f), 0f);
            Assert.False(RoadNetworkGenerator.SameCorridorCrossing(a, far, out _), "20 m off");
        }
        finally { RoadNetworkGenerator.RoadSnap = snap; RoadNetworkGenerator.CorridorSnap = corridor; WorldGenerator.instance = null; }
    }
    [Fact]
    public void AChainOfCrossingsBesideABridgeBecomesThatBridge()
    {
        // Measured: a raised pool, a wade and a 38 m bridge ran 6 m beside a 53 m bridge; piece
        // by piece no bank matched, end to end both did.
        float corridor = RoadNetworkGenerator.CorridorSnap, snap = RoadNetworkGenerator.RoadSnap;
        var registry = (List<RoadCrossing>)RoadNetworkGenerator.GetRoadCrossings();
        WorldGenerator.instance = new Terrace();
        try
        {
            RoadNetworkGenerator.RoadSnap = 6f; RoadNetworkGenerator.CorridorSnap = 14f;
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
        finally { registry.Clear(); RoadNetworkGenerator.RoadSnap = snap; RoadNetworkGenerator.CorridorSnap = corridor; WorldGenerator.instance = null; }
    }
}
