using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// Staircases as the second structure source (a run's pieces reach the shared per-zone plans,
/// its walked pieces keep vegetation off and rock out, and the run survives
/// a save.
/// </summary>
public class StairStructuresTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public StairStructuresTests()
    {
        _scope.WithWorld(new SyntheticWorld());
        StairStructures.Reset();
        StructurePlans.Reset();
    }

    public void Dispose()
    {
        StairStructures.Reset();
        StructurePlans.Reset();
        _scope.Dispose();
    }

    private static StairRun Flight()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 40), (x, z) => 0.5f * z - (z > 10f && z < 16f ? 3f : 0f));
        Assert.True(plan.Reached, plan.Reason);
        return StairRun.FromPlan(new Vector2(0, 0), new Vector2(0, 40), 2f, plan);
    }

    /// <summary>Asking once for every point finds exactly the pieces asking point by point finds
    /// (the overlap check's one lock per layout).</summary>
    [Fact]
    public void PiecesNearAnyIsPiecesNearForEachPointTogether()
    {
        StairStructures.Add(Flight());
        StairPlan plan = StairLayout.Build(new Vector2(30, 0), new Vector2(30, 40), (x, z) => 0.5f * z);
        Assert.True(plan.Reached, plan.Reason);
        StairStructures.Add(StairRun.FromPlan(new Vector2(30, 0), new Vector2(30, 40), 2f, plan));
        var points = new List<Vector2> { new(0, 5), new(1, 20), new(29, 33), new(15, 15), new(500, 500) };

        var together = StairStructures.PiecesNearAny(points, 1.5f);
        var oneByOne = new HashSet<StructurePiece>();
        foreach (Vector2 q in points)
            foreach (StructurePiece p in StairStructures.PiecesNear(new Vector3(q.x, 0f, q.y), 1.5f)) oneByOne.Add(p);

        Assert.NotEmpty(oneByOne);
        Assert.True(oneByOne.SetEquals(together), $"one by one {oneByOne.Count}, together {together.Count}");
        Assert.Equal(together.Count, new HashSet<StructurePiece>(together).Count);
        Assert.Empty(StairStructures.PiecesNearAny(new List<Vector2>(), 1.5f));
    }

    [Fact]
    public void ARunsPiecesReachThePerZonePlans()
    {
        StairRun run = Flight();
        StairStructures.Add(run);

        Assert.Equal(run.Pieces.Count, StructurePlans.TotalPlannedPieces);
        foreach (StructurePiece piece in run.Pieces)
            Assert.Contains(piece, StructurePlans.PlanFor(ZoneSystem.GetZone(piece.Position))!);
        Assert.Contains(run.Pieces, p => p.Kind == StructurePieceKind.StairStep && p.Prefab == StairKit.Stone.Stair);
        Assert.Contains(run.Pieces, p => p.Kind == StructurePieceKind.StairSupport && p.Prefab == StairKit.Stone.Support);
    }

    [Fact]
    public void WalkedPiecesKeepVegetationOffButPillarsDoNot()
    {
        StairRun run = Flight();
        StairStructures.Add(run);
        Vector2s zone = ZoneSystem.GetZone(run.Pieces[0].Position);

        var areas = StructurePlans.ClearAreas(zone);
        int walked = StructurePlans.PlanFor(zone)!.Count(p => p.Kind != StructurePieceKind.StairSupport);
        Assert.Equal(walked, areas.Count);
    }

    [Fact]
    public void RockClearanceIsTheRunsBoxesNearAPoint()
    {
        StairRun run = Flight();
        StairStructures.Add(run);
        StairClearance first = run.Clearances[0];

        var near = StructurePlans.ClearancesNear(first.Centre, 3f);
        Assert.Contains(near, c => c.IsBox && c.Centre.Equals(first.Centre) && c.Half.Equals(first.Half) && c.YawDegrees == first.YawDegrees);
        Assert.All(near, c => Assert.False(c.WholeOnly));
        Assert.Empty(StructurePlans.ClearancesNear(first.Centre + new Vector3(500f, 0f, 0f), 3f));
    }

    [Fact]
    public void RunsSurviveASave()
    {
        StairRun run = Flight();
        StairStructures.Add(run);
        byte[] saved = StairStructures.Serialize();

        StairStructures.Reset();
        StairStructures.Load(saved);

        StairRun loaded = Assert.Single(StairStructures.Runs);
        Assert.Equal(run.From, loaded.From);
        Assert.Equal(run.To, loaded.To);
        Assert.Equal(run.Pieces.Select(p => (p.Kind, p.Prefab, p.Position, p.YawDegrees)), loaded.Pieces.Select(p => (p.Kind, p.Prefab, p.Position, p.YawDegrees)));
        Assert.Equal(run.Clearances.Select(c => (c.Centre, c.Half, c.YawDegrees, c.Surface)), loaded.Clearances.Select(c => (c.Centre, c.Half, c.YawDegrees, c.Surface)));
        Assert.Equal(saved, StairStructures.Serialize());
    }

    [Fact]
    public void DamagedSaveDataIsRefusedAndChangesNothing()
    {
        StairRun run = Flight();
        StairStructures.Add(run);
        byte[] saved = StairStructures.Serialize();

        Assert.Throws<EndOfStreamException>(() => StairStructures.Load(saved.Take(saved.Length - 1).ToArray()));
        Assert.Single(StairStructures.Runs);
        Assert.Throws<InvalidDataException>(() => StairStructures.Load(saved.Concat(new byte[] { 0 }).ToArray()));
        Assert.Single(StairStructures.Runs);

        StairStructures.Load(null);
        Assert.Empty(StairStructures.Runs);
    }
}
