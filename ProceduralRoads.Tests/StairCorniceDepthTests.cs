using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Release bar "nothing falls": black marble's cornice columns (StairSupport) read with the game's support
/// rule (SupportGraph, StairSupport.Holding). A column hung out from a face steeper than its 2:1 step keeps 0.708 of the
/// course under it a course; more than StairSupport.MostHungCourses hung over the first that touches the ground leave the
/// stair on top under the margin, or falling. The search refuses such a piece; the first build leaves out a support the rule
/// breaks and says a walking piece it holds under the margin.
/// </summary>
public class StairCorniceDepthTests
{
    private static readonly StairKit Kit = StairKit.BlackMarble;

    /// <summary>One black marble stair climbing north (turned 180: it climbs toward its local -z), its foot at
    /// (0, <paramref name="underside"/>, 1): the slope's side west (its left), its two cornice columns at z 1.5 and 0.5
    /// stepping a metre west a course.</summary>
    private static StructurePiece Stair(float underside) =>
        new() { Kind = StructurePieceKind.StairStep, Prefab = Kit.Stair, Position = new Vector3(0f, underside, 1f), YawDegrees = 180f };

    /// <summary>A sheer face <paramref name="faceAt"/> m west (x under it), 100 m high behind it; in front, a gorge about 50 m
    /// down whose floor rises west a metre a metre (as fix/bm-outcorner-line's), so the stair's wall side is west.</summary>
    private static Func<float, float, float> Face(float faceAt) => (x, z) => x < faceAt ? 100f : -50f - x;

    private static List<ViaductSupport.Held> Hold(List<StructurePiece> walking, List<StructurePiece> laid, Func<float, float, float> ground) =>
        StairSupport.Holding(walking.Concat(laid).ToList(), (x, z) => ground(x, z));

    private static string Say(IEnumerable<ViaductSupport.Held> held) => string.Join(", ", held.OrderBy(h => h.Share).Take(4)
        .Select(h => FormattableString.Invariant($"{h.Piece.Prefab} at {h.Piece.Position} {h.Support:F0} ({h.Share:F2})")));

    /// <summary>
    /// The ceiling is the rule's: a face 6.5 m out first meets the seventh course (6 hung over it), and every piece, the stair
    /// on top included, holds at 1.2 times its least (the cornice's over-reading taken off); 7.5 m out it meets the eighth
    /// (7 hung) and the top course and the stair on it are held under the margin. The first is laid, the second refused.
    /// </summary>
    [Theory]
    [InlineData(-6.5f, 6)]
    [InlineData(-7.5f, 7)]
    public void TheHungCeilingIsWhatTheGamesSupportRuleHolds(float faceAt, int hung)
    {
        Func<float, float, float> ground = Face(faceAt);
        var walking = new List<StructurePiece> { Stair(20f) };
        StairSupport.Walk w = StairSupport.WalkOf(walking[0], Kit)!.Value;
        // Laid as an older build's layout would be, whatever Choose says now: the courses down to the one sunk in the face.
        List<StructurePiece> column = Enumerable.Range(0, hung + 2).SelectMany(c => new[] { 1.5f, 0.5f }.Select(along =>
            new StructurePiece
            {
                Kind = StructurePieceKind.StairSupport, Prefab = "blackmarble_out_1",
                Position = new Vector3(-c, (along > 1f ? w.Underside : w.Underside - 1f) - 1f - 2f * c, along), YawDegrees = 0f,
            })).Append(new StructurePiece
            {
                Kind = StructurePieceKind.StairSupport, Prefab = "blackmarble_2x1x1", Position = new Vector3(0f, w.Underside - 0.5f, 0.5f), YawDegrees = 0f,
            }).ToList();
        List<ViaductSupport.Held> held = Hold(walking, column, ground);
        // The grounded courses are the ones the face meets: 'hung' courses over the first.
        var upper = held.Where(h => h.Piece.Prefab == "blackmarble_out_1" && h.Piece.Position.z > 1f).OrderByDescending(h => h.Piece.Position.y).ToList();
        Assert.Equal(hung, upper.FindIndex(h => h.Support >= SupportGraph.Marble.Max - 0.01f));
        ViaductSupport.Held stair = held.Single(h => h.Piece == walking[0]);
        if (hung <= StairSupport.MostHungCourses)
        {
            Assert.True(held.All(h => h.Share >= DvergrViaduct.HoldMargin), Say(held));
            Assert.False(StairSupport.Choose(Kit, w, ground).Refused);
        }
        else
        {
            Assert.True(stair.Share < DvergrViaduct.HoldMargin, $"the stair over {hung} hung courses: {stair.Support:F0}");
            Assert.True(upper[0].Share < DvergrViaduct.HoldMargin, $"the top course over {hung} hung courses: {upper[0].Support:F0}");
            Assert.True(StairSupport.Choose(Kit, w, ground).Refused);
        }
    }

    /// <summary>
    /// A nine-course column (fix/bm-outcorner-line's sheer face 7.5 m out, the lowest course sunk in it, seven hung over the
    /// first that touches it): the search refuses the stair over it, and the first build of an older layout that has it
    /// lays no column (the stair said unheld), so nothing it lays falls.
    /// </summary>
    [Fact]
    public void ANineCourseColumnIsRefusedAndNeverLaid()
    {
        Func<float, float, float> ground = Face(-7.5f);
        StructurePiece stair = Stair(20f);
        StairSupport.Walk w = StairSupport.WalkOf(stair, Kit)!.Value;
        Assert.True(StairSupport.Choose(Kit, w, ground).Refused);

        var shortfall = new StairSupport.Shortfall();
        var walking = new List<StructurePiece> { stair };
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, ground, (x, z) => ground(x, z), shortfall);
        Assert.Single(shortfall.Unheld); // refused, so left unheld as an older layout's piece is (no column laid)
        Assert.Empty(laid.Where(p => p.Prefab == "blackmarble_out_1"));
        foreach (ViaductSupport.Held h in Hold(walking, laid, ground).Where(h => h.Piece != stair))
            Assert.True(h.Support >= h.Min, $"{h.Piece.Prefab} at {h.Piece.Position} laid to fall ({h.Support:F0})");
    }

    /// <summary>
    /// The first build's own reading (StairSupport.HoldUp), on a column the search never sees: the ground the search chose
    /// on (natural) meets the column's third course, but the finished ground is cut 50 m down under all of it but the face
    /// 7.5 m out (a road's cut under it). The column is laid on the finished ground, out to that face, not to the third
    /// course and hanging there: none of it falls, and every support piece laid holds by the rule; the stair, held under
    /// the margin, is laid and said.
    /// </summary>
    [Fact]
    public void AColumnIsLaidOutToTheFaceTheFinishedGroundLeaves()
    {
        Func<float, float, float> natural = Face(-2.5f), cut = Face(-7.5f);
        StructurePiece stair = Stair(20f);
        var walking = new List<StructurePiece> { stair };
        var shortfall = new StairSupport.Shortfall();
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, natural, (x, z) => cut(x, z), shortfall);
        List<ViaductSupport.Held> held = StairSupport.Holding(walking.Concat(laid).ToList(), (x, z) => cut(x, z));
        foreach (ViaductSupport.Held h in held.Where(h => h.Piece != stair))
            Assert.True(h.Support >= h.Min, $"{h.Piece.Prefab} at {h.Piece.Position} laid to fall ({h.Support:F0})");
        Assert.Empty(shortfall.Fell);
        Assert.Contains(laid, p => p.Prefab == "blackmarble_out_1" && p.Position.x < -5f);
        Assert.Contains(new Vector3(0f, 20f, 1f), shortfall.Weak);
    }

    /// <summary>
    /// The first build's reading leaves out a support the calibration says may break (StairSupport.HoldUp asks
    /// ViaductSupport.Held.Falls, the model's over-reading taken off, as every other verdict does), not only one under its
    /// material's least as read: a black marble stair 12 m up, its wall side east, the ground the search chose on a face 3.5 m
    /// out, the finished ground a face 9 m out (a road's cut under it). Laid out to that face, the cornice at (0, 10, 0.5) reads
    /// 103 against its least of 100, which the 1.2 over-reading of blackmarble_out_1 puts at 0.86 of it: the game breaks it.
    /// On the parent it was laid.
    /// </summary>
    [Fact]
    public void ASupportTheCalibrationSaysMayBreakIsNotLaid()
    {
        Func<float, float, float> natural = (x, z) => x > 3.5f ? 100f : x - 50f, cut = (x, z) => x > 9f ? 100f : x - 27f;
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Kit.Stair, Position = new Vector3(0f, 12f, 1f), YawDegrees = 180f };
        var walking = new List<StructurePiece> { stair };
        var shortfall = new StairSupport.Shortfall();
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, natural, (x, z) => cut(x, z), shortfall);
        Assert.Contains(laid, p => p.Prefab == "blackmarble_out_1");
        foreach (ViaductSupport.Held h in Hold(walking, laid, cut).Where(h => h.Piece != stair))
            Assert.False(h.Falls, $"{h.Piece.Prefab} at {h.Piece.Position} laid to fall ({h.Support:F1} of {h.Min:F0}, share {h.Share:F2})");
        Assert.Contains(new Vector3(0f, 10f, 0.5f), shortfall.Fell);
    }

    /// <summary>
    /// A staircase over a deep drop: from a ledge at 20 to one at 40 across a gorge 50 m deep, a sheer face 7.5 m west of
    /// the line. Whatever the search lays, every stone and marble piece of it, dressed, holds at the margin by the game's
    /// support rule: none falls.
    /// </summary>
    [Fact]
    public void AStaircaseOverADeepDropLaysNothingThatFalls()
    {
        Func<float, float, float> ground = (x, z) => x < -7.5f ? 100f : z < 4f ? 20f : z > 40f ? 40f : -50f;
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 44f), ground, kit: Kit);
        if (!plan.Reached) return; // refused outright: nothing laid, nothing falls
        StairRun run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(plan.End.x, plan.End.z), 2f, plan, Kit);
        List<StructurePiece> dressed = Kit.Dress(run, ground, (x, z) => ground(x, z), false);
        List<ViaductSupport.Held> held = StairSupport.Holding(dressed, (x, z) => ground(x, z));
        Assert.True(held.All(h => h.Share >= DvergrViaduct.HoldMargin), Say(held));
    }
}
