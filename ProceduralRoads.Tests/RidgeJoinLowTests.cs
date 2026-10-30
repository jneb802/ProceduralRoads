using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Two staircases met top to top over a ridge are joined lower down where their flights pass close, both tops taken away
/// (RoadStairRuns.JoinLow): the two black marble flights up a Mistlands ridge, from 76 and 75.4 up to tops at 92.9, as laid
/// (5 m east and 220 m north of where they stood, every stair, floor and filler; no columns). Joined by hand: everything
/// over 87.9 taken away, and from the first's floor at 90.9 three straight stairs (yaw 157.5) down onto the second's
/// switchback pad at 87.9.
/// </summary>
public sealed class RidgeJoinLowTests
{
    private static StairPiece S(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Stair, Prefab = StairKit.BlackMarble.Stair, Position = new Vector3(x, y, z), YawDegrees = yaw };
    private static StairPiece F(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Landing, Prefab = StairKit.BlackMarble.Floor, Position = new Vector3(x, y, z), YawDegrees = yaw };
    private static StairPiece B(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Filler, Prefab = "blackmarble_2x1x1", Position = new Vector3(x, y, z), YawDegrees = yaw };
    private static StairPiece C(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Filler, Prefab = "blackmarble_1x1", Position = new Vector3(x, y, z), YawDegrees = yaw };

    /// <summary>The ground well under both flights.</summary>
    private static float Low(float x, float z) => 70f;

    /// <summary>The first, climbing from the west to the shared top floor at 92.9 (its floor at 90.9 at -4.8,1.0, yaw 135,
    /// straight on from its stair below); the second, the way the road comes down, laid climbing from its foot in the east
    /// (its switchback's pad at 87.9, two floors at -0.5,-7.3 and -1.9,-5.8, the flight back on 45 from the second).</summary>
    internal static (RoadStairSpan up, StairPlan down) Ridge()
    {
        StairKit kit = StairKit.BlackMarble;
        var first = new StairPlan { Kit = kit.Name, Reached = true };
        first.Pieces.AddRange(new[] {
            S(-11.707f, 76.437f, 20.707f, 315f),
            S(-10.293f, 77.437f, 19.293f, 315f),
            B(-9.232f, 77.937f, 18.232f, 315f),
            S(-8.338f, 78.437f, 17.986f, 292.5f),
            B(-6.952f, 78.937f, 17.413f, 292.5f),
            S(-6.031f, 79.437f, 17.528f, 270f),
            B(-4.531f, 79.937f, 17.528f, 270f),
            S(-3.725f, 80.437f, 17.987f, 247.5f),
            B(-2.339f, 80.937f, 18.561f, 247.5f),
            S(-1.770f, 81.437f, 19.293f, 225f),
            F(-0.355f, 81.937f, 20.707f, 45f),
            F(1.059f, 81.937f, 19.293f, 45f),
            S(-0.355f, 82.437f, 17.879f, 45f),
            B(-1.416f, 82.937f, 16.818f, 45f),
            S(-1.662f, 83.437f, 15.924f, 22.5f),
            C(-1.932f, 83.937f, 14.188f, 67.5f),
            B(-2.236f, 83.937f, 14.538f, 22.5f),
            S(-3.509f, 84.437f, 14.076f, 67.5f),
            B(-4.895f, 84.937f, 13.502f, 67.5f),
            S(-5.464f, 85.437f, 12.770f, 45f),
            B(-6.525f, 85.937f, 11.709f, 45f),
            S(-6.771f, 86.437f, 10.815f, 22.5f),
            S(-7.536f, 87.437f, 8.967f, 22.5f),
            B(-8.110f, 87.937f, 7.582f, 22.5f),
            S(-7.995f, 88.437f, 6.661f, 0f),
            B(-7.995f, 88.937f, 5.161f, 0f),
            S(-7.536f, 89.437f, 4.354f, 337.5f),
            B(-6.962f, 89.937f, 2.968f, 337.5f),
            S(-6.229f, 90.437f, 2.399f, 315f),
            F(-4.815f, 90.937f, 0.985f, 135f),
            B(-3.901f, 90.937f, -0.015f, 270f),
            B(-3.755f, 90.937f, -0.076f, 315f),
            S(-2.401f, 91.437f, -0.015f, 270f),
            F(-0.401f, 91.937f, -0.015f, 90f),
            F(1.140f, 91.937f, 0.292f, 67.5f),
            F(2.987f, 91.937f, 1.057f, 67.5f),
            F(3.753f, 91.937f, 1.822f, 22.5f),
            F(4.518f, 91.937f, 3.669f, 22.5f),
            S(6.366f, 92.437f, 2.904f, 292.5f),
            F(8.213f, 92.937f, 2.139f, 112.5f) });
        var second = new StairPlan { Kit = kit.Name, Reached = true, Reversed = true };
        second.Pieces.AddRange(new[] {
            S(12.271f, 75.437f, -13.896f, 225f),
            S(13.685f, 76.437f, -12.482f, 225f),
            S(15.099f, 77.437f, -11.068f, 225f),
            B(16.160f, 77.937f, -10.008f, 225f),
            C(16.013f, 77.937f, -9.568f, 270f),
            S(17.513f, 78.437f, -10.068f, 270f),
            S(18.737f, 79.437f, -7.914f, 112.5f),
            C(17.318f, 79.937f, -6.878f, 67.5f),
            B(17.352f, 79.937f, -7.340f, 112.5f),
            S(16.124f, 80.437f, -7.915f, 67.5f),
            B(14.738f, 80.937f, -8.488f, 67.5f),
            S(13.817f, 81.437f, -8.373f, 90f),
            S(11.817f, 82.437f, -8.373f, 90f),
            B(10.317f, 82.937f, -8.373f, 90f),
            S(9.511f, 83.437f, -7.914f, 112.5f),
            S(7.663f, 84.437f, -7.149f, 112.5f),
            B(6.277f, 84.937f, -6.575f, 112.5f),
            S(5.708f, 85.437f, -5.843f, 135f),
            B(4.647f, 85.937f, -4.782f, 135f),
            C(4.794f, 85.937f, -4.343f, 90f),
            S(3.294f, 86.437f, -4.843f, 90f),
            C(1.587f, 86.937f, -4.429f, 45f),
            B(1.794f, 86.937f, -4.843f, 90f),
            S(0.880f, 87.437f, -5.843f, 45f),
            F(-0.534f, 87.937f, -7.257f, 225f),
            F(-1.948f, 87.937f, -5.843f, 225f),
            S(-0.534f, 88.437f, -4.429f, 225f),
            S(0.880f, 89.437f, -3.015f, 225f),
            B(1.940f, 89.937f, -1.954f, 225f),
            C(1.794f, 89.937f, -1.515f, 270f),
            S(3.294f, 90.437f, -2.015f, 270f),
            B(4.794f, 90.937f, -2.015f, 270f),
            S(5.601f, 91.437f, -1.556f, 247.5f),
            B(6.986f, 91.937f, -0.982f, 247.5f),
            C(7.336f, 91.937f, -1.286f, 202.5f),
            S(7.448f, 92.437f, 0.292f, 202.5f) });
        foreach (StairPlan plan in new[] { first, second })
        {
            // Each walked piece's clearances, as laid: a stair's in two stretches.
            foreach (StairPiece p in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
                for (int s = p.Kind == StairPieceKind.Stair ? 2 : 1; s > 0; s--)
                    plan.Clearances.Add(new StairClearance(p.Position, new Vector3(1f, 1f, 1f), p.YawDegrees, p.Kind == StairPieceKind.Stair ? p.Position.y + 0.5f : p.Position.y + kit.FloorTop));
            StairPiece top = plan.Pieces.Last(p => p.Kind != StairPieceKind.Filler);
            float heading = top.Kind == StairPieceKind.Stair ? StairLayout.Wrap(top.YawDegrees + 180f) : top.YawDegrees;
            Vector2 edge = new Vector2(top.Position.x, top.Position.z) + StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f);
            plan.End = new Vector3(edge.x, top.Position.y + (top.Kind == StairPieceKind.Stair ? StairLayout.PieceRise : kit.FloorTop), edge.y);
            plan.EndHeading = heading;
            plan.LastRowKind = top.Kind;
        }
        var up = new RoadStairSpan { Plan = first, Kit = kit, ToPoint = new Vector2(first.End.x, first.End.z), ToHeight = first.End.y };
        return (up, second);
    }

    /// <summary>The three stairs laid by hand: centres and yaw.</summary>
    private static readonly Vector3[] ByHand = { new(-1.98f, 88.44f, -4.67f), new(-2.74f, 89.44f, -2.82f), new(-3.51f, 90.44f, -0.97f) };

    private static List<StairPiece> Walked(StairPlan plan) => plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).ToList();

    [Fact]
    public void ARidgesTwoFlightsAreJoinedAsByHand()
    {
        StairKit kit = StairKit.BlackMarble;
        var (up, down) = Ridge();
        Assert.Empty(StairChain.BackTracks(up.Plan.Pieces, down.Pieces, kit));
        Assert.Equal(93.437f, up.Plan.End.y, 3);
        Assert.Equal(93.437f, down.End.y, 3);

        StairPiece floor = up.Plan.Pieces.Single(p => p.Kind == StairPieceKind.Landing && Mathf.Abs(p.Position.y - 90.937f) < 0.01f);
        var pad = down.Pieces.Where(p => p.Kind == StairPieceKind.Landing).ToList();

        Assert.True(RoadStairRuns.JoinLow(up, down, kit, Low, 2f, (_, _) => true, out Vector2 at));
        // Three stairs straight down from the floor at 90.9 onto the pad at 87.9, where they were laid by hand, within a few cm.
        var stairs = down.Pieces.Where(p => p.Kind == StairPieceKind.Stair && p.Position.y > 88f).OrderBy(p => p.Position.y).ToList();
        Assert.Equal(3, stairs.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Vector3.Distance(stairs[i].Position, ByHand[i]) < 0.05f, $"stair {i} at {stairs[i].Position}, by hand {ByHand[i]}");
            Assert.Equal(157.5f, stairs[i].YawDegrees, 3);
        }
        // Everything over the floor at 90.9 taken away from both; the floor and the pad kept; the two meet at the floor's far edge.
        Assert.All(Walked(up.Plan).Concat(Walked(down)), p => Assert.True(p.Position.y < 91f, $"{p.Kind} at {p.Position}"));
        Assert.Contains(floor, up.Plan.Pieces);
        Assert.All(pad, p => Assert.Contains(p, down.Pieces));
        Assert.DoesNotContain(down.Pieces, p => p.Kind == StairPieceKind.Stair && Mathf.Abs(p.Position.y - 88.437f) < 0.01f && Mathf.Abs(p.YawDegrees - 225f) < 0.01f);
        Assert.Equal(91.437f, up.Plan.End.y, 3);
        Assert.True(Vector2.Distance(at, new Vector2(-4.108f, 0.278f)) < 0.01f, $"met at {at}");
        // The top stair turned 22.5 degrees off the floor on its inside corner, the wedge filled level with the floor; edge to edge.
        Assert.Contains(down.Pieces, p => p.Kind == StairPieceKind.Filler && Mathf.Abs(p.Position.y + kit.FloorTop - 91.437f) < 0.01f);
        Assert.Empty(FlatRunJoinTests.JoinFaults(up.Plan.Pieces.Concat(down.Pieces).ToList(), kit));
        // Over the ridge and on down its far side: the two legs go off away from each other, before and after.
        Assert.Empty(StairChain.BackTracks(up.Plan.Pieces, down.Pieces, kit));
    }

    /// <summary>A flight cut back (TrimAbove) keeps its turn counts to the pieces left, and a snapshot puts them all back.</summary>
    [Fact]
    public void CuttingAFlightBackTakesItsTurnsOutOfItsStats()
    {
        StairKit kit = StairKit.BlackMarble;
        StairPieceKind s = StairPieceKind.Stair, f = StairPieceKind.Landing;
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 70f, new[] { 0f, 22.5f, 22.5f, 22.5f, 67.5f, 67.5f, 22.5f, 22.5f },
            kit, Low, 0, new[] { s, s, f, f, f, s, s, s });
        int turns = plan.Turns, steps = plan.TurnSteps, overlap = plan.OverlapTurnSteps;
        // A stair turned a step, floors turned two over each other, a stair turned two back.
        Assert.Equal(3, turns);
        Assert.Equal(1 + 2 + 2, steps);
        Assert.Equal(2, overlap);
        var keep = new StairLayout.PlanSnapshot(plan);
        // Cut back to the second stair: the floors' turn over each other and the two stair turns above go.
        int row = plan.Pieces.FindIndex(p => p.Kind == s && Mathf.Abs(p.Position.y - 71f) < 0.01f);
        StairLayout.TrimAbove(plan, row, false, kit);
        Assert.Equal(1, plan.Turns);
        Assert.Equal(1, plan.TurnSteps);
        Assert.Equal(0, plan.OverlapTurnSteps);
        keep.Restore(plan);
        Assert.Equal((turns, steps, overlap), (plan.Turns, plan.TurnSteps, plan.OverlapTurnSteps));
    }

    /// <summary>Control: a rib of rock standing between the floor at 90.9 and the pad below: they stay met at their tops, untouched.</summary>
    [Fact]
    public void TwoFlightsWithACrestBetweenAreNotJoinedLower()
    {
        StairKit kit = StairKit.BlackMarble;
        var (up, down) = Ridge();
        int pieces = up.Plan.Pieces.Count + down.Pieces.Count;
        static float Rib(float x, float z) => Vector2.Distance(new Vector2(x, z), new Vector2(-2.85f, -2.65f)) < 1.5f ? 99f : Low(x, z);
        Assert.False(RoadStairRuns.JoinLow(up, down, kit, Rib, 2f, (_, _) => true, out _));
        Assert.Equal(pieces, up.Plan.Pieces.Count + down.Pieces.Count);
        Assert.Equal(93.437f, up.Plan.End.y, 3);
    }
}
