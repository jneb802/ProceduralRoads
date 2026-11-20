using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The public read of the laid roads (RoadSpatialGrid.ReadLaidRoads), over a small laid network: each road's label and its
/// points in laid and travel order, each point's y the surface height the network stores for it now (not the ground under it,
/// a raise included), and the same read after the record is saved and loaded back.
/// </summary>
[Collection("RoadStatics")]
public sealed class LaidRoadsReadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private const float Ground = 50f;

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Ground;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 79;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    public LaidRoadsReadTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
    }

    public void Dispose()
    {
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        _scope.Dispose();
    }

    /// <summary>A piece of <paramref name="count"/> points 5 m apart from <paramref name="from"/> along <paramref name="dir"/>, the
    /// i-th stored at <paramref name="h0"/> + i (a slope: the stored surface, not the flat ground).</summary>
    private static RoadSpatialGrid.PlannedPath Piece(Vector2 from, Vector2 dir, int count, float h0)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int i = 0; i < count; i++) { points.Add(from + dir * (5f * i)); heights.Add(h0 + i); }
        return new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 5f * (count - 1), false);
    }

    private static void Lay(string label, params RoadSpatialGrid.PlannedPath[] pieces)
    {
        RoadSpatialGrid.CommitRoad(pieces.ToList(), ownChecked: true);
        RoadSpatialGrid.RecordLaid(pieces, label);
    }

    private static readonly Vector2 East = new(1f, 0f), North = new(0f, 1f);

    [Fact]
    public void EachLaidRoadIsReadInOrderWithItsStoredHeights()
    {
        Lay("A", Piece(new Vector2(100f, 100f), East, 6, 60f), Piece(new Vector2(130f, 100f), East, 4, 70f));
        Lay("B", Piece(new Vector2(400f, 100f), North, 5, 80f));
        var roads = RoadSpatialGrid.ReadLaidRoads();
        Assert.Equal(new[] { 0, 1 }, roads.Select(r => r.Road));
        Assert.Equal(new[] { "A", "B" }, roads.Select(r => r.Label));
        var a = roads[0];
        Assert.Equal(new[] { 6, 4 }, a.Pieces.Select(p => p.Length));
        Assert.Equal(new[] { 100f, 105f, 110f, 115f, 120f, 125f, 130f, 135f, 140f, 145f }, a.Pieces.SelectMany(p => p).Select(p => p.x));
        Assert.All(a.Pieces.SelectMany(p => p), p => Assert.Equal(100f, p.z));
        // y is the stored surface, not the ground under it.
        Assert.Equal(new[] { 60f, 61f, 62f, 63f, 64f, 65f, 70f, 71f, 72f, 73f }, a.Pieces.SelectMany(p => p).Select(p => p.y));
        Assert.Equal(new[] { 80f, 81f, 82f, 83f, 84f }, roads[1].Pieces.Single().Select(p => p.y));
        // As it stands now: a raise moves it, and the read sees it.
        var at = RoadSpatialGrid.StoredAt(new[] { new Vector2(110f, 100f) })![0];
        RoadSpatialGrid.SetRoadHeights(new List<(RoadSpatialGrid.RoadPoint at, float to)> { (at, 66.5f) });
        Assert.Equal(66.5f, RoadSpatialGrid.ReadLaidRoads()[0].Pieces[0][2].y);
    }

    [Fact]
    public void TheReadIsTheSameAfterTheRecordIsSavedAndLoaded()
    {
        Lay("A", Piece(new Vector2(100f, 100f), East, 6, 60f));
        Lay("B", Piece(new Vector2(400f, 100f), North, 5, 80f));
        var before = RoadSpatialGrid.ReadLaidRoads();
        RoadSpatialGrid.LoadLaid(RoadSpatialGrid.SerializeLaid());
        var after = RoadSpatialGrid.ReadLaidRoads();
        Assert.Equal(before.Select(r => (r.Road, r.Label)), after.Select(r => (r.Road, r.Label)));
        Assert.Equal(before.Select(r => r.Pieces.SelectMany(p => p).ToArray()), after.Select(r => r.Pieces.SelectMany(p => p).ToArray()));
        // No laid record (a client's: it is sent the points, not the roads): nothing to read.
        RoadSpatialGrid.ClearLaid();
        Assert.Empty(RoadSpatialGrid.ReadLaidRoads());
    }
}
