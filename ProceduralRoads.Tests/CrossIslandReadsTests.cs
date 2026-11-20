using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Islands build as separate networks: an island's worker does not see what another island's worker committed, whichever
/// finished first, and sees untagged work (laid before the islands, or loaded) and its own. Off an island worker everything
/// is seen.
/// </summary>
[Collection("RoadStatics")]
public sealed class CrossIslandReadsTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 30f;
        public override int GetSeed() => 77;
    }

    public CrossIslandReadsTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadSpatialGrid.Clear(); StairStructures.Reset(); CrossIslandReads.Reset();
        CrossIslandReads.Current = 0;
    }

    public void Dispose()
    {
        CrossIslandReads.Current = 0; CrossIslandReads.Reset();
        RoadSpatialGrid.Clear(); StairStructures.Reset();
        _scope.Dispose();
    }

    private static void CommitAs(int island, Vector2 at)
    {
        CrossIslandReads.Current = island;
        try { RoadSpatialGrid.CommitLevelled(new[] { at }, new[] { 30f }, 4f); }
        finally { CrossIslandReads.Current = 0; }
    }

    [Fact]
    public void AnotherIslandsRoadPointIsNotSeenButAnUntaggedOneIs()
    {
        CommitAs(1, new Vector2(0f, 0f));       // island A's road, 10 m from where island B asks
        CommitAs(0, new Vector2(40f, 0f));      // laid before the islands: untagged, 30 m away
        var asked = new Vector2(10f, 0f);

        CrossIslandReads.Current = 2;
        Assert.False(RoadSpatialGrid.TryGetRoadWithin(asked, 20f, out _));
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(asked, 50f, out Vector2 nearest));
        Assert.Equal(new Vector2(40f, 0f), nearest);
        Assert.True(RoadSpatialGrid.TryGetRoadHeightWithin(asked, 50f, out _, out float distance, out _));
        Assert.Equal(30f, distance, 3);
        Assert.False(RoadSpatialGrid.TryGetRoadAtGradeWithin(asked, 20f, 30f, 1f, out _));
        Assert.All(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(10f, 0f, 0f), 50f), p => Assert.Equal(0, p.island));
        Assert.False(RoadSpatialGrid.AnyRoadAt(0f, 0f));
        Assert.True(CrossIslandReads.Total > 0);
        Assert.Contains("of island 1 passed over by island 2", CrossIslandReads.Describe());

        // Island A sees its own; off an island worker, everything is seen and nothing counted.
        CrossIslandReads.Current = 1;
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(asked, 20f, out nearest));
        Assert.Equal(new Vector2(0f, 0f), nearest);
        CrossIslandReads.Current = 0;
        long counted = CrossIslandReads.Total;
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(asked, 20f, out nearest));
        Assert.Equal(new Vector2(0f, 0f), nearest);
        Assert.True(RoadSpatialGrid.AnyRoadAt(0f, 0f));
        Assert.Equal(counted, CrossIslandReads.Total);
    }

    [Fact]
    public void AnotherIslandsStaircaseIsNotSeen()
    {
        var run = new StairRun { From = new Vector2(0f, 0f), To = new Vector2(8f, 0f), Width = 2f };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Position = new Vector3(2f, 30f, 0f) });
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Position = new Vector3(6f, 31f, 0f) });
        CrossIslandReads.Current = 1;
        StairStructures.Add(run);
        StairStructures.MarkLaid(new[] { new Vector2(2f, 0f) });

        CrossIslandReads.Current = 2;
        Assert.Empty(StairStructures.PiecesNear(new Vector3(3f, 0f, 0f), 5f));
        Assert.Empty(StairStructures.PiecesNearAny(new[] { new Vector2(3f, 0f) }, 5f));
        Assert.Null(StairStructures.Carrying(new Vector2(2f, 0f), new Vector2(6f, 0f), 2f));
        Assert.Null(StairStructures.Between(new Vector2(0f, 0f), new Vector2(8f, 0f), 1f));
        Assert.Empty(StairStructures.Runs);
        int cellX = Mathf.RoundToInt(2f / RoadPathfinder.CellSize);
        Assert.False(StairStructures.OnLaidStair(cellX, 0));

        CrossIslandReads.Current = 1;
        Assert.Equal(2, StairStructures.PiecesNear(new Vector3(3f, 0f, 0f), 5f).Count);
        Assert.True(StairStructures.OnLaidStair(cellX, 0));
        CrossIslandReads.Current = 0;
        Assert.Single(StairStructures.Runs);
        Assert.NotNull(StairStructures.Carrying(new Vector2(2f, 0f), new Vector2(6f, 0f), 2f));
    }

    /// <summary>An island past what a tag holds neither tags its work nor filters what it reads.</summary>
    [Fact]
    public void AnIslandPastTheTagIsUnfiltered()
    {
        CrossIslandReads.Current = 300;
        Assert.Equal(0, CrossIslandReads.Tag);
        Assert.True(CrossIslandReads.Sees(5));
        CrossIslandReads.Current = 3;
        Assert.Equal(3, CrossIslandReads.Tag);
        Assert.False(CrossIslandReads.Sees(5));
        Assert.True(CrossIslandReads.Sees(0));
        Assert.True(CrossIslandReads.Sees(3));
    }
}
