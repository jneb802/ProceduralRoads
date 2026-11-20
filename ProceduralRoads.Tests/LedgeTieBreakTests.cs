using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// The foot's ledge price only chooses among a staircase's starts: a stretch none of
/// whose layouts priced so is usable is laid once more without the price, its layouts cached apart, before the road gives
/// it up, and only where a layout of it paid the price at a foot; the
/// layout rules the caller had are back afterwards.
/// </summary>
public class LedgeTieBreakTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithZoneSystem();

    /// <summary>A face of 1.2 from x = 0 to 20; with a ledge, the ground south of the road's line (z under -0.6) 3 m lower
    /// west of x = 4, so a foot stair at the face's foot stands out past the bench's edge.</summary>
    private sealed class FaceWorld : WorldGenerator
    {
        public bool Ledge;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float h = 40f + Mathf.Clamp(wx * 1.2f, 0f, 24f);
            return Ledge && wx < 4f && wy < -0.6f ? h - 3f : h;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    public LedgeTieBreakTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void AStretchNoPricedLayoutLaysIsLaidAgainUnpricedOnlyWhereAFootPaidTheLedgePrice(bool ledge, int unpriced)
    {
        var world = new FaceWorld { Ledge = ledge };
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (float x = -16f; x <= 40.01f; x += 8f) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2 from, Vector2 to)> { (path[2], path[3]), (path[3], path[4]), (path[4], path[5]) };
        // Another road across the face, north to south: every layout of the stretch stands on it.
        var across = new List<Vector2>();
        for (float z = -80f; z <= 80.01f; z += 2f) across.Add(new Vector2(10f, z));
        Assert.True(RoadSpatialGrid.AddRoadPath(across, 4f, world));
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.True(RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(new Vector2(10f, 0f), 1f, out _), "the road across is not in the grid");
        int pricesBefore = StairLayout.LedgePrices;

        Assert.Null(RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world));

        Assert.Equal(ledge, StairLayout.LedgePrices > pricesBefore);
        Assert.Equal(unpriced, RoadStairRuns.LedgeUnpriced);
        Assert.False(StairLayout.UnpricedLedge);
    }
}
