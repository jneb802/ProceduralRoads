using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A location has one way in up a face (LocationWaysIn, the one owner of the ways into a location). Read from a generated
/// MountainCave02 whose landing stands at about 143.5 near 5706,3585: the first road's approach climbs from its foot at
/// 5724.2,3596.1 (130.9) west up the slope; the second road's way in came up the same face beside it three ways: a staircase
/// 6.5 to 16 m off, a road onto the landing 8 to 14 m off once that was refused, and a staircase 11 to 19 m off once refused
/// in the corridor nearer. Each is the first's way in built twice; ways in going opposite ways are not.
/// </summary>
public class ApproachCorridorTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    // The suite runs on plain roads (PlainRoads: the corridor snap off); the rule is read with the mod's corridor.
    private readonly IDisposable _corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);

    public ApproachCorridorTests()
    {
        StairStructures.Reset();
        LocationWaysIn.Reset();
        RoadNetworkGenerator.RoadSnap = (float)PlainRoads.ModDefaults["road snap"];
        RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
    }

    public void Dispose()
    {
        StairStructures.Reset();
        LocationWaysIn.Reset();
        _corridor.Dispose();
        _scope.Dispose();
    }

    private const bool S = true, F = false;

    /// <summary>The location the ways come into (its centre behind the landing); an approach carries it as its location
    /// (StairRun.ExpectedLocation, RoadStairRuns.LocationOf).</summary>
    private static readonly Vector2 Cave = new(5698f, 3586f);
    private static readonly Vector2 Foot = new(5724.19f, 3596.05f);

    /// <summary>The first approach's walked pieces as laid (stair or floor, x, y, z, yaw).</summary>
    private static readonly (bool stair, float x, float y, float z, float yaw)[] First =
    {
        (S, 5723.13f, 129.92f, 3596.55f, 22.5f),
        (S, 5722.37f, 130.92f, 3594.70f, 22.5f),
        (S, 5722.37f, 131.92f, 3592.09f, 337.5f),
        (S, 5722.82f, 132.92f, 3589.78f, 0f),
        (S, 5722.37f, 133.92f, 3587.47f, 22.5f),
        (S, 5720.52f, 134.92f, 3585.63f, 67.5f),
        (S, 5718.67f, 135.92f, 3584.86f, 67.5f),
        (S, 5716.06f, 136.92f, 3584.86f, 112.5f),
        (S, 5714.21f, 137.92f, 3586.71f, 157.5f),
        (S, 5712.90f, 138.92f, 3588.66f, 135f),
        (S, 5711.49f, 139.92f, 3590.08f, 135f),
        (S, 5710.08f, 140.92f, 3591.49f, 135f),
        (S, 5707.36f, 141.92f, 3592.03f, 67.5f),
        (S, 5705.51f, 142.92f, 3591.27f, 67.5f),
    };

    /// <summary>The second road's approach as laid, from its foot up to the landing.</summary>
    private static readonly (bool stair, float x, float y, float z, float yaw)[] Second =
    {
        (S, 5738.10f, 134.04f, 3583.98f, 112.5f),
        (S, 5736.25f, 135.04f, 3584.75f, 112.5f),
        (S, 5733.64f, 136.04f, 3584.75f, 67.5f),
        (F, 5731.79f, 136.54f, 3583.98f, 247.5f),
        (S, 5729.94f, 137.04f, 3583.22f, 67.5f),
        (F, 5728.09f, 137.54f, 3582.45f, 247.5f),
        (S, 5726.24f, 138.04f, 3581.69f, 67.5f),
        (S, 5724.29f, 139.04f, 3580.38f, 45f),
        (S, 5722.34f, 140.04f, 3579.07f, 67.5f),
        (F, 5720.49f, 140.54f, 3578.31f, 247.5f),
        (F, 5718.64f, 140.54f, 3577.54f, 247.5f),
        (F, 5716.79f, 140.54f, 3576.78f, 247.5f),
        (S, 5714.07f, 141.04f, 3577.32f, 135f),
        (S, 5712.12f, 142.04f, 3578.62f, 112.5f),
        (S, 5709.81f, 143.04f, 3579.08f, 90f),
    };

    /// <summary>The first road's approach as laid once its first layout (<see cref="First"/>) was refused for a top with no
    /// way off it (StairEnds) and the next aim tried, read back from the game's save: from the same foot north up the slope,
    /// then west onto the landing along 3581.6, its top floor 2.5 m from <see cref="Second"/>'s. Its line foot to top heads
    /// 31 degrees off the second's (over CorridorAngle) while their last flights climb onto the landing side by side.</summary>
    private static readonly (bool stair, float x, float y, float z, float yaw)[] Retried =
    {
        (S, 5725.12f, 129.92f, 3596.26f, 337.5f),
        (S, 5725.89f, 130.92f, 3594.42f, 337.5f),
        (S, 5726.65f, 131.92f, 3592.57f, 337.5f),
        (S, 5727.11f, 132.92f, 3590.26f, 0f),
        (S, 5727.11f, 133.92f, 3588.26f, 0f),
        (S, 5726.11f, 134.92f, 3585.85f, 45f),
        (S, 5724.16f, 135.92f, 3584.54f, 67.5f),
        (S, 5722.31f, 136.92f, 3583.78f, 67.5f),
        (S, 5720.35f, 137.92f, 3582.47f, 45f),
        (S, 5718.40f, 138.92f, 3581.16f, 67.5f),
        (S, 5716.09f, 139.92f, 3580.70f, 90f),
        (S, 5713.79f, 140.92f, 3581.16f, 112.5f),
        (S, 5711.48f, 141.92f, 3581.62f, 90f),
        (S, 5709.48f, 142.92f, 3581.62f, 90f),
        (F, 5707.48f, 143.42f, 3581.62f, 270f),
    };

    /// <summary>The second road's approach as laid once the corridor refused it at every aim nearer: the same face, from its
    /// foot at 5738.2,3583.8 (134.5) up to 5711.1,3572.9 (143.5), its flights 11 to 19 m from the first's and its top 18 m
    /// from the first's (a corridor run of 10 m, under CorridorRun).</summary>
    private static readonly (bool stair, float x, float y, float z, float yaw)[] SecondFurther =
    {
        (S, 5738.19f, 134.04f, 3583.78f, 90f),
        (S, 5736.19f, 135.04f, 3583.78f, 90f),
        (S, 5733.77f, 136.04f, 3582.78f, 45f),
        (S, 5731.36f, 137.04f, 3581.78f, 90f),
        (S, 5728.95f, 138.04f, 3580.78f, 45f),
        (S, 5726.53f, 139.04f, 3579.78f, 90f),
        (S, 5724.12f, 140.04f, 3578.78f, 45f),
        (S, 5722.16f, 141.04f, 3577.47f, 67.5f),
        (F, 5720.31f, 141.54f, 3576.71f, 247.5f),
        (F, 5718.47f, 141.54f, 3575.94f, 247.5f),
        (F, 5716.62f, 141.54f, 3575.18f, 247.5f),
        (F, 5714.77f, 141.54f, 3574.41f, 247.5f),
        (S, 5712.92f, 142.04f, 3573.64f, 67.5f),
        (S, 5711.08f, 143.04f, 3572.88f, 67.5f),
    };

    /// <summary>The second road's way onto the landing laid as a road once its staircase was refused beside the first's: from
    /// 5748.0,3576.5 (134.4) west up the same slope to 5703.0,3577.0 (144.0), 8 to 14 m from the first's flights.</summary>
    private static readonly (float x, float z, float h)[] ItsRoad =
    {
        (5748.04f, 3576.53f, 134.38f), (5747.06f, 3576.54f, 134.38f), (5746.08f, 3576.55f, 134.40f), (5745.10f, 3576.56f, 134.44f),
        (5744.12f, 3576.57f, 134.49f), (5743.14f, 3576.58f, 134.57f), (5742.16f, 3576.59f, 134.67f), (5741.19f, 3576.61f, 134.78f),
        (5740.21f, 3576.62f, 134.93f), (5739.23f, 3576.63f, 135.16f), (5738.25f, 3576.64f, 135.40f), (5737.27f, 3576.65f, 135.63f),
        (5736.29f, 3576.66f, 135.86f), (5735.31f, 3576.67f, 136.12f), (5734.33f, 3576.68f, 136.39f), (5733.35f, 3576.69f, 136.67f),
        (5732.37f, 3576.70f, 136.98f), (5731.39f, 3576.71f, 137.36f), (5730.42f, 3576.72f, 137.76f), (5729.44f, 3576.73f, 138.16f),
        (5728.46f, 3576.74f, 138.55f), (5727.48f, 3576.75f, 138.98f), (5726.50f, 3576.76f, 139.44f), (5725.52f, 3576.77f, 139.93f),
        (5724.54f, 3576.78f, 140.42f), (5723.56f, 3576.79f, 140.51f), (5722.58f, 3576.80f, 140.60f), (5721.60f, 3576.81f, 140.69f),
        (5720.62f, 3576.82f, 140.78f), (5719.65f, 3576.83f, 140.86f), (5718.67f, 3576.84f, 140.95f), (5717.69f, 3576.85f, 141.04f),
        (5716.71f, 3576.86f, 141.34f), (5715.73f, 3576.87f, 141.65f), (5714.75f, 3576.88f, 142.01f), (5713.77f, 3576.89f, 142.38f),
        (5712.79f, 3576.90f, 142.78f), (5711.81f, 3576.91f, 143.16f), (5710.83f, 3576.92f, 143.51f), (5709.85f, 3576.93f, 143.83f),
        (5708.87f, 3576.94f, 143.88f), (5707.90f, 3576.95f, 143.92f), (5706.92f, 3576.96f, 143.95f), (5705.94f, 3576.97f, 143.98f),
        (5704.96f, 3576.98f, 144.00f), (5703.98f, 3576.99f, 144.00f), (5703.00f, 3577.00f, 144.00f),
    };

    private static StairPlan Plan(IEnumerable<(bool stair, float x, float y, float z, float yaw)> pieces, Vector2 shift = default)
    {
        var plan = new StairPlan { Kit = "stone", Reached = true };
        foreach (var (stair, x, y, z, yaw) in pieces)
            plan.Pieces.Add(new StairPiece
            {
                Kind = stair ? StairPieceKind.Stair : StairPieceKind.Landing,
                Prefab = stair ? "stone_stair" : "stone_floor_2x2",
                Position = new Vector3(x + shift.x, y, z + shift.y),
                YawDegrees = yaw,
            });
        StairPiece last = plan.Pieces[plan.Pieces.Count - 1];
        plan.End = last.Position;
        return plan;
    }

    /// <summary>A staircase laid as an approach of the cave, from its first piece to its last.</summary>
    private static StairRun LayApproach(IReadOnlyList<(bool stair, float x, float y, float z, float yaw)> pieces)
    {
        var (_, fx, _, fz, _) = pieces[0];
        var (_, tx, _, tz, _) = pieces[pieces.Count - 1];
        var run = StairRun.FromPlan(new Vector2(fx, fz), new Vector2(tx, tz), RoadStairRuns.Width, Plan(pieces), StairKit.Stone);
        run.KeepsLocationRocks = true;
        run.ExpectedLocation = (Cave, 143f);
        StairStructures.Add(run);
        return run;
    }

    /// <summary>The first road's approach, laid: an approach of the cave, or (<paramref name="approach"/> false) a road's own staircase.</summary>
    private static StairRun LayFirst(bool approach = true)
    {
        StairPlan plan = Plan(First.Append((F, 5703.66f, 143.42f, 3590.50f, 247.5f)));
        var run = StairRun.FromPlan(Foot, new Vector2(5703.66f, 3590.50f), RoadStairRuns.Width, plan, StairKit.Stone);
        run.KeepsLocationRocks = approach;
        if (approach) run.ExpectedLocation = (Cave, 143f);
        StairStructures.Add(run);
        return run;
    }

    private static (bool stair, float x, float y, float z, float yaw)[] Turned(IEnumerable<(bool stair, float x, float y, float z, float yaw)> pieces, float turn)
    {
        float c = Mathf.Cos(turn * Mathf.Deg2Rad), sn = Mathf.Sin(turn * Mathf.Deg2Rad);
        return pieces.Select(p =>
        {
            float dx = p.x - Cave.x, dz = p.z - Cave.y;
            return (p.stair, Cave.x + dx * c - dz * sn, p.y, Cave.y + dx * sn + dz * c, p.yaw - turn);
        }).ToArray();
    }

    private static (bool stair, float x, float y, float z, float yaw)[] ComingDown((bool stair, float x, float y, float z, float yaw)[] pieces)
    {
        float top = pieces.Max(p => p.y), bottom = pieces.Min(p => p.y);
        return pieces.Select(p => (p.stair, p.x, top + bottom - p.y, p.z, p.yaw + 180f)).ToArray();
    }

    /// <summary>The second staircase up the same face is that way in built twice, laid near or (refused nearer) further across.</summary>
    [Fact]
    public void ASecondStaircaseUpTheSameFaceIsAWayInTheCaveHas()
    {
        LayFirst();
        Assert.True(RoadStairRuns.DuplicatesAWayIn(Plan(Second), Cave));
        Assert.True(RoadStairRuns.DuplicatesAWayIn(Plan(SecondFurther), Cave));
    }

    /// <summary>A road up that face onto the landing is too: its road ends on the first's road instead, at the first's foot.</summary>
    [Fact]
    public void ARoadUpTheSameFaceJoinsTheApproachAtItsFoot()
    {
        StairRun first = LayFirst();
        var way = LocationWaysIn.Of(ItsRoad.Select(p => new Vector2(p.x, p.z)).ToList(), ItsRoad.Select(p => p.h).ToList(), Cave);
        Assert.Same(first, LocationWaysIn.Joining(Cave, way, out Vector2 joinAt)?.Approach);
        Assert.True(Vector2.Distance(joinAt, Foot) < 0.01f, $"joined at {joinAt}");
    }

    /// <summary>Ways in going opposite ways are other ways in: one coming down onto the landing beside the first coming up,
    /// one up another face (the far side, either side: turned 135 degrees, past SameSideAngle whichever way the layout leans), and the same as a road.</summary>
    [Theory]
    [InlineData(0f, true)]
    [InlineData(180f, false)]
    [InlineData(135f, false)]
    [InlineData(-135f, false)]
    public void AWayInGoingAnotherWayIsStillLaid(float turn, bool comingDown)
    {
        LayFirst();
        var other = Turned(comingDown ? ComingDown(SecondFurther) : SecondFurther, turn);
        Assert.False(RoadStairRuns.DuplicatesAWayIn(Plan(other), Cave));
        var road = Turned(comingDown ? ItsRoad.Select(p => (S, p.x, p.h, p.z, 0f)).ToArray() is var r ? ComingDown(r) : r
            : ItsRoad.Select(p => (S, p.x, p.h, p.z, 0f)).ToArray(), turn);
        Assert.Null(LocationWaysIn.Joining(Cave, LocationWaysIn.Of(road.Select(p => new Vector2(p.x, p.z)).ToList(), road.Select(p => p.y).ToList(), Cave), out _));
    }

    /// <summary>A road's own staircase is no way into a location; and the rule is off with the corridor snap.</summary>
    [Fact]
    public void NotBesideARoadsOwnStaircaseNorWithTheCorridorSnapOff()
    {
        LayFirst(approach: false);
        Assert.False(RoadStairRuns.DuplicatesAWayIn(Plan(Second), Cave));
        StairStructures.Reset();
        LayFirst();
        RoadNetworkGenerator.CorridorSnap = 0f;
        Assert.False(RoadStairRuns.DuplicatesAWayIn(Plan(Second), Cave));
    }

    /// <summary>The twin as built: the first approach laid as retried (<see cref="Retried"/>, north from its foot then west),
    /// the second (<see cref="Second"/>, west from its foot) climbs onto the landing beside its last flight. Their lines foot
    /// to top are 31 degrees apart and their roads leave their feet south and east; they step onto the landing the same way,
    /// so the second is that way in built twice.</summary>
    [Fact]
    public void ASecondStaircaseBesideARetriedFirstsLastFlightIsAWayInTheCaveHas()
    {
        LayApproach(Retried);
        Assert.True(RoadStairRuns.DuplicatesAWayIn(Plan(Second), Cave));
    }

    /// <summary>Two staircases climbing onto the landing the same way are one way in whichever way their roads leave their
    /// feet: the retried first and its mirror across its last flight (its foot north of the landing's line, its road leaving
    /// north, the first's south), the second's top 2.5 m beside the first's as in game.</summary>
    [Fact]
    public void TheSameClimbWithItsRoadLeavingTheOtherWayIsStillTheSameWayIn()
    {
        LayApproach(Retried);
        float axis = 3581.62f - 1.25f;
        var mirrored = Retried.Select(p => (p.stair, p.x, p.y, 2f * axis - p.z, 180f - p.yaw)).ToArray();
        var first = LocationWaysIn.Of(Plan(Retried), Cave)!;
        var second = LocationWaysIn.Of(Plan(mirrored), Cave)!;
        float apart = (float)(Math.Acos(Vector2.Dot((first.Inner - first.Outer).normalized, (second.Inner - second.Outer).normalized)) * 180.0 / Math.PI);
        Assert.True(apart > 2f * RoadNetworkGenerator.CorridorAngle, $"their lines foot to top {apart:F0} degrees apart");
        Assert.True(Vector2.Dot((first.Line[1] - first.Line[0]).normalized, (second.Line[1] - second.Line[0]).normalized) < 0f,
            "their feet set off opposite ways");
        Assert.True(RoadStairRuns.DuplicatesAWayIn(Plan(mirrored), Cave));
    }

    /// <summary>The whole-network invariant (LocationWaysIn.Twins, beside the staircase-end audit): the twin as built is named
    /// with its location and both staircases' ends; either alone, or the first with one up another face, is none.</summary>
    [Fact]
    public void TheTwinAsBuiltIsTheNetworksOneTwin()
    {
        LayApproach(Retried);
        Assert.Empty(LocationWaysIn.Twins());
        LayApproach(Turned(SecondFurther, 135f));
        Assert.Empty(LocationWaysIn.Twins());
        LayApproach(Second);
        var twins = LocationWaysIn.Twins();
        var twin = Assert.Single(twins);
        Assert.Contains("(5725.1, 3596.3) -> (5707.5, 3581.6)", twin.line);
        Assert.Contains("(5738.1, 3584.0) -> (5709.8, 3579.1)", twin.line);
        Assert.True(Vector2.Distance(twin.location, RoadStairRuns.IntoTheLocation(Cave)) < 0.01f, twin.line);
    }

    /// <summary>The invariant is the rule's own test, read whether or not the rule ran (the corridor snap off).</summary>
    [Fact]
    public void TheInvariantReadsTheNetworkWithTheRuleOff()
    {
        RoadNetworkGenerator.CorridorSnap = 0f;
        LayApproach(Retried);
        LayApproach(Second);
        Assert.False(RoadStairRuns.DuplicatesAWayIn(Plan(Second), Cave));
        Assert.Single(LocationWaysIn.Twins());
    }

    /// <summary>A road arriving as road is a way in too (LocationWaysIn.Record): a second road up the same face beside it, 8 m
    /// off at its heights, joins it where its arrival passes nearest; one in from the other side does not.</summary>
    [Fact]
    public void ASecondRoadUpTheSameFaceJoinsTheFirstRoad()
    {
        var points = ItsRoad.Select(p => new Vector2(p.x, p.z)).ToList();
        var heights = ItsRoad.Select(p => p.h).ToList();
        LocationWaysIn.Record(Cave, LocationWaysIn.Of(points, heights, Cave)!);
        var beside = LocationWaysIn.Of(points.ConvertAll(p => p + new Vector2(0f, -8f)), heights, Cave);
        Assert.NotNull(LocationWaysIn.Joining(Cave, beside, out Vector2 joinAt));
        Assert.Contains(points, p => Vector2.Distance(p, joinAt) < 0.01f);
        var farSide = Turned(ItsRoad.Select(p => (S, p.x, p.h, p.z, 0f)), 180f);
        Assert.Null(LocationWaysIn.Joining(Cave, LocationWaysIn.Of(farSide.Select(p => new Vector2(p.x, p.z)).ToList(), farSide.Select(p => p.y).ToList(), Cave), out _));
    }

    /// <summary>A straight way into the Cave heading <paramref name="degrees"/> (from east), 16 m long, climbing (1) or coming down (-1) 3 m.</summary>
    private static LocationWaysIn.Way Straight(float degrees, int rise)
    {
        var dir = new Vector2(Mathf.Cos(degrees * Mathf.PI / 180f), Mathf.Sin(degrees * Mathf.PI / 180f));
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int k = 0; k <= 8; k++)
        {
            points.Add(Cave - dir * (16f - 2f * k));
            heights.Add(rise > 0 ? 130f + 0.375f * k : 133f - 0.375f * k);
        }
        return LocationWaysIn.Of(points, heights, Cave)!;
    }

    /// <summary>The same-side angle is the locations' own (<see cref="LocationWaysIn.SameSideAngle"/>, the project owner's, 5 Nov), not the road
    /// corridor's: two ways climbing the same way 60 degrees apart are one way in; at the angle or more, two.</summary>
    [Fact]
    public void TwoWaysClimbingTheSameWaySixtyDegreesApartAreOneWayIn()
    {
        Assert.True(LocationWaysIn.SameSideAngle > RoadNetworkGenerator.CorridorAngle);
        Assert.True(LocationWaysIn.SameWay(Straight(0f, 1), Straight(60f, 1)));
        Assert.True(LocationWaysIn.SameWay(Straight(0f, -1), Straight(60f, -1)));
        Assert.True(LocationWaysIn.SameWay(Straight(0f, 1), Straight(LocationWaysIn.SameSideAngle - 1f, 1)));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(45f)]
    public void TwoWaysClimbingTheSameWayAtTheSameSideAngleOrMoreApartAreTwo(float more)
    {
        Assert.False(LocationWaysIn.SameWay(Straight(0f, 1), Straight(LocationWaysIn.SameSideAngle + more, 1)));
        Assert.False(LocationWaysIn.SameWay(Straight(0f, -1), Straight(-(LocationWaysIn.SameSideAngle + more), -1)));
    }

    /// <summary>One close staircase going up and one going down are two ways in, however close.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(10f)]
    [InlineData(60f)]
    public void AWayUpAndAWayDownBesideEachOtherAreBothKept(float apart)
    {
        Assert.False(LocationWaysIn.SameWay(Straight(0f, 1), Straight(apart, -1)));
        Assert.False(LocationWaysIn.SameWay(Straight(apart, -1), Straight(0f, 1)));
    }

    /// <summary>A turning approach whose top lands farther from the location's centre than its foot: east 20 m along the location's
    /// row, then north, climbing a metre a stair. Its location end is its own (<see cref="StairRun.To"/>, the plan's end), never the
    /// end nearer the centre, so it reads as climbing (Rise +1), as a staircase and as a plan; coming down it reads -1.</summary>
    private static (bool stair, float x, float y, float z, float yaw)[] TurningAway(bool up)
    {
        var pieces = new List<(bool, float, float, float, float)>();
        for (int k = 0; k < 14; k++)
        {
            float x = k < 10 ? Cave.x + 10f + 2f * k : Cave.x + 28f;
            float z = k < 10 ? Cave.y : Cave.y + 2f * (k - 9);
            pieces.Add((S, x, up ? 130f + k : 143f - k, z, k < 10 ? 90f : 0f));
        }
        return pieces.ToArray();
    }

    [Fact]
    public void ATurningApproachWhoseTopIsFartherFromTheLocationThanItsFootStillClimbs()
    {
        var up = TurningAway(true);
        var top = new Vector2(up[up.Length - 1].x, up[up.Length - 1].z);
        Assert.True(Vector2.Distance(top, Cave) > Vector2.Distance(new Vector2(up[0].x, up[0].z), Cave), "its top is farther from the centre than its foot");
        var run = StairRun.FromPlan(new Vector2(up[0].x, up[0].z), top, RoadStairRuns.Width, Plan(up), StairKit.Stone);
        run.KeepsLocationRocks = true;
        run.ExpectedLocation = (Cave, 143f);
        Assert.Equal(1, LocationWaysIn.Of(run, Cave)!.Rise);
        Assert.Equal(1, LocationWaysIn.Of(Plan(up), Cave)!.Rise);
        Assert.Equal(-1, LocationWaysIn.Of(Plan(TurningAway(false)), Cave)!.Rise);
    }

    /// <summary>An up staircase and a down staircase side by side (the same lines, 2 m apart) are both kept.</summary>
    [Fact]
    public void AnUpStaircaseAndADownStaircaseSideBySideAreBothKept()
    {
        var up = TurningAway(true);
        var down = TurningAway(false).Select(p => (p.stair, p.x, p.y, p.z + 2f, p.yaw)).ToArray();
        LayApproach(up);
        Assert.True(LocationWaysIn.There(Cave).Count == 1);
        Assert.False(RoadStairRuns.DuplicatesAWayIn(Plan(down), Cave));
        LayApproach(down);
        Assert.Empty(LocationWaysIn.Twins());
        Assert.Equal(2, LocationWaysIn.There(Cave).Count);
    }
}
