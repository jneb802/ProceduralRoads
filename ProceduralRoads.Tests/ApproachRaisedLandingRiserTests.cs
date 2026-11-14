using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An approach climbing to a raised landing does not end on a stair that steps off onto the landing more than a step under
/// it (StairLayout's Route.StepsOffUnderTheLanding, read with the raised landing's surface rule,
/// RoadStairRuns.RaisedLandingSurfaceAllowed). Its rises are whole metres from its road's end, and the search took the one
/// within half a rise of the ground at its top's edge: with the landing about half a rise off them, that was the stair under
/// the landing or the stair over it by centimetres of ground. In game the Dvergr boss entrance at -3328,-5760 (its road's end
/// on a pad at 39.99, its door landing at about 49.44 where the approach met it): one network's approach ended at 48.99,
/// 0.45 m under the landing, every finish refused (the stair climbed on from under it does not meet its lip, the floor and
/// stair down off it in the location's rock), and the entrance was left unconnected; others' ended at 49.99, 0.49 m over a
/// landing read at 49.50, and finished onto it with a stair down (LandingThenDown).
/// </summary>
public sealed class ApproachRaisedLandingRiserTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public ApproachRaisedLandingRiserTests() { RoadNetworkGenerator.Reset(); RoadLocationProtection.Reset(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadLocationProtection.Reset(); _scope.Dispose(); }

    /// <summary>The road's end in game: the pad the approach set off from, every rise of it a whole metre over 39.99.</summary>
    private const float Pad = 39.99f;

    /// <summary>A location's level landing at <paramref name="landing"/> over |x| 4, z -4 to 14 round its centre, the ground
    /// falling a metre a metre off it down to the pad's height, the road's end 16 m out on the slope's foot.</summary>
    private static Func<float, float, float> Ground(float landing) => (x, z) =>
    {
        float outside = Mathf.Max(Mathf.Abs(x) - 4f, Mathf.Max(-4f - z, z - 14f));
        return outside <= 0f ? landing : Mathf.Max(Pad, landing - outside);
    };

    private static readonly LevelOp[] Ops = { new(0, 0, 0, 4, false), new(0, 8, 0, 6, false) };

    /// <summary>The approach from the road's end at (0, 30) onto the landing, and each top as its search left it, finished on a copy
    /// through the approach's own gates (RoadStairRuns.BeforeTopFinish: its refusals of a finished top) and the finish it took.</summary>
    private static (RoadStairSpan? span, string why, List<(Vector3 top, StairLayout.TopFinish finish)> tops) Lay(float landing)
    {
        var ground = Ground(landing);
        var end = new Vector2(0f, 30f);
        var request = new LocationApproachRequest
        {
            Centre = new Vector2(), RoadEnd = end, RoadHeight = ground(end.x, end.y), Arriving = 180f,
            RoadTail = new List<Vector2> { new Vector2(0f, 60f), end },
        };
        var tops = new List<(Vector3, StairLayout.TopFinish)>();
        RoadStairRuns.BeforeTopFinish = (plan, fits, g) => tops.Add((plan.End, RoadStairRuns.FinishOverLedge(plan, g, RoadStairRuns.Width, fits)));
        try
        {
            RoadStairSpan? span = RoadStairRuns.PlanPlacedLocationApproach(request, 0f, Ops, TestLattice.Of(ground), (_, _) => Heightmap.Biome.Mistlands,
                77, out string why, root: landing);
            return (span, why, tops);
        }
        finally { RoadStairRuns.BeforeTopFinish = null; }
    }

    /// <summary>The walk's highest surface and whether it meets the raised landing (RoadStairRuns.RaisedLandingWalk, the
    /// approach's own arrival test).</summary>
    private static (float highest, (bool clear, bool arrival) walk) Walked(StairPlan plan, float landing)
    {
        var ground = Ground(landing);
        var region = PlacedLocationShape.Of(new Vector2(), 0f, Ops[0], 0f, null, ground, root: landing)
            .ConnectedLanding(ground, PlacedLocationShape.LevelGroundClearance);
        float highest = plan.Pieces.Where(p => p.Kind != StairPieceKind.Support).Max(p => StairLayout.WalkingSurface(p).Max(s => s.y));
        return (highest, RoadStairRuns.RaisedLandingWalk(plan, region));
    }

    /// <summary>The landing as the network that lost the boss entrance read it, 49.44: the stair under it (48.99) 0.45 m short,
    /// the one over it (49.99) 0.55 m proud of the landing. Before, every layout ended on the stair under the landing, stepping
    /// off onto it, each finish refused ("does not meet the raised landing's lip"), and none was laid; now the walk climbs to
    /// the stair over the landing and comes down onto it.</summary>
    [Fact]
    public void AnApproachHalfARiseUnderARaisedLandingClimbsTheStairOverItAndFinishesOntoIt()
    {
        const float landing = 49.44f;
        var (span, why, tops) = Lay(landing);
        Assert.True(span != null, why);
        Assert.NotEmpty(tops);
        var (highest, walk) = Walked(span!.Plan, landing);
        Assert.Equal(Pad + 10f, highest, 2);
        Assert.Equal((true, true), walk);
    }

    /// <summary>The landing as the networks that reached it read it, 49.50: the stair over it 0.49 m proud. Its search ended on
    /// that stair before and does now, finished with the floor and a stair down onto the landing (in game LandingThenDown, the
    /// stair down off the top; here FloorThenDown, a floor between).</summary>
    [Fact]
    public void AnApproachHalfARiseUnderARaisedLandingReadLevelStillFinishesWithAStairDownOntoIt()
    {
        const float landing = 49.50f;
        var (span, why, tops) = Lay(landing);
        Assert.True(span != null, why);
        var (top, finish) = Assert.Single(tops);
        Assert.Equal(Pad + 10f, top.y, 2);
        Assert.Contains(finish, new[] { StairLayout.TopFinish.LandingThenDown, StairLayout.TopFinish.FloorThenDown });
        Assert.Equal((true, true), Walked(span!.Plan, landing).walk);
    }
}
