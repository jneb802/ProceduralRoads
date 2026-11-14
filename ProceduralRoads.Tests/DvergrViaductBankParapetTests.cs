using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using StepValley = ProceduralRoads.Tests.DvergrViaductEndStairTests.StepValley;

namespace ProceduralRoads.Tests;

/// <summary>
/// The parapet a black marble viaduct's end without stairs runs on past the deck's end onto the bank
/// (<see cref="DvergrViaduct.BankParapet"/>, <see cref="StairRailing.LayBankParapet"/>), as built by hand in game at a
/// river viaduct's east end, (4442, -4992) to (4356.5, -4992), where the bank beside the road falls away both sides: the
/// deck's arches run on one over its edge, a blackmarble_1x1 cap and a
/// blackmarble_base_1 sloping onto the bank, black marble under them to the ground; on both sides whatever the bank does
/// (one side alone looks uneven); a side the support rule cannot hold left out whole, and only the pieces a road turns
/// across left out of a side.
/// </summary>
public sealed class DvergrViaductBankParapetTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly float _raisedShare = DvergrViaduct.RaisedShare;

    public DvergrViaductBankParapetTests()
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

    /// <summary>The east end: the deck's course top at 46.063, its edge at x 4443.222 on the line z -4992, the road
    /// meeting it level (no steps), the deck running west.</summary>
    private static readonly StructureEnds.End EastEnd = new(new Vector2(4443.222f, -4992f), new Vector2(-1f, 0f), 46.063f, 0);

    /// <summary>
    /// The finished ground at the east end, as the hand-built pieces stand on it (the terrain there was not loaded to read):
    /// the road's apron at 46 from z -4990 to -4994; the bank north of it at 42 (rising to 42.5 two modules out, where the
    /// parapet piece took one block under it), south at 42.5 (rising to 44, where it took none). By whole metre.
    /// </summary>
    private static float EastEndGround(int x, int z) =>
        z >= -4994 && z <= -4990 ? 46f
        : z > -4990 ? (x <= 4445 ? 42f : 42.5f)
        : (x <= 4445 ? 42.5f : 44f);

    /// <summary>The railing the owner added, read in game (cli_zdos_at): prefab, centre, and the yaw where it shows. The black marble
    /// under it, blackmarble_2x2x2s, is now the long piece beside the road, not over it (<see cref="TheSupportsAreTheLongPieceOffTheRoad"/>).</summary>
    private static readonly (string prefab, Vector3 at, float? yaw)[] HandBuilt =
    {
        ("blackmarble_arch", new(4443.222f, 47.063f, -4994.5f), 180f),
        ("blackmarble_arch", new(4443.222f, 47.063f, -4989.5f), 180f),
        ("blackmarble_1x1", new(4444.722f, 46.563f, -4994.5f), null),
        ("blackmarble_1x1", new(4444.722f, 46.563f, -4989.5f), null),
        ("blackmarble_base_1", new(4446.222f, 46.063f, -4994.5f), 0f),
        ("blackmarble_base_1", new(4446.222f, 46.063f, -4989.5f), 0f),
    };

    private static float Finished(Vector2 p) => BuriedPieces.MeshHeight(p.x, p.y, EastEndGround);

    /// <summary>The deck's heading and its left, as the viaduct's (from bank (4442, -4992) west).</summary>
    private static readonly Vector2 Dir = new(-1f, 0f), Side = new(0f, -1f);

    /// <summary>The deck's arches' turn at x 4443.222, one on from the deck's last at 4441.222 (yaw 0 in game, turn 90):
    /// 270, the tall ends paired.</summary>
    private const float ArchTurnThere = 270f;

    [Fact]
    public void TheEastEndGetsTheHandBuiltParapetOnBothSides()
    {
        float yaw = BridgeLayout.YawDegrees(Dir);
        List<List<StructurePiece>> sides = DvergrViaduct.BankParapet(EastEnd, Finished, Dir, Side, ArchTurnThere, yaw);
        Assert.Equal(2, sides.Count);
        List<StructurePiece> laid = sides.SelectMany(s => s).ToList();
        foreach (var (prefab, at, want) in HandBuilt)
            Assert.True(laid.Count(p => p.Prefab == prefab && Vector3.Distance(p.Position, at) < 0.01f
                && (want is not float y || Apart(p.YawDegrees, y) < 0.1f)) == 1, $"no {prefab} at {at}; laid: {Say(laid)}");
        AnyMoreAreCoursesUnderTheHandBuilt(laid, HandBuilt.Select(h => (h.prefab, h.at)));
        Assert.All(laid, p => Assert.Equal(StructurePieceKind.ViaductParapet, p.Kind));
        // Clear of the road's walking box: nothing over its surface within its width, the player's height up.
        foreach (StructurePiece p in laid)
        {
            var (_, hv, hy) = DvergrViaduct.Extents(p.Prefab);
            if (p.Position.y + hy <= EastEnd.Height + 1e-3f || p.Position.y - hy >= EastEnd.Height + RoadRockPolicy.PlayerHeight) continue;
            Assert.True(Mathf.Abs(p.Position.z - EastEnd.Edge.y) - hv >= BridgeLayout.DeckHalfWidth - 1e-3f, $"{p.Prefab} at {p.Position} is in the road");
        }
    }

    /// <summary>The fixture's east end (every piece the game read east of x 4430) with the parapet laid: every piece of it
    /// holds 1.2 times black marble's least or more.</summary>
    [Fact]
    public void TheEastEndsParapetHoldsByTheSupportRule()
    {
        var graph = new List<SupportGraph.Piece>();
        foreach (ViaductSupportTests.Reading r in ViaductSupportTests.GameReadings().Where(r => r.Position.x > 4430f && r.Position.x < EastEnd.Edge.x))
            graph.Add(Box(r.Prefab, r.Position, r.Support >= SupportGraph.Marble.Max - 0.1f));
        int first = graph.Count;
        List<StructurePiece> laid = DvergrViaduct.BankParapet(EastEnd, Finished, Dir, Side, ArchTurnThere, BridgeLayout.YawDegrees(Dir)).SelectMany(s => s).ToList();
        foreach (StructurePiece p in laid)
            graph.Add(Box(p.Prefab, p.Position, OnTheGround(p)));
        float[] support = SupportGraph.Settle(graph);
        for (int i = first; i < graph.Count; i++)
            Assert.True(support[i] >= DvergrViaduct.HoldMargin * SupportGraph.Marble.Min,
                FormattableString.Invariant($"{laid[i - first].Prefab} at {laid[i - first].Position}: {support[i]:F0}"));
    }

    /// <summary>
    /// The foundation carried on down as the staircases' walls are (StairSupport.Bury, one owner): at the 24 m viaduct's
    /// east end in game, its deck's edge at x 9081 on the line z 128, the deck at 55.613, the bank falling away across the
    /// columns there (taken here at 52.6 under the first, 53.2 under the second; the terrain was not loaded to read). Each
    /// column is the long piece (<see cref="StairRailing.ParapetHalfCoursePrefab"/>) stacked a course at a time from the
    /// deck's top down until its lowest is sunk Bury into the lowest ground under its footprint, and nothing under that.
    /// </summary>
    [Fact]
    public void TheFoundationGoesDownAsTheStairsDo()
    {
        var end = new StructureEnds.End(new Vector2(9081f, 128f), new Vector2(-1f, 0f), 55.613f, 0);
        float Ground(int x, int z) => z >= 127 && z <= 129 ? 55.6f : x <= 9082 ? 52.6f : 53.2f;
        List<StructurePiece> laid = StairRailing.LayBankParapet(end, 4f, Ground).SelectMany(s => s.pieces).Where(p => p.Position.z < 128f).ToList();
        var (hu, hv, hy) = DvergrViaduct.Extents(StairRailing.ParapetHalfCoursePrefab);
        foreach (float x in laid.Where(p => p.Prefab == StairRailing.ParapetHalfCoursePrefab).Select(p => p.Position.x).Distinct())
        {
            List<StructurePiece> column = laid.Where(p => p.Prefab == StairRailing.ParapetHalfCoursePrefab && Mathf.Abs(p.Position.x - x) < 0.01f)
                .OrderByDescending(p => p.Position.y).ToList();
            float low = BuriedPieces.RangeOver(new[]
            {
                new Vector2(x - hu, 125f), new Vector2(x + hu, 125f), new Vector2(x + hu, 126f), new Vector2(x - hu, 126f),
            }, Ground).lowest;
            for (int k = 1; k < column.Count; k++)
                Assert.Equal(column[k - 1].Position.y - 2f * hy, column[k].Position.y, 3);
            Assert.True(column[column.Count - 1].Position.y - hy <= low - StairSupport.Bury + 1e-3f, $"x {x}: lowest at {column[column.Count - 1].Position.y} not sunk into {low}");
            Assert.True(column.Count == 1 || column[column.Count - 2].Position.y - hy > low - StairSupport.Bury, $"x {x}: a course under what was sunk");
        }
        Assert.Contains(laid, p => p.Prefab == StairRailing.ParapetHalfCoursePrefab && Mathf.Abs(p.Position.x - (end.Edge.x + hu)) < 0.01f
            && Mathf.Abs(p.Position.y + hy - end.Height) < 0.01f);
    }

    /// <summary>
    /// The supports under a bank parapet are the long black marble piece (<see cref="StairRailing.ParapetHalfCoursePrefab"/>,
    /// 2 m along the parapet, 1 m thick, 1 m tall), never a 2 x 2 x 2 (the owner, 5 Nov 2026: those left a gap in the road
    /// surface): none is the cube, each stands in the parapet's own strip, its road-side face no nearer the deck's middle than
    /// the road's edge (<see cref="BridgeLayout.DeckHalfWidth"/>), the first flush with the road under the cap, and each
    /// column reaches the ground, its lowest sunk <see cref="StairSupport.Bury"/> into the lowest ground under it.
    /// </summary>
    [Fact]
    public void TheSupportsAreTheLongPieceOffTheRoad()
    {
        var (hu, hv, hy) = DvergrViaduct.Extents(StairRailing.ParapetHalfCoursePrefab);
        Assert.Equal((1f, 0.5f, 0.5f), (hu, hv, hy));
        var sides = StairRailing.LayBankParapet(EastEnd, 2f * BridgeLayout.DeckHalfWidth, EastEndGround);
        Assert.Equal(2, sides.Count);
        foreach (var (outward, pieces) in sides)
        {
            Assert.DoesNotContain(pieces, p => p.Prefab == StairRailing.ParapetCoursePrefab);
            List<StructurePiece> supports = pieces.Where(p => p.Prefab == StairRailing.ParapetHalfCoursePrefab).ToList();
            Assert.NotEmpty(supports);
            foreach (StructurePiece p in supports)
            {
                Assert.True(Mathf.Abs(Vector2.Dot(new Vector2(p.Position.x, p.Position.z) - EastEnd.Edge, outward)) - hv >= BridgeLayout.DeckHalfWidth - 1e-3f,
                    $"{p.Prefab} at {p.Position} reaches over the road");
                // Its long side runs along the deck, so its yaw puts local x along the deck's line.
                Assert.Equal(StairLayout.Wrap(BridgeLayout.YawDegrees(EastEnd.Up) + 90f), p.YawDegrees, 2);
            }
            Assert.Contains(supports, p => Mathf.Abs(p.Position.y + hy - EastEnd.Height) < 0.01f);
            foreach (float x in supports.Select(p => p.Position.x).Distinct())
            {
                float lowest = supports.Where(p => Mathf.Abs(p.Position.x - x) < 0.01f).Min(p => p.Position.y) - hy;
                Vector2 c = new(x, supports.First(p => Mathf.Abs(p.Position.x - x) < 0.01f).Position.z);
                float ground = BuriedPieces.RangeOver(new[]
                {
                    c + new Vector2(-hu, -hv), c + new Vector2(hu, -hv), c + new Vector2(hu, hv), c + new Vector2(-hu, hv),
                }, EastEndGround).lowest;
                Assert.True(lowest <= ground - StairSupport.Bury + 1e-3f, $"column at x {x}: lowest {lowest} does not reach ground {ground}");
            }
        }
    }

    /// <summary>
    /// A bank parapet's arch is never left out on a burial verdict but the one owner's (BuriedPieces.Buried, the game's mesh
    /// over its whole footprint, read by the viaduct's whole-structure omission): on ground not known (NaN), where the nine
    /// samples' "lower than its top" never held and so left it out; and over a bank standing over it everywhere but at one
    /// vertex inside its footprint, a dip between those nine samples, where it shows.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABankParapetsArchIsNotLeftOutOverADipOrUnknownGround(bool unknown)
    {
        // The deck along +z, the arches 2.5 m either side of its edge at (12.6, 20.6) and (7.6, 20.6), their tops at 32; the
        // bank at 33.1 but 31.9 at the vertices (12, 20) and (7, 20), inside each arch's footprint and at most 0.4 into the
        // triangles any of the nine samples reads.
        var end = new StructureEnds.End(new Vector2(10.1f, 20.6f), new Vector2(0f, 1f), 30f, 0);
        float Vertex(int x, int z) => (x == 12 || x == 7) && z == 20 ? 31.9f : 33.1f;
        Func<Vector2, float> finished = unknown ? _ => float.NaN : p => BuriedPieces.MeshHeight(p.x, p.y, Vertex);
        Vector2 dir = new(0f, 1f), side = new(-1f, 0f);
        List<List<StructurePiece>> sides = DvergrViaduct.BankParapet(end, finished, dir, side, 90f, BridgeLayout.YawDegrees(dir));
        Assert.Equal(2, sides.Count(laid => laid.Exists(p => p.Prefab == DvergrViaduct.Arch)));
        // And the owner's verdict, which the viaduct's omission asks of every piece, keeps each over the dip.
        if (!unknown)
            foreach (StructurePiece arch in sides.SelectMany(laid => laid).Where(p => p.Prefab == DvergrViaduct.Arch))
                Assert.False(BuriedPieces.Buried(arch.Prefab, arch.Position, arch.YawDegrees, Vertex), $"arch at {arch.Position}");
    }

    // ------------------------------------------------------------ where it runs on, and where not

    /// <summary>A bank beside the road level with it, a step under it within the ledge, or rising over it: the parapet runs
    /// on past the deck's end on both sides all the same (one side alone looks uneven), its cap and parapet piece, the
    /// deck's course carried on under the cap flush with the road (laid on a level or rising bank too: flush with the ground,
    /// the game grounds it), black marble under the parapet piece only over the step down (the courses stop sunk in the
    /// ground), none wholly in it. None at an end with stairs
    /// (their own parapet) or none set.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void ALevelOrRisingBankGetsItOnBothSides(float under)
    {
        var level = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 30f, 0);
        // The bank two steps down, whatever the ledge: black marble stands under the parapet piece only if its foot is over a drop.
        float drop = 2f * StairLayout.PieceRise;
        Func<int, int, float> ground = (x, z) => Mathf.Abs(z) <= 2 ? 30f : 30f - under * drop;
        List<(Vector2 outward, List<StructurePiece> pieces)> sides = StairRailing.LayBankParapet(level, 4f, ground);
        Assert.Equal(2, sides.Count);
        Assert.Equal(new[] { -1f, 1f }, sides.Select(s => Mathf.Round(s.outward.y)).OrderBy(y => y));
        foreach (var (outward, pieces) in sides)
        {
            Assert.Single(pieces, p => p.Prefab == StairRailing.ParapetPrefab);
            Assert.Single(pieces, p => p.Prefab == StairRailing.ParapetCapPrefab);
            // The deck's course carried on under the arch and cap, its top at the road's, sunk into the bank; black marble
            // under the parapet piece only where the bank stands under it (the step down). On a bank level with the road or
            // rising over it the course is flush with the road's ground, not wholly in it: laid.
            Assert.True(pieces.Exists(p => p.Prefab == StairRailing.ParapetHalfCoursePrefab && Mathf.Abs(p.Position.x + 1f) < 0.01f
                && Mathf.Abs(p.Position.y + DvergrViaduct.Extents(p.Prefab).y - level.Height) < 0.01f), Say(pieces));
            Assert.True(under > 0f == pieces.Exists(p => p.Prefab != StairRailing.ParapetPrefab && Mathf.Abs(p.Position.x + 3f) < 0.01f
                && p.Position.y < level.Height - 1f), Say(pieces));
            Assert.All(pieces, p => Assert.False(BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, ground), $"{p.Prefab} at {p.Position} in the ground"));
        }
        Assert.Empty(StairRailing.LayBankParapet(new StructureEnds.End(level.Edge, level.Up, 30f, 2), 4f, (x, z) => 20f));
        Assert.Empty(StairRailing.LayBankParapet(default, 4f, (x, z) => 20f));
    }

    /// <summary>A drop on one side only: both sides run on, black marble under the one over the drop down to the ground,
    /// under the level one only the course flush with the road.</summary>
    [Fact]
    public void BothSidesRunOnOverAOneSidedDrop()
    {
        var end = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 30f, 0);
        List<(Vector2 outward, List<StructurePiece> pieces)> sides = StairRailing.LayBankParapet(end, 4f, (x, z) => z <= 2 ? 30f : 26f);
        Assert.Equal(2, sides.Count);
        List<StructurePiece> over = sides.Single(s => s.outward.y > 0.5f).pieces, level = sides.Single(s => s.outward.y < -0.5f).pieces;
        Assert.All(over, p => Assert.True(p.Position.z > 2f - 1e-3f, $"{p.Prefab} at {p.Position}"));
        Assert.All(level, p => Assert.True(p.Position.z < -2f + 1e-3f, $"{p.Prefab} at {p.Position}"));
        Assert.Contains(over, p => p.Prefab == StairRailing.ParapetHalfCoursePrefab);
        Assert.DoesNotContain(level, p => p.Prefab == StairRailing.ParapetHalfCoursePrefab && p.Position.y < end.Height - 0.5f - 0.01f);
        Assert.Equal(over.Count(p => p.Prefab == StairRailing.ParapetPrefab), level.Count(p => p.Prefab == StairRailing.ParapetPrefab));
    }

    // ------------------------------------------------------------ on a viaduct

    /// <summary>A step valley whose banks fall away <see cref="Notch"/> beside the road's line just past the rims (|z| over
    /// 0.8, |x| 24 to 30, from the decks' end modules out), a sheer ridge 1.6 m wide left under the line: the black marble
    /// under the parapet hangs clear of the ground but at its foot (beside a real road its fill reaches the courses under
    /// the road's edge and grounds them; this is the support rule's case where it does not).</summary>
    private sealed class NotchedValley : WorldGenerator
    {
        private readonly StepValley _valley = new() { Depth = _ => 18f };
        public float Notch;
        public override float GetHeight(float wx, float wy)
        {
            float h = _valley.GetHeight(wx, wy);
            return Mathf.Abs(wy) > 0.8f && Mathf.Abs(wx) >= 24f && Mathf.Abs(wx) <= 30f ? Mathf.Min(h, _valley.Top - Notch) : h;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => _valley.Seed;
    }

    private RoadCrossing At(NotchedValley world)
    {
        DvergrViaduct.RaisedShare = 0f;
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        var (near, far) = DvergrViaduct.Ends(c, world);
        Assert.Equal((0, 0), (near.Steps, far.Steps));
        return c;
    }

    /// <summary>The banks falling 4 m beside the road at both ends: each end railed past its edge on both sides, whole and
    /// ruined, its arch paired with the deck's last (turned 180 from it), every piece held and none left out.</summary>
    [Fact]
    public void BothLevelEndsOverADropRunOnAndHold()
    {
        var world = new NotchedValley { Notch = 4f };
        RoadCrossing c = At(world);
        var (near, far) = DvergrViaduct.Ends(c, world);
        foreach (bool ruin in new[] { false, true })
        {
            List<StructurePiece> pieces = DvergrViaduct.Solve(c, world, world.GetSeed(), ruin, out DvergrViaduct.HoldReport report);
            Assert.DoesNotContain(report.DroppedPieces, p => p.Prefab != DvergrViaduct.Arch && IsBank(p));
            foreach (StructureEnds.End end in new[] { near, far })
            {
                Vector2 side = new(-end.Up.y, end.Up.x);
                foreach (int s in new[] { -1, 1 })
                {
                    Vector2 cap = end.Edge - end.Up * 1.5f + side * (s * 2.5f), arch = end.Edge + side * (s * 2.5f);
                    Assert.Contains(pieces, p => p.Prefab == StairRailing.ParapetCapPrefab && Flat(p, cap) && p.Kind == StructurePieceKind.ViaductParapet);
                    Assert.Contains(pieces, p => p.Prefab == StairRailing.ParapetPrefab && Flat(p, end.Edge - end.Up * 3f + side * (s * 2.5f)));
                    StructurePiece endArch = pieces.Single(p => p.Prefab == DvergrViaduct.Arch && Flat(p, arch));
                    StructurePiece lastArch = pieces.Single(p => p.Prefab == DvergrViaduct.Arch && Flat(p, arch + end.Up * 2f));
                    Assert.Equal(180f, Apart(endArch.YawDegrees, lastArch.YawDegrees), 1);
                }
            }
            foreach (ViaductSupport.Held h in ViaductSupport.Of(c, world, pieces).Where(h => IsBank(h.Piece)))
                Assert.True(h.Share >= DvergrViaduct.HoldMargin, FormattableString.Invariant(
                    $"{(ruin ? "ruined" : "whole")}: {h.Piece.Prefab} at {h.Piece.Position} holds {h.Share:F2} of its least"));
        }
    }

    /// <summary>The banks falling 40 m beside the road's ridge: black marble down to the ground would not hold its top (the
    /// support rule loses a quarter every 2 m course up), so each side is left out whole, said in the report, and nothing
    /// of it stands, no column in the drop; the deck's own arches stay.</summary>
    [Fact]
    public void ASideTheSupportCannotHoldIsLeftOutWhole()
    {
        var world = new NotchedValley { Notch = 40f };
        RoadCrossing c = At(world);
        var (near, far) = DvergrViaduct.Ends(c, world);
        List<StructurePiece> pieces = DvergrViaduct.Solve(c, world, world.GetSeed(), false, out DvergrViaduct.HoldReport report);
        Assert.True(!pieces.Any(p => p.Prefab == StairRailing.ParapetPrefab || p.Prefab == StairRailing.ParapetCapPrefab),
            string.Join("; ", ViaductSupport.Of(c, world, pieces).Where(h => IsBank(h.Piece) && h.Piece.Prefab != DvergrViaduct.Arch).Select(h => FormattableString.Invariant($"{h.Piece.Prefab} {h.Piece.Position} {h.Share:F2}"))));
        Assert.Contains(report.DroppedPieces, p => p.Prefab == StairRailing.ParapetPrefab);
        Assert.True(report.Dropped.TryGetValue(StairRailing.ParapetPrefab, out int bases) && bases == 4, Say(report.DroppedPieces));
        // Nothing of the bank parapet past either deck end stands.
        foreach (StructureEnds.End end in new[] { near, far })
        {
            var past = pieces.Where(p => p.Kind == StructurePieceKind.ViaductParapet
                && Vector2.Dot(new Vector2(p.Position.x, p.Position.z) - end.Edge, -end.Up) > 1.01f).ToList();
            Assert.True(past.Count == 0, $"edge {end.Edge}: {Say(past)}; dropped {Say(report.DroppedPieces)}");
        }
        Assert.Contains(pieces, p => p.Prefab == DvergrViaduct.Arch);
    }

    // ------------------------------------------------------------ a road turning at the end

    /// <summary>
    /// The road turning 90 degrees right off a level end over a drop both sides, on a 3 m radius from <paramref name="on"/>
    /// metres past the edge: on the inside of the turn only a piece that blocks more of the road's width than
    /// DvergrViaduct.ParapetRoadOverlap (one of the rock clearing's five slots across the road, DvergrViaduct.OnTheRoad) is
    /// left out, said as left off the road, with the black marble that held only it; the rest of that side kept, its arch,
    /// its cap (a slot at most) and the course under them flush with the road. Turning a metre out the parapet piece blocks
    /// three of the road's five slots and goes; two metres out, two, and goes; three metres out it clips one and stays. The
    /// outside keeps all its own; no course stands under nothing.
    /// </summary>
    [Theory]
    [InlineData(1f, true)]
    [InlineData(2f, true)]
    [InlineData(3f, false)]
    public void ARoadTurningAtTheEndTakesOnlyWhatIsOnIt(float on, bool baseTaken)
    {
        // The deck runs east from its edge at x 0; the road leaves west, then turns right (north, +z).
        var end = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 30f, 0);
        Vector2 dir = end.Up, side = new(-dir.y, dir.x);
        var road = new List<Vector2> { new(0f, 0f) };
        for (float x = -1f; x >= -on; x -= 1f) road.Add(new Vector2(x, 0f));
        for (int k = 1; k <= 9; k++)
        {
            float a = -Mathf.PI / 2f - k * (Mathf.PI / 2f) / 9f;
            road.Add(new Vector2(-on + 3f * Mathf.Cos(a), 3f + 3f * Mathf.Sin(a)));
        }
        for (int k = 1; k <= 8; k++) road.Add(new Vector2(-on - 3f, 3f + k));
        Func<Vector2, float> ground = p => BuriedPieces.MeshHeight(p.x, p.y, (x, z) => Mathf.Abs(z) <= 2 ? 30f : 26f);

        // Straight on, both sides run on, as many pieces each.
        List<List<StructurePiece>> straight = DvergrViaduct.BankParapet(end, ground, dir, side, 90f, BridgeLayout.YawDegrees(dir));
        Assert.Equal(2, straight.Count);
        int whole = straight[0].Count;

        // The road a little under the deck's surface on the turn, within a foot's landing.
        RoadSpatialGrid.CommitLevelled(road, road.Select(q => q.x < -0.5f ? 30f - 0.1f : 30f).ToList(), RoadConstants.DefaultRoadWidth);
        var off = new List<StructurePiece>();
        List<List<StructurePiece>> sides = DvergrViaduct.BankParapet(end, ground, dir, side, 90f, BridgeLayout.YawDegrees(dir), off);
        Assert.Equal(2, sides.Count);
        List<StructurePiece> outside = sides.Single(k => k[0].Position.z < 0f), inside = sides.Single(k => k[0].Position.z > 0f);
        Assert.Equal(whole, outside.Count);
        Assert.Contains(outside, p => p.Prefab == StairRailing.ParapetPrefab);
        Assert.Contains(inside, p => p.Prefab == DvergrViaduct.Arch);
        Assert.Contains(inside, p => p.Prefab == StairRailing.ParapetCapPrefab);
        Assert.Equal(baseTaken, !inside.Exists(p => p.Prefab == StairRailing.ParapetPrefab));
        // The course carried on under the arch and cap, its top flush with the road, under the turn's inside edge: kept (the
        // owner's base at (9054, 54.6, 126) in game).
        Assert.Contains(inside, p => p.Prefab == StairRailing.ParapetHalfCoursePrefab && Flat(p, end.Edge - end.Up + side * 2.5f)
            && Mathf.Abs(p.Position.y + DvergrViaduct.Extents(p.Prefab).y - end.Height) < 0.01f);
        Assert.Equal(baseTaken, off.Exists(p => p.Prefab == StairRailing.ParapetPrefab));
        Assert.Equal(whole, inside.Count + off.Count);
        Assert.All(off, p => Assert.True(p.Position.z > 2f - 1e-3f, $"{p.Prefab} at {p.Position}"));
        Assert.All(inside.Concat(outside), p => Assert.False(DvergrViaduct.OnTheRoad(p, dir, side), $"{p.Prefab} at {p.Position} on the road"));
        // Every course kept stands under a railing piece kept.
        foreach (StructurePiece c in inside.Where(p => p.Prefab == StairRailing.ParapetHalfCoursePrefab))
            Assert.Contains(inside, r => r.Prefab != StairRailing.ParapetHalfCoursePrefab && r.Position.y > c.Position.y
                && Mathf.Abs(r.Position.x - c.Position.x) < DvergrViaduct.Extents(r.Prefab).u + DvergrViaduct.Extents(c.Prefab).u - 1e-3f);
    }

    /// <summary>A bank parapet's piece is on the road over the road's headroom box (RockClearances.Bottom to Top: a lift over
    /// the surface to a player's headroom over that, 2.1 m), the box the rock clearing and a stair keep clear: a piece 1.95 m
    /// over the road is on it (it stood clear from 1.85), one wholly over 2.1 m is not.</summary>
    [Theory]
    [InlineData(1.95f, true)]
    [InlineData(1.0f, true)]
    [InlineData(2.2f, false)]
    [InlineData(-1.5f, false)]
    public void ABankParapetsRoadBandIsTheRoadsHeadroomBox(float above, bool onTheRoad)
    {
        var road = new List<Vector2>();
        for (float x = -10f; x <= 10f; x += 1f) road.Add(new Vector2(x, 0f));
        RoadSpatialGrid.CommitLevelled(road, road.Select(_ => 30f).ToList(), RoadConstants.DefaultRoadWidth);
        // An unknown prefab is a unit box (half 0.5): its underside is `above` over the surface.
        var piece = new StructurePiece { Prefab = "unit", Position = new Vector3(0f, 30f + above + 0.5f, 0f) };
        Assert.Equal(onTheRoad, DvergrViaduct.OnTheRoad(piece, new Vector2(1f, 0f), new Vector2(0f, 1f)));
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Every hand-built piece laid, and any other piece laid a course of black marble under one of them (their boxes
    /// overlapping across the ground, the course lower): the foundation carried on down as the staircases' walls are
    /// (StairSupport.Bury), where the hand-built one stopped higher.</summary>
    internal static void AnyMoreAreCoursesUnderTheHandBuilt(List<StructurePiece> laid, IEnumerable<(string prefab, Vector3 at)> handBuilt)
    {
        var built = handBuilt.ToList();
        foreach (StructurePiece p in laid)
        {
            if (built.Exists(h => h.prefab == p.Prefab && Vector3.Distance(h.at, p.Position) < 0.01f)) continue;
            var (pu, pv, _) = DvergrViaduct.Extents(p.Prefab);
            Assert.True((p.Prefab == StairRailing.ParapetCoursePrefab || p.Prefab == StairRailing.ParapetHalfCoursePrefab)
                && built.Exists(h => Mathf.Abs(h.at.x - p.Position.x) < pu + DvergrViaduct.Extents(h.prefab).u - 1e-3f
                    && Mathf.Abs(h.at.z - p.Position.z) < pv + DvergrViaduct.Extents(h.prefab).v - 1e-3f && h.at.y > p.Position.y),
                $"{p.Prefab} at {p.Position} is under no hand-built piece; laid: {Say(laid)}");
        }
    }

    /// <summary>A piece of a parapet run on past a deck's end: past the deck's arches' edge pieces, a cap, a parapet piece
    /// or the black marble under them.</summary>
    private static bool IsBank(StructurePiece p) =>
        p.Kind == StructurePieceKind.ViaductParapet && p.Prefab != DvergrViaduct.HeadLeft && p.Prefab != DvergrViaduct.HeadRight;

    private static bool Flat(StructurePiece p, Vector2 at) => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) < 0.01f;

    private static SupportGraph.Piece Box(string prefab, Vector3 c, bool grounded)
    {
        var (hu, hv, hy) = DvergrViaduct.Extents(prefab);
        var lo = new Vector3(c.x - hu, c.y - hy, c.z - hv);
        var hi = new Vector3(c.x + hu, c.y + hy, c.z + hv);
        if (prefab == DvergrViaduct.StepPrefab) { lo.y = c.y; hi.y = c.y + 1f; }
        return new SupportGraph.Piece(c, lo, hi, (BuriedPieces.DataOf(prefab, out BuriedPieces.Measured measured) ? measured.Material : null)!.Value, grounded);
    }

    private static bool OnTheGround(StructurePiece p)
    {
        var (hu, hv, hy) = DvergrViaduct.Extents(p.Prefab);
        float hi = float.MinValue;
        for (float u = -hu; u <= hu + 1e-3f; u += 0.5f)
            for (float v = -hv; v <= hv + 1e-3f; v += 0.5f)
                hi = Mathf.Max(hi, BuriedPieces.MeshHeight(p.Position.x + u, p.Position.z + v, EastEndGround));
        return hi >= p.Position.y - hy - SupportGraph.Reach;
    }

    private static float Apart(float a, float b) => Mathf.Abs(StairLayout.Wrap(a - b + 180f) - 180f);

    private static string Say(IEnumerable<StructurePiece> pieces) =>
        string.Join("; ", pieces.Select(p => FormattableString.Invariant($"{p.Prefab} ({p.Position.x:F3}, {p.Position.y:F3}, {p.Position.z:F3}) yaw {p.YawDegrees:F0}")));
}
