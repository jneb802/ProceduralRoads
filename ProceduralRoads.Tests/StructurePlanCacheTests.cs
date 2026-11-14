using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class StructurePlanCacheTests : IDisposable
{
    private readonly ValheimWorldScope scope = new();
    private readonly Func<StairRun, WorldGenerator, Func<int, int, float>, StairSupport.Shortfall, bool, List<StructurePiece>> dressing = StairStructures.Dressing;

    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
    }

    public StructurePlanCacheTests()
    {
        scope.WithWorld(new Flat()).WithZoneSystem().WithZdos().WithScene().AsServer();
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    public void Dispose()
    {
        StairStructures.Dressing = dressing;
        NetworkCutoff.Forget();
        RoadNetworkGenerator.Reset();
        scope.Dispose();
    }

    private static StairRun Run(float x)
    {
        var run = new StairRun { From = new Vector2(x, 0f), To = new Vector2(x, 2f), Width = 4f, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Prefab = "stone_floor_2x2", Kind = StructurePieceKind.StairFloor, Position = new Vector3(x, 40f, 0f) });
        run.Clearances.Add(new StairClearance(new Vector3(x, 41f, 0f), new Vector3(1f, 1f, 1f), 0f, 40f));
        return run;
    }

    private static void Answer(params StairRun[] expected)
    {
        HashSet<StairRun> cached = StructurePlans.BuildableStaircases();
        Assert.True(cached.SetEquals(expected), "cached buildable staircases differ from the current plans");
        Assert.Same(cached, StructurePlans.BuildableStaircases());
        // Rebuild through the owner, with all the same plan and protection inputs.
        StructurePlans.InvalidatePlans();
        Assert.True(cached.SetEquals(StructurePlans.BuildableStaircases()), "cached answer differs from a fresh rebuild");
        foreach (StairRun run in StairStructures.Runs)
        {
            bool hasWalk = StairStructures.WalkBoxes().Any(box => run.Clearances.Contains(box));
            Assert.Equal(expected.Contains(run), hasWalk);
            Assert.Equal(expected.Contains(run), StructurePlans.ClearancesNear(run.Clearances[0].Centre, 0f).Count > 0);
        }
    }

    [Fact]
    public void CachedStaircasesMatchFreshPlansAfterLayingRefusalUnknownGroundAndProtection()
    {
        var laid = Run(0f);
        var refused = Run(20f);
        var waiting = Run(100f);
        StairStructures.Dressing = (run, world, ground, shortfall, _) =>
        {
            if (ReferenceEquals(run, refused))
            {
                shortfall.Falling.Add(run.Pieces[0].Position);
                return new List<StructurePiece>();
            }
            return new List<StructurePiece>(run.Pieces);
        };
        Answer();
        StairStructures.Add(laid);
        Answer(laid);
        StairStructures.Add(refused);
        Answer(laid);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(waiting.From, 8f) });
        StairStructures.Add(waiting);
        // On ground not known: not laid (StructureGround.NotLaid), and nothing waits for it.
        Answer(laid);
        var home = ZoneSystem.GetZone(laid.Pieces[0].Position);
        var other = ZoneSystem.GetZone(waiting.Pieces[0].Position);
        NetworkCutoff.SnapshotWorld(new[] { home, other });
        NetworkCutoff.BeginNewNetwork();
        NetworkCutoff.UseNewNetwork();
        Answer();
        NetworkCutoff.Admit(new[] { home });
        Answer(laid);
        NetworkCutoff.State saved = NetworkCutoff.Keep();
        NetworkCutoff.Readmit(new[] { home });
        Answer();
        NetworkCutoff.PutBack(saved);
        Answer(laid);
        NetworkCutoff.Forget();
        Answer(laid);
        RoadNetworkGenerator.Reset();
        Answer();
    }

    [Fact]
    public void ConcurrentReadersShareThePublishedAnswer()
    {
        StairStructures.Dressing = (run, world, ground, shortfall, _) => new List<StructurePiece>(run.Pieces);
        var laid = Run(0f);
        StairStructures.Add(laid);
        var answers = new HashSet<StairRun>[8];
        Parallel.For(0, answers.Length, i => answers[i] = StructurePlans.BuildableStaircases());
        Assert.All(answers, answer =>
        {
            Assert.Same(answers[0], answer);
            Assert.True(answer.SetEquals(new[] { laid }));
        });
    }

    /// <summary>The offline count of staircases refused at their first build (StairStructures.DressAll, through the one call,
    /// Dresser): a staircase its kit refuses is named, one it lays is not, and the same answer the plans give
    /// (StructurePlans.BuildableStaircases).</summary>
    [Fact]
    public void AStaircaseRefusedAtItsFirstBuildIsCountedOffline()
    {
        var laid = Run(0f);
        var refused = Run(20f);
        StairStructures.Dressing = (run, world, ground, shortfall, _) =>
        {
            if (!ReferenceEquals(run, refused)) return new List<StructurePiece>(run.Pieces);
            shortfall.Falling.Add(run.Pieces[0].Position);
            return new List<StructurePiece>();
        };
        StairStructures.Add(laid);
        StairStructures.Add(refused);
        var (dressed, lines) = StairStructures.DressAll(WorldGenerator.instance!);
        Assert.Equal(2, dressed);
        string line = Assert.Single(lines);
        Assert.Contains("(20.0, 0.0)", line);
        Assert.True(StructurePlans.BuildableStaircases().SetEquals(new[] { laid }));
    }
}
