using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using StepValley = ProceduralRoads.Tests.DvergrViaductEndStairTests.StepValley;

namespace ProceduralRoads.Tests;

/// <summary>
/// The railing down a black marble viaduct's end stairs: a stepped black marble parapet on both sides
/// (<see cref="StairRailing.LayParapet"/>), as built by hand in game at a river viaduct's west end,
/// (4442, -4992) to (4356.5, -4992) in game: a blackmarble_base_1 a step capped by a blackmarble_1x1, black
/// marble under it where the stairs' edge has nothing to stand on, railed by the staircases' own rules (apart from the
/// material it behaves like the other railings where it meets the road), and every piece held by the game's support
/// rule.
/// </summary>
public sealed class DvergrViaductParapetTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly float _raisedShare = DvergrViaduct.RaisedShare;

    public DvergrViaductParapetTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        DvergrViaduct.RaisedShare = _raisedShare;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    // ------------------------------------------------------------ the hand-built example

    /// <summary>The west end as the fixture has it (ViaductSupportTests' readings): the deck's course top at 47.063, its
    /// edge at x 4355.222 on the line z -4992, two steps down to the road (blackmarble_stair at x 4354.222 and 4352.222).</summary>
    private static readonly StructureEnds.End WestEnd = new(new Vector2(4355.222f, -4992f), new Vector2(1f, 0f), 47.063f, 2);

    /// <summary>
    /// The finished ground at the west end, as the game's readings there have it (the fixture holds pieces, not the ground):
    /// both steps' lanes and the road at the foot about 45, the lower step on the ground and the upper on its lanes'
    /// columns (one block under the north lane, z -4991, two under the south, z -4993); the bank falling away south of
    /// the stairs, where the hand-built parapet stands on black marble, to 44 under the south parapet (z -4994.5) and rising to
    /// 46 under the north one (z -4989.5), where none was laid. By z alone, at whole metres.
    /// </summary>
    private static float WestEndGround(int x, int z) => z switch
    {
        >= -4989 => 46.2f,
        -4990 => 45.8f,
        -4991 => 45.5f,
        -4992 => 45.2f,
        -4993 => 45.0f,
        -4994 => 44.5f,
        _ => 43.5f,
    };

    /// <summary>The hand-laid pieces (as read before and after, the BlackMarble, meat, hide and trophy drops left out), all
    /// yaw 180, but the 1x1 laid at (4353.722, 45.563, -4990.5): it lies wholly inside the north lane's column block
    /// (blackmarble_2x2x2 at 4354.222, 45.063, -4991), so it adds nothing to see or to hold.</summary>
    private static readonly (string prefab, Vector3 at)[] HandBuiltParapet =
    {
        ("blackmarble_base_1", new(4352.222f, 46.063f, -4994.5f)),
        ("blackmarble_base_1", new(4352.222f, 46.063f, -4989.5f)),
        ("blackmarble_base_1", new(4354.222f, 47.063f, -4994.5f)),
        ("blackmarble_base_1", new(4354.222f, 47.063f, -4989.5f)),
        ("blackmarble_1x1", new(4353.722f, 46.563f, -4994.5f)),
        ("blackmarble_1x1", new(4353.722f, 46.563f, -4989.5f)),
        ("blackmarble_1x1", new(4355.722f, 47.563f, -4994.5f)),
        ("blackmarble_1x1", new(4355.722f, 47.563f, -4989.5f)),
        ("blackmarble_2x1x1", new(4354.222f, 45.563f, -4994.5f)),
        ("blackmarble_2x2x2", new(4352.222f, 44.063f, -4994.0f)),
        ("blackmarble_2x2x2", new(4354.222f, 44.063f, -4994.0f)),
    };

    [Fact]
    public void TheWestEndGetsTheHandBuiltParapet()
    {
        List<StructurePiece> laid = StairRailing.LayParapet(WestEnd, 2f * BridgeLayout.DeckHalfWidth, WestEndGround);
        foreach (var (prefab, at) in HandBuiltParapet)
            Assert.True(laid.Count(p => p.Prefab == prefab && Vector3.Distance(p.Position, at) < 0.01f && Apart(p.YawDegrees, 180f) < 0.1f) == 1,
                $"no {prefab} at {at} yaw 180; laid: {Say(laid)}");
        DvergrViaductBankParapetTests.AnyMoreAreCoursesUnderTheHandBuilt(laid, HandBuiltParapet);
    }

    /// <summary>The fixture's west end (every piece the game read west of x 4364, grounded where it read black marble's
    /// most) with the parapet laid: every parapet piece holds 1.2 times black marble's least or more.</summary>
    [Fact]
    public void TheWestEndsParapetHoldsByTheSupportRule()
    {
        var graph = new List<SupportGraph.Piece>();
        foreach (ViaductSupportTests.Reading r in ViaductSupportTests.GameReadings().Where(r => r.Position.x < 4364f))
            graph.Add(Box(r.Prefab, r.Position, r.Support >= SupportGraph.Marble.Max - 0.1f));
        int first = graph.Count;
        List<StructurePiece> laid = StairRailing.LayParapet(WestEnd, 2f * BridgeLayout.DeckHalfWidth, WestEndGround);
        foreach (StructurePiece p in laid)
            graph.Add(Box(p.Prefab, p.Position, OnTheGround(p, WestEndGround)));
        float[] support = SupportGraph.Settle(graph);
        for (int i = first; i < graph.Count; i++)
            Assert.True(support[i] >= DvergrViaduct.HoldMargin * SupportGraph.Marble.Min,
                FormattableString.Invariant($"{laid[i - first].Prefab} at {laid[i - first].Position}: {support[i]:F0}"));
    }

    // ------------------------------------------------------------ the railing rules

    /// <summary>No stairs where the road meets an end level, so no parapet.</summary>
    [Fact]
    public void AnEndAtGradeHasNoParapet()
    {
        var level = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 30f, 0);
        Assert.Empty(StairRailing.LayParapet(level, 4f, (x, z) => 30f));
        Assert.Empty(StairRailing.LayParapet(default, 4f, (x, z) => 30f));
    }

    /// <summary>
    /// Where it meets the road the parapet is railed as a staircase is (StairRailing, the one owner): the flight railed on
    /// both sides right down to the road, which comes onto its foot, and an end step in the finished ground bare, and the
    /// next up while it is in the ground too. The same steps laid as a staircase (one 2 m flight on the same line, the road
    /// at its foot) are railed at the same steps by the staircases' wooden rails, whatever the ground at the foot.
    /// </summary>
    [Theory]
    [InlineData(10f, 0)]     // the road at the foot's level: every step railed
    [InlineData(10.6f, 1)]   // the foot step half in the ground
    [InlineData(11.7f, 2)]   // the two lowest steps in it
    public void AtTheRoadTheParapetIsRailedAsAStaircaseIs(float roadAtTheFoot, int bare)
    {
        // Three steps down from an edge at 13 (x 0) to the road at 10 (x -6), the ground at the foot raised by the road's
        // fill to roadAtTheFoot, under the middle step at its foot or 0.2 under the fill, under the top one at its foot.
        var end = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 13f, 3);
        float Ground(int x, int z) => x <= -4 ? roadAtTheFoot : x <= -2 ? Mathf.Max(11f, roadAtTheFoot - 0.2f) : 12f;
        List<StructurePiece> parapet = StairRailing.LayParapet(end, 4f, Ground);
        int Step(Vector3 p) => Mathf.FloorToInt(-p.x / StairLayout.PieceRun);   // 0 the top step, its middle at x -1
        var railed = parapet.Where(p => p.Prefab == StairRailing.ParapetPrefab).Select(p => Step(p.Position)).ToList();
        Assert.Equal(Enumerable.Range(0, 3 - bare).SelectMany(k => new[] { k, k }).OrderBy(k => k), railed.OrderBy(k => k));
        Assert.All(parapet.Where(p => p.Prefab == StairRailing.ParapetPrefab), p => Assert.Equal(2.5f, Mathf.Abs(p.Position.z), 3));

        // The same three steps as a staircase climbing east from the road at its foot.
        var walking = Enumerable.Range(0, 3).Select(k => new StructurePiece
        {
            Kind = StructurePieceKind.StairStep, Prefab = StairKit.BlackMarble.Stair,
            Position = new Vector3(-1f - 2f * k, 12f - k, 0f), YawDegrees = 270f,
        }).ToList();
        List<StructurePiece> wood = StairRailing.Lay(StairKit.BlackMarble, walking,
            new[] { new StairEnd(end.Meet, end.Meet, false), new StairEnd(end.Edge, end.Edge, false) }, Ground);
        var woodRailed = wood.Where(p => p.Prefab == BridgeLayout.StairRailPrefab && Mathf.Abs(p.Position.y - Mathf.Round(p.Position.y)) < 0.01f
            && p.Position.y < 13f - Step(p.Position)).Select(p => Step(p.Position)).ToList();
        Assert.Equal(railed.OrderBy(k => k), woodRailed.OrderBy(k => k));
    }

    // ------------------------------------------------------------ on a viaduct

    /// <summary>A viaduct raised <paramref name="steps"/> over level banks: both its ends a flight of that many steps, each
    /// railed on both sides, step by step, whole and ruined, the parapet's every piece held 1.2 times its least or more and
    /// none left out by the support rule; the Dvergr wood kit keeps no parapet of its own.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BothEndsOfARaisedViaductAreRailedInBlackMarble(int steps)
    {
        DvergrViaduct.RaisedShare = 1f;
        var world = new StepValley { Depth = _ => 18f };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        for (world.Seed = 1; world.Seed < 1000 && DvergrViaduct.DrawnSteps(c, world.Seed) != steps; world.Seed++) { }
        Assert.Equal(steps, DvergrViaduct.RaisedSteps(c, world));
        var (near, far) = DvergrViaduct.Ends(c, world);
        Assert.Equal((steps, steps), (near.Steps, far.Steps));
        foreach (bool ruin in new[] { false, true })
        {
            List<StructurePiece> pieces = DvergrViaduct.Solve(c, world, world.GetSeed(), ruin, out DvergrViaduct.HoldReport report);
            List<StructurePiece> bases = pieces.FindAll(p => p.Prefab == StairRailing.ParapetPrefab);
            Assert.Equal(2 * 2 * steps, bases.Count);
            foreach (StructureEnds.End end in new[] { near, far })
            {
                Vector2 side = new(-end.Up.y, end.Up.x);
                float yaw = StairLayout.Wrap(BridgeLayout.YawDegrees(end.Up) + 90f);
                for (int k = 0; k < steps; k++)
                foreach (int s in new[] { -1, 1 })
                {
                    Vector2 at = end.Edge - end.Up * (StairLayout.PieceRun * k + 1f) + side * (s * 2.5f);
                    float foot = end.Height - StairLayout.PieceRise * (k + 1);
                    Assert.Contains(bases, p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) < 0.01f
                        && Mathf.Abs(p.Position.y - (foot + 1f)) < 0.01f && Apart(p.YawDegrees, yaw) < 0.1f
                        && p.Kind == StructurePieceKind.ViaductParapet);
                    Vector2 cap = end.Edge - end.Up * (StairLayout.PieceRun * k - 0.5f) + side * (s * 2.5f);
                    Assert.Contains(pieces, p => p.Prefab == StairRailing.ParapetCapPrefab && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), cap) < 0.01f
                        && Mathf.Abs(p.Position.y - (foot + 1.5f)) < 0.01f);
                }
            }
            Assert.DoesNotContain(report.DroppedPieces, IsParapet);
            foreach (ViaductSupport.Held h in ViaductSupport.Of(c, world, pieces).Where(h => IsParapet(h.Piece)))
                Assert.True(h.Share >= DvergrViaduct.HoldMargin, FormattableString.Invariant(
                    $"{(ruin ? "ruined" : "whole")}: {h.Piece.Prefab} at {h.Piece.Position} holds {h.Share:F2} of its least"));
        }
    }

    /// <summary>A viaduct at grade both ends: no stairs, so no stairs' parapet; only the parapet run on past each deck end
    /// onto the bank (DvergrViaduct.BankParapet), a parapet piece each side two modules out, on the level bank no black
    /// marble under it but the deck's course carried on under its cap flush with the road (flush with the ground: the game
    /// grounds it, so it is laid, BuriedPieces.Buried).</summary>
    [Fact]
    public void AViaductAtGradeHasOnlyItsBankParapet()
    {
        DvergrViaduct.RaisedShare = 0f;
        var world = new StepValley { Depth = _ => 18f };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        var (near, far) = DvergrViaduct.Ends(c, world);
        Assert.Equal((0, 0), (near.Steps, far.Steps));
        List<StructurePiece> pieces = DvergrViaduct.SolveComplete(c, world, world.GetSeed());
        foreach (StructurePiece p in pieces.Where(p => p.Prefab == StairRailing.ParapetHalfCoursePrefab))
            Assert.True(Mathf.Abs(p.Position.y + DvergrViaduct.Extents(p.Prefab).y - near.Height) < 0.01f
                || Mathf.Abs(p.Position.y + DvergrViaduct.Extents(p.Prefab).y - far.Height) < 0.01f, $"{p.Prefab} at {p.Position} under the road");
        List<StructurePiece> bases = pieces.FindAll(p => p.Prefab == StairRailing.ParapetPrefab);
        Assert.Equal(4, bases.Count);
        foreach (StructureEnds.End end in new[] { near, far })
        {
            Vector2 side = new(-end.Up.y, end.Up.x);
            foreach (int s in new[] { -1, 1 })
                Assert.Contains(bases, p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end.Edge - end.Up * 3f + side * (s * 2.5f)) < 0.01f);
        }
    }

    /// <summary>One end lower than the deck: its stairs railed step by step; the end at grade has only the parapet run on
    /// onto the bank, a parapet piece each side.</summary>
    [Fact]
    public void OnlyTheEndWithStairsIsRailed()
    {
        DvergrViaduct.RaisedShare = 0f;
        var world = new StepValley { Depth = _ => 18f, FarDrop = _ => 2f };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        var (near, far) = DvergrViaduct.Ends(c, world);
        Assert.Equal((0, 2), (near.Steps, far.Steps));
        List<StructurePiece> bases = DvergrViaduct.SolveComplete(c, world, world.GetSeed()).FindAll(p => p.Prefab == StairRailing.ParapetPrefab);
        Assert.Equal(6, bases.Count);
        Assert.Equal(4, bases.Count(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), far.Edge) < 2f * StairLayout.PieceRun + 2.6f));
        Vector2 nearSide = new(-near.Up.y, near.Up.x);
        foreach (int s in new[] { -1, 1 })
            Assert.Contains(bases, p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), near.Edge - near.Up * 3f + nearSide * (s * 2.5f)) < 0.01f);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A parapet piece of a viaduct's end stairs: a parapet kind but not an arch or a head.</summary>
    private static bool IsParapet(StructurePiece p) =>
        p.Kind == StructurePieceKind.ViaductParapet && p.Prefab != DvergrViaduct.Arch && p.Prefab != DvergrViaduct.HeadLeft && p.Prefab != DvergrViaduct.HeadRight;

    /// <summary>A piece's box on the deck along x, as ViaductSupport and ViaductSupportTests take it.</summary>
    private static SupportGraph.Piece Box(string prefab, Vector3 c, bool grounded)
    {
        var (hu, hv, hy) = DvergrViaduct.Extents(prefab);
        var lo = new Vector3(c.x - hu, c.y - hy, c.z - hv);
        var hi = new Vector3(c.x + hu, c.y + hy, c.z + hv);
        if (prefab == DvergrViaduct.StepPrefab) { lo.y = c.y; hi.y = c.y + 1f; }
        return new SupportGraph.Piece(c, lo, hi, (BuriedPieces.DataOf(prefab, out BuriedPieces.Measured measured) ? measured.Material : null)!.Value, grounded);
    }

    /// <summary>Whether a piece's box meets the ground: its bottom within the game's reach of the highest ground under it.</summary>
    private static bool OnTheGround(StructurePiece p, Func<int, int, float> ground)
    {
        var (hu, hv, hy) = DvergrViaduct.Extents(p.Prefab);
        float hi = float.MinValue;
        for (float u = -hu; u <= hu + 1e-3f; u += 0.5f)
            for (float v = -hv; v <= hv + 1e-3f; v += 0.5f)
                hi = Mathf.Max(hi, BuriedPieces.MeshHeight(p.Position.x + u, p.Position.z + v, ground));
        return hi >= p.Position.y - hy - SupportGraph.Reach;
    }

    /// <summary>How far apart two yaws are, 0 to 180 degrees.</summary>
    private static float Apart(float a, float b) => Mathf.Abs(StairLayout.Wrap(a - b + 180f) - 180f);

    private static string Say(IEnumerable<StructurePiece> pieces) =>
        string.Join("; ", pieces.Select(p => FormattableString.Invariant($"{p.Prefab} ({p.Position.x:F3}, {p.Position.y:F3}, {p.Position.z:F3}) yaw {p.YawDegrees:F0}")));
}
