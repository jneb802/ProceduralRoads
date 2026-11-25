using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The railed wooden kits (BridgeLayout.RailedKit): kit A (WoodRail) and kit B (WoodBraced) laid as the hand-built
/// reference bridge was, piece for piece; taller ends and their railing posts, high decks, the height limit and a free span
/// (no column where it would stand too tall or in the channel), fords' spans, bank columns on the levelled road, the navigation gap's edges, ruin, snap points, the kit choice and its record.
/// </summary>
public sealed class RailedBridgeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public RailedBridgeTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>
    /// Kit A as built (pieces.txt), in the bridge's frame: prefab, along the deck from its centre, across it (to the right of
    /// the deck's heading), height, yaw relative to the deck's heading. Without the stray wood_beam_26 at (9, -2, 32.9),
    /// which is not part of the kit.
    /// </summary>
    private static readonly string[] ReferenceA =
    {
        "wood_beam -5.0 -2.0 34.9 90.0",
        "wood_beam -5.0 -2.0 35.9 90.0",
        "wood_beam -5.0 2.0 34.9 90.0",
        "wood_beam -5.0 2.0 35.9 90.0",
        "wood_beam -3.0 -2.0 34.9 90.0",
        "wood_beam -3.0 -2.0 35.9 90.0",
        "wood_beam -3.0 2.0 34.9 90.0",
        "wood_beam -3.0 2.0 35.9 90.0",
        "wood_beam -1.0 -2.01 34.9 90.0",
        "wood_beam -1.0 -2.01 35.9 90.0",
        "wood_beam -1.0 2.0 34.9 90.0",
        "wood_beam -1.0 2.0 35.9 90.0",
        "wood_beam 1.0 -2.0 34.9 90.0",
        "wood_beam 1.0 -2.0 35.9 90.0",
        "wood_beam 1.0 2.0 34.9 90.0",
        "wood_beam 1.0 2.0 35.9 90.0",
        "wood_beam 3.0 -2.01 34.9 90.0",
        "wood_beam 3.0 -2.01 35.9 90.0",
        "wood_beam 3.0 2.0 34.9 90.0",
        "wood_beam 3.0 2.0 35.9 90.0",
        "wood_beam 5.0 -2.0 34.9 90.0",
        "wood_beam 5.0 -2.0 35.9 90.0",
        "wood_beam 5.0 2.0 34.9 90.0",
        "wood_beam 5.0 2.0 35.9 90.0",
        "wood_beam_26 -7.0 -2.01 33.9 270.0",
        "wood_beam_26 -7.0 -2.0 34.9 270.0",
        "wood_beam_26 -7.0 2.0 33.9 270.0",
        "wood_beam_26 -7.0 2.0 34.9 270.0",
        "wood_beam_26 7.0 -2.0 33.9 90.0",
        "wood_beam_26 7.0 -2.0 34.9 90.0",
        "wood_beam_26 7.0 2.0 33.9 90.0",
        "wood_beam_26 7.0 2.0 34.9 90.0",
        "wood_floor -5.0 -1.0 34.9 90.0",
        "wood_floor -5.0 1.0 34.9 90.0",
        "wood_floor -3.0 -1.0 34.9 90.0",
        "wood_floor -3.0 1.0 34.9 90.0",
        "wood_floor -1.0 -1.0 34.9 90.0",
        "wood_floor -0.99 1.0 34.9 90.0",
        "wood_floor 1.0 -0.99 34.9 90.0",
        "wood_floor 1.0 1.0 34.9 90.0",
        "wood_floor 2.99 1.0 34.9 90.0",
        "wood_floor 3.0 -1.0 34.9 90.0",
        "wood_floor 5.0 -1.0 34.9 90.0",
        "wood_floor 5.0 1.0 34.9 90.0",
        "wood_pole -8.0 -2.01 34.4 90.0",
        "wood_pole -8.0 2.0 34.4 90.0",
        "wood_pole -6.0 -2.0 35.4 90.0",
        "wood_pole -6.0 2.0 35.4 90.0",
        "wood_pole -4.0 -2.01 35.4 90.0",
        "wood_pole -4.0 2.0 35.4 90.0",
        "wood_pole -2.0 -2.0 35.4 90.0",
        "wood_pole -2.0 2.0 35.4 90.0",
        "wood_pole -0.0 -2.0 35.4 90.0",
        "wood_pole 0.0 2.0 35.4 90.0",
        "wood_pole 2.0 -2.0 35.4 90.0",
        "wood_pole 2.0 2.0 35.4 90.0",
        "wood_pole 4.0 -2.0 35.4 90.0",
        "wood_pole 4.0 2.0 35.4 90.0",
        "wood_pole 6.0 -2.01 35.4 90.0",
        "wood_pole 6.0 2.0 35.4 90.0",
        "wood_pole 8.0 -2.0 34.4 90.0",
        "wood_pole 8.0 2.0 34.4 90.0",
        "wood_pole2 -6.0 -2.0 33.9 270.0",
        "wood_pole2 -6.0 2.0 33.9 270.0",
        "wood_pole2 -4.0 -2.01 31.9 270.0",
        "wood_pole2 -4.0 -2.01 33.9 270.0",
        "wood_pole2 -4.0 2.0 31.9 270.0",
        "wood_pole2 -4.0 2.0 33.9 270.0",
        "wood_pole2 -2.0 -2.0 29.9 90.0",
        "wood_pole2 -2.0 -2.0 31.9 90.0",
        "wood_pole2 -2.0 -2.0 33.9 90.0",
        "wood_pole2 -2.0 2.0 29.9 90.0",
        "wood_pole2 -2.0 2.0 31.9 90.0",
        "wood_pole2 -2.0 2.0 33.9 90.0",
        "wood_pole2 -0.0 -2.0 29.9 90.0",
        "wood_pole2 -0.0 -2.0 31.9 90.0",
        "wood_pole2 -0.0 -2.0 33.9 90.0",
        "wood_pole2 0.0 2.0 29.9 90.0",
        "wood_pole2 0.0 2.0 31.9 90.0",
        "wood_pole2 0.0 2.0 33.9 90.0",
        "wood_pole2 2.0 -2.0 29.9 90.0",
        "wood_pole2 2.0 -2.0 31.9 90.0",
        "wood_pole2 2.0 -2.0 33.9 90.0",
        "wood_pole2 2.0 2.0 29.9 90.0",
        "wood_pole2 2.0 2.0 31.9 90.0",
        "wood_pole2 2.0 2.0 33.9 90.0",
        "wood_pole2 4.0 -2.0 29.9 90.0",
        "wood_pole2 4.0 -2.0 31.9 90.0",
        "wood_pole2 4.0 -2.0 33.9 90.0",
        "wood_pole2 4.0 2.0 29.9 90.0",
        "wood_pole2 4.0 2.0 31.9 90.0",
        "wood_pole2 4.0 2.0 33.9 90.0",
        "wood_pole2 6.0 -2.01 31.9 90.0",
        "wood_pole2 6.0 -2.01 33.9 90.0",
        "wood_pole2 6.0 2.0 31.9 90.0",
        "wood_pole2 6.0 2.0 33.9 90.0",
        "wood_stair -7.0 -1.0 33.9 180.0",
        "wood_stair -7.0 1.0 33.9 180.0",
        "wood_stair 7.0 -1.0 33.9 0.0",
        "wood_stair 7.0 1.0 33.9 0.0",
    };

    /// <summary>Kit B as built (pieces-v2.txt): kit A with its right side edited (columns every 4 m, the braces); the
    /// stray beam left out.</summary>
    private static readonly string[] ReferenceB =
    {
        "wood_beam -5.0 -2.0 34.9 90.0",
        "wood_beam -5.0 -2.0 35.9 90.0",
        "wood_beam -5.0 2.0 34.9 90.0",
        "wood_beam -5.0 2.0 35.9 90.0",
        "wood_beam -3.0 -2.0 34.9 90.0",
        "wood_beam -3.0 -2.0 35.9 90.0",
        "wood_beam -3.0 2.0 34.9 90.0",
        "wood_beam -3.0 2.0 35.9 90.0",
        "wood_beam -1.0 -2.01 34.9 90.0",
        "wood_beam -1.0 -2.01 35.9 90.0",
        "wood_beam -1.0 2.0 34.9 90.0",
        "wood_beam -1.0 2.0 35.9 90.0",
        "wood_beam 1.0 -2.0 34.9 90.0",
        "wood_beam 1.0 -2.0 35.9 90.0",
        "wood_beam 1.0 2.0 34.9 90.0",
        "wood_beam 1.0 2.0 35.9 90.0",
        "wood_beam 3.0 -2.01 34.9 90.0",
        "wood_beam 3.0 -2.01 35.9 90.0",
        "wood_beam 3.0 2.0 34.9 90.0",
        "wood_beam 3.0 2.0 35.9 90.0",
        "wood_beam 5.0 -2.0 34.9 90.0",
        "wood_beam 5.0 -2.0 35.9 90.0",
        "wood_beam 5.0 2.0 34.9 90.0",
        "wood_beam 5.0 2.0 35.9 90.0",
        "wood_beam_26 -7.0 -2.01 33.9 270.0",
        "wood_beam_26 -7.0 -2.0 34.9 270.0",
        "wood_beam_26 -7.0 2.0 33.9 270.0",
        "wood_beam_26 -7.0 2.0 34.9 270.0",
        "wood_beam_26 7.0 -2.0 33.9 90.0",
        "wood_beam_26 7.0 -2.0 34.9 90.0",
        "wood_beam_26 7.0 2.0 33.9 90.0",
        "wood_beam_26 7.0 2.0 34.9 90.0",
        "wood_beam_67 -7.0 2.0 30.9 90.0",
        "wood_beam_67 -5.0 2.0 30.9 270.0",
        "wood_beam_67 -3.0 2.0 30.9 90.0",
        "wood_beam_67 -1.0 2.0 30.9 270.0",
        "wood_beam_67 1.0 2.0 30.9 90.0",
        "wood_beam_67 3.0 2.0 30.9 270.0",
        "wood_beam_67 5.0 2.0 30.9 90.0",
        "wood_beam_67 7.0 2.0 30.9 270.0",
        "wood_floor -5.0 -1.0 34.9 90.0",
        "wood_floor -5.0 1.0 34.9 90.0",
        "wood_floor -3.0 -1.0 34.9 90.0",
        "wood_floor -3.0 1.0 34.9 90.0",
        "wood_floor -1.0 -1.0 34.9 90.0",
        "wood_floor -0.99 1.0 34.9 90.0",
        "wood_floor 1.0 -0.99 34.9 90.0",
        "wood_floor 1.0 1.0 34.9 90.0",
        "wood_floor 2.99 1.0 34.9 90.0",
        "wood_floor 3.0 -1.0 34.9 90.0",
        "wood_floor 5.0 -1.0 34.9 90.0",
        "wood_floor 5.0 1.0 34.9 90.0",
        "wood_pole -8.0 -2.01 34.4 90.0",
        "wood_pole -8.0 2.0 34.4 90.0",
        "wood_pole -6.0 -2.0 35.4 90.0",
        "wood_pole -6.0 2.0 35.4 90.0",
        "wood_pole -4.0 -2.01 35.4 90.0",
        "wood_pole -4.0 2.0 35.4 90.0",
        "wood_pole -2.0 -2.0 35.4 90.0",
        "wood_pole -2.0 2.0 35.4 90.0",
        "wood_pole -0.0 -2.0 35.4 90.0",
        "wood_pole 0.0 2.0 35.4 90.0",
        "wood_pole 2.0 -2.0 35.4 90.0",
        "wood_pole 2.0 2.0 35.4 90.0",
        "wood_pole 4.0 -2.0 35.4 90.0",
        "wood_pole 4.0 2.0 35.4 90.0",
        "wood_pole 6.0 -2.01 35.4 90.0",
        "wood_pole 6.0 2.0 35.4 90.0",
        "wood_pole 8.0 -2.0 34.4 90.0",
        "wood_pole 8.0 2.0 34.4 90.0",
        "wood_pole2 -6.0 -2.0 33.9 270.0",
        "wood_pole2 -6.0 2.0 33.9 270.0",
        "wood_pole2 -4.0 -2.01 31.9 270.0",
        "wood_pole2 -4.0 -2.01 33.9 270.0",
        "wood_pole2 -2.0 -2.0 29.9 90.0",
        "wood_pole2 -2.0 -2.0 31.9 90.0",
        "wood_pole2 -2.0 -2.0 33.9 90.0",
        "wood_pole2 -2.0 2.0 29.9 90.0",
        "wood_pole2 -2.0 2.0 31.9 90.0",
        "wood_pole2 -2.0 2.0 33.9 90.0",
        "wood_pole2 -0.0 -2.0 29.9 90.0",
        "wood_pole2 -0.0 -2.0 31.9 90.0",
        "wood_pole2 -0.0 -2.0 33.9 90.0",
        "wood_pole2 2.0 -2.0 29.9 90.0",
        "wood_pole2 2.0 -2.0 31.9 90.0",
        "wood_pole2 2.0 -2.0 33.9 90.0",
        "wood_pole2 2.0 2.0 29.9 90.0",
        "wood_pole2 2.0 2.0 31.9 90.0",
        "wood_pole2 2.0 2.0 33.9 90.0",
        "wood_pole2 4.0 -2.0 29.9 90.0",
        "wood_pole2 4.0 -2.0 31.9 90.0",
        "wood_pole2 4.0 -2.0 33.9 90.0",
        "wood_pole2 6.0 -2.01 31.9 90.0",
        "wood_pole2 6.0 -2.01 33.9 90.0",
        "wood_pole2 6.0 2.0 31.9 90.0",
        "wood_pole2 6.0 2.0 33.9 90.0",
        "wood_stair -7.0 -1.0 33.9 180.0",
        "wood_stair -7.0 1.0 33.9 180.0",
        "wood_stair 7.0 -1.0 33.9 0.0",
        "wood_stair 7.0 1.0 33.9 0.0",
    };

    /// <summary>
    /// The reference's ground: its column feet, 32.9 m at the near deck end (-6), 30.9 m at -4, 28.9 m from -2 to 4 and
    /// 30.9 m at the far end (6), rising to 33.9 m at the stairs' feet (+-7 and on) where the one step down lands; flat
    /// across the deck. A Meadows world, no river. <see cref="Bank"/>: outward of the deck ends, its own ground instead (the
    /// taller ends), by the distance from the deck's centre.
    /// </summary>
    private sealed class ReferenceWorld : WorldGenerator
    {
        private static readonly Vector2[] Profile =
            { new(-7f, 33.9f), new(-6f, 32.9f), new(-4f, 30.9f), new(-2f, 28.9f), new(4f, 28.9f), new(6f, 30.9f), new(7f, 33.9f) };
        public Func<float, float>? Bank;
        /// <summary>From the deck ends outward, by (x, z): the ground instead (a hillside beside the stairs).</summary>
        public Func<float, float, float>? Hill;
        public float Drop;   // lowers the ground between the deck ends (a high deck)
        public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
        public int Seed = 76;

        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) >= 6f && Hill != null) return Hill(wx, wy);
            if (Mathf.Abs(wx) > 6f && Bank != null) return Bank(Mathf.Abs(wx));
            float h = Profile[Profile.Length - 1].y;
            if (wx <= Profile[0].x) h = Profile[0].y;
            else
                for (int i = 1; i < Profile.Length; i++)
                    if (wx <= Profile[i].x) { h = Mathf.Lerp(Profile[i - 1].y, Profile[i].y, (wx - Profile[i - 1].x) / (Profile[i].x - Profile[i - 1].x)); break; }
            return Mathf.Abs(wx) < 6f ? h - Drop : h;
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => Seed;
    }

    /// <summary>The reference crossing: 12 m along +x, deck centre at the origin. The deck stands at 34.9 m as built; the
    /// layout takes a deck from the banks or the water (BridgeLayout.DeckHeight), so the water is put 0.5 m under it.</summary>
    private static RoadCrossing ReferenceCrossing()
    {
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.9f - BridgeLayout.DeckFreeboard;
        return c;
    }

    private (RoadCrossing c, ReferenceWorld w) Reference()
    {
        var w = new ReferenceWorld();
        _scope.WithWorld(w);
        return (ReferenceCrossing(), w);
    }

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

    /// <summary>A planned piece in the reference crossing's frame (deck heading +x: along = x, across to its right = -z).</summary>
    private static FramePiece Frame(StructurePiece p) =>
        new(p.Prefab, new Vector3(p.Position.x, -p.Position.z, p.Position.y), p.YawDegrees - 90f);

    /// <summary>Yaws a piece cannot be told apart at: a pole or a plain beam turned half round, a plate a quarter.</summary>
    private static bool SameYaw(string prefab, float a, float b)
    {
        float period = prefab == BridgeLayout.DeckPrefab ? 90f
            : prefab is BridgeLayout.PostPrefab or BridgeLayout.RailPostPrefab or BridgeLayout.BeamPrefab ? 180f : 360f;
        float d = ((a - b) % period + period) % period;
        return Mathf.Min(d, period - d) < 0.5f;
    }

    /// <summary>Each reference piece matched to one planned piece of its prefab and yaw, nearest first; returns the
    /// largest distance, and fails on any piece left over on either side.</summary>
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

    // ----------------------------------------------------------- the reference

    [Fact]
    public void KitAIsTheHandBuiltBridge()
    {
        var (c, w) = Reference();
        float worst = Match(ReferenceA.Select(Parse), BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail), 0.05f);
        Assert.True(worst < 0.03f, $"worst piece {worst:F3} m off");
    }

    [Fact]
    public void KitBIsTheHandBuiltBridgeWithItsEditedSideOnBothSides()
    {
        var (c, w) = Reference();
        // The right side (across > 0) was edited; the left is kit A's. Kit B is the edited side, mirrored.
        var right = ReferenceB.Select(Parse).Where(p => p.At.y > 0f).ToList();
        var expected = right.Concat(right.Select(p => new FramePiece(p.Prefab, new Vector3(p.At.x, -p.At.y, p.At.z), p.Yaw)));
        float worst = Match(expected, BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Braced), 0.05f);
        Assert.True(worst < 0.03f, $"worst piece {worst:F3} m off");
    }

    // ------------------------------------------------------------ snap points

    /// <summary>
    /// Every piece of either kit, at the reference crossing, with taller ends, with a long stair run, with the deck 16 m
    /// over the bed and with a bank the road cuts under an end, has a snap point on a snap point of another piece (within
    /// 2 cm), as a player's hammer puts it: plates on stringers, stringers on posts, posts stacked, stairs on the deck,
    /// rails on their posts, braces on columns and stringers.
    /// </summary>
    [Theory]
    [InlineData(false, "reference")]
    [InlineData(true, "reference")]
    [InlineData(false, "tall ends")]
    [InlineData(true, "tall ends")]
    [InlineData(false, "long stairs")]
    [InlineData(true, "long stairs")]
    [InlineData(false, "high deck")]
    [InlineData(true, "high deck")]
    [InlineData(false, "cut bank")]
    [InlineData(true, "cut bank")]
    public void EveryPieceSnapsToANeighbour(bool braced, string ground)
    {
        var (c, w) = Reference();
        if (ground == "tall ends") w.Bank = TallBank;
        if (ground == "long stairs") w.Bank = LongBank;
        if (ground == "high deck") w.Drop = 10f;
        if (ground == "cut bank") w.Hill = CutBank;
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, braced ? BridgeLayout.Braced : BridgeLayout.Rail);
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
        Assert.True(loose.Count == 0, "pieces on no neighbour's snap point:\n" + string.Join("\n", loose));
    }

    // ------------------------------------------------------------ taller ends

    /// <summary>Banks that fall away a little faster than a step (0.58 in 1, to 3.5 m under the deck): four steps down.</summary>
    private static float TallBank(float a) => Mathf.Max(34.9f - 0.58f * (a - 6f), 34.9f - 3.5f);

    /// <summary>Banks that fall away as <see cref="TallBank"/> to 6.5 m under the deck: a long run, seven steps down.</summary>
    private static float LongBank(float a) => Mathf.Max(34.9f - 0.58f * (a - 6f), 34.9f - 6.5f);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATallerEndRailsEveryStepAndPutsTheNewelAtItsFoot(bool braced)
    {
        var (c, w) = Reference();
        w.Bank = TallBank;
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, braced ? BridgeLayout.Braced : BridgeLayout.Rail).Select(Frame).ToList();
        foreach (float end in new[] { -1f, 1f })
        {
            var stairs = plan.Where(p => p.Prefab == BridgeLayout.StairPrefab && p.At.x * end > 6f).ToList();
            int steps = stairs.Count / 2;
            Assert.Equal(4, steps);
            for (int k = 0; k < steps; k++)
            {
                float along = end * (7f + 2f * k), foot = 34.9f - 1f - k;
                foreach (float across in new[] { -2f, 2f })
                    foreach (float y in new[] { foot, foot + 1f })   // its stringer and its handrail
                        Assert.Contains(plan, p => p.Prefab == BridgeLayout.StairRailPrefab && Vector3.Distance(p.At, new Vector3(along, across, y)) < 0.02f);
            }
            float footAlong = end * (6f + 2f * steps), footY = 34.9f - steps;
            foreach (float across in new[] { -2f, 2f })
                Assert.Contains(plan, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.At, new Vector3(footAlong, across, footY + 0.5f)) < 0.02f);
            // A railing post each side at every joint down the run (the newel the last), none beyond the deck's at the top.
            Assert.Equal(2 * steps, plan.Count(p => p.Prefab == BridgeLayout.RailPostPrefab && p.At.x * end > 6.5f));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALongEndStairHasARailingPostEveryTwoMetresUnderAContinuousHandrail(bool braced)
    {
        var (c, w) = Reference();
        w.Bank = LongBank;
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, braced ? BridgeLayout.Braced : BridgeLayout.Rail);
        var frame = plan.Select(Frame).ToList();
        foreach (float end in new[] { -1f, 1f })
        {
            int steps = frame.Count(p => p.Prefab == BridgeLayout.StairPrefab && p.At.x * end > 6f) / 2;
            Assert.True(steps >= 6, $"{steps} steps: not a long run");
            foreach (float across in new[] { -2f, 2f })
            {
                // From the deck's railing post at the top down to the newel at the foot, one post every 2 m along.
                for (int k = 0; k <= steps; k++)
                {
                    var at = new Vector3(end * (6f + 2f * k), across, 34.9f - k + 0.5f);
                    int i = frame.FindIndex(p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.At, at) < 0.02f);
                    Assert.True(i >= 0, $"no railing post at {at}");
                    // Its foot on a stringer's joint, its top on a handrail's: the handrail runs on over it.
                    var snaps = BridgeLayout.SnapPoints(plan[i]);
                    foreach (float y in new[] { -0.5f, 0.5f })
                    {
                        Vector3 snap = snaps.OrderBy(v => Mathf.Abs(v.y - (plan[i].Position.y + y))).First();
                        Assert.Contains(plan, p => (p.Prefab == BridgeLayout.StairRailPrefab || p.Prefab == BridgeLayout.BeamPrefab)
                            && BridgeLayout.SnapPoints(p).Any(v => Vector3.Distance(v, snap) < 0.02f));
                    }
                }
                // A handrail over every step: none missing between the deck and the foot.
                for (int k = 0; k < steps; k++)
                    Assert.Contains(frame, p => p.Prefab == BridgeLayout.StairRailPrefab
                        && Vector3.Distance(p.At, new Vector3(end * (7f + 2f * k), across, 34.9f - k)) < 0.02f);
            }
            Assert.Equal(2 * steps, frame.Count(p => p.Prefab == BridgeLayout.RailPostPrefab && p.At.x * end > 6.5f));
            // The steps the bank falls away under stand on columns under their edges at the joints, down to the ground;
            // none under a step's middle.
            var stairColumns = frame.Where(p => p.Prefab == BridgeLayout.PostPrefab && p.At.x * end > 6.5f).ToList();
            Assert.NotEmpty(stairColumns);
            foreach (FramePiece pole in stairColumns)
            {
                Assert.Equal(2f, Mathf.Abs(pole.At.y), 2);
                Assert.Equal(0f, (Mathf.Abs(pole.At.x) - 6f) % 2f, 2);
            }
            foreach (var column in stairColumns.GroupBy(p => (Mathf.Round(p.At.x), p.At.y > 0f)))
            {
                FramePiece low = column.OrderBy(p => p.At.z).First();
                Assert.True(low.At.z - 1f <= LongBank(Mathf.Abs(low.At.x)) + BuriedPieces.Growth, $"{low} stops over the ground");
            }
        }
    }

    /// <summary>Whether a piece stands over the road run on under an end's joining piece (its bottom step), where the
    /// road's levelling cuts a hillside beside the stairs to the foot's height (StructureGround).</summary>
    private static bool UnderAJoint(RoadCrossing c, WorldGenerator w, FramePiece p)
    {
        var (near, far) = BridgeStructures.Ends(c, w);
        var at = new Vector2(p.At.x, -p.At.y);
        float half = BridgeLayout.DeckHalfWidth + BuriedPieces.Growth;
        return near.UnderTheJoint(at, half) || far.UnderTheJoint(at, half);
    }

    [Fact]
    public void AStairPostWhollyInTheGroundIsLeftOut()
    {
        // A hillside beside the long stairs on their left (world +z) standing over the posts' tops there: none is laid on
        // that side below the deck but under the bottom step, where the road cuts the hillside away; the right side keeps
        // its posts.
        var (c, w) = Reference();
        w.Bank = LongBank;
        w.Hill = (x, z) => z > 1.5f && Mathf.Abs(x) > 7f ? 36.5f : LongBank(Mathf.Abs(x));
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail).Select(Frame).ToList();
        var stairPosts = plan.Where(p => p.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(p.At.x) > 6.5f).ToList();
        Assert.DoesNotContain(stairPosts, p => p.At.y < 0f && !UnderAJoint(c, w, p));
        Assert.Contains(stairPosts, p => p.At.y > 0f);
    }

    [Fact]
    public void AStairRailWhollyInTheGroundIsLeftOut()
    {
        // A hillside beside the long stairs on their left (across -2, world +z), 0.3 m over the stair's line: each left
        // stringer up the flight lies under the ground, out of sight, and is left out, but by the bottom step, where the
        // road cuts the hillside away; the handrails and the right side's stringers stand out of it.
        var (c, w) = Reference();
        w.Bank = LongBank;
        float Line(float x) => 34.9f - 0.5f * (Mathf.Abs(x) - 6f);
        w.Hill = (x, z) => z > 1.5f ? Line(x) + 0.3f : LongBank(Mathf.Abs(x));
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail).Select(Frame).ToList();
        var rails = plan.Where(p => p.Prefab == BridgeLayout.StairRailPrefab).ToList();
        bool Stringer(FramePiece p) => Mathf.Abs(p.At.z - (Line(p.At.x) - 0.5f)) < 0.05f;
        bool Handrail(FramePiece p) => Mathf.Abs(p.At.z - (Line(p.At.x) + 0.5f)) < 0.05f;
        int steps = plan.Count(p => p.Prefab == BridgeLayout.StairPrefab) / 2;
        Assert.True(steps >= 12, $"{steps} steps in both ends' runs");
        // The run's last stringers reach the road under the bottom step at their low end.
        Assert.DoesNotContain(rails, p => Stringer(p) && p.At.y < 0f && Mathf.Abs(p.At.x) < 16f);
        Assert.Contains(rails, p => Stringer(p) && p.At.y < 0f);
        Assert.Equal(steps, rails.Count(p => Stringer(p) && p.At.y > 0f));
        Assert.Equal(2 * steps, rails.Count(Handrail));
    }

    // ------------------------------------------------- ends the road meets

    /// <summary>
    /// The long ends (seven steps each side) met by the road: the near end level, the far end landed on its third row. The
    /// near end lays no stairs, no stair rail, post or column: its rails end on the deck's end post. The far end lays three
    /// steps, railed down each, the newel at the foot of the third, nothing past it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEndMetLevelLaysNoStairsAndOneLandedOnARowLaysThatManyWithTheNewelAtTheLast(bool braced)
    {
        var (c, w) = Reference();
        w.Bank = LongBank;
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        Assert.Equal(7, BridgeLayout.SolveRailedComplete(c, w, 1, kit).Count(p => p.Kind == StructurePieceKind.Stair && p.Position.x > 6f) / 2);
        MetEnds.Set(c, w, fromLevel: true, toRow: 3);
        var (near, far) = BridgeStructures.Ends(c, w);
        Assert.Equal((0, 3), (near.Steps, far.Steps));
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, kit).Select(Frame).ToList();

        // The near end: nothing past the deck's end but the bank's brace leaning into it; its end post stands each side
        // under the end bay's handrail.
        Assert.DoesNotContain(plan, p => p.At.x < -6.5f && p.Prefab != BridgeLayout.BracePrefab);
        foreach (float across in new[] { -2f, 2f })
        {
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.At, new Vector3(-6f, across, 35.4f)) < 0.02f);
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.BeamPrefab && Vector3.Distance(p.At, new Vector3(-5f, across, 35.9f)) < 0.02f);
        }

        // The far end: three steps, a stringer and a handrail over each, the newel at the third's foot, nothing beyond it.
        Assert.Equal(6, plan.Count(p => p.Prefab == BridgeLayout.StairPrefab && p.At.x > 6f));
        for (int k = 0; k < 3; k++)
            foreach (float across in new[] { -2f, 2f })
                foreach (float y in new[] { 33.9f - k, 34.9f - k })
                    Assert.Contains(plan, p => p.Prefab == BridgeLayout.StairRailPrefab && Vector3.Distance(p.At, new Vector3(7f + 2f * k, across, y)) < 0.02f);
        Assert.Equal(12, plan.Count(p => p.Prefab == BridgeLayout.StairRailPrefab && p.At.x > 6f));
        foreach (float across in new[] { -2f, 2f })
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.At, new Vector3(12f, across, 34.9f - 3f + 0.5f)) < 0.02f);
        Assert.Equal(6, plan.Count(p => p.Prefab == BridgeLayout.RailPostPrefab && p.At.x > 6.5f));
        Assert.DoesNotContain(plan, p => p.At.x > 12f + 0.02f);
        // The stairs laid are what the road is brought to: their foot is the far end's meeting point.
        Assert.Equal(12f, far.Meet.x, 3);
        Assert.Equal(34.9f - 3f, far.MeetHeight, 3);
        Assert.Equal((true, true), BridgeLayout.StairRunsLand(c, w));
    }

    /// <summary>
    /// One rule for how every structure's end stairs are laid (BridgeLayout.LayEndStairs from the end as the road meets it):
    /// each kit, run through the same meetings (the whole run, landed on a row, met level) lays at each end exactly the
    /// steps its end reports, their foot at its meeting point, the road's paint stopping there; the kits sharing the wooden
    /// ends on one crossing (both railed kits, the Dvergr wood kit, the stone kit, and their ford spans) lay the same steps
    /// to the same foot.
    /// </summary>
    [Theory]
    [InlineData("whole run")]
    [InlineData("landed")]
    [InlineData("level")]
    public void EveryKitLaysTheStairsItsEndIsMetWith(string meeting)
    {
        var (c, w) = Reference();
        w.Bank = LongBank;
        var wooden = new[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced, BridgeStructures.Kit.DvergrWood, BridgeStructures.Kit.Stone };
        Meet(c, meeting, w);
        Assert.Equal(meeting == "whole run" ? (7, 7) : meeting == "landed" ? (3, 3) : (0, 0),
            (BridgeStructures.Ends(c, w).near.Steps, BridgeStructures.Ends(c, w).far.Steps));
        Assert.Single(wooden.Select(kit => { c.Kit = kit; return AssertLaysItsEnds(c, w, kit.ToString()); }).Distinct());

        var (span, fw) = FordSpan();
        Meet(span, meeting, fw);
        Assert.Single(new[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.DvergrWood, BridgeStructures.Kit.Stone }
            .Select(kit => { span.Kit = kit; return AssertLaysItsEnds(span, fw, "ford span " + kit); }).Distinct());

        // The viaduct, its far flat 3 m under the near one: three steps down at its far end as it lays them.
        var valley = new DvergrViaductEndStairTests.StepValley { FarDrop = _ => 3f };
        _scope.WithWorld(valley);
        RoadCrossing v = RoadCrossing.Between(new Vector2(-24f, 0f), new Vector2(24f, 0f), 46f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        v.Kit = BridgeStructures.Kit.DvergrViaduct;
        Meet(v, meeting, valley);
        Assert.Equal(meeting == "whole run" ? 3 : meeting == "landed" ? 1 : 0, BridgeStructures.Ends(v, valley).far.Steps);
        AssertLaysItsEnds(v, valley, "viaduct");
    }

    /// <summary>Both ends of <paramref name="c"/> met as <paramref name="meeting"/> says: as the kit lays them, landed on
    /// the row halfway down their whole run, or met level along their line.</summary>
    private static void Meet(RoadCrossing c, string meeting, WorldGenerator world)
    {
        c.FromEnd = c.ToEnd = null;
        var (near, far) = StructureEnds.Natural(c, world);
        if (meeting == "landed") MetEnds.Set(c, world, fromRow: near.Steps / 2, toRow: far.Steps / 2);
        else if (meeting == "level") MetEnds.Set(c, world, fromLevel: true, toLevel: true);
    }

    /// <summary>
    /// Each end of <paramref name="c"/> as its kit lays it (the complete plan: nothing ruined away): as many steps as the
    /// end reports, the lowest at its meeting height and its foot edge at its meeting point; the road's points along the
    /// line unpainted exactly over the stretch past that foot the stairs stand on (StructureEnds.Unpainted). Returns the
    /// steps and feet, to compare kits by.
    /// </summary>
    private static string AssertLaysItsEnds(RoadCrossing c, WorldGenerator world, string kit)
    {
        var (near, far) = BridgeStructures.Ends(c, world);
        var stairs = BridgeStructures.SolveComplete(c, world, 1).FindAll(p => p.Kind == StructurePieceKind.Stair);
        string laid = "";
        foreach (var (end, isNear) in new[] { (near, true), (far, false) })
        {
            Vector2 bank = isNear ? c.FromBank : c.ToBank, other = isNear ? c.ToBank : c.FromBank;
            var flight = stairs.FindAll(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), bank) < Vector2.Distance(new Vector2(p.Position.x, p.Position.z), other));
            string which = $"{kit}, {(isNear ? "near" : "far")} end";
            Assert.True(flight.Count == 2 * end.Steps, $"{which}: {flight.Count / 2} steps laid, its end reports {end.Steps}");
            Vector2 foot = end.Edge;
            if (end.Steps > 0)
            {
                float lowest = flight.Min(p => p.Position.y);
                var bottom = flight.FindAll(p => Mathf.Abs(p.Position.y - lowest) < 0.01f);
                Assert.Equal(2, bottom.Count);
                foot = new Vector2(bottom.Average(p => p.Position.x), bottom.Average(p => p.Position.z)) - end.Up * BridgeLayout.StairHalfRun;
                Assert.True(Vector2.Distance(foot, end.Meet) < 0.02f, $"{which}: the stairs' foot {foot}, the end's meeting point {end.Meet}");
                Assert.Equal(end.MeetHeight, lowest, 3);
            }
            // The paint stops at the foot of the stairs laid; with none, at the deck's edge.
            var points = Enumerable.Range(-12, 33).Select(i => foot + end.Up * (i * 0.25f)).ToList();
            List<bool>? unpainted = StructureEnds.Unpainted(points, new[] { end }, 2f);
            for (int i = 0; i < points.Count; i++)
            {
                float along = (i - 12) * 0.25f;
                bool under = along > 0.25f + 1e-3f && along <= StructureEnds.JoinOverlap + 0.25f - 1e-3f;
                bool edge = Mathf.Abs(along - 0.25f) < 1e-3f || Mathf.Abs(along - StructureEnds.JoinOverlap - 0.25f) < 1e-3f;
                if (!edge) Assert.True(under == (unpainted?[i] ?? false), $"{which}: the road {along:F2} m past the end's meeting point is {(under ? "painted" : "unpainted")}");
            }
            laid += FormattableString.Invariant($"{end.Steps}@({foot.x:F2}, {foot.y:F2}) ");
        }
        return laid;
    }

    // ------------------------------------------------------ the height limit

    /// <summary>A plan's columns: its wood_pole2 stacks, by where they stand (along, across in the bridge's frame).</summary>
    private static Dictionary<(float along, float across), List<FramePiece>> Columns(IEnumerable<StructurePiece> plan) =>
        plan.Select(Frame).Where(p => p.Prefab == BridgeLayout.PostPrefab)
            .GroupBy(p => (Mathf.Round(p.At.x * 10f) / 10f + 0f, Mathf.Round(p.At.y * 10f) / 10f + 0f))   // + 0: no -0 key
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.At.z).ToList());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeckSixteenMetresOverTheGroundKeepsItsRailedKitWithColumnsToTheGround(bool braced)
    {
        var (c, w) = Reference();
        // Deck 34.9 m, the bed 10 m under the reference's: 18.9 m from -2 to 4, the columns there 16 m.
        w.Drop = 10f;
        Assert.NotNull(BridgeStructures.RailedKitOf(BridgeStructures.KitFor(c, w)));

        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        var columns = Columns(BridgeLayout.SolveRailedComplete(c, w, 1, kit));
        Assert.Equal(braced ? 8 : 14, columns.Count);
        foreach (var column in columns)
        {
            var (along, across) = column.Key;
            var poles = column.Value;
            float ground = w.GetHeight(along, -across);
            // Stacked from the deck down, each on the one under it, to a foot in the ground and none wholly under it.
            for (int k = 0; k < poles.Count; k++)
                Assert.Equal(34.9f - 1f - 2f * k, poles[k].At.z, 2);
            float foot = poles[poles.Count - 1].At.z - 1f;
            Assert.True(foot <= ground + BuriedPieces.Growth, $"column at ({along}, {across}) stops {foot - ground:F2} m over the ground");
            Assert.True(foot + 2f > ground, $"column at ({along}, {across}) ends in a pole wholly under the ground");
        }
        // The middle columns: eight poles, 16 m.
        Assert.Equal(8, columns[(braced ? 2f : 0f, 2f)].Count);
        // Kit B's one tier of braces stays right under the deck.
        if (braced)
            Assert.All(BridgeLayout.SolveRailedComplete(c, w, 1, kit).Where(p => p.Prefab == BridgeLayout.BracePrefab),
                p => Assert.Equal(34.9f - BridgeLayout.BraceRise, p.Position.y, 2));
    }

    /// <summary>
    /// Over the wood support ceiling a station has no column, and the deck is carried over the run without one from the
    /// columns either side, as a free span is, when they stand within the free span (16 m) of each other. The deck at
    /// 34.9 m, the bed between -2 and 4 lowered 12.5 m: columns there would be 18.5 m, over the 18 m most; at -4 (16.5 m) and
    /// at the banks they stand. The run from -4 to 6 is 10 m: carried, plates, stringers and rails over it, nothing under it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverTheWoodSupportCeilingAStationHasNoColumnAndTheDeckIsCarriedOverIt(bool braced)
    {
        var (c, w) = Reference();
        Assert.Equal(RoadConstants.MaxBridgePierHeight, BridgeLayout.RailedMaxColumn);
        w.Drop = 12.5f;
        Assert.NotNull(BridgeStructures.RailedKitOf(BridgeStructures.KitFor(c, w)));
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        foreach (var plan in new[] { BridgeLayout.SolveRailedComplete(c, w, 1, kit), BridgeLayout.SolveRailed(c, w, 1, kit) })
        {
            var columns = Columns(plan);
            Assert.DoesNotContain(columns.Keys, k => k.along > -3f && k.along < 5f);
            Assert.All(columns.Keys, k => Assert.True(34.9f - w.GetHeight(k.along, -k.across) <= BridgeLayout.RailedMaxColumn, $"the column at {k} stands over the ceiling"));
            Assert.Contains(columns.Keys, k => k.along == -4f);
            Assert.Contains(columns.Keys, k => k.along == 6f);
            // Nothing under the deck between them but the bed's debris; no braces hang into the run.
            Assert.DoesNotContain(plan, p => p.Kind != StructurePieceKind.Debris && p.Position.y < 34.9f - 0.01f
                && p.Position.x > -3f && p.Position.x < 5f && p.Prefab != BridgeLayout.BracePrefab);
            Assert.DoesNotContain(plan, p => p.Prefab == BridgeLayout.BracePrefab && p.Position.x > -3f && p.Position.x < 5f);
        }
        // The complete deck runs bank to bank over it, railed, and every piece is on a neighbour's snap point.
        var complete = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        Assert.Equal(12, complete.Count(p => p.Prefab == BridgeLayout.DeckPrefab));
        Assert.Equal(12, complete.Count(p => p.Prefab == BridgeLayout.BeamPrefab && Mathf.Abs(p.Position.y - 34.9f - BridgeLayout.RailHeight) < 0.01f));
        AssertEverySnaps(complete);
    }

    /// <summary>A run without a column longer than the free span would fall: its deck is left open, the rails stopping on
    /// newels over the columns either side. The deck at 34.9 m over a 40 m crossing whose bed between -14 and 14 lies 20 m
    /// under it: the 32 m between the standing columns is no free span.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARunWithoutAColumnPastTheFreeSpanIsLeftOpen(bool braced)
    {
        var w = new CanyonWorld();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-20f, 0f), new Vector2(20f, 0f), 14.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.4f;
        Assert.False(BridgeLayout.IsFreeSpan(c, w));
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, braced ? BridgeLayout.Braced : BridgeLayout.Rail);
        Assert.DoesNotContain(plan, p => p.Kind != StructurePieceKind.Debris && Mathf.Abs(p.Position.x) < 15f);
        // Railing posts over the last columns each side, the deck up to them.
        foreach (float edge in new[] { -16f, 16f })
        {
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.PostPrefab && Mathf.Abs(p.Position.x - edge) < 0.01f);
            Assert.Equal(2, plan.Count(p => p.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(p.Position.x - edge) < 0.01f && Mathf.Abs(p.Position.y - 35.4f) < 0.01f));
        }
        Assert.Contains(plan, p => p.Prefab == BridgeLayout.DeckPrefab);
        AssertEverySnaps(plan);
    }

    /// <summary>A Meadows canyon 30 m wide, its floor at 14.9 m, between banks at 33.9 m.</summary>
    private sealed class CanyonWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 15f ? 14.9f : 33.9f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 76;
    }

    /// <summary>A free span (16 m or less over a channel that takes no pier) is railed: columns at its banks only, its deck
    /// and rails carried bank to bank, no stub or debris in the channel.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFreeSpanIsRailedBankToBankWithNothingInTheChannel(bool braced)
    {
        // The reference's 12 m with its bed 20 m under the deck: a pier there would stand over the ceiling.
        var w = new ReferenceWorld { Drop = 20f };
        _scope.WithWorld(w);
        var c = ReferenceCrossing();
        c.RiverbedHeight = 8.9f;
        Assert.True(BridgeLayout.IsFreeSpan(c, w));
        Assert.NotNull(BridgeStructures.RailedKitOf(BridgeStructures.KitFor(c, w)));
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        for (int seed = 1; seed <= 10; seed++)
        {
            var plan = BridgeLayout.SolveRailed(c, w, seed, kit);
            Assert.DoesNotContain(plan, p => p.Position.y < 34.9f - BridgeLayout.BraceRise - 0.01f && Mathf.Abs(p.Position.x) < 5.9f);
            Assert.DoesNotContain(plan, p => p.Kind == StructurePieceKind.Debris);
            Assert.All(Columns(plan).Keys, k => Assert.Equal(6f, Mathf.Abs(k.along)));
            // The end bays' plates always stand, so the deck is carried from both banks.
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.DeckPrefab && Mathf.Abs(p.Position.x + 5f) < 0.01f);
            Assert.Contains(plan, p => p.Prefab == BridgeLayout.DeckPrefab && Mathf.Abs(p.Position.x - 5f) < 0.01f);
        }
        var complete = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        Assert.Equal(12, complete.Count(p => p.Prefab == BridgeLayout.DeckPrefab));
        AssertEverySnaps(complete);
    }

    // ------------------------------------------------------- the bank columns

    /// <summary>A bank standing 0.1 m under the deck across its end station (+-6) and to +-6.5, the reference's ground
    /// beyond: the one step down lands on it, and the road, levelled to the step's foot, runs on under the step to the deck's
    /// end, cutting the bank away there.</summary>
    private static float CutBank(float x, float z) => Mathf.Abs(x) <= 6.5f ? 34.8f : 33.9f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABankColumnReachesTheRoadLevelledUnderTheEnd(bool braced)
    {
        var (c, w) = Reference();
        w.Hill = CutBank;
        var (near, far) = BridgeStructures.Ends(c, w);
        Assert.Equal(1, near.Steps);
        Assert.Equal(1, far.Steps);
        // The generator's bank stands within the ground's grip of the deck: no pole there on it. On the road's cut, one.
        var columns = Columns(BridgeLayout.SolveRailedComplete(c, w, 1, braced ? BridgeLayout.Braced : BridgeLayout.Rail));
        foreach (float along in new[] { -6f, 6f })
            foreach (float across in new[] { -2f, 2f })
            {
                FramePiece pole = Assert.Single(columns[(along, across)]);
                Assert.Equal(33.9f, pole.At.z, 2);   // 34.9 down to 32.9: its foot under the road at 33.9, its top out of it
            }
    }

    /// <summary>Every bridge and ford's span but the Mistlands' is railed, or from the Black Forest on stone where that fits
    /// (StoneBridgeTests); a Meadows one always railed.</summary>
    [Fact]
    public void EveryBridgeAndFordSpanButTheMistlandsIsRailedOrStone()
    {
        var (c, w) = Reference();
        var (ford, _) = FordSpan();
        _scope.WithWorld(w);
        foreach (Heightmap.Biome b in new[] { Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Plains,
            Heightmap.Biome.Mountain, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean })
        {
            w.Biome = b;
            var kits = b == Heightmap.Biome.Meadows
                ? new BridgeStructures.Kit?[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced }
                : new BridgeStructures.Kit?[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced, BridgeStructures.Kit.Stone };
            Assert.Contains(BridgeStructures.KitFor(c, w), kits);
            Assert.Contains(BridgeStructures.KitFor(ford, w), kits);
        }
        // Built in it: a Mountain bridge's railing posts and handrails where it draws a railed kit.
        w.Biome = Heightmap.Biome.Mountain;
        for (w.Seed = 76; BridgeStructures.KitFor(c, w) == BridgeStructures.Kit.Stone; w.Seed++) { }
        Assert.Contains(BridgeStructures.Solve(c, w, 1), p => p.Prefab == BridgeLayout.RailPostPrefab);
        Assert.Contains(BridgeStructures.Solve(c, w, 1), p => p.Prefab == BridgeLayout.BeamPrefab && Mathf.Abs(p.Position.y - (34.9f + BridgeLayout.RailHeight)) < 0.01f);
        w.Biome = Heightmap.Biome.Mistlands;
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, w));
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(ford, w));
    }

    // ------------------------------------------------------------ a ford's span

    /// <summary>A shallow ford 12 m between banks at 30.5 m, its bed at 29.5 m, the water at 30 m: a span's deck stands 1 m
    /// over the higher bank (<see cref="BridgeLayout.SpanDeckHeight"/>), at 31.5 m, a step over the road's floor.</summary>
    private sealed class FordWorld : WorldGenerator
    {
        public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 6f ? 29.5f : 30.5f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 76;
    }

    private (RoadCrossing c, FordWorld w) FordSpan()
    {
        var w = new FordWorld();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 29.5f, new Vector2(0f, 0f), 0f, CrossingKind.Ford, FordStyle.Span);
        c.WaterLevel = 30f;
        return (c, w);
    }

    /// <summary>
    /// A ford's span in either railed kit, as a bridge is laid: plates and stringers at the footbridge's deck (1 m over the
    /// banks), a railing post at every carried station each side under a handrail, columns from the deck to the ford's bed,
    /// and at each end the one step down to the bank, railed, its newel at its foot. No navigation gap (a ford has none).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFordSpanIsLaidInTheRailedKitOnItsFootbridgesDeck(bool braced)
    {
        var (c, w) = FordSpan();
        float deck = BridgeLayout.SpanDeckHeight(c, w);
        Assert.Equal(31.5f, deck, 3);
        Assert.Equal(deck, BridgeStructures.DeckHeight(c, w), 3);
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        var plan = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        var frame = plan.Select(Frame).ToList();
        // Two plates a bay over its six bays, at the deck.
        Assert.Equal(12, frame.Count(p => p.Prefab == BridgeLayout.DeckPrefab));
        Assert.All(frame.Where(p => p.Prefab == BridgeLayout.DeckPrefab), p => Assert.Equal(deck, p.At.z, 3));
        // Stringers at the deck and handrails over them, both edges, every bay.
        Assert.Equal(12, frame.Count(p => p.Prefab == BridgeLayout.BeamPrefab && Mathf.Abs(p.At.z - deck) < 0.01f));
        Assert.Equal(12, frame.Count(p => p.Prefab == BridgeLayout.BeamPrefab && Mathf.Abs(p.At.z - deck - BridgeLayout.RailHeight) < 0.01f));
        // A railing post at every station each side over the deck.
        Assert.Equal(14, frame.Count(p => p.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(p.At.z - deck - 0.5f) < 0.01f));
        // Columns over the bed: one 2 m pole from the deck to the bed at 29.5 m, under both edges, at every column station.
        var columns = Columns(plan).Where(k => Mathf.Abs(k.Key.along) < 5.9f).ToList();
        Assert.Equal(braced ? 4 : 10, columns.Count);
        Assert.All(columns, k => Assert.Equal(30.5f, Assert.Single(k.Value).At.z, 3));
        // One step down at each end, landed on the bank, railed each side with its newel at the foot.
        var (near, far) = BridgeStructures.Ends(c, w);
        Assert.Equal(1, near.Steps);
        Assert.Equal(1, far.Steps);
        Assert.Equal(4, frame.Count(p => p.Prefab == BridgeLayout.StairPrefab));
        Assert.Equal(8, frame.Count(p => p.Prefab == BridgeLayout.StairRailPrefab));
        foreach (float end in new[] { -8f, 8f })
            Assert.Equal(2, frame.Count(p => p.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(p.At.x - end) < 0.01f));
        Assert.Null(BridgeLayout.NavigationGap(c, w));
        // The kit the owner picks lays it, ruined.
        Assert.NotNull(BridgeStructures.RailedKitOf(BridgeStructures.KitFor(c, w)));
        Assert.Contains(BridgeStructures.Solve(c, w, 1), p => p.Prefab == BridgeLayout.RailPostPrefab);
    }

    /// <summary>Every piece of a ford's span, in either kit, complete and ruined, has a snap point on another's.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPieceOfAFordSpanSnapsToANeighbour(bool braced)
    {
        var (c, w) = FordSpan();
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        var complete = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        AssertEverySnaps(complete);
        for (int seed = 1; seed <= 5; seed++)
            AssertEverySnaps(BridgeLayout.SolveRailed(c, w, seed, kit).Where(p => p.Kind != StructurePieceKind.Debris).ToList(), complete);
    }

    /// <summary>Every piece on another's snap point (within 2 cm); with the <paramref name="complete"/> plan of a ruin, a
    /// lone stub (a pole standing on the ground, all a fallen column leaves, which the complete plan has not) is let be.</summary>
    private static void AssertEverySnaps(List<StructurePiece> plan, List<StructurePiece>? complete = null)
    {
        var keys = new HashSet<string>((complete ?? plan).Select(Key));
        var snaps = plan.Select(BridgeLayout.SnapPoints).ToList();
        var loose = new List<string>();
        for (int i = 0; i < plan.Count; i++)
        {
            Assert.NotEmpty(snaps[i]);
            bool met = false;
            for (int j = 0; j < plan.Count && !met; j++)
                if (j != i)
                    met = snaps[i].Any(a => snaps[j].Any(b => Vector3.Distance(a, b) < 0.02f));
            if (!met && !(plan[i].Prefab == BridgeLayout.PostPrefab && !keys.Contains(Key(plan[i])))) loose.Add(Frame(plan[i]).ToString());
        }
        Assert.True(loose.Count == 0, "pieces on no neighbour's snap point:\n" + string.Join("\n", loose));
    }

    // ------------------------------------------------------- the navigation gap

    /// <summary>A Meadows river 3 m deep, 34 m between banks at 31 m: the deck 1 m over the water, under a longship's
    /// hull, so the deck opens over its way.</summary>
    private sealed class MeadowsRiver : WorldGenerator
    {
        public float Bank = 31f;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 16f ? 27f : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < 16f ? 1f : 0f;
            width = 32f;
        }
        public override int GetSeed() => 76;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtTheNavigationGapTheRailsStopOnANewelEachSideAndNothingStandsInIt(bool braced)
    {
        var w = new MeadowsRiver();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-17f, 0f), new Vector2(17f, 0f), 27f, new Vector2(0f, 0f), 30f, CrossingKind.Bridge);
        BridgeLayout.ShipWay way = Assert.IsType<BridgeLayout.ShipWay>(BridgeLayout.NavigationGap(c, w));
        Assert.True(way.DeckWidth > 0f, "the deck should open over the ship's way");
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        float deck = BridgeLayout.DeckHeight(c, w);
        int edgesSeen = 0;
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            var plan = BridgeLayout.SolveRailed(c, w, seed, kit);
            float Along(StructurePiece p) => c.Along(new Vector2(p.Position.x, p.Position.z));
            float half = way.PierWidth * 0.5f + BridgeLayout.FairwayClearance;
            // Nothing in the way kept open, the gap's own pieces and the bed's debris alike.
            Assert.DoesNotContain(plan, p => p.Kind != StructurePieceKind.Debris && Mathf.Abs(Along(p) - way.Centre) < half - 1f);
            // The open bays' ends: the last stations that stand, a railing post each side over the walking surface.
            var (first, count, _) = BridgeLayout.RailedNavigationBays(c, w, kit)!.Value;
            float lo = first * BridgeLayout.BayLength(kit), hi = (first + count) * BridgeLayout.BayLength(kit);
            foreach (float edge in new[] { lo, hi })
            {
                // Where the bay outward of the edge still stands (ruin may take it, and the rails with it).
                float outward = edge < way.Centre ? edge - 1f : edge + 1f;
                if (!plan.Any(p => p.Prefab == BridgeLayout.DeckPrefab && Mathf.Abs(Along(p) - outward) < 0.01f)) continue;
                edgesSeen++;
                var newels = plan.Where(p => p.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(Along(p) - edge) < 0.01f
                    && Mathf.Abs(p.Position.y - (deck + 0.5f)) < 0.01f).ToList();
                Assert.Equal(2, newels.Count);
                // Its column stands, and the stringers stop on it: none reaches over the gap.
                Assert.Contains(plan, p => p.Prefab == BridgeLayout.PostPrefab && Mathf.Abs(Along(p) - edge) < 0.01f);
            }
            Assert.DoesNotContain(plan, p => p.Prefab == BridgeLayout.BeamPrefab && Along(p) > lo && Along(p) < hi);
        }
        Assert.True(edgesSeen >= 5, $"only {edgesSeen} gap edges of 10 kept their outward bay");
        // The complete plan fills the gap in: a player may close it.
        var complete = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        Assert.Contains(complete, p => p.Prefab == BridgeLayout.DeckPrefab && Mathf.Abs(c.Along(new Vector2(p.Position.x, p.Position.z)) - way.Centre) < 2f);
    }

    /// <summary>
    /// The opening over a river is whole bays of each kit's own grid (2 m for kit A, 4 m for kit B, from the near deck end):
    /// the fewest that hold the ship's way and its clearance each side past half a pole, placed nearest the way's centre, a
    /// column standing at both ends and none between. Over the deck the ship clears, the same bays without their columns.
    /// </summary>
    [Theory]
    [InlineData(false, 17f)]
    [InlineData(true, 17f)]
    [InlineData(false, 18f)]
    [InlineData(true, 18f)]
    public void TheNavigationGapIsTheFewestWholeBaysOfTheKitsOwnGrid(bool braced, float half)
    {
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        // The deck 1 m over the water at banks of 31 m: the ship meets it. At banks of 43 m it passes under. Crossings 34 m
        // and 36 m long put the river's middle on and off the kits' grids.
        foreach (float bank in new[] { 31f, 43f })
        {
            var w = new MeadowsRiver { Bank = bank };
            _scope.WithWorld(w);
            var c = RoadCrossing.Between(new Vector2(-half, 0f), new Vector2(half, 0f), 27f, new Vector2(0f, 0f), 30f, CrossingKind.Bridge);
            BridgeLayout.ShipWay way = BridgeLayout.NavigationGap(c, w)!.Value;
            var (first, count, deckOpen) = BridgeLayout.RailedNavigationBays(c, w, kit)!.Value;
            Assert.Equal(way.DeckWidth > 0f, deckOpen);
            float bay = BridgeLayout.BayLength(kit);
            Assert.Equal(braced ? 4f : 2f, bay);
            float need = way.PierWidth + 2f * BridgeLayout.FairwayClearance + BridgeLayout.PoleWidth;
            // Holds the way, and one bay fewer would not.
            Assert.True(count * bay >= need, $"{count} bays of {bay} m do not hold {need:F1} m");
            Assert.True((count - 1) * bay < need, $"{count - 1} bays of {bay} m already hold {need:F1} m");
            // Nearest the way's centre: within half a bay of it.
            Assert.InRange((first + count * 0.5f) * bay - way.Centre, -bay * 0.5f - 1e-3f, bay * 0.5f + 1e-3f);
            float lo = first * bay, hi = (first + count) * bay;
            foreach (var plan in new[] { BridgeLayout.SolveRailed(c, w, 1, kit), BridgeLayout.SolveRailedComplete(c, w, 1, kit) })
            {
                bool complete = plan.Count == BridgeLayout.SolveRailedComplete(c, w, 1, kit).Count;
                float Along(StructurePiece p) => c.Along(new Vector2(p.Position.x, p.Position.z));
                // A column at both ends, on the kit's grid.
                foreach (float edge in new[] { lo, hi })
                    Assert.Contains(plan, p => p.Prefab == BridgeLayout.PostPrefab && p.Kind == StructurePieceKind.Post && Mathf.Abs(Along(p) - edge) < 0.01f);
                // None between, ruined or (under a deck the ship clears) repaired.
                if (!complete || !deckOpen)
                    Assert.DoesNotContain(plan, p => p.Prefab == BridgeLayout.PostPrefab && p.Kind == StructurePieceKind.Post && Along(p) > lo + 0.01f && Along(p) < hi - 0.01f);
                // The deck: open over the bays in the ruin where the ship meets it, else carried over them.
                bool decked = plan.Any(p => p.Prefab == BridgeLayout.DeckPrefab && Along(p) > lo && Along(p) < hi);
                Assert.Equal(complete || !deckOpen, decked);
            }
        }
    }

    // ------------------------------------------------------------------ ruin

    private static string Key(StructurePiece p) => FormattableString.Invariant($"{p.Prefab} {p.Position.x:F2} {p.Position.y:F2} {p.Position.z:F2}");

    /// <summary>
    /// The ruin: fixed by the seed and the crossing; every piece standing is the complete bridge's but a fallen column's
    /// stub or debris, worn as a vanilla location's (<see cref="BridgeLayout.LocationDamageMin"/> to
    /// <see cref="BridgeLayout.LocationDamageMax"/>); the ends, their stairs and rails and the bank bays always stand.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRuinIsTheSharedRuinAndTheCompletePlanIsTheReference(bool braced)
    {
        // A longer crossing over the reference's ground: interior stations to fall.
        var w = new ReferenceWorld { Bank = a => a < 24f ? 28.9f : 33.9f };
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-24f, 0f), new Vector2(24f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.4f;
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        var complete = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        var keys = new HashSet<string>(complete.Select(Key));
        float deck = BridgeStructures.DeckHeight(c, w);
        bool BankBay(StructurePiece p) => p.Position.y >= deck - 0.01f && (p.Position.x < -24f + 2f + 0.01f || p.Position.x > 24f - 2f - 0.01f);
        int lostSomething = 0;
        for (int seed = 1; seed <= 20; seed++)
        {
            var ruined = BridgeLayout.SolveRailed(c, w, seed, kit);
            // Deterministic by the seed and the crossing.
            Assert.Equal(ruined.Select(Key), BridgeLayout.SolveRailed(c, w, seed, kit).Select(Key));
            Assert.Equal(ruined.Select(p => p.HealthFraction), BridgeLayout.SolveRailed(c, w, seed, kit).Select(p => p.HealthFraction));
            // Every piece that stands is the complete bridge's, worn; only the stubs and debris of a fallen column are not.
            foreach (StructurePiece p in ruined)
            {
                Assert.InRange(p.HealthFraction, BridgeLayout.LocationDamageMin, BridgeLayout.LocationDamageMax);
                if (p.Kind != StructurePieceKind.Debris && !keys.Contains(Key(p)))
                    Assert.True(p.Prefab == BridgeLayout.PostPrefab && p.Position.y < c.WaterLevel, $"{Key(p)} is not the complete bridge's");
            }
            if (ruined.Count(p => keys.Contains(Key(p))) < complete.Count) lostSomething++;
            // The ends always stand: both stair runs and their rails, and the deck and railing over the bank bays.
            Assert.Equal(complete.Count(p => p.Prefab == BridgeLayout.StairPrefab), ruined.Count(p => p.Prefab == BridgeLayout.StairPrefab));
            Assert.Equal(complete.Count(p => p.Prefab == BridgeLayout.StairRailPrefab), ruined.Count(p => p.Prefab == BridgeLayout.StairRailPrefab));
            Assert.Equal(complete.Count(BankBay), ruined.Count(BankBay));
        }
        Assert.True(lostSomething >= 15, $"only {lostSomething} of 20 seeds ruined anything");
    }

    /// <summary>The long crossing the rates are read on: 80 m, 40 bays, no river.</summary>
    private (RoadCrossing c, ReferenceWorld w) LongCrossing()
    {
        var w = new ReferenceWorld { Bank = a => a < 40f ? 28.9f : 33.9f };
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-40f, 0f), new Vector2(40f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.4f;
        return (c, w);
    }

    /// <summary>
    /// A railed kit ruins at every kit's shares (<see cref="BridgeLayout.BridgeRuin"/>): over an 80 m deck and 300 seeds,
    /// the plates lost at the deck share and a bay's two at the hole share, the handrails at the course share (their posts
    /// less, as one under a handrail left is put back), the braces at the support share (and with a fallen column under
    /// them); never a plate over a bank bay, never two bays running without a plate; a fallen column takes no deck with it.
    /// On average about 6 % of the plates are gone (the deck share's) where the wooden ruin took about 19 %.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARailedKitRuinsAtTheSharedProfileWithOnlyJumpableGaps(bool braced)
    {
        var (c, w) = LongCrossing();
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        BridgeLayout.RuinProfile shares = BridgeLayout.BridgeRuin;
        float deck = BridgeStructures.DeckHeight(c, w);
        int last = BridgeLayout.Bays(c.Width);
        float Survives(float at, float share) =>
            BridgeLayout.RuinProfile.Survives(BridgeLayout.SpanSurvival(1f - Mathf.Abs(at - last * 0.5f) / (last * 0.5f)), share);
        int BayOf(StructurePiece p) => Mathf.FloorToInt((p.Position.x + 40f) / BridgeLayout.DeckSpan);
        bool IsPlate(StructurePiece p) => p.Prefab == BridgeLayout.DeckPrefab;
        bool IsHandrail(StructurePiece p) => p.Prefab == BridgeLayout.BeamPrefab && Mathf.Abs(p.Position.y - (deck + BridgeLayout.RailHeight)) < 0.01f;
        bool IsDeckPost(StructurePiece p) => p.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(p.Position.x) < 40.01f;
        bool IsBrace(StructurePiece p) => p.Prefab == BridgeLayout.BracePrefab;

        var complete = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        Assert.Equal(2 * last, complete.Count(IsPlate));
        float expectPlates = 0f, expectHoles = 0f, expectRails = 0f, expectBraces = 0f;
        for (int j = 1; j < last - 1; j++)
        {
            float open = 1f - Survives(j + 0.5f, shares.HoleShare);
            expectHoles += open;
            expectPlates += 2f * (1f - Survives(j + 0.5f, shares.DeckShare) * (1f - open));
            expectRails += 2f * (1f - Survives(j + 0.5f, shares.CourseShare));
            if (braced) expectBraces += 2f * (1f - Survives(j + 0.5f, shares.SupportShare));
        }
        var completeKeys = new HashSet<string>(complete.Select(Key));
        int plates = complete.Count(IsPlate), rails = complete.Count(IsHandrail), posts = complete.Count(IsDeckPost), braces = complete.Count(IsBrace);
        int lostPlates = 0, lostRails = 0, lostPosts = 0, lostBraces = 0, holes = 0, fallen = 0;
        const int Seeds = 300;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            var ruined = BridgeLayout.SolveRailed(c, w, seed, kit);
            lostPlates += plates - ruined.Count(IsPlate);
            lostRails += rails - ruined.Count(IsHandrail);
            lostPosts += posts - ruined.Count(IsDeckPost);
            lostBraces += braces - ruined.Count(IsBrace);
            fallen += ruined.Count(p => !completeKeys.Contains(Key(p)));
            var plated = new int[last];
            foreach (StructurePiece p in ruined.Where(IsPlate)) plated[BayOf(p)]++;
            // Every bay keeps its stringers (the deck stands over every fallen column).
            Assert.Equal(complete.Count(p => p.Prefab == BridgeLayout.BeamPrefab && Mathf.Abs(p.Position.y - deck) < 0.01f),
                ruined.Count(p => p.Prefab == BridgeLayout.BeamPrefab && Mathf.Abs(p.Position.y - deck) < 0.01f));
            for (int j = 0; j < last; j++)
            {
                if (plated[j] > 0) continue;
                Assert.True(j > 0 && j < last - 1, $"seed {seed}: a plate over a bank bay lost");
                Assert.True(plated[j - 1] > 0, $"seed {seed}: bays {j - 1} and {j} both open from side to side: not a gap a player jumps");
                holes++;
            }
        }
        void Rate(string what, int lost, float expected, float low, float high)
        {
            float rate = lost / (float)Seeds;
            Assert.True(rate >= expected * low && rate <= expected * high,
                FormattableString.Invariant($"{what}: {rate:F2} lost a seed, expected {expected:F2} ({low}-{high} of it)"));
        }
        Rate("plates", lostPlates, expectPlates, 0.85f, 1.15f);
        Rate("open bays", holes, expectHoles, 0.7f, 1.3f);
        Rate("handrails", lostRails, expectRails, 0.85f, 1.15f);
        Assert.True(lostPosts < lostRails, $"posts lost {lostPosts}, handrails {lostRails}: a post under a handrail left is put back");
        Assert.True(fallen > 0, "no column fell in 300 seeds");
        if (braced)
            // The braces at the support share, and a few more with the columns that fell under them.
            Rate("braces", lostBraces, expectBraces, 0.9f, 1.25f);
        Assert.InRange(lostPlates / (float)(Seeds * plates), 0.04f, 0.08f);
    }

    /// <summary>Every piece of a ruined railed kit over the long crossing, 40 seeds, rests on a neighbour's snap point as the
    /// complete kit's do, but a fallen column's stub or debris, which lies in the bed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPieceOfTheRuinSnapsToANeighbour(bool braced)
    {
        var kit = braced ? BridgeLayout.Braced : BridgeLayout.Rail;
        var (c, w) = LongCrossing();
        var complete = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
        for (int seed = 1; seed <= 40; seed++)
        {
            var ruin = BridgeLayout.SolveRailed(c, w, seed, kit);
            // A stub: a post broken off at the water, nothing over it, standing on the bed.
            bool Stub(StructurePiece p) => p.Prefab == BridgeLayout.PostPrefab
                && p.Position.y + BridgeLayout.PostSegment * 0.5f <= c.WaterLevel + 0.3f + 1e-3f
                && !ruin.Any(q => q.Prefab == BridgeLayout.PostPrefab && q.Position.y > p.Position.y + 0.01f
                    && Mathf.Abs(q.Position.x - p.Position.x) < 0.01f && Mathf.Abs(q.Position.z - p.Position.z) < 0.01f);
            AssertEverySnaps(ruin.Where(p => p.Kind != StructurePieceKind.Debris && !Stub(p)).ToList(), complete);
        }
    }

    [Fact]
    public void BridgeStructuresBuildsTheRuinedKitItChose()
    {
        var (c, w) = Reference();
        BridgeStructures.Kit? kit = BridgeStructures.KitFor(c, w);
        BridgeLayout.RailedKit railed = Assert.IsType<BridgeLayout.RailedKit>(BridgeStructures.RailedKitOf(kit));
        Assert.Equal(BridgeLayout.SolveRailed(c, w, 7, railed).Select(Key), BridgeStructures.Solve(c, w, 7).Select(Key));
        Assert.Equal(kit.ToString(), railed.Name);
    }

    // ------------------------------------------------------------ kit choice

    [Fact]
    public void KitAOrBIsHalfAndHalfBySeedAndStable()
    {
        var w = new ReferenceWorld();
        _scope.WithWorld(w);
        int rail = 0, n = 400;
        for (int i = 0; i < n; i++)
        {
            var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
            w.Seed = 1000 + i;
            BridgeStructures.Kit? k = BridgeStructures.KitFor(c, w);
            Assert.Equal(k, BridgeStructures.KitFor(c, w));
            if (k == BridgeStructures.Kit.WoodRail) rail++;
            else Assert.Equal(BridgeStructures.Kit.WoodBraced, k);
        }
        Assert.InRange(rail, n * 0.4f, n * 0.6f);
        // Per crossing too: one world seed, crossings at different places.
        w.Seed = 76;
        rail = 0;
        for (int i = 0; i < n; i++)
        {
            var c = RoadCrossing.Between(new Vector2(-6f + i * 0.1f, 0f), new Vector2(6f + i * 0.1f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
            if (BridgeLayout.PickKit(c, w.Seed, new[] { BridgeLayout.Rail, BridgeLayout.Braced }) == BridgeLayout.Rail) rail++;
        }
        Assert.InRange(rail, n * 0.4f, n * 0.6f);
    }

    /// <summary>The record stores a kit as its number and one: the numbers are fixed, and 0 (the retired plain wooden kit,
    /// stored as 1) names no kit any more, so a record carrying it is refused as any unknown kit is.</summary>
    [Fact]
    public void TheKitNumbersAreFixedAndTheRetiredOneIsNoKit()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, new[] { BridgeStructures.Kit.DvergrWood, BridgeStructures.Kit.DvergrViaduct,
            BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced, BridgeStructures.Kit.Stone }.Select(k => (int)k));
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.Kit = BridgeStructures.Kit.WoodRail;
        byte[] bytes = RoadNetworkPersistence.SerializeRoadCrossings(new[] { c });
        // The kit is the crossing's twelfth number, after the record's format and count.
        const int kitAt = 8 + 11 * 4;
        Assert.Equal((int)BridgeStructures.Kit.WoodRail + 1, BitConverter.ToInt32(bytes, kitAt));
        BitConverter.GetBytes(1).CopyTo(bytes, kitAt);
        Assert.False(RoadNetworkPersistence.DeserializeRoadCrossings(bytes, new List<RoadCrossing>()));
    }

    [Theory]
    [InlineData(BridgeStructures.Kit.WoodRail)]
    [InlineData(BridgeStructures.Kit.WoodBraced)]
    [InlineData(BridgeStructures.Kit.DvergrWood)]
    [InlineData(BridgeStructures.Kit.Stone)]
    public void TheKitIsKeptWithTheCrossingRecordAndNamedInIt(BridgeStructures.Kit kit)
    {
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.Kit = kit;
        byte[] bytes = RoadNetworkPersistence.SerializeRoadCrossings(new[] { c });
        Assert.Equal(RoadNetworkPersistence.CrossingsFormat, BitConverter.ToInt32(bytes, 0));
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(bytes, loaded));
        RoadCrossing back = Assert.Single(loaded);
        Assert.Equal(kit, back.Kit);
        Assert.EndsWith(", " + kit, back.ToString());
        // Kept whatever the rules would choose now.
        var swamp = new ReferenceWorld { Biome = Heightmap.Biome.Swamp };
        _scope.WithWorld(swamp);
        Assert.Equal(kit, BridgeStructures.KitFor(back, swamp));
    }

    // ------------------------------------------------------------ the kit chosen at the first build

    /// <summary>The network stores a deck's kind, not its kit (BridgeStructures.DecideKind): a bridge that is not a viaduct
    /// is stored undecided, and its first build chooses the kit KitFor picks among the kits installed then
    /// (BridgeStructures.ChooseKit), once.</summary>
    [Fact]
    public void ACrossingIsStoredWithoutAKitAndGetsOneWhenFirstBuilt()
    {
        var (c, w) = Reference();
        w.Biome = Heightmap.Biome.Mountain;
        BridgeStructures.DecideKind(c, w);
        Assert.Equal(BridgeStructures.Kit.Undecided, c.Kit);
        BridgeStructures.Kit? first = BridgeStructures.KitFor(c, w);
        Assert.Contains(first, BridgeStructures.KitsThatFit(c, w).Cast<BridgeStructures.Kit?>());
        Assert.True(BridgeStructures.ChooseKit(c, w, new[] { c }));
        Assert.Equal(first, c.Kit);
        Assert.False(BridgeStructures.ChooseKit(c, w, new[] { c }));
        Assert.Equal(first, c.Kit);
        // A wading ford stays road: no kit, none chosen.
        var wade = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Ford, FordStyle.Wade);
        BridgeStructures.DecideKind(wade, w);
        Assert.Null(wade.Kit);
        Assert.False(BridgeStructures.ChooseKit(wade, w, new[] { wade }));
    }

    /// <summary>The kit chosen for a site goes on every record of it (another road's crossing snapped to it), so the
    /// bridge's other zones, the repair report and the saved crossings agree; a record with a kit is not changed.</summary>
    [Fact]
    public void EveryRecordOfTheSiteGetsTheKitChosen()
    {
        var (c, w) = Reference();
        w.Biome = Heightmap.Biome.Mountain;
        BridgeStructures.DecideKind(c, w);
        var other = RoadCrossing.Between(new Vector2(6f, 0f), new Vector2(-6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        other.SnapTo(c);
        Assert.Equal(BridgeStructures.Kit.Undecided, other.Kit);
        var elsewhere = RoadCrossing.Between(new Vector2(94f, 0f), new Vector2(106f, 0f), 28.9f, new Vector2(100f, 0f), 0f, CrossingKind.Bridge);
        BridgeStructures.DecideKind(elsewhere, w);
        var piece = new StructurePiece { Crossing = c };
        Assert.True(BridgeStructures.ChooseKits(new[] { piece, piece }, w, new[] { c, other, elsewhere }));
        Assert.True(BridgeStructures.Chosen(c.Kit));
        Assert.Equal(c.Kit, other.Kit);
        Assert.Equal(BridgeStructures.Kit.Undecided, elsewhere.Kit);
    }

    /// <summary>A kit added in a later build (here the stone kit, after a build that had only the wooden ones) never
    /// changes a crossing built before it, though the pick among the kits installed now would; a crossing not built yet
    /// may be built in it.</summary>
    [Fact]
    public void ANewKitLeavesABuiltCrossingAsItIsAndMayGoToOneNotBuiltYet()
    {
        var (c, w) = Reference();
        w.Biome = Heightmap.Biome.Mountain;
        var older = new[] { BridgeStructures.Kit.DvergrWood, BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced };
        for (w.Seed = 76; BridgeStructures.KitAmong(c, w, BridgeStructures.Installed) != BridgeStructures.Kit.Stone; w.Seed++) { }
        var built = ReferenceCrossing();
        var unbuilt = ReferenceCrossing();
        BridgeStructures.DecideKind(built, w);
        BridgeStructures.DecideKind(unbuilt, w);
        Assert.True(BridgeStructures.ChooseKit(built, w, new[] { built }, older));
        BridgeStructures.Kit kit = Assert.IsType<BridgeStructures.Kit>(built.Kit);
        Assert.Contains(kit, new[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced });
        Assert.False(BridgeStructures.ChooseKit(built, w, new[] { built }, BridgeStructures.Installed));
        Assert.Equal(kit, built.Kit);
        Assert.Equal(kit, BridgeStructures.KitAmong(built, w, BridgeStructures.Installed));
        Assert.True(BridgeStructures.ChooseKit(unbuilt, w, new[] { unbuilt }, BridgeStructures.Installed));
        Assert.Equal(BridgeStructures.Kit.Stone, unbuilt.Kit);
    }

    /// <summary>The network does not depend on the kit: the deck height, the deck's width, both ends and whether it is a
    /// viaduct are the same undecided and in every kit that fits, and every kit lays its pieces within that envelope.</summary>
    [Fact]
    public void TheNetworkIsTheSameWhicheverKitIsChosen()
    {
        var (c, w) = Reference();
        w.Biome = Heightmap.Biome.Mountain;
        BridgeStructures.DecideKind(c, w);
        (float, float, (StructureEnds.End, StructureEnds.End), bool) Envelope() =>
            (BridgeStructures.DeckHeight(c, w), BridgeStructures.DeckHalfWidth(c, w), BridgeStructures.Ends(c, w), BridgeStructures.IsViaduct(c, w));
        var undecided = Envelope();
        IReadOnlyList<BridgeStructures.Kit> fit = BridgeStructures.KitsThatFit(c, w);
        Assert.Equal(new[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced, BridgeStructures.Kit.Stone }, fit);
        foreach (BridgeStructures.Kit kit in fit)
        {
            c.Kit = kit;
            Assert.Equal(undecided, Envelope());
            float deck = undecided.Item1, half = undecided.Item2;
            foreach (StructurePiece p in BridgeStructures.SolveComplete(c, w, 1))
            {
                if (p.Kind == StructurePieceKind.Debris || p.Kind == StructurePieceKind.Flair) continue;
                var at = new Vector2(p.Position.x, p.Position.z);
                float across = Mathf.Abs(Vector2.Dot(at - c.Center, new Vector2(-c.Direction.y, c.Direction.x)));
                Assert.True(across <= half + 0.5f, FormattableString.Invariant($"{kit}: {p.Prefab} {across:F2} m off the deck's line, the deck {half:F2} m"));
                Assert.True(p.Position.y <= deck + BridgeLayout.RailHeight + 1f, FormattableString.Invariant($"{kit}: {p.Prefab} at {p.Position.y:F2} m over a deck at {deck:F2} m"));
            }
        }
    }

    /// <summary>The record keeps an undecided kit as -1 (format 5) and a chosen one as its number and one; a record before
    /// format 5 carrying -1 is refused, as any unknown kit is.</summary>
    [Fact]
    public void AnUndecidedKitAndAChosenOneRoundTripTheRecord()
    {
        var c = ReferenceCrossing();
        c.Kit = BridgeStructures.Kit.Undecided;
        byte[] bytes = RoadNetworkPersistence.SerializeRoadCrossings(new[] { c });
        const int kitAt = 8 + 11 * 4;
        Assert.Equal(5, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(RoadNetworkPersistence.UndecidedKit, BitConverter.ToInt32(bytes, kitAt));
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(bytes, loaded));
        Assert.Equal(BridgeStructures.Kit.Undecided, Assert.Single(loaded).Kit);
        var (_, w) = Reference();
        Assert.True(BridgeStructures.ChooseKit(loaded[0], w, loaded));
        BridgeStructures.Kit? chosen = loaded[0].Kit;
        var again = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(loaded), again));
        Assert.Equal(chosen, Assert.Single(again).Kit);
        BitConverter.GetBytes(4).CopyTo(bytes, 0);
        Assert.False(RoadNetworkPersistence.DeserializeRoadCrossings(bytes, new List<RoadCrossing>()));
    }
}
