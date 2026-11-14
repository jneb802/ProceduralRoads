using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>Independent raised surface, rather than the much wider protected footprint.</summary>
public sealed class PlacedLandingStairTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private const float Lip = 183.31f, Radius = 12f;
    /// <summary>A real placed location's footprint (StoneTowerRuins05's, its exterior radius and levelling).</summary>
    private const float Footprint = 22f;
    private static float Ground(float x, float z) => Lip - Mathf.Clamp((new Vector2(x, z).magnitude - Radius) * 1.2f, 0f, 11.66f);
    /// <summary>The ground rising away from the landing, 1 in 2: a road's end above it.</summary>
    private static float Rising(float x, float z) => Lip + Mathf.Max(0f, (new Vector2(x, z).magnitude - Radius) * 0.5f);

    public PlacedLandingStairTests()
    {
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 35f) });
    }

    public void Dispose()
    {
        PlacedLocations.Reset(); RoadLocationProtection.Reset(); RoadNetworkGenerator.Reset(); _scope.Dispose();
    }

    private static StairPlan Approach(float yaw, bool descending = false, float endOut = 26f, float footprint = 35f)
    {
        Vector2 end = StairLayout.Forward(yaw) * endOut;
        float Terrain(float x, float z) => descending ? Rising(x, z) : Ground(x, z);
        var request = new LocationApproachRequest
        {
            Centre = new Vector2(0f, 0f), RoadEnd = end, RoadHeight = Terrain(end.x, end.y),
            Arriving = StairLayout.Wrap(yaw + 180f), RoadTail = new List<Vector2> { end + StairLayout.Forward(yaw) * 30f, end },
        };
        RoadStairSpan? span = RoadStairRuns.PlanPlacedLocationApproach(request, yaw,
            new[] { new LevelOp(0f, 0f, 0f, Radius, false) }, TestLattice.Of(Terrain), (_, _) => Heightmap.Biome.Mountain, 77,
            out string why, inFootprint: p => p.magnitude < footprint, root: Lip);
        Assert.True(span != null, why);
        return span!.Plan;
    }

    // The oracle uses explicit fixture geometry and prefab dimensions. It does not call production validity or Cuts.
    internal static IEnumerable<(Vector2 at, float height, StairPiece piece)> Walk(StairPlan plan)
    {
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
        {
            bool stair = piece.Kind == StairPieceKind.Stair;
            float halfAcross = piece.Prefab.Contains("1x1") ? 0.5f : 1f;
            float halfAlong = piece.Prefab.Contains("wall_") || piece.Prefab.Contains("2x1x1") || piece.Prefab.Contains("1x1") ? 0.5f : 1f;
            float angle = piece.YawDegrees * (float)Math.PI / 180f;
            Vector2 right = new((float)Math.Cos(angle), -(float)Math.Sin(angle));
            Vector2 forward = new((float)Math.Sin(angle), (float)Math.Cos(angle));
            for (float z = -halfAlong; z <= halfAlong + 0.001f; z += 0.25f)
                for (float x = -halfAcross; x <= halfAcross + 0.001f; x += 0.25f)
                    yield return (new Vector2(piece.Position.x, piece.Position.z) + right * x + forward * z,
                        piece.Position.y + (stair ? (1f - z) / 2f : 0.5f), piece);
        }
    }

    private static string? Clipped(StairPlan plan)
    {
        StairPiece? last = plan.Pieces.LastOrDefault(p => p.Kind != StairPieceKind.Support);
        bool DownContact(StairPiece pc)
        {
            if (pc != last || pc.Kind != StairPieceKind.Stair || Math.Abs(Math.IEEERemainder(pc.YawDegrees - plan.EndHeading, 360f)) > 0.01f) return false;
            var high=Walk(plan).Where(s=>s.piece==pc && s.height>=pc.Position.y+1f-0.01f);
            // The descending contact may overlap the raised disc in XY; every high-edge sample must remain clear.
            return high.All(s=>s.at.magnitude>=Radius-0.01f || s.height>=Lip-StairLayout.StepUp);
        }
        return Walk(plan).Where(s => !DownContact(s.piece) && s.at.magnitude < Radius - 0.01f && s.height < Lip - StairLayout.StepUp)
            .OrderBy(s => s.height)
            .Select(s => $"{s.piece.Kind} {s.piece.Prefab} origin {s.piece.Position} yaw {s.piece.YawDegrees} at {s.at}: walk {s.height:F3} below raised surface {Lip:F3}; end {plan.End}")
            .FirstOrDefault();
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(225f)]
    public void AReachableRaisedLandingHasAnOutsideApproachAtItsLip(float yaw)
    {
        StairPlan plan = Approach(yaw);
        Assert.False(plan.Reversed);
        Assert.True(Clipped(plan) == null, Clipped(plan));
        Assert.True(Walk(plan).Any(s => s.at.magnitude <= Radius + 1f && s.height >= Lip - 0.01f), $"no arrival at lip; end {plan.End}");
        Assert.Contains(plan.Pieces, p => p.Kind == StairPieceKind.Stair && new Vector2(p.Position.x, p.Position.z).magnitude < 35f);
    }

    [Fact]
    public void TheOracleRejectsBuriedStairsAndFillersWithoutCuts()
    {
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true };
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(0f, Lip - 0.66f, 0f), YawDegrees = 180f });
        Assert.NotNull(Clipped(plan));
        plan.Pieces.Clear();
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Filler, Prefab = "stone_wall_1x1", Position = new Vector3(0f, Lip - 0.9f, 0f), YawDegrees = 45f });
        Assert.NotNull(Clipped(plan));
        Assert.Empty(plan.Cuts);
    }

    /// <summary>
    /// A staircase coming down onto the landing from a road's end above it, set up as on real worlds: the road's end just
    /// outside the location's footprint, StoneTowerRuins05's 22 m, 26.4 m out (its nearest approach on seven offline test worlds;
    /// MountainCave02's 27.7 and 28 m out of 26.2). It comes down onto a floor level with the landing at its lip, its arrival
    /// within the hold on facing into the location (StairLayout.ArrivalSteps, 90 degrees), and no walked piece in the footprint,
    /// where no earthwork is made, deeper in the ground than an approach is allowed (StairCover.ApproachRefusal). This was DescendingOntoTheLandingKeepsItsEmbeddedFoot,
    /// with the road's end 26 m out inside a 35 m footprint, where its last stair stood below the landing's height, set into
    /// it: that arose only with a road's end inside the footprint, which real worlds do not produce (a road stops outside a
    /// placed location's footprint, at its ring, RoadEndRing; none of the 92 approaches to placed locations on seven
    /// offline test worlds set off inside one). A realistic setup, the test changed to what happens there. Laid from 27 m out:
    /// from 26.4 m its top arrived on the road's end's ring only within the search's old band round the circle, which an
    /// arrival no longer has (decision 6j: the start's precise target, RoadEndRing.Allows).
    /// </summary>
    [Fact]
    public void DescendingOntoTheLandingItComesDownOntoAFloorLevelWithIt()
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        StairPlan plan = Approach(0f, descending: true, endOut: 27f, footprint: Footprint);
        Assert.True(plan.Reversed);
        // Its foot at the landing: the walked piece nearest the centre, a floor whose top is level with the landing, at its lip.
        StairPiece foot = plan.Pieces.Where(p => p.Kind is StairPieceKind.Stair or StairPieceKind.Landing)
            .OrderBy(p => new Vector2(p.Position.x, p.Position.z).magnitude).First();
        Vector2 at = new Vector2(foot.Position.x, foot.Position.z);
        Assert.Equal(StairPieceKind.Landing, foot.Kind);
        Assert.InRange(foot.Position.y + StairLayout.PieceRise / 2f, Lip - 0.05f, Lip + 0.05f);
        Assert.InRange(at.magnitude, Radius - RoadStairRuns.LipReach - 0.01f, Radius + 0.01f);
        // The flight down onto it heads within the hold of the location's centre.
        var (walk, toward, stair) = TowerPlacedFirstTests.ArrivingStair(plan, at, new Vector2(0f, 0f));
        float off = Mathf.Abs(StairLayout.Wrap(walk - toward + 180f) - 180f);
        Assert.True(off <= StairLayout.ArrivalSteps * StairLayout.HeadingStep + 0.01f, $"its last stair at {stair} walked heading {walk}, the centre at {toward}");
        // Inside the footprint, which the terrain writer leaves to the location's own levelling (outside it, at the road's end,
        // the staircase's earthwork uncovers its top floor: RoadStairRuns.CutsOutsideFootprints).
        float buried = RoadStairRuns.Cover(plan, Rising, p => p.magnitude < Footprint);
        Assert.True(buried <= StairCover.ApproachRefusal + 0.001f, $"a walked piece {buried:F2} m into the ground in the footprint");
    }

    [Theory]
    [InlineData(0.8f, true)]
    [InlineData(1.8f, false)]
    public void AnOvershootUsesAtMostOneReversedStair(float overshoot, bool fitsOne)
    {
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, EndHeading = 0f, End = new Vector3(0f, Lip + overshoot, 0f) };
        StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(plan, (_, _) => Lip, 2f, _ => true);
        // Landing first: a floor, then its stair down, the floor's exit.
        Assert.Equal(fitsOne, finish == StairLayout.TopFinish.FloorThenDown);
        Assert.InRange(plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair), fitsOne ? 1 : 0, 1);
    }

    [Fact]
    public void ARejectedFinishRestoresAllPlanStateBeforeTheNextAttempt()
    {
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, EndHeading = 0f, End = new Vector3(0f, Lip + 0.8f, 0f) };
        StairPlan before = plan.Copy();
        Assert.Equal(StairLayout.TopFinish.None, RoadStairRuns.FinishOverLedge(plan, (_, _) => Lip, 2f, _ => false));
        Assert.Equal(before.Pieces, plan.Pieces); Assert.Equal(before.Clearances, plan.Clearances); Assert.Equal(before.Cuts, plan.Cuts);
        Assert.Equal(before.Stairs, plan.Stairs); Assert.Equal(before.Landings, plan.Landings);
        Assert.Equal(before.End, plan.End); Assert.Equal(before.Rise, plan.Rise); Assert.Equal(before.WalkedLength, plan.WalkedLength);
        Assert.Equal(StairLayout.TopFinish.FloorThenDown, RoadStairRuns.FinishOverLedge(plan, (_, _) => Lip, 2f, _ => true));
        Assert.Equal(1, plan.Stairs);
    }

    [Fact]
    public void ARejectedReplacementRestoresTheFloorAndItsLastRowMetadata()
    {
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, EndHeading = 0f,
            End = new Vector3(0f, Lip + 0.8f, 0f), LastRowPieces = 0, LastRowClearances = 0,
            LastRowPieceCount = 1, LastRowClearanceCount = 1, LastRowKind = StairPieceKind.Landing,
            LastRowStart = new Vector2(0f, -2f), LastRowHeading = 0f, LastRowHeight = Lip + 0.8f,
            Stairs = 3, Landings = 1, Rise = 3f, WalkedLength = 8f };
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(0f, Lip + 0.3f, -1f) });
        plan.Clearances.Add(new StairClearance(new Vector3(0f, Lip + 1f, -1f), new Vector3(1f, 1f, 1f), 0f, Lip + 0.8f));
        plan.Cuts.Add(new Vector3(0f, Lip - 0.15f, -1f));
        StairPlan before = plan.Copy();
        Assert.Equal(StairLayout.TopFinish.None, RoadStairRuns.FinishOverLedge(plan, (_, _) => Lip, 2f, _ => false));
        Assert.Equal(before.Pieces.Select(p => (p.Kind, p.Prefab, p.Position, p.YawDegrees)), plan.Pieces.Select(p => (p.Kind, p.Prefab, p.Position, p.YawDegrees)));
        Assert.Equal(before.Clearances, plan.Clearances); Assert.Equal(before.Cuts, plan.Cuts);
        Assert.Equal((before.Stairs, before.Landings, before.End, before.Rise, before.WalkedLength), (plan.Stairs, plan.Landings, plan.End, plan.Rise, plan.WalkedLength));
        Assert.Equal((before.LastRowPieces, before.LastRowClearances, before.LastRowPieceCount, before.LastRowClearanceCount),
            (plan.LastRowPieces, plan.LastRowClearances, plan.LastRowPieceCount, plan.LastRowClearanceCount));
        Assert.Equal((before.LastRowKind, before.LastRowStart, before.LastRowHeading, before.LastRowHeight),
            (plan.LastRowKind, plan.LastRowStart, plan.LastRowHeading, plan.LastRowHeight));
        Assert.Equal(StairLayout.TopFinish.BackToBack, RoadStairRuns.FinishOverLedge(plan, (_, _) => Lip, 2f, p => p.Pieces.All(pc => pc.Position.z <= 0.01f)));
        Assert.Equal(StairPieceKind.Stair, plan.LastRowKind);
        Assert.Equal(plan.Pieces.Count - plan.LastRowPieces, plan.LastRowPieceCount);
        Assert.Equal(plan.Clearances.Count - plan.LastRowClearances, plan.LastRowClearanceCount);
    }

    [Fact]
    public void ARefusedCallbackCannotMutateTheRetainedFloorOrReplacementState()
    {
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=0,End=new Vector3(0,Lip+0.8f,0),
            LastRowKind=StairPieceKind.Landing,LastRowStart=new Vector2(0,-2),LastRowHeading=0,LastRowHeight=Lip+0.8f,
            LastRowPieces=0,LastRowClearances=0,LastRowPieceCount=1,LastRowClearanceCount=0,Landings=1 };
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(0,Lip+0.3f,-1) });
        var before=plan.Copy(deepPieces:true);
        int calls=0;
        Assert.Equal(StairLayout.TopFinish.None,RoadStairRuns.FinishOverLedge(plan,(_,_)=>Lip,2,p=>
        {
            calls++;p.Pieces[0].Position=new Vector3(999,999,999);p.Pieces[0].YawDegrees=99;
            p.End=new Vector3(888,888,888);p.Stairs=99;p.Landings=99;p.LastRowStart=new Vector2(999,999);p.LastRowHeight=999;
            p.Cuts.Add(new Vector3(999,999,999));return false;
        }));
        Assert.True(calls>=1); // The rule's finish (a floor top over the ground by up to a ledge: made a stair down) and those it falls through to, each refused.
        Assert.Equal(before.Pieces.Select(p=>(p.Position,p.YawDegrees,p.Kind,p.Prefab)),plan.Pieces.Select(p=>(p.Position,p.YawDegrees,p.Kind,p.Prefab)));
        Assert.Equal((before.End,before.Stairs,before.Landings,before.LastRowStart,before.LastRowHeight),
            (plan.End,plan.Stairs,plan.Landings,plan.LastRowStart,plan.LastRowHeight));
        Assert.Equal(before.Cuts,plan.Cuts);Assert.Equal(before.Clearances,plan.Clearances);
        Assert.Equal((before.LastRowPieces,before.LastRowPieceCount,before.LastRowClearances,before.LastRowClearanceCount),
            (plan.LastRowPieces,plan.LastRowPieceCount,plan.LastRowClearances,plan.LastRowClearanceCount));
    }

    [Fact]
    public void AnExistingTerminalReverseCannotAcquireASecondReverse()
    {
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=0,End=new Vector3(0,Lip+1.8f,0) };
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Stair,Prefab="stone_stair",Position=new Vector3(0,Lip+0.8f,1),YawDegrees=0 });
        Assert.Equal(StairLayout.TopFinish.None,RoadStairRuns.FinishOverLedge(plan,(_,_)=>Lip,2,_=>true));
        Assert.Single(plan.Pieces);Assert.Equal(StairPieceKind.Stair,plan.Pieces[0].Kind);
    }

    [Fact]
    public void TheValidWalkSurvivesSettlementFirstBuildReloadAndZoneReplay()
    {
        StairPlan plan = Approach(0f);
        var run = StairRun.FromPlan(new Vector2(0f, 26f), new Vector2(plan.End.x, plan.End.z), 2f, plan, StairKit.Stone);
        StairStructures.Add(run);
        // The deliberately sunk lead-in is not a critical walking piece; the burial owner may remove it.
        var walking = run.Pieces.Skip(plan.SunkPieces).Where(p => p.Kind != StructurePieceKind.StairSupport)
            .Select(p => (p.Kind, p.Prefab, p.Position, p.YawDegrees)).ToArray();
        StairStructures.Settle(run, (x, z) => Ground(x, z));
        var settledWalk = run.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport)
            .Select(p => (p.Kind, p.Prefab, p.Position, p.YawDegrees)).ToArray();
        Assert.All(walking, piece => Assert.Contains(piece, settledWalk));
        Assert.NotEmpty(run.Ground);
        // The location's ground, published by its preload (it is the world's ground here): known to the first build, which
        // lays nothing on ground not known.
        PlacedLocations.Preload(new Vector2(0f, 0f), Ground);
        List<StructurePiece> first = StairStructures.Build(run, new RaisedWorld());
        BuiltStair built = Assert.Single(BuiltStairs.Sites);
        // Its layout's ground, and what its kit added (now that the location's ground is known, none of it unsaved).
        Assert.Equal(run.Ground, built.Ground.Take(run.Ground.Count));
        byte[] layout = StairStructures.Serialize(), pieces = BuiltStairs.Serialize();
        var pins = run.Ground.ToArray();
        StairStructures.Reset(); StairStructures.Load(layout); BuiltStairs.Load(pieces);
        StairRun loaded = Assert.Single(StairStructures.Runs);
        Assert.Equal(pins, loaded.Ground);
        Func<StairRun, WorldGenerator, List<StructurePiece>> dresser = StairStructures.Dresser;
        try
        {
            StairStructures.Dresser = (_, _) => throw new InvalidOperationException("a saved first build must replay its own pieces");
            Assert.Equal(first.Select(PieceKey), StairStructures.Build(loaded, new RaisedWorld()).Select(PieceKey));
            var replay = new StairStructures().Plan(new RaisedWorld(), 77).ToList();
            Assert.Equal(first.Select(PieceKey), replay.Select(PieceKey));
            var replayPlan = new StairPlan { Kit = plan.Kit, EndHeading = plan.EndHeading };
            foreach (StructurePiece piece in replay.Where(p => StairStructures.IsLayoutKind(p.Kind) && p.Kind != StructurePieceKind.StairSupport))
                    replayPlan.Pieces.Add(new StairPiece { Prefab = piece.Prefab, Position = piece.Position, YawDegrees = piece.YawDegrees,
                        Kind = piece.Kind == StructurePieceKind.StairStep ? StairPieceKind.Stair : piece.Kind == StructurePieceKind.StairFloor ? StairPieceKind.Landing : StairPieceKind.Filler });
            Assert.True(Clipped(replayPlan) == null, Clipped(replayPlan));
            foreach (var zone in replay.Select(p => ZoneSystem.GetZone(p.Position)).Distinct())
                Assert.Equal(replay.Where(p => ZoneSystem.GetZone(p.Position) == zone).Select(PieceKey),
                    new StairStructures().Plan(new RaisedWorld(), 77).Where(p => ZoneSystem.GetZone(p.Position) == zone).Select(PieceKey));
            Assert.All(pins, pin => Assert.Equal(pin.Height, StructureGround.Held(new Vector2(pin.X, pin.Z))!.Value));
        }
        finally { StairStructures.Dresser = dresser; }
    }

    private sealed class RaisedWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Ground(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A piece's identity: its prefab and exact pose.</summary>
    private static (string, float, float, float, float, float, float) PieceKey(StructurePiece p) =>
        (p.Prefab, p.Position.x, p.Position.y, p.Position.z, p.PitchDegrees, p.YawDegrees, p.RollDegrees);
}
