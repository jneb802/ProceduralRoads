using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The railing round two floors met in line but set aside from each other (StairRailing.Lay, through the one call that
/// dresses a network's staircases and the railing catalogue's bays alike, StairStructures.DressAmong), against the bays as
/// captured in game (Fixtures/railcat-*.tsv: each bay's pieces, its join first): the offset join as corrected by hand, and
/// the joins accepted as laid, which keep their pieces.
/// </summary>
public class RailingOffsetJoinTests
{
    private readonly ITestOutputHelper m_out;
    public RailingOffsetJoinTests(ITestOutputHelper output) { m_out = output; }

    /// <summary>A captured bay: its join (at its walking surface) and its pieces as they stood, but its vines and its
    /// columns (they stand on the ground the bay was laid on; see <see cref="Walked"/> for its other supports).</summary>
    private static (Vector3 join, List<RailingCatalogue.Placed> pieces) Captured(string file)
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        string[] j = lines[0].Split('\t');
        static float R(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        var join = new Vector3(R(j[1]), R(j[2]), R(j[3]));
        var pieces = lines.Skip(1).Select(RailingCatalogue.Parse).OfType<RailingCatalogue.Placed>()
            .Where(p => p.Prefab is not ("vines" or "blackmarble_out_1")).ToList();
        return (join, pieces);
    }

    /// <summary>The captured pieces but its supports: its railing (wood), the blocks a hand laid, and the blocks standing
    /// where its layout's walking pieces are (a block elsewhere is a support, laid on the ground the bay stood on).</summary>
    private static List<RailingCatalogue.Placed> Walked(List<RailingCatalogue.Placed> captured, StairKit kit, string name, Vector3 join)
    {
        var layout = Runs(kit, name, join).SelectMany(r => r.Pieces).ToList();
        return captured.Where(p => p.Prefab.StartsWith("wood_", StringComparison.Ordinal) || !p.Laid
            || layout.Exists(l => l.Prefab == p.Prefab && Vector3.Distance(l.Position, p.At) <= RailingCatalogue.Kept)).ToList();
    }

    private static List<StairRun> Runs(StairKit kit, string name, Vector3 join) =>
        RailingCatalogue.Place(RailingCatalogue.Bays(kit).Single(b => b.Name == name), new Vector2(join.x, join.z), join.y);

    /// <summary>The bay as its kits lay it with its join where it was captured: its walking pieces and railing.</summary>
    private static List<RailingCatalogue.Placed> Laid(StairKit kit, string name, Vector3 join)
    {
        List<StairRun> runs = Runs(kit, name, join);
        float ground = join.y - 5f;
        var dressed = RailingCatalogue.Dress(runs, _ => (x, z) => ground, (x, z) => ground, new StairSupport.Shortfall(), null);
        Assert.All(dressed, d => Assert.NotEmpty(d));
        return dressed.SelectMany(d => d).Where(p => p.Kind is not (StructurePieceKind.StairSupport or StructurePieceKind.Flair))
            .Select(RailingCatalogue.Placed.Of).ToList();
    }

    /// <summary>The turn a prefab looks the same after: a 1 m block a quarter turn, else the catalogue's.</summary>
    private static float Period(string prefab) => prefab.EndsWith("_1x1", StringComparison.Ordinal) ? 90f : RailingCatalogue.Period(prefab);

    private static float Turned(float a, float b, float period)
    {
        float d = (a - b) % period;
        if (d < 0f) d += period;
        return Mathf.Min(d, period - d);
    }

    /// <summary>Each expected piece paired with a laid one of its prefab within <paramref name="reach"/> and
    /// <paramref name="turn"/> degrees (nearest first): what is left unpaired on either side, said.</summary>
    private static List<string> Unpaired(IEnumerable<RailingCatalogue.Placed> expected, List<RailingCatalogue.Placed> laid, Vector3 join,
        float reach, float turn)
    {
        var left = new List<RailingCatalogue.Placed>(laid);
        var missing = new List<string>();
        string Local(RailingCatalogue.Placed p) => FormattableString.Invariant(
            $"{p.Prefab} at ({p.At.x - join.x:F3}, {p.At.y - join.y:F3}, {p.At.z - join.z:F3}) yaw {p.Yaw:F1}");
        foreach (RailingCatalogue.Placed e in expected)
        {
            int best = -1;
            for (int i = 0; i < left.Count; i++)
            {
                RailingCatalogue.Placed l = left[i];
                if (l.Prefab != e.Prefab || Vector3.Distance(l.At, e.At) > reach || Turned(l.Yaw, e.Yaw, Period(e.Prefab)) > turn) continue;
                if (best < 0 || Vector3.Distance(l.At, e.At) < Vector3.Distance(left[best].At, e.At)) best = i;
            }
            if (best < 0) missing.Add("missing " + Local(e));
            else left.RemoveAt(best);
        }
        return missing.Concat(left.Select(l => "extra " + Local(l))).ToList();
    }

    /// <summary>
    /// Two black marble floors met in line, the second set half a metre aside and half a floor back over the first (a
    /// clipped join), as corrected by hand in game: each corner wedge of the step filled with the kit's 1 m block, the rails
    /// run across the step from corner to corner on the build's 22.5 degree grid, and no post at the join. The blocks stand
    /// and the rails where the hand put them (2 cm, a degree), the rails laid on from the longer leg's corner; every other
    /// piece stands exactly as laid and kept.
    /// </summary>
    [Fact]
    public void ASidewaysOffsetJoinFillsBothCornerWedgesAndRunsItsRailsDiagonallyWithNoPostsAtTheJoin()
    {
        var (join, captured) = Captured("railcat-blackmarble-offset-0.5.now.tsv");
        List<RailingCatalogue.Placed> now = Walked(captured, StairKit.BlackMarble, "offset-0.5", join);
        List<RailingCatalogue.Placed> laid = Laid(StairKit.BlackMarble, "offset-0.5", join);
        foreach (RailingCatalogue.Placed p in laid.Where(p => p.Prefab != StairKit.BlackMarble.Floor))
            m_out.WriteLine(FormattableString.Invariant($"laid {p.Prefab} ({p.At.x - join.x:F3}, {p.At.y - join.y:F3}, {p.At.z - join.z:F3}) yaw {p.Yaw:F1}"));
        // The hand's own pieces first, each paired with the nearest laid one; then everything kept, exactly.
        var byHand = now.Where(p => !p.Laid).ToList();
        Assert.Equal(6, byHand.Count);
        var rest = new List<RailingCatalogue.Placed>(laid);
        foreach (RailingCatalogue.Placed h in byHand)
        {
            const float reach = 0.02f;
            int i = rest.FindIndex(l => l.Prefab == h.Prefab && Vector3.Distance(l.At, h.At) <= reach && Turned(l.Yaw, h.Yaw, Period(h.Prefab)) <= 1f);
            Assert.True(i >= 0, FormattableString.Invariant(
                $"the hand's {h.Prefab} at ({h.At.x - join.x:F3}, {h.At.y - join.y:F3}, {h.At.z - join.z:F3}) yaw {h.Yaw:F1} not laid within {reach} m and 1 degree"));
            m_out.WriteLine(FormattableString.Invariant($"hand {h.Prefab}: laid {Vector3.Distance(rest[i].At, h.At):F3} m and {Turned(rest[i].Yaw, h.Yaw, Period(h.Prefab)):F2} degrees off"));
            rest.RemoveAt(i);
        }
        List<string> kept = Unpaired(now.Where(p => p.Laid), rest, join, RailingCatalogue.Kept, 1f);
        Assert.True(kept.Count == 0, "not as kept: " + string.Join("; ", kept));
        // No post at the join: none within a metre of the step's two inner corners.
        foreach (Vector2 corner in new[] { new Vector2(1f, -1f), new Vector2(-0.5f, 0f) })
            Assert.DoesNotContain(laid, p => p.Prefab == BridgeLayout.RailPostPrefab
                && Vector2.Distance(new Vector2(p.At.x - join.x, p.At.z - join.z), corner) < 0.3f);
    }

    /// <summary>Stone mirrors black marble: the same join laid in stone gets its own 1 m block in each wedge and the same
    /// diagonal rails and posts.</summary>
    [Fact]
    public void AStoneOffsetJoinIsRailedAndFilledAsBlackMarblesWithItsOwnBlock()
    {
        var join = new Vector3(100f, 40f, 200f);
        string Stone(string prefab) => prefab == StairKit.BlackMarble.Floor ? StairKit.Stone.Floor
            : prefab == "blackmarble_1x1" ? "stone_wall_1x1" : prefab;
        List<RailingCatalogue.Placed> marble = Laid(StairKit.BlackMarble, "offset-0.5", join);
        List<RailingCatalogue.Placed> stone = Laid(StairKit.Stone, "offset-0.5", join);
        Assert.Equal(2, marble.Count(p => p.Prefab == "blackmarble_1x1"));
        var expected = marble.Select(p => new RailingCatalogue.Placed(Stone(p.Prefab), p.At, p.Yaw, p.Pitch, p.Roll, true));
        List<string> unpaired = Unpaired(expected, stone, join, 0.001f, 0.1f);
        Assert.True(unpaired.Count == 0, string.Join("; ", unpaired));
    }

    /// <summary>The joins accepted in game as laid keep every walking piece and every piece of their railing: a floor turned
    /// 22.5 and 45 degrees off a floor across two staircases, a floor onto a stair at a jog, and two staircases met on a ridge
    /// as a network laid them.</summary>
    [Theory]
    [InlineData("turn-left-22.5")]
    [InlineData("turn-left-45.0")]
    [InlineData("floor-to-stair-22.5")]
    [InlineData("network-bend")]
    public void AJoinAcceptedAsLaidKeepsItsPieces(string name)
    {
        var (join, all) = Captured($"railcat-blackmarble-{name}.laid.tsv");
        List<RailingCatalogue.Placed> captured = Walked(all, StairKit.BlackMarble, name, join);
        List<RailingCatalogue.Placed> laid = Laid(StairKit.BlackMarble, name, join);
        List<string> unpaired = Unpaired(captured, laid, join, RailingCatalogue.Kept, 1f);
        Assert.True(unpaired.Count == 0, $"{name}: " + string.Join("; ", unpaired));
    }

    /// <summary>A sideways step's blocks are walking pieces of the staircase that lays them (StairRailing.StepBlocks), laid
    /// with its floors before its kit's burial and support pass (StairKit.Dress): the supports laid are those the pass lays
    /// for the floors and blocks together, not for the floors alone; and a block wholly in the ground is left out as any buried
    /// filler is, while the floors beside it, partly clear, stand and the step is still railed.</summary>
    [Fact]
    public void ASidewaysStepsBlocksGoThroughTheStaircasesBurialAndSupportPass()
    {
        StairKit kit = StairKit.Stone;
        RailingCatalogue.Bay bay = RailingCatalogue.Bays(kit).Single(b => b.Name == "offset-0.5");
        var joinAt = new Vector2(100f, 200f);
        const float ground = 35f;
        float surface = RailingCatalogue.SurfaceOver(bay, ground);
        List<StairRun> runs = RailingCatalogue.Place(bay, joinAt, surface);
        StairRun first = runs[0];
        List<StructurePiece> beside = StairStructures.MetAtEnds(first, runs);
        Func<int, int, float> flat = (x, z) => ground;
        List<StructurePiece> blocks = StairRailing.StepBlocks(kit, first.Pieces, first.Ends, flat, beside);
        StructurePiece block = Assert.Single(blocks);
        Assert.Equal("stone_wall_1x1", block.Prefab);

        // Held: the staircase's supports are the pass's over its floors and its block.
        static string Key(StructurePiece p) => FormattableString.Invariant($"{p.Prefab} {p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3} {p.YawDegrees:F1}");
        List<StructurePiece> dressed = StairStructures.DressAmong(first, runs, _ => (x, z) => ground, flat, new StairSupport.Shortfall(), false, null);
        List<string> supports = dressed.Where(p => p.Kind == StructurePieceKind.StairSupport).Select(Key).OrderBy(k => k).ToList();
        var floors = first.Pieces.ToList();
        List<string> withBlock = StairSupport.Lay(kit, floors.Append(block).ToList(), (x, z) => ground, flat).Select(Key).OrderBy(k => k).ToList();
        List<string> without = StairSupport.Lay(kit, floors, (x, z) => ground, flat).Select(Key).OrderBy(k => k).ToList();
        Assert.NotEqual(without, withBlock);
        Assert.Equal(withBlock, supports);
        Assert.Contains(dressed, p => p.Kind == StructurePieceKind.StairFiller && Key(p) == Key(block));

        // Buried: the ground over the block's footprint stands a metre over the walk; the block is left out, the floors stand,
        // and the step is still railed across.
        var at = new Vector2(block.Position.x, block.Position.z);
        Func<int, int, float> raised = (x, z) => Vector2.Distance(new Vector2(x, z), at) <= 2.5f ? surface + 1f : ground;
        List<StructurePiece> buried = StairStructures.DressAmong(first, runs, _ => (x, z) => raised(Mathf.RoundToInt(x), Mathf.RoundToInt(z)), raised,
            new StairSupport.Shortfall(), false, null);
        Assert.NotEmpty(buried);
        Assert.DoesNotContain(buried, p => p.Kind == StructurePieceKind.StairFiller && Key(p) == Key(block));
        foreach (StructurePiece floor in first.Pieces)
            Assert.Contains(buried, p => p.Kind == StructurePieceKind.StairFloor && Vector3.Distance(p.Position, floor.Position) < 0.001f);
        float yaw = StairLayout.Wrap(block.YawDegrees);
        Assert.Contains(buried, p => p.Kind == StructurePieceKind.Beam && Turned(p.YawDegrees, yaw, 180f) < 1f);
    }

    /// <summary>Two floors in line, the second set <paramref name="offset"/> aside and starting <paramref name="on"/> past the
    /// join (the catalogue's clipped join), dressed as generation dresses them: every piece, both staircases.</summary>
    private static List<StructurePiece> Stepped(float offset, float on, out Vector3 join)
    {
        join = new Vector3(100f, 40f, 200f);
        RailingCatalogue.Bay bay = RailingCatalogue.Straight(StairKit.BlackMarble, "step", offset, on);
        List<StairRun> runs = RailingCatalogue.Place(bay, new Vector2(join.x, join.z), join.y);
        float ground = join.y - 5f;
        var dressed = RailingCatalogue.Dress(runs, _ => (x, z) => ground, (x, z) => ground, new StairSupport.Shortfall(), null);
        Assert.All(dressed, d => Assert.NotEmpty(d));
        var layout = runs.SelectMany(r => r.Pieces).ToList();
        return dressed.SelectMany(d => d).Where(p => p.Kind != StructurePieceKind.StairFiller
            || !layout.Exists(l => Vector3.Distance(l.Position, p.Position) < 0.001f)).ToList();
    }

    private static int Blocks(List<StructurePiece> pieces) => pieces.Count(p => p.Kind == StructurePieceKind.StairFiller);

    private static bool PostNear(List<StructurePiece> pieces, Vector3 join, Vector2 local) => pieces.Exists(p =>
        p.Kind == StructurePieceKind.Post && Vector2.Distance(new Vector2(p.Position.x - join.x, p.Position.z - join.z), local) < 0.1f);

    /// <summary>A step a joint (0.45 m) aside is closed by its two posts as before, no block; just over a joint it is a
    /// sideways step: a block in each wedge, no post at its inner corners.</summary>
    [Theory]
    [InlineData(0.45f, false)]
    [InlineData(0.46f, true)]
    public void ASidewaysStepStartsJustOverAJoint(float offset, bool step)
    {
        List<StructurePiece> pieces = Stepped(offset, -StairLayout.PieceRun / 2f, out Vector3 join);
        Assert.Equal(step ? 2 : 0, Blocks(pieces));
        // The right step's inner corner (1, -1): the first floor's leg ends 5 cm before it, the second's 5 cm after.
        Assert.Equal(!step, PostNear(pieces, join, new Vector2(1f, -1.05f)));
        Assert.Equal(!step, PostNear(pieces, join, new Vector2(1.05f, -1f)));
    }

    /// <summary>A step whose leg along the floors is longer than the block (1.5 m: the second floor a quarter of the way
    /// back over the first) is railed post to post as before: one block and one rail pair would not close it.</summary>
    [Fact]
    public void ASidewaysStepLongerThanItsBlockIsRailedPostToPost()
    {
        List<StructurePiece> pieces = Stepped(0.5f, -StairLayout.PieceRun / 4f, out Vector3 join);
        Assert.Equal(0, Blocks(pieces));
        Assert.True(PostNear(pieces, join, new Vector2(1f, -0.55f)), "no post at the first floor's leg's inner end");
    }

    /// <summary>A step a whole metre aside (each leg a block long, 1.41 m corner to corner) takes the 2 m rail, which stands on
    /// the middle of the line from corner to corner at the grid's heading (45 degrees), overhanging neither corner alone.</summary>
    [Fact]
    public void ASidewaysStepsLongRailStandsOnTheMiddleOfItsLine()
    {
        List<StructurePiece> pieces = Stepped(1f, -StairLayout.PieceRun / 2f, out Vector3 join);
        Assert.Equal(2, Blocks(pieces));
        foreach (Vector2 middle in new[] { new Vector2(1.5f, -1.5f), new Vector2(-0.5f, 0.5f) })
            foreach (float tier in new[] { 0f, BridgeLayout.RailHeight })
                Assert.Contains(pieces, p => p.Prefab == StairRailing.LevelRailPrefab
                    && Vector3.Distance(p.Position, join + new Vector3(middle.x, tier, middle.y)) < 0.001f && Turned(p.YawDegrees, 135f, 180f) < 0.1f);
    }
}
