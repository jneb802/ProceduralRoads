using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The stone kit (BridgeLayout.SolveStone): the hand-built half bridge, mirrored, piece for piece; its 6 m unit repeated over
/// a longer deck and the spans that are not whole units; piers to the ground and the support limit; taller ends stepping the
/// railing down; ends met level or landed on a row; snap points; ruin; the biomes, the pick and the ship's way.
/// </summary>
public sealed class StoneBridgeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StoneBridgeTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>
    /// The hand-built half (pieces.txt, 89 stone pieces), in the bridge's frame: prefab, along the deck from the full
    /// bridge's centre (the half's deck end opposite its stairs, where it is mirrored), across it (to the right of the deck's
    /// heading), height, yaw relative to the deck's heading. Its railing stands about 0.03 m off the deck's edges, as
    /// placed.
    /// </summary>
    private static readonly string[] ReferenceHalf =
    {
        "stone_arch 1.00 -1.97 34.33 270.0",
        "stone_arch 1.00 -1.51 33.33 270.0",
        "stone_arch 1.00 -0.50 33.33 270.0",
        "stone_arch 1.00 0.49 33.33 270.0",
        "stone_arch 1.00 1.50 33.33 270.0",
        "stone_arch 1.00 2.02 34.33 270.0",
        "stone_arch 5.00 -1.98 34.33 90.0",
        "stone_arch 5.00 -1.50 33.33 90.0",
        "stone_arch 5.00 -0.51 33.33 90.0",
        "stone_arch 5.00 0.49 33.33 90.0",
        "stone_arch 5.00 1.50 33.33 90.0",
        "stone_arch 5.00 2.02 34.33 90.0",
        "stone_arch 6.99 0.49 33.33 270.0",
        "stone_arch 6.99 2.02 34.33 270.0",
        "stone_arch 7.00 -1.97 34.33 270.0",
        "stone_arch 7.00 -1.51 33.33 270.0",
        "stone_arch 7.00 -0.50 33.33 270.0",
        "stone_arch 7.00 1.49 33.33 270.0",
        "stone_floor_2x2 1.00 -1.00 34.33 90.0",
        "stone_floor_2x2 1.00 1.00 34.33 90.0",
        "stone_floor_2x2 3.00 1.00 34.33 90.0",
        "stone_floor_2x2 3.01 -1.00 34.33 90.0",
        "stone_floor_2x2 5.00 -1.00 34.33 90.0",
        "stone_floor_2x2 5.00 0.99 34.33 90.0",
        "stone_floor_2x2 6.99 -1.00 34.33 270.0",
        "stone_floor_2x2 7.00 1.00 34.33 270.0",
        "stone_floor_2x2 9.00 -1.00 34.33 270.0",
        "stone_floor_2x2 9.00 0.99 34.33 270.0",
        "stone_pillar 10.49 -1.51 32.83 0.0",
        "stone_pillar 10.50 -0.51 32.83 0.0",
        "stone_pillar 10.50 0.49 32.83 0.0",
        "stone_pillar 10.50 1.50 32.83 0.0",
        "stone_pillar 11.50 -1.51 32.83 0.0",
        "stone_pillar 11.50 -0.50 32.83 0.0",
        "stone_pillar 11.51 0.49 32.83 0.0",
        "stone_stair 11.00 -1.00 33.83 0.0",
        "stone_stair 11.00 1.00 33.83 0.0",
        "stone_wall_2x1 0.00 -1.97 35.33 90.0",
        "stone_wall_2x1 0.00 2.03 35.33 90.0",
        "stone_wall_2x1 2.00 -1.98 35.33 90.0",
        "stone_wall_2x1 2.00 2.02 35.33 90.0",
        "stone_wall_2x1 2.50 -1.00 28.33 180.0",
        "stone_wall_2x1 2.50 -1.00 30.33 0.0",
        "stone_wall_2x1 2.50 -1.00 32.33 180.0",
        "stone_wall_2x1 2.50 1.00 29.33 0.0",
        "stone_wall_2x1 2.50 1.00 31.33 180.0",
        "stone_wall_2x1 2.50 1.00 33.33 0.0",
        "stone_wall_2x1 3.00 -1.97 34.33 90.0",
        "stone_wall_2x1 3.00 -1.51 29.33 90.0",
        "stone_wall_2x1 3.00 -1.51 31.33 90.0",
        "stone_wall_2x1 3.00 -1.51 33.33 270.0",
        "stone_wall_2x1 3.00 -0.50 29.33 90.0",
        "stone_wall_2x1 3.00 -0.50 31.33 90.0",
        "stone_wall_2x1 3.00 -0.50 33.33 270.0",
        "stone_wall_2x1 3.00 1.49 28.33 270.0",
        "stone_wall_2x1 3.00 1.49 30.33 90.0",
        "stone_wall_2x1 3.00 1.49 32.33 270.0",
        "stone_wall_2x1 3.00 2.03 34.33 90.0",
        "stone_wall_2x1 3.01 0.49 28.33 270.0",
        "stone_wall_2x1 3.01 0.49 30.33 90.0",
        "stone_wall_2x1 3.01 0.49 32.33 270.0",
        "stone_wall_2x1 3.50 -1.00 30.33 0.0",
        "stone_wall_2x1 3.50 -1.00 32.33 180.0",
        "stone_wall_2x1 3.50 0.99 29.33 0.0",
        "stone_wall_2x1 3.50 0.99 31.33 180.0",
        "stone_wall_2x1 3.50 0.99 33.33 0.0",
        "stone_wall_2x1 4.00 -1.97 35.33 90.0",
        "stone_wall_2x1 4.00 2.02 35.33 90.0",
        "stone_wall_2x1 6.00 -1.98 35.33 90.0",
        "stone_wall_2x1 6.00 2.02 35.33 90.0",
        "stone_wall_2x1 8.00 -1.98 35.33 90.0",
        "stone_wall_2x1 8.00 2.02 35.33 90.0",
        "stone_wall_2x1 8.50 -1.01 32.33 180.0",
        "stone_wall_2x1 8.50 1.00 31.33 180.0",
        "stone_wall_2x1 8.50 1.00 33.33 0.0",
        "stone_wall_2x1 9.00 -1.98 34.33 90.0",
        "stone_wall_2x1 9.00 -1.51 31.33 90.0",
        "stone_wall_2x1 9.00 -1.51 33.33 270.0",
        "stone_wall_2x1 9.00 -0.51 31.33 90.0",
        "stone_wall_2x1 9.00 -0.51 33.33 270.0",
        "stone_wall_2x1 9.00 0.49 32.33 270.0",
        "stone_wall_2x1 9.00 1.50 32.33 270.0",
        "stone_wall_2x1 9.00 2.02 34.33 90.0",
        "stone_wall_2x1 9.50 -1.00 32.33 180.0",
        "stone_wall_2x1 9.50 1.00 33.33 0.0",
        "stone_wall_2x1 10.00 -1.97 35.33 90.0",
        "stone_wall_2x1 10.01 2.02 35.33 90.0",
        "stone_wall_2x1 11.00 -1.98 34.33 90.0",
        "stone_wall_2x1 11.00 2.02 34.33 90.0",
    };

    /// <summary>The three pieces of its courses the hand build left out and the kit lays: a pier's and the bank pier's
    /// lowest course each one block short, the footing one pillar short.</summary>
    private static readonly string[] HandLeftOut =
    {
        "stone_wall_2x1 3.50 -1.00 28.33 0.0",
        "stone_wall_2x1 9.50 1.00 31.33 0.0",
        "stone_pillar 11.50 1.50 32.83 0.0",
    };

    // ------------------------------------------------------------- the worlds

    /// <summary>
    /// The reference's ground, by the distance along the deck from its centre, flat across it: the bed at 28 m under the
    /// arches and the inner piers (six courses), 31 m under the bank piers (three courses), rising to 33.7 m by the stairs,
    /// whose one step lands there. A Black Forest world, no river. <see cref="Bank"/>: past the deck's ends, its own ground
    /// instead (taller ends); <see cref="Drop"/> lowers the bed under the arches and the inner piers.
    /// </summary>
    private sealed class ReferenceWorld : WorldGenerator
    {
        public Func<float, float>? Bank;
        public float Drop;
        public Heightmap.Biome Biome = Heightmap.Biome.BlackForest;
        public int Seed = 76;

        public override float GetHeight(float wx, float wy)
        {
            float a = Mathf.Abs(wx);
            if (a > 10f && Bank != null) return Bank(a);
            if (a <= 7f) return 28f - Drop;
            if (a < 8f) return Mathf.Lerp(28f - Drop, 31f, a - 7f);
            if (a <= 10f) return 31f;
            return Mathf.Lerp(31f, 33.7f, (a - 10f) * 2f);
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => Seed;
    }

    /// <summary>The reference crossing: 20 m along +x, deck centre at the origin, the walking surface at 34.83 m as built
    /// (the water put 0.5 m under it, the layout taking its deck from the banks or the water).</summary>
    private (RoadCrossing c, ReferenceWorld w) Reference()
    {
        var w = new ReferenceWorld();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-10f, 0f), new Vector2(10f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = Deck - BridgeLayout.DeckFreeboard;
        return (c, w);
    }

    private const float Deck = 34.83f;

    /// <summary>A valley <see cref="Half"/> each side of the origin along x, its bed at <see cref="Bed"/>, the banks beyond at
    /// <see cref="Bank"/>; with <see cref="River"/>, a river over the whole bed.</summary>
    private sealed class ValleyWorld : WorldGenerator
    {
        public float Half = 16f, Bed = 28f, Bank = 33.7f;
        public bool River;
        public Heightmap.Biome Biome = Heightmap.Biome.BlackForest;
        public int Seed = 76;

        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < Half ? Bed : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => Seed;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = River && Mathf.Abs(wx) < Half ? 1f : 0f;
            width = 2f * Half;
        }
    }

    /// <summary>A bridge over <paramref name="w"/>'s valley bank to bank, its deck at 34.83 m.</summary>
    private RoadCrossing Valley(ValleyWorld w)
    {
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-w.Half, 0f), new Vector2(w.Half, 0f), w.Bed, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = Deck - BridgeLayout.DeckFreeboard;
        return c;
    }

    /// <summary>A shallow ford 12 m between banks at 30 m, its bed at 29 m: a span's deck 1 m over the higher bank, at 31 m.</summary>
    private sealed class FordWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 6f ? 29f : 30f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override int GetSeed() => 76;
    }

    private (RoadCrossing c, FordWorld w) FordSpan()
    {
        var w = new FordWorld();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 29f, new Vector2(0f, 0f), 0f, CrossingKind.Ford, FordStyle.Span);
        c.WaterLevel = 29.5f;
        return (c, w);
    }

    /// <summary>Banks falling away from the deck's ends a little faster than a step (0.58 in 1), to 4.5 m under the deck:
    /// a run of several steps.</summary>
    private static float TallBank(float a) => Mathf.Max(Deck - 0.58f * (a - 10f), Deck - 4.5f);

    // --------------------------------------------------------------- the frame

    private readonly struct FramePiece
    {
        public readonly string Prefab;
        public readonly Vector3 At;   // along, across, height
        public readonly float Yaw;
        public FramePiece(string prefab, Vector3 at, float yaw) { Prefab = prefab; At = at; Yaw = yaw; }
        public override string ToString() => FormattableString.Invariant($"{Prefab} ({At.x:F2}, {At.y:F2}, {At.z:F2}) yaw {Yaw:F0}");
    }

    private static FramePiece Parse(string line)
    {
        string[] f = line.Split(' ');
        float F(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
        return new FramePiece(f[0], new Vector3(F(1), F(2), F(3)), F(4));
    }

    /// <summary>A planned piece in the crossing's frame (deck heading +x: along = x, across to its right = -z).</summary>
    private static FramePiece Frame(StructurePiece p) =>
        new(p.Prefab, new Vector3(p.Position.x, -p.Position.z, p.Position.y), p.YawDegrees - 90f);

    /// <summary>A piece mirrored about the deck's middle (along 0): turned half round.</summary>
    private static FramePiece Mirror(FramePiece p) => new(p.Prefab, new Vector3(-p.At.x, p.At.y, p.At.z), p.Yaw + 180f);

    /// <summary>Yaws a piece cannot be told apart at: a floor or a pillar turned a quarter, a block half round.</summary>
    private static bool SameYaw(string prefab, float a, float b)
    {
        float period = prefab is BridgeLayout.StoneFloorPrefab or BridgeLayout.StonePillarPrefab ? 90f
            : prefab == BridgeLayout.StonePierBlock ? 180f : 360f;
        float d = ((a - b) % period + period) % period;
        return Mathf.Min(d, period - d) < 0.5f;
    }

    /// <summary>Each expected piece matched to one planned piece of its prefab and yaw, nearest first; returns the largest
    /// distance, and fails on any piece left over on either side.</summary>
    private static float Match(IEnumerable<FramePiece> expected, List<StructurePiece> plan, float tolerance)
    {
        var left = plan.Select(Frame).ToList();
        var unmatched = new List<string>();
        float worst = 0f;
        foreach (FramePiece e in expected)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i].Prefab != e.Prefab || !SameYaw(e.Prefab, left[i].Yaw, e.Yaw)) continue;
                float d = Vector3.Distance(left[i].At, e.At);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0 || bestD > tolerance) { unmatched.Add("missing " + e + (best >= 0 ? $", nearest {left[best]}" : "")); continue; }
            worst = Mathf.Max(worst, bestD);
            left.RemoveAt(best);
        }
        unmatched.AddRange(left.Select(p => "extra " + p));
        Assert.True(unmatched.Count == 0, string.Join("\n", unmatched));
        return worst;
    }

    private static string Key(StructurePiece p) => FormattableString.Invariant($"{p.Prefab} {p.Position.x:F2} {p.Position.y:F2} {p.Position.z:F2}");

    /// <summary>Every piece on another's snap point (within 2 cm), as a player's hammer puts it.</summary>
    private static void AssertEverySnaps(List<StructurePiece> plan, string what)
    {
        var snaps = plan.Select(BridgeLayout.SnapPoints).ToList();
        var loose = new List<string>();
        for (int i = 0; i < plan.Count; i++)
        {
            Assert.NotEmpty(snaps[i]);
            bool met = false;
            for (int j = 0; j < plan.Count && !met; j++)
                if (j != i)
                    met = snaps[i].Any(a => snaps[j].Any(b => Vector3.Distance(a, b) < 0.02f));
            if (!met) loose.Add(Frame(plan[i]).ToString());
        }
        Assert.True(loose.Count == 0, $"{what}: pieces on no neighbour's snap point:\n" + string.Join("\n", loose));
    }

    private static bool IsPierBlock(StructurePiece p) => p.Prefab == BridgeLayout.StonePierBlock && p.Position.y < Deck - 1f;
    private static bool IsUnderArch(StructurePiece p) => p.Prefab == BridgeLayout.StoneArchPrefab && p.Position.y < Deck - 1f;
    /// <summary>A deck end's strip: a block laid across a lane, level with the floors.</summary>
    private static bool IsStrip(StructurePiece p) => IsStrip(p, Deck);
    private static bool IsStrip(StructurePiece p, float deck) =>
        p.Prefab == BridgeLayout.StonePierBlock && Mathf.Abs(p.Position.y - (deck - 0.5f)) < 0.01f && Mathf.Abs(Mathf.Abs(p.Position.z) - 1f) < 0.01f;

    // ---------------------------------------------------------- the reference

    /// <summary>
    /// The hand-built half and its mirror (one top course block over the middle, shared), with the three blocks the hand
    /// build left out of its lowest courses, is the stone kit on the reference's ground, piece for piece but for the floors:
    /// ten bays (a pier, two arches, a pier, ...), the walking surface the network's deck, one step down at each end on its
    /// footing. The hand build laid its floors on the bays; the kit lays them a metre along, on the joints between bays
    /// (<see cref="BridgeLayout.StoneFloorOffset"/>), so a floor can rest half on each of two crowns over a 6 m opening:
    /// nine floors a lane where the hand build had ten, and at each end a 1 m strip, a block laid across each lane.
    /// </summary>
    [Fact]
    public void TheStoneKitIsTheHandBuiltHalfMirrored()
    {
        var (c, w) = Reference();
        var half = ReferenceHalf.Concat(HandLeftOut).Select(Parse).ToList();
        Assert.Equal(92, half.Count);
        var whole = new List<FramePiece>(half);
        foreach (FramePiece p in half.Select(Mirror))
            if (!whole.Any(q => q.Prefab == p.Prefab && Vector3.Distance(q.At, p.At) < 0.1f)) whole.Add(p);
        Assert.Equal(2 * 92 - 2, whole.Count);
        Assert.Equal(20, whole.Count(p => p.Prefab == BridgeLayout.StoneFloorPrefab));

        Assert.True(BridgeLayout.StoneFits(c, w));
        var plan = BridgeLayout.SolveStoneComplete(c, w, 1);
        float worst = Match(whole.Where(p => p.Prefab != BridgeLayout.StoneFloorPrefab),
            plan.Where(p => p.Prefab != BridgeLayout.StoneFloorPrefab && !IsStrip(p)).ToList(), 0.05f);
        Assert.True(worst < 0.04f, $"worst piece {worst:F3} m off");
        // The deck: floors on the joints (along -8 .. 8 m from the middle), a strip on each end pier (along +-9.5 m).
        var deck = new List<FramePiece>();
        for (int i = 1; i <= 9; i++)
            foreach (float across in new[] { -1f, 1f })
                deck.Add(new FramePiece(BridgeLayout.StoneFloorPrefab, new Vector3(-10f + 2f * i, across, Deck - 0.5f), 0f));
        foreach (float along in new[] { -9.5f, 9.5f })
            foreach (float across in new[] { -1f, 1f })
                deck.Add(new FramePiece(BridgeLayout.StonePierBlock, new Vector3(along, across, Deck - 0.5f), 0f));
        Match(deck, plan.Where(p => p.Prefab == BridgeLayout.StoneFloorPrefab || IsStrip(p)).ToList(), 0.01f);

        var P = BridgeLayout.StoneBay.Pier;
        var N = BridgeLayout.StoneBay.ArchNear;
        var F = BridgeLayout.StoneBay.ArchFar;
        Assert.Equal(new[] { P, N, F, P, N, F, P, N, F, P }, BridgeLayout.StoneBays(10));
        Assert.Equal(Deck, BridgeStructures.DeckHeight(c, w), 3);
        Assert.All(plan.Where(p => p.Prefab == BridgeLayout.StoneFloorPrefab), p => Assert.Equal(Deck - 0.5f, p.Position.y, 3));
        var (near, far) = BridgeStructures.Ends(c, w);
        Assert.Equal((1, 1), (near.Steps, far.Steps));
    }

    // ------------------------------------------------------------ snap points

    /// <summary>
    /// Every piece, complete and ruined, on the reference, with taller ends, with piers at the support limit, with an end
    /// met level and one landed on a row, over a long valley and as a ford's span, has a snap point on a snap point of
    /// another piece: floors on arches and piers, arches on piers and on each other's crowns, the courses on each other,
    /// the railing along itself, steps on their footing, pillars stacked.
    /// </summary>
    [Theory]
    [InlineData("reference")]
    [InlineData("tall ends")]
    [InlineData("high deck")]
    [InlineData("met level, landed")]
    [InlineData("long")]
    [InlineData("ford span")]
    public void EveryPieceSnapsToANeighbour(string ground)
    {
        RoadCrossing c;
        WorldGenerator w;
        if (ground == "long") { var v = new ValleyWorld { Half = 23f }; c = Valley(v); w = v; }
        else if (ground == "ford span") (c, w) = FordSpan();
        else
        {
            var (rc, rw) = Reference();
            if (ground != "reference" && ground != "high deck") rw.Bank = TallBank;
            if (ground == "high deck") rw.Drop = 3f;
            if (ground == "met level, landed") MetEnds.Set(rc, rw, fromLevel: true, toRow: 2);
            c = rc;
            w = rw;
        }
        var complete = BridgeLayout.SolveStoneComplete(c, w, 1);
        Assert.NotEmpty(complete);
        AssertEverySnaps(complete, ground + ", complete");
        for (int seed = 1; seed <= 40; seed++)
            AssertEverySnaps(BridgeLayout.SolveStone(c, w, seed), $"{ground}, seed {seed}");
    }

    // ------------------------------------------------------------ the unit

    /// <summary>
    /// The bays for every deck from 2 to 80 m: symmetrical about the middle, a pier bay at each end, every opening its two
    /// arch bays (near half, far half) between piers, 4 m, or 6 m with an open bay between them; the piers between openings
    /// one bay but for at most a middle one of two, the end piers alike. Decks of 3 k + 1 bays (8, 14, 20 ... m) are whole
    /// 4 m units; most others are made up in 6 m openings with no pier widened: 10, 18, 22, 24 m. None for 2, 4 and 6 m.
    /// </summary>
    [Fact]
    public void EveryDeckLengthIsLaidSymmetricallyInWholeArches()
    {
        var P = BridgeLayout.StoneBay.Pier;
        var N = BridgeLayout.StoneBay.ArchNear;
        var F = BridgeLayout.StoneBay.ArchFar;
        var O = BridgeLayout.StoneBay.Open;
        for (int bays = 1; bays <= 40; bays++)
        {
            var layout = BridgeLayout.StoneBays(bays);
            if (bays is 1 or 2 or 3) { Assert.Null(layout); continue; }
            Assert.NotNull(layout);
            Assert.Equal(bays, layout!.Length);
            BridgeLayout.StoneBay Mirrored(BridgeLayout.StoneBay b) => b == N ? F : b == F ? N : b;
            for (int j = 0; j < bays; j++)
            {
                Assert.Equal(layout[j], Mirrored(layout[bays - 1 - j]));
                if (layout[j] == N)
                {
                    Assert.Equal(P, layout[j - 1]);
                    int k = layout[j + 1] == O ? j + 2 : j + 1;
                    Assert.Equal(F, layout[k]);
                    Assert.Equal(P, layout[k + 1]);
                }
                if (layout[j] == O) Assert.Equal((N, F), (layout[j - 1], layout[j + 1]));
            }
            // Runs of pier bays: the ends alike, between openings one bay, or two at the middle.
            var runs = new List<int>();
            for (int j = 0; j < bays; j++)
            {
                if (layout[j] != P) continue;
                int n = 0;
                while (j < bays && layout[j] == P) { n++; j++; }
                runs.Add(n);
            }
            Assert.True(runs.Count >= 2, $"{bays} bays: no opening");
            Assert.Equal(runs[0], runs[runs.Count - 1]);
            var inner = runs.Skip(1).Take(runs.Count - 2).ToList();
            Assert.True(inner.Count(n => n == 2) <= 1 && inner.All(n => n is 1 or 2), $"{bays} bays: inner piers {string.Join(",", inner)}");
            if (bays % 3 == 1) Assert.True(runs.All(n => n == 1) && !layout.Contains(O), $"{bays} bays: whole units with a wide pier or a 6 m opening");
        }
        Assert.Equal(new[] { P, N, O, F, P }, BridgeLayout.StoneBays(5));
        Assert.Equal(new[] { P, N, F, P, N, O, F, P, N, F, P }, BridgeLayout.StoneBays(11));
        Assert.Equal(new[] { P, P, N, F, P, P }, BridgeLayout.StoneBays(6));
        foreach (int bays in new[] { 5, 9, 11, 12 })
            Assert.True(BridgeLayout.StoneBays(bays)!.Count(b => b == P) == BridgeLayout.StoneBays(bays)!.Count(b => b == N) + 1, $"{bays} bays: a pier widened");
    }

    /// <summary>
    /// A 48 m deck (24 bays: seven openings, the two either side of the middle one 6 m) repeats the reference's unit: a pier
    /// between openings, two arches in each, their tall ends on the piers, an open bay between the long openings' arches,
    /// each pier's courses down to the bed; over an open bay no railing beside the deck, the floors and the top course
    /// carried over it on the two crowns either side.
    /// </summary>
    [Fact]
    public void ALongerSpanRepeatsTheUnitWithItsLongOpeningsNearTheMiddle()
    {
        var w = new ValleyWorld { Half = 24f };
        var c = Valley(w);
        Assert.True(BridgeLayout.StoneFits(c, w));
        var plan = BridgeLayout.SolveStoneComplete(c, w, 1).Select(p => (p, f: Frame(p))).ToList();
        int Bay(FramePiece f) => Mathf.FloorToInt((f.At.x + 24f) / 2f);
        var pierBays = plan.Where(x => IsPierBlock(x.p)).Select(x => Bay(x.f)).Distinct().OrderBy(b => b).ToArray();
        Assert.Equal(new[] { 0, 3, 6, 10, 13, 17, 20, 23 }, pierBays);
        foreach (var (near, far) in new[] { (1, 2), (4, 5), (7, 9), (11, 12), (14, 16), (18, 19), (21, 22) })
        {
            // The near half's tall end on the pier before it (yaw +90: its local x toward the near end), the far half's on
            // the pier after it.
            Assert.Equal(4, plan.Count(x => IsUnderArch(x.p) && Bay(x.f) == near && SameYaw(x.p.Prefab, x.f.Yaw, 90f)));
            Assert.Equal(4, plan.Count(x => IsUnderArch(x.p) && Bay(x.f) == far && SameYaw(x.p.Prefab, x.f.Yaw, 270f)));
        }
        Assert.Equal(7 * 2 * 4, plan.Count(x => IsUnderArch(x.p)));
        foreach (int open in new[] { 8, 15 })
        {
            Assert.DoesNotContain(plan, x => Bay(x.f) == open && x.p.Prefab != BridgeLayout.StoneFloorPrefab && x.f.At.z < Deck);   // nothing under the deck, no railing beside it
            float a = -24f + 2f * open;
            // The two floors of each lane either side of its middle, each centred on a crown; the top course the same.
            foreach (float along in new[] { a, a + 2f })
            {
                Assert.Equal(2, plan.Count(x => x.p.Prefab == BridgeLayout.StoneFloorPrefab && Mathf.Abs(x.f.At.x - along) < 0.01f));
                Assert.Equal(2, plan.Count(x => x.p.Prefab == BridgeLayout.StonePierBlock && x.f.At.z > Deck && Mathf.Abs(x.f.At.x - along) < 0.01f));
            }
        }
        // Every pier's courses: the top one level with the arches, the lowest with its foot in the bed (28 m): six.
        foreach (int bay in pierBays)
        {
            var heights = plan.Where(x => IsPierBlock(x.p) && Bay(x.f) == bay).Select(x => x.f.At.z).Distinct().OrderByDescending(z => z).ToList();
            Assert.Equal(Enumerable.Range(0, 6).Select(k => Deck - 1.5f - k), heights, new FloatNear());
        }
    }

    private sealed class FloatNear : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => Mathf.Abs(a - b) < 0.01f;
        public int GetHashCode(float v) => 0;
    }

    // ------------------------------------------------------ the support limit

    /// <summary>
    /// The derived limits (BridgeSupport): nine pier courses, seven footing pillars. With the bed 3 m lower the inner piers
    /// stand nine courses, each on the one under it to a foot in the ground, and the kit fits; a metre lower they would
    /// need ten, and the stone kit does not fit: the crossing draws a railed kit, whatever the seed.
    /// </summary>
    [Fact]
    public void PiersStackToTheGroundAndPastTheSupportLimitTheKitDoesNotFit()
    {
        Assert.Equal(9, BridgeSupport.StoneMaxPierCourses());
        // A floor half on a crown over a 6 m opening keeps more of the arch than the railing over a crown does.
        Assert.True(BridgeSupport.StoneOnCrownFactor > BridgeSupport.StoneOnArchFactor * BridgeSupport.StoneRailOnRailFactor);
        Assert.Equal(7, BridgeSupport.StoneMaxFootingPillars());
        var (c, w) = Reference();
        w.Drop = 3f;
        Assert.True(BridgeLayout.StoneFits(c, w));
        var plan = BridgeLayout.SolveStoneComplete(c, w, 1).Where(IsPierBlock).Select(Frame).ToList();
        foreach (float along in new[] { -3f, 3f })
        {
            var pier = plan.Where(p => Mathf.Abs(p.At.x - along) <= 1f).ToList();
            var heights = pier.Select(p => p.At.z).Distinct().OrderByDescending(z => z).ToList();
            Assert.Equal(9, heights.Count);
            Assert.Equal(36, pier.Count);
            Assert.Equal(Deck - 1.5f, heights[0], 3);
            float foot = heights[heights.Count - 1] - 0.5f;
            Assert.True(foot <= w.GetHeight(along, 0f) + BuriedPieces.Growth && foot + 1f > w.GetHeight(along, 0f));
        }
        bool drawn = false;
        for (int seed = 1; seed <= 30 && !drawn; seed++) { w.Seed = seed; drawn = BridgeStructures.KitFor(c, w) == BridgeStructures.Kit.Stone; }
        Assert.True(drawn, "the stone kit is never drawn where it fits");

        w.Drop = 4f;
        Assert.False(BridgeLayout.StoneFits(c, w));
        for (int seed = 1; seed <= 60; seed++)
        {
            w.Seed = seed;
            Assert.NotEqual(BridgeStructures.Kit.Stone, BridgeStructures.KitFor(c, w));
        }
    }

    /// <summary>
    /// A bridge is built in the kit its plan was solved in: the pieces carry it, and the first build saves it, though the
    /// ground has changed since so that the kits that fit (and the pick among them) would now give another.
    /// </summary>
    [Fact]
    public void TheKitSavedAtTheFirstBuildIsTheOneThePiecesWereSolvedIn()
    {
        var (c, w) = Reference();
        w.Drop = 3f;
        BridgeStructures.DecideKind(c, w);
        for (w.Seed = 1; BridgeStructures.KitFor(c, w) != BridgeStructures.Kit.Stone; w.Seed++) Assert.True(w.Seed < 60);
        List<StructurePiece> pieces = BridgeStructures.Solve(c, w, w.Seed);
        Assert.All(pieces, p => Assert.Equal(BridgeStructures.Kit.Stone, p.Kit));
        Assert.All(pieces, p => Assert.Same(c, p.Crossing));
        w.Drop = 4f;
        Assert.NotEqual(BridgeStructures.Kit.Stone, BridgeStructures.KitFor(c, w));
        Assert.True(BridgeStructures.ChooseKits(pieces, w, new[] { c }));
        Assert.Equal(BridgeStructures.Kit.Stone, c.Kit);
    }

    /// <summary>
    /// A stone bridge chosen where it fitted never stacks a pier past the support limit when the ground under it is lowered
    /// later (a road cut under a pier): the courses past the limit, at the pier's foot, are left out.
    /// </summary>
    [Fact]
    public void AStoneBridgeKeepsItsPiersWithinTheSupportLimitWhenTheGroundIsLoweredLater()
    {
        var (c, w) = Reference();
        c.Kit = BridgeStructures.Kit.Stone;
        w.Drop = 6f;
        Assert.False(BridgeLayout.StoneFits(c, w));
        foreach (bool complete in new[] { true, false })
        {
            var plan = (complete ? BridgeLayout.SolveStoneComplete(c, w, 1) : BridgeLayout.SolveStone(c, w, 1)).Where(IsPierBlock).Select(Frame).ToList();
            foreach (float along in new[] { -3f, 3f })
            {
                var heights = plan.Where(p => Mathf.Abs(p.At.x - along) <= 1f).Select(p => p.At.z).Distinct().ToList();
                Assert.NotEmpty(heights);
                Assert.True(heights.Count <= BridgeSupport.StoneMaxPierCourses(), $"{heights.Count} courses");
                if (complete) Assert.Equal(BridgeSupport.StoneMaxPierCourses(), heights.Count);
            }
        }
    }

    // ------------------------------------------------------------- the ends

    /// <summary>
    /// Taller ends: every step two stone_stairs, on a footing of eight pillars stacked from its foot to the ground, a railing
    /// block beside it each side from its foot to its top, each a metre lower and 2 m further out than the one before; the
    /// top course's last block half over the first step.
    /// </summary>
    [Fact]
    public void ATallerEndStepsItsRailingDownBesideItsStepsOnTheirFooting()
    {
        var (c, w) = Reference();
        w.Bank = TallBank;
        var (near, far) = BridgeStructures.Ends(c, w);
        Assert.True(near.Steps >= 3 && far.Steps == near.Steps, $"{near.Steps}/{far.Steps} steps");
        var plan = BridgeLayout.SolveStoneComplete(c, w, 1).Select(Frame).ToList();
        foreach (float end in new[] { -1f, 1f })
        {
            for (int k = 0; k < far.Steps; k++)
            {
                float along = end * (11f + 2f * k), foot = Deck - 1f - k;
                foreach (float across in new[] { -1f, 1f })
                    Assert.Contains(plan, p => p.Prefab == BridgeLayout.StoneStairPrefab && Vector3.Distance(p.At, new Vector3(along, across, foot)) < 0.02f);
                foreach (float across in new[] { -2f, 2f })
                    Assert.Contains(plan, p => p.Prefab == BridgeLayout.StonePierBlock && Vector3.Distance(p.At, new Vector3(along, across, foot + 0.5f)) < 0.02f);
                foreach (float da in new[] { -0.5f, 0.5f })
                    foreach (float across in new[] { -1.5f, -0.5f, 0.5f, 1.5f })
                    {
                        var column = plan.Where(p => p.Prefab == BridgeLayout.StonePillarPrefab
                            && Mathf.Abs(p.At.x - (along + da)) < 0.02f && Mathf.Abs(p.At.y - across) < 0.02f).OrderByDescending(p => p.At.z).ToList();
                        // None where its top pillar would lie wholly in the bank (the bank here steeper than the run).
                        if (column.Count == 0)
                        {
                            Assert.True(BuriedPieces.Buried(BridgeLayout.StonePillarPrefab, new Vector3(along + da, foot - 1f, -across), 90f,
                                (i, z) => w.GetHeight(i, z)), $"no footing at ({along + da}, {across})");
                            continue;
                        }
                        for (int m = 0; m < column.Count; m++)
                            Assert.Equal(foot - 1f - 2f * m, column[m].At.z, 2);
                        Assert.True(column[column.Count - 1].At.z - 1f <= TallBank(Mathf.Abs(along + da)) + BuriedPieces.Growth);
                    }
            }
            Assert.Equal(2 * far.Steps, plan.Count(p => p.Prefab == BridgeLayout.StoneStairPrefab && p.At.x * end > 10f));
            Assert.Equal(2 * far.Steps, plan.Count(p => p.Prefab == BridgeLayout.StonePierBlock && p.At.x * end > 10.5f));
            foreach (float across in new[] { -2f, 2f })
                Assert.Contains(plan, p => p.Prefab == BridgeLayout.StonePierBlock && Vector3.Distance(p.At, new Vector3(end * 10f, across, Deck + 0.5f)) < 0.02f);
        }
    }

    /// <summary>
    /// The taller ends met by the road: the near end level, the far end landed on its second row. The near end lays nothing
    /// past the deck: no step, footing or railing, the top course stopping a metre short of the end over the railing's
    /// last block. The far end lays two steps on their footing, the railing stepping down beside both, nothing past their
    /// foot.
    /// </summary>
    [Fact]
    public void AnEndMetLevelLaysNoStairsAndOneLandedOnARowLaysThatMany()
    {
        var (c, w) = Reference();
        w.Bank = TallBank;
        MetEnds.Set(c, w, fromLevel: true, toRow: 2);
        var (near, far) = BridgeStructures.Ends(c, w);
        Assert.Equal((0, 2), (near.Steps, far.Steps));
        var plan = BridgeLayout.SolveStoneComplete(c, w, 1).Select(Frame).ToList();
        Assert.DoesNotContain(plan, p => p.At.x < -9.51f);
        Assert.DoesNotContain(plan, p => p.Prefab != BridgeLayout.StonePierBlock && p.Prefab != BridgeLayout.StoneFloorPrefab && p.At.x < -8.99f
            && p.At.z > Deck - 1f);
        foreach (float across in new[] { -2f, 2f })
        {
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.StonePierBlock && Vector3.Distance(p.At, new Vector3(-9f, across, Deck - 0.5f)) < 0.02f);
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.StonePierBlock && Vector3.Distance(p.At, new Vector3(-8f, across, Deck + 0.5f)) < 0.02f);
        }
        Assert.Equal(4, plan.Count(p => p.Prefab == BridgeLayout.StoneStairPrefab && p.At.x > 10f));
        Assert.Equal(4, plan.Count(p => p.Prefab == BridgeLayout.StonePierBlock && p.At.x > 10.5f));
        Assert.DoesNotContain(plan, p => p.At.x > 14f);
        Assert.Equal(14f, far.Meet.x, 3);
    }

    // ------------------------------------------------------------------ ruin

    /// <summary>The bay (0 from the near end) a piece of a valley bridge (<see cref="Valley"/>) stands in, by its centre.</summary>
    private static int BayOf(ValleyWorld w, StructurePiece p) => Mathf.FloorToInt((p.Position.x + w.Half) / BridgeLayout.DeckSpan);
    /// <summary>The joint between bays (0 at the near end) a floor or a top course block is centred on.</summary>
    private static int JointOf(ValleyWorld w, StructurePiece p) => Mathf.RoundToInt((p.Position.x + w.Half) / BridgeLayout.DeckSpan);

    private static bool IsFloor(StructurePiece p) => p.Prefab == BridgeLayout.StoneFloorPrefab;
    /// <summary>The railing's course beside the deck (a block or an arch a bay, level with the deck) and its top course, on
    /// the deck (not the railing beside the steps).</summary>
    private static bool IsRailing(ValleyWorld w, StructurePiece p) =>
        Mathf.Abs(Mathf.Abs(p.Position.z) - BridgeLayout.DeckHalfWidth) < 0.01f && p.Position.y > Deck - 1f
        && Mathf.Abs(p.Position.x) <= w.Half + 0.01f;
    private static bool IsEdgeCourse(ValleyWorld w, StructurePiece p) => IsRailing(w, p) && p.Position.y < Deck;
    private static bool IsTopCourse(ValleyWorld w, StructurePiece p) => IsRailing(w, p) && p.Position.y > Deck;

    /// <summary>
    /// The ruin: fixed by the seed and the crossing; every piece standing is the complete bridge's, worn as a vanilla
    /// location's (<see cref="BridgeLayout.LocationDamageMin"/> to <see cref="BridgeLayout.LocationDamageMax"/>, as the
    /// Dvergr viaduct's); the piers, the steps, their footing and the railing beside them always stand, and so do the end
    /// strips and the floors and railing over the bank bays; most seeds lose some railing.
    /// </summary>
    [Fact]
    public void TheRuinIsFixedWornAndThePiersStepsAndBankBaysStand()
    {
        var w = new ValleyWorld { Half = 24f };
        var c = Valley(w);
        int bays = BridgeLayout.Bays(c.Width);
        var complete = BridgeLayout.SolveStoneComplete(c, w, 1);
        var keys = new HashSet<string>(complete.Select(Key));
        bool Kept(StructurePiece p) => IsPierBlock(p) || IsStrip(p) || p.Prefab is BridgeLayout.StoneStairPrefab or BridgeLayout.StonePillarPrefab
            || (p.Prefab == BridgeLayout.StonePierBlock && Mathf.Abs(p.Position.x) > w.Half)
            || (IsFloor(p) && (JointOf(w, p) == 1 || JointOf(w, p) == bays - 1))
            || (IsEdgeCourse(w, p) && (BayOf(w, p) == 0 || BayOf(w, p) == bays - 1));
        Assert.Equal(4, complete.Count(IsStrip));
        int lostRailing = 0;
        for (int seed = 1; seed <= 20; seed++)
        {
            var ruined = BridgeLayout.SolveStone(c, w, seed);
            Assert.Equal(ruined.Select(Key), BridgeLayout.SolveStone(c, w, seed).Select(Key));
            Assert.Equal(ruined.Select(p => p.HealthFraction), BridgeLayout.SolveStone(c, w, seed).Select(p => p.HealthFraction));
            foreach (StructurePiece p in ruined)
            {
                Assert.Contains(Key(p), keys);
                Assert.InRange(p.HealthFraction, BridgeLayout.LocationDamageMin, BridgeLayout.LocationDamageMax);
            }
            Assert.Equal(complete.Count(Kept), ruined.Count(Kept));
            if (ruined.Count(p => IsRailing(w, p)) < complete.Count(p => IsRailing(w, p))) lostRailing++;
        }
        Assert.True(lostRailing >= 15, $"only {lostRailing} of 20 seeds lost any railing");
    }

    /// <summary>
    /// The stone kit ruins at every kit's shares of the base curve's losses
    /// (<see cref="BridgeLayout.BridgeRuin"/>): over a 76 m deck (four 6 m openings among eleven) and 300 seeds, the floors
    /// lost at the deck share (and the open joints'), the arches under the deck at the support share, the railing at the course
    /// share (and more, for the pieces that go with a neighbour), and joints open from side to side, both floors and the top
    /// course over them, at the hole share; a floor over a bank bay never lost, and never two joints running open.
    /// </summary>
    [Fact]
    public void TheStoneKitRuinsAtTheSharedProfileWithOnlyJumpableGaps()
    {
        BridgeLayout.RuinProfile shares = BridgeLayout.BridgeRuin;
        var w = new ValleyWorld { Half = 38f };
        var c = Valley(w);
        int bays = BridgeLayout.Bays(c.Width);
        var layout = BridgeLayout.StoneBays(bays)!;
        Assert.Equal(4, layout.Count(b => b == BridgeLayout.StoneBay.Open));
        var complete = BridgeLayout.SolveStoneComplete(c, w, 1);
        float Survives(float at, float share) =>
            BridgeLayout.RuinProfile.Survives(BridgeLayout.SpanSurvival(1f - Mathf.Abs(at - bays * 0.5f) / (bays * 0.5f)), share);

        // Expected losses per seed, from the shares alone.
        float expectFloors = 0f, expectArches = 0f, expectEdges = 0f, expectTops = 0f, expectHoles = 0f;
        for (int i = 2; i <= bays - 2; i++)
        {
            float open = 1f - Survives(i, shares.HoleShare);
            expectHoles += open;
            expectFloors += 2f * (1f - Survives(i, shares.DeckShare) * (1f - open));
            expectTops += 2f * (1f - Survives(i, shares.CourseShare) * (1f - open));
        }
        for (int j = 0; j < bays; j++)
        {
            if (layout[j] is BridgeLayout.StoneBay.ArchNear or BridgeLayout.StoneBay.ArchFar) expectArches += 4f * (1f - Survives(j + 0.5f, shares.SupportShare));
            if (j > 0 && j < bays - 1 && layout[j] != BridgeLayout.StoneBay.Open) expectEdges += 2f * (1f - Survives(j + 0.5f, shares.CourseShare));
        }

        int floors = complete.Count(IsFloor), arches = complete.Count(IsUnderArch);
        int edges = complete.Count(p => IsEdgeCourse(w, p)), tops = complete.Count(p => IsTopCourse(w, p));
        Assert.Equal(2 * (bays - 1), floors);
        int lostFloors = 0, lostArches = 0, lostEdges = 0, lostTops = 0, holes = 0;
        const int Seeds = 300;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            var ruined = BridgeLayout.SolveStone(c, w, seed);
            lostFloors += floors - ruined.Count(IsFloor);
            lostArches += arches - ruined.Count(IsUnderArch);
            lostEdges += edges - ruined.Count(p => IsEdgeCourse(w, p));
            lostTops += tops - ruined.Count(p => IsTopCourse(w, p));
            var floored = new int[bays + 1];
            var topped = new int[bays + 1];
            foreach (StructurePiece p in ruined)
            {
                if (IsFloor(p)) floored[JointOf(w, p)]++;
                else if (IsTopCourse(w, p)) topped[JointOf(w, p)]++;
            }
            for (int i = 1; i < bays; i++)
            {
                if (floored[i] > 0) continue;
                Assert.True(i > 1 && i < bays - 1, $"seed {seed}: a floor over a bank bay lost");
                Assert.True(floored[i - 1] > 0, $"seed {seed}: joints {i - 1} and {i} both open from side to side: not a gap a player jumps");
                if (topped[i] == 0) holes++;
            }
        }
        void Rate(string what, int lost, float expected, float low, float high)
        {
            float rate = lost / (float)Seeds;
            Assert.True(rate >= expected * low && rate <= expected * high,
                FormattableString.Invariant($"{what}: {rate:F2} lost a seed, expected {expected:F2} ({low}-{high} of it)"));
        }
        Rate("floors", lostFloors, expectFloors, 0.85f, 1.15f);
        Rate("arches", lostArches, expectArches, 0.9f, 1.1f);
        Rate("open joints", holes, expectHoles, 0.75f, 1.25f);
        // On average over the deck, about the viaduct's: floors 6 % (with the open joints, 7 %), arches 32 %, the railing its
        // course's 17 % (TheRailingIsLostAtTheCourseShare).
        Assert.InRange(lostFloors / (float)(Seeds * floors), 0.05f, 0.09f);
        Assert.InRange(lostArches / (float)(Seeds * arches), 0.27f, 0.36f);
    }

    /// <summary>
    /// The railing is lost at the profile's course share, no more: a piece the rolls left with no neighbour (or a top block
    /// with nothing under it) has a neighbour put back, not dropped. Both courses, over an ordinary 60 m deck and over a 76 m
    /// one with four 6 m openings, against the share at each piece's place (the open joints' top blocks included).
    /// </summary>
    [Theory]
    [InlineData(34f, 0)]
    [InlineData(38f, 4)]
    public void TheRailingIsLostAtTheCourseShare(float half, int openBays)
    {
        BridgeLayout.RuinProfile shares = BridgeLayout.BridgeRuin;
        var w = new ValleyWorld { Half = half };
        var c = Valley(w);
        int bays = BridgeLayout.Bays(c.Width);
        var layout = BridgeLayout.StoneBays(bays)!;
        Assert.Equal(openBays, layout.Count(b => b == BridgeLayout.StoneBay.Open));
        var complete = BridgeLayout.SolveStoneComplete(c, w, 1);
        float Survives(float at, float share) =>
            BridgeLayout.RuinProfile.Survives(BridgeLayout.SpanSurvival(1f - Mathf.Abs(at - bays * 0.5f) / (bays * 0.5f)), share);
        float expectEdges = 0f, expectTops = 0f;
        for (int i = 2; i <= bays - 2; i++)
        {
            float open = 1f - Survives(i, shares.HoleShare);
            expectTops += 2f * (1f - Survives(i, shares.CourseShare) * (1f - open));
        }
        for (int j = 1; j < bays - 1; j++)
            if (layout[j] != BridgeLayout.StoneBay.Open) expectEdges += 2f * (1f - Survives(j + 0.5f, shares.CourseShare));
        int edges = complete.Count(p => IsEdgeCourse(w, p)), tops = complete.Count(p => IsTopCourse(w, p));
        int lostEdges = 0, lostTops = 0;
        const int Seeds = 300;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            var ruined = BridgeLayout.SolveStone(c, w, seed);
            lostEdges += edges - ruined.Count(p => IsEdgeCourse(w, p));
            lostTops += tops - ruined.Count(p => IsTopCourse(w, p));
        }
        float edgeRate = lostEdges / (float)Seeds, topRate = lostTops / (float)Seeds;
        Assert.True(edgeRate >= expectEdges * 0.85f && edgeRate <= expectEdges * 1.15f,
            FormattableString.Invariant($"edge course: {edgeRate:F2} lost a seed, expected {expectEdges:F2}"));
        Assert.True(topRate >= expectTops * 0.85f && topRate <= expectTops * 1.15f,
            FormattableString.Invariant($"top course: {topRate:F2} lost a seed, expected {expectTops:F2}"));
        Assert.InRange((lostEdges + lostTops) / (float)(Seeds * (edges + tops)), 0.15f, 0.19f);
    }

    /// <summary>
    /// A floor rests on a pier under it, on an arch of its lane under either half, or between the two floors of its lane
    /// either side (stone held between two neighbours keeps their support less its vertical loss, WearNTear.UpdateSupport):
    /// where the ruin would leave one on none of them, an arch under it stands. Found over many seeds and checked floor by
    /// floor, against the rolls that would have taken every arch under it.
    /// </summary>
    [Fact]
    public void AFloorOnNothingKeepsAnArchUnderIt()
    {
        var w = new ValleyWorld { Half = 38f };
        var c = Valley(w);
        int bays = BridgeLayout.Bays(c.Width);
        var layout = BridgeLayout.StoneBays(bays)!;
        bool IsArchBay(int j) => layout[j] is BridgeLayout.StoneBay.ArchNear or BridgeLayout.StoneBay.ArchFar;
        int found = 0;
        for (int seed = 1; seed <= 3000 && found < 3; seed++)
        {
            var ruined = BridgeLayout.SolveStone(c, w, seed);
            var floor = new bool[bays + 1, 2];
            var arch = new bool[bays, 4];
            foreach (StructurePiece p in ruined)
            {
                if (IsFloor(p) || IsStrip(p)) floor[JointOf(w, p), p.Position.z < 0f ? 0 : 1] = true;
                else if (IsUnderArch(p)) arch[BayOf(w, p), Mathf.RoundToInt(p.Position.z + 1.5f)] = true;
            }
            var r = new BridgeLayout.Ruin(seed, c);
            for (int i = 1; i < bays; i++)
            {
                if (layout[i - 1] == BridgeLayout.StoneBay.Pier || layout[i] == BridgeLayout.StoneBay.Pier) continue;
                for (int l = 0; l < 2; l++)
                {
                    if (!floor[i, l] || (floor[i - 1, l] && floor[i + 1, l])) continue;
                    var under = new[] { i - 1, i }.Where(IsArchBay).SelectMany(j => new[] { (j, 2 * l), (j, 2 * l + 1) }).ToList();
                    Assert.True(under.Any(a => arch[a.Item1, a.Item2]), $"seed {seed}: the floor on joint {i} rests on nothing");
                    if (under.All(a => r.Roll(a.Item1, a.Item2, BridgeLayout.RoleStoneArch) >=
                            BridgeLayout.RuinProfile.Survives(BridgeLayout.SpanSurvival(1f - Mathf.Abs(a.Item1 + 0.5f - bays * 0.5f) / (bays * 0.5f)),
                                BridgeLayout.BridgeRuin.SupportShare)))
                        found++;
                }
            }
        }
        Assert.True(found >= 1, "no seed rolled every arch under a floor away");
    }

    // ------------------------------------------------------------ kit choice

    /// <summary>
    /// From the Black Forest on (every biome but the Meadows and the Mistlands) a crossing the stone kit fits draws it, WoodRail
    /// or WoodBraced at even odds by the seed, every visit the same; a Meadows crossing never draws it, a Mistlands one is
    /// Dvergr wood. A crossing it does not fit draws the railed kits as the two-kit pick always did.
    /// </summary>
    [Fact]
    public void FromTheBlackForestOnTheStoneKitIsDrawnAtEvenOddsWithTheRailedKits()
    {
        var (c, w) = Reference();
        const int n = 240;
        foreach (Heightmap.Biome b in new[] { Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.Plains,
            Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean })
        {
            w.Biome = b;
            var count = new Dictionary<BridgeStructures.Kit, int>();
            for (int i = 0; i < n; i++)
            {
                w.Seed = 1000 + i;
                BridgeStructures.Kit k = BridgeStructures.KitFor(c, w)!.Value;
                Assert.Equal(k, BridgeStructures.KitFor(c, w));
                count[k] = count.TryGetValue(k, out int v) ? v + 1 : 1;
            }
            foreach (var kit in new[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced, BridgeStructures.Kit.Stone })
                Assert.InRange(count.TryGetValue(kit, out int m) ? m : 0, n / 3 * 0.7f, n / 3 * 1.3f);
            Assert.Equal(n, count.Values.Sum());
        }
        w.Biome = Heightmap.Biome.Meadows;
        for (int i = 0; i < n; i++)
        {
            w.Seed = 1000 + i;
            Assert.Contains(BridgeStructures.KitFor(c, w), new BridgeStructures.Kit?[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced });
        }
        w.Biome = Heightmap.Biome.Mistlands;
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, w));

        // A 10 m deck takes one 6 m opening; a 6 m deck no stone bays: the railed kits, by the same roll as the two-kit pick.
        w.Biome = Heightmap.Biome.Plains;
        var ten = RoadCrossing.Between(new Vector2(-5f, 0f), new Vector2(5f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        ten.WaterLevel = Deck - BridgeLayout.DeckFreeboard;
        Assert.True(BridgeLayout.StoneFits(ten, w));
        var short6 = RoadCrossing.Between(new Vector2(-3f, 0f), new Vector2(3f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        short6.WaterLevel = Deck - BridgeLayout.DeckFreeboard;
        Assert.False(BridgeLayout.StoneFits(short6, w));
        for (int i = 0; i < n; i++)
        {
            w.Seed = 1000 + i;
            var expected = new BridgeLayout.Ruin(w.Seed, short6).Roll(0, BridgeLayout.Ruin.Centre, BridgeLayout.RoleKit) < 0.5f
                ? BridgeStructures.Kit.WoodRail : BridgeStructures.Kit.WoodBraced;
            Assert.Equal(expected, BridgeStructures.KitFor(short6, w));
        }
    }

    /// <summary>The kit drawn is the kit built: a stone crossing's plan is the ruined stone kit's.</summary>
    [Fact]
    public void BridgeStructuresBuildsTheStoneKitItChose()
    {
        var (c, w) = Reference();
        c.Kit = BridgeStructures.Kit.Stone;
        Assert.Equal(BridgeLayout.SolveStone(c, w, 7).Select(Key), BridgeStructures.Solve(c, w, 7).Select(Key));
        Assert.Equal(BridgeLayout.SolveStoneComplete(c, w, 7).Select(Key), BridgeStructures.SolveComplete(c, w, 7).Select(Key));
        Assert.Equal(BridgeLayout.DeckHalfWidth, BridgeStructures.DeckHalfWidth(c, w));
    }

    // --------------------------------------------------------- the ship's way

    /// <summary>A river <paramref name="half"/> each side of the origin, its bed at 27 m, the water at 30 m, the banks at
    /// <paramref name="bank"/>, and a bridge over it from a metre past each bank.</summary>
    private (RoadCrossing c, ValleyWorld w) River(float half, float bank = 31f)
    {
        var w = new ValleyWorld { Half = half, Bed = 27f, Bank = bank, River = true };
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-half - 1f, 0f), new Vector2(half + 1f, 0f), 27f, new Vector2(0f, 0f), 30f, CrossingKind.Bridge);
        c.WaterLevel = 30f;
        return (c, w);
    }

    /// <summary>
    /// Over a river a stone deck opens whole openings, and the piers between them, but for the half arch springing from
    /// each pier either side, the deck and railing over it: the fewest whose span holds the ship's way, measured with the
    /// stone deck's own depth (<see cref="BridgeLayout.StoneDeckDepth"/>). Over a 34 m river (five openings, the middle one
    /// 6 m) that is the middle three, 18 m from the second pier's face to the fifth's, its half arches 2 m into it either
    /// side: each half arch's four arches under the deck, the deck over it ending on a strip on its crown, the railing's
    /// lower course to its crown and its top course a metre short; nothing between the crowns, complete or ruined;
    /// everything snapping. The crossing draws stone at even odds; the same valley without the river (a lake) is one deck.
    /// </summary>
    [Fact]
    public void OverARiverTheStoneKitOpensBetweenTwoHalfArches()
    {
        var (c, w) = River(16f);
        int bays = BridgeLayout.Bays(c.Width);
        var layout = BridgeLayout.StoneBays(bays)!;
        BridgeLayout.ShipWay way = Assert.IsType<BridgeLayout.ShipWay>(BridgeLayout.StoneShipWay(c, w));
        Assert.True(way.DeckWidth > 0f);
        Assert.True(BridgeLayout.StoneNavigationBays(c, w, layout, out int first, out int count));
        Assert.Equal((4, 9), (first, count));
        Assert.Equal(BridgeLayout.StoneBay.ArchNear, layout[first]);
        Assert.Equal(BridgeLayout.StoneBay.ArchFar, layout[first + count - 1]);
        Assert.True(first * 2f <= way.Centre - way.PierWidth * 0.5f - BridgeLayout.FairwayClearance);
        Assert.True((first + count) * 2f >= way.Centre + way.PierWidth * 0.5f + BridgeLayout.FairwayClearance);
        Assert.True(BridgeLayout.StoneFits(c, w));

        float deck = BridgeStructures.DeckHeight(c, w);
        float Along(StructurePiece p) => p.Position.x + 17f;
        bool At(StructurePiece p, string prefab, float along, float y) =>
            p.Prefab == prefab && Mathf.Abs(Along(p) - along) < 0.01f && Mathf.Abs(p.Position.y - y) < 0.01f;
        var complete = BridgeLayout.SolveStoneComplete(c, w, 1);
        foreach (var plan in new[] { complete }.Concat(Enumerable.Range(1, 20).Select(s => BridgeLayout.SolveStone(c, w, s))))
        {
            Assert.DoesNotContain(plan, p => Along(p) > 10.01f && Along(p) < 23.99f);
            Assert.True(BridgeLayout.InStoneNavigationGap(c, w, 17f) && !BridgeLayout.InStoneNavigationGap(c, w, 9.9f)
                && !BridgeLayout.InStoneNavigationGap(c, w, 24.1f));
        }
        // The half arches: four arches abreast under the deck in the bays beside the piers (along 9 and 25 m).
        foreach (float along in new[] { 9f, 25f }) Assert.Equal(4, complete.Count(p => At(p, BridgeLayout.StoneArchPrefab, along, deck - 1.5f)));
        // The deck over them: a floor on the joint at each pier's face (half on the pier, half on the arch), a strip on each
        // crown.
        foreach (float along in new[] { 8f, 26f }) Assert.Equal(2, complete.Count(p => At(p, BridgeLayout.StoneFloorPrefab, along, deck - 0.5f)));
        Assert.Equal(new[] { 9.5f, 24.5f }, complete.Where(p => IsStrip(p, deck) && Mathf.Abs(Along(p) - 17f) < 10f)
            .Select(Along).Distinct().OrderBy(a => a));
        // The railing over them: its lower course an arch over each half arch's bay to its crown, the top course on the
        // joints at the piers' faces (8 and 26 m), a metre short of the crowns, none at 10 or 24 m.
        bool Rail(StructurePiece p, string prefab, float y, float along) => At(p, prefab, along, y)
            && Mathf.Abs(Mathf.Abs(p.Position.z) - BridgeLayout.DeckHalfWidth) < 0.01f;
        foreach (float along in new[] { 9f, 25f }) Assert.Equal(2, complete.Count(p => Rail(p, BridgeLayout.StoneArchPrefab, deck - 0.5f, along)));
        foreach (float along in new[] { 8f, 26f }) Assert.Equal(2, complete.Count(p => Rail(p, BridgeLayout.StonePierBlock, deck + 0.5f, along)));
        foreach (float along in new[] { 10f, 24f }) Assert.Equal(0, complete.Count(p => Rail(p, BridgeLayout.StonePierBlock, deck + 0.5f, along)));
        AssertEverySnaps(complete, "river, complete");
        for (int seed = 1; seed <= 20; seed++) AssertEverySnaps(BridgeLayout.SolveStone(c, w, seed), $"river, seed {seed}");
        int stone = 0;
        for (int seed = 1; seed <= 60; seed++)
        {
            w.Seed = seed;
            if (BridgeStructures.KitFor(c, w) == BridgeStructures.Kit.Stone) stone++;
        }
        Assert.InRange(stone, 10, 35);
        w.River = false;
        Assert.Null(BridgeLayout.StoneShipWay(c, w));
        Assert.True(BridgeLayout.StoneNavigationBays(c, w, layout, out _, out int none));
        Assert.Equal(0, none);
    }

    /// <summary>A stone_arch's colliders in its own frame (ValheimRIP stone_arch): its tall end, its crown's slab, and the
    /// strut between them, turned 37.5 degrees about its local z; each a centre, a size, and a turn.</summary>
    private static readonly (Vector3 centre, Vector3 size, float turn)[] ArchColliders =
    {
        (new Vector3(0.81137f, 0f, 0f), new Vector3(0.37726f, 1f, 1f), 0f),
        (new Vector3(0f, 0.38549f, 0f), new Vector3(2f, 0.22901f, 1f), 0f),
        (new Vector3(0.571f, 0.054f, 0f), new Vector3(0.37726f, 0.77673f, 1f), 37.53f),
    };

    /// <summary>
    /// How far along the deck (world x here) a piece's solid reaches between heights <paramref name="bottom"/> and
    /// <paramref name="top"/>, sampled through its colliders: an arch's three, a floor's or a block's box. Null where none of
    /// it stands at those heights.
    /// </summary>
    private static (float min, float max)? Reach(StructurePiece p, float bottom, float top)
    {
        var boxes = p.Prefab switch
        {
            BridgeLayout.StoneArchPrefab => ArchColliders,
            BridgeLayout.StoneFloorPrefab => new[] { (Vector3.zero, new Vector3(2f, 1f, 2f), 0f) },
            BridgeLayout.StonePierBlock => new[] { (Vector3.zero, new Vector3(2f, 1f, 1f), 0f) },
            BridgeLayout.StonePillarPrefab => new[] { (Vector3.zero, new Vector3(1f, 2f, 1f), 0f) },
            _ => new[] { (Vector3.zero, new Vector3(2f, 1f, 2f), 0f) },
        };
        float min = float.MaxValue, max = float.MinValue;
        const int n = 24;
        foreach (var (centre, size, turn) in boxes)
            for (int a = 0; a <= n; a++)
                for (int b = 0; b <= n; b++)
                    for (int k = 0; k <= 2; k++)
                    {
                        var local = new Vector3((a / (float)n - 0.5f) * size.x, (b / (float)n - 0.5f) * size.y, (k / 2f - 0.5f) * size.z);
                        float t = turn * Mathf.Deg2Rad;
                        local = new Vector3(local.x * Mathf.Cos(t) - local.y * Mathf.Sin(t), local.x * Mathf.Sin(t) + local.y * Mathf.Cos(t), local.z) + centre;
                        Vector3 world = p.Position + BridgeLayout.Rotate(local, p.PitchDegrees, p.YawDegrees, p.RollDegrees);
                        if (world.y < bottom || world.y > top) continue;
                        min = Mathf.Min(min, world.x);
                        max = Mathf.Max(max, world.x);
                    }
        return min <= max ? (min, max) : null;
    }

    /// <summary>
    /// The ship's way through a stone opening clears at every height band of the ship (its hull to 4 m, the mast, the
    /// yard, the masthead), each with the passing margin and the clearance each side: measured on the laid pieces through
    /// their colliders (the half arches' from the prefab), complete and ruined, over rivers of many widths and decks at
    /// several heights over the water.
    /// </summary>
    [Fact]
    public void TheShipsWayThroughAStoneOpeningClearsAtEveryHeight()
    {
        int checkedBridges = 0;
        foreach (float half in new[] { 9f, 10f, 11f, 12f, 13f, 14f, 15f, 16f, 17f, 18f, 19f, 20f, 22f, 24f })
            foreach (float bank in new[] { 31f, 33f, 35f })
            {
                var (c, w) = River(half, bank);
                if (!BridgeLayout.StoneFits(c, w)) continue;
                BridgeLayout.ShipWay way = BridgeLayout.StoneShipWay(c, w)!.Value;
                float centre = way.Centre - half - 1f;   // in world x
                foreach (var plan in new[] { BridgeLayout.SolveStoneComplete(c, w, 1), BridgeLayout.SolveStone(c, w, 7) })
                    foreach (Ships.Band band in Ships.RiverShip.Bands)
                    {
                        float clear = band.Width * 0.5f + Ships.PassingMargin + BridgeLayout.FairwayClearance;
                        foreach (StructurePiece p in plan)
                        {
                            if (Reach(p, c.WaterLevel + band.Bottom, c.WaterLevel + band.Top) is not (float min, float max)) continue;
                            Assert.False(max > centre - clear + 0.01f && min < centre + clear - 0.01f,
                                FormattableString.Invariant($"half {half} bank {bank}: {p.Prefab} at ({p.Position.x:F2}, {p.Position.y:F2}) reaches {min:F2}..{max:F2} into the {band.Bottom}-{band.Top} m band's way {centre - clear:F2}..{centre + clear:F2}"));
                        }
                    }
                checkedBridges++;
            }
        Assert.True(checkedBridges >= 30, $"only {checkedBridges} stone river bridges checked");
    }

    /// <summary>
    /// Where the half arches would stand in the ship's way, the opening is widened by whole openings until they do not: over a
    /// 30 m river (two 6 m openings either side of the middle pier, 14 m from pier face to pier face, which holds the hull's
    /// way between the piers) the half arches and the deck over them would leave 10 m at the hull's height, under its 11.8 m,
    /// so the kit opens the fewest more that hold it, the deck all but its bank piers: 26 m between the piers, 22 m between
    /// the crowns. A river too narrow for any opening with its half arches (an 18 m deck, its openings 14 m from end pier to
    /// end pier) takes another kit.
    /// </summary>
    [Fact]
    public void WhereTheHalfArchesWouldStandInTheWayMoreOpens()
    {
        var (c, w) = River(14f);
        var layout = BridgeLayout.StoneBays(BridgeLayout.Bays(c.Width))!;
        BridgeLayout.ShipWay way = BridgeLayout.StoneShipWay(c, w)!.Value;
        float deck = BridgeStructures.DeckHeight(c, w) - c.WaterLevel;
        // Bays 4-10 (14 m, an opening, the middle pier, an opening) hold the way between the piers alone ...
        Assert.True(4 * 2f <= way.Centre - way.PierWidth * 0.5f - BridgeLayout.FairwayClearance
            && 11 * 2f >= way.Centre + way.PierWidth * 0.5f + BridgeLayout.FairwayClearance);
        // ... but not the hull past their half arches.
        Assert.False(BridgeLayout.StoneGapHoldsShip(way, 8f, 22f, deck));
        Assert.True(BridgeLayout.StoneNavigationBays(c, w, layout, out int first, out int count));
        Assert.Equal((1, 13), (first, count));
        Assert.True(BridgeLayout.StoneGapHoldsShip(way, first * 2f, (first + count) * 2f, deck));
        Assert.True(BridgeLayout.StoneFits(c, w));
        AssertEverySnaps(BridgeLayout.SolveStoneComplete(c, w, 1), "widened, complete");
        for (int seed = 1; seed <= 20; seed++) AssertEverySnaps(BridgeLayout.SolveStone(c, w, seed), $"widened, seed {seed}");

        var (narrow, nw) = River(8f);
        var narrowLayout = BridgeLayout.StoneBays(BridgeLayout.Bays(narrow.Width))!;
        Assert.False(BridgeLayout.StoneNavigationBays(narrow, nw, narrowLayout, out _, out _));
        Assert.False(BridgeLayout.StoneFits(narrow, nw));
    }

    /// <summary>
    /// The ruin of an opening's half arches is the rest's, but the strip on each crown, which ends the deck there, always
    /// stands on an arch of its lane: where the rolls took both, one is put back. Over 300 seeds the half arches lose some
    /// arches, and every strip keeps one under it. The strip keeps more of the arch than the railing over an arch does, so
    /// the piers' course limit is as it was.
    /// </summary>
    [Fact]
    public void AStripOnAHalfArchsCrownAlwaysStandsOnAnArch()
    {
        var (c, w) = River(16f);
        float deck = BridgeStructures.DeckHeight(c, w);
        float Along(StructurePiece p) => p.Position.x + 17f;
        int lost = 0;
        for (int seed = 1; seed <= 300; seed++)
        {
            var ruined = BridgeLayout.SolveStone(c, w, seed);
            foreach (var (strip, bay) in new[] { (9.5f, 9f), (24.5f, 25f) })
                foreach (float lane in new[] { -1f, 1f })
                {
                    Assert.Contains(ruined, p => IsStrip(p, deck) && Mathf.Abs(Along(p) - strip) < 0.01f && Mathf.Abs(p.Position.z - lane) < 0.01f);
                    int arches = ruined.Count(p => p.Prefab == BridgeLayout.StoneArchPrefab && Mathf.Abs(p.Position.y - (deck - 1.5f)) < 0.01f
                        && Mathf.Abs(Along(p) - bay) < 0.01f && Mathf.Abs(p.Position.z - lane) < 1f);
                    Assert.True(arches >= 1, $"seed {seed}: the strip at {strip} m, lane {lane}, rests on no arch");
                    lost += 2 - arches;
                }
        }
        Assert.True(lost > 100, $"the half arches lost only {lost} arches over 300 seeds");
        Assert.True(BridgeSupport.StoneStripOnCrownFactor > BridgeSupport.StoneOnArchFactor * BridgeSupport.StoneRailOnRailFactor);
    }

    /// <summary>
    /// A ship passing under a stone deck would need its piers out of a way no stone opening spans, so the kit does not fit:
    /// over the river with high banks the crossing draws a railed kit, whatever the seed. (Stone's support never lifts its
    /// deck that high: nine courses under a deck at least 0.5 m over the water leave its underside under a ship's mast.)
    /// A deck whose openings all together span less than the ship's way (a 14 m deck's two 4 m openings and the pier
    /// between them, 10 m from end pier to end pier) does not fit either.
    /// </summary>
    [Fact]
    public void TheStoneKitFitsNoRiverAShipPassesUnderOrNoOpeningSpans()
    {
        var w = new ValleyWorld { Half = 16f, Bed = 27f, Bank = 43f, River = true };
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-17f, 0f), new Vector2(17f, 0f), 27f, new Vector2(0f, 0f), 30f, CrossingKind.Bridge);
        c.WaterLevel = 30f;
        BridgeLayout.ShipWay way = Assert.IsType<BridgeLayout.ShipWay>(BridgeLayout.StoneShipWay(c, w));
        Assert.Equal(0f, way.DeckWidth);
        Assert.False(BridgeLayout.StoneNavigationBays(c, w, BridgeLayout.StoneBays(BridgeLayout.Bays(c.Width))!, out _, out _));
        Assert.False(BridgeLayout.StoneFits(c, w));
        for (int seed = 1; seed <= 60; seed++)
        {
            w.Seed = seed;
            Assert.NotEqual(BridgeStructures.Kit.Stone, BridgeStructures.KitFor(c, w));
        }
        // The highest a stone deck's underside stands over a river: its piers' courses on the bed, the water over the bed as
        // shallow as a ship floats in.
        Assert.True(BridgeSupport.StoneMaxPierCourses() + BridgeLayout.StoneFloorDepth - BridgeLayout.StoneDeckDepth
            - RoadCrossingDetector.FairwayMinDepth < Ships.RiverShip.Top);

        var narrow = new ValleyWorld { Half = 7f, Bed = 27f, Bank = 31f, River = true };
        _scope.WithWorld(narrow);
        var n = RoadCrossing.Between(new Vector2(-7f, 0f), new Vector2(7f, 0f), 27f, new Vector2(0f, 0f), 30f, CrossingKind.Bridge);
        n.WaterLevel = 30f;
        Assert.NotNull(BridgeLayout.StoneShipWay(n, narrow));
        Assert.Equal(7, BridgeLayout.StoneBays(BridgeLayout.Bays(n.Width))!.Length);
        Assert.False(BridgeLayout.StoneFits(n, narrow));
    }
}
