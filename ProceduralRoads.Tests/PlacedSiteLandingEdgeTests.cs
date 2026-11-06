using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road onto a placed place's landing ends at the edge of its flat, not inside it (<see cref="PlacedSiteShape.Landings"/>).
/// In game: Mistlands_Excavation3 at (1649.6, -6338.7), turned 315, its flat 17 m round its centre at its
/// root (59.99), its wooden wall about 14.7 m round it with a gap the road comes in by from the west. The road ran through the
/// gap and on about 3.5 m inside the wall line, ending near (1638, -6337), 11 m from the centre.
/// </summary>
public sealed class PlacedSiteLandingEdgeTests
{
    private static readonly Vector2 Centre = new(1650f, -6339f);
    private const float Yaw = 270f, Root = 59.99f, Wall = 14.7f, Width = 4f;

    /// <summary>Mistlands_Excavation3's levelling (its placed record): the 17 m flat smoothed out to 25 m, its sinks.</summary>
    private static readonly IReadOnlyList<LevelOp> Ops = new[]
    {
        new LevelOp(0f, 0f, 0f, 17f, false, 25f, 3f),
        new LevelOp(-0.55f, 0.3f, -7f, 0f, false, 6f, 2f),
        new LevelOp(-0.16f, -3.97f, -5.5f, 0f, false, 4f, 2f),
        new LevelOp(1.79f, 5.09f, -4f, 0f, false, 4f, 2f),
        new LevelOp(-4.44f, 3.18f, -3f, 0f, false, 4f, 2f),
    };

    /// <summary>The camp's placed ground: its flat, blended out to the natural ground 3 m over its root round it (the road
    /// stands at 63.1 at x 1625.7, 24 m out on the west).</summary>
    private static readonly Func<float, float, float> Placed = Built();

    private static float Ground(float x, float z) => Placed(x, z);

    /// <summary>The ground the game makes (<see cref="PlacedSiteGround.Build"/>) from that natural ground, its sinks included.
    /// The camp stands on a whole metre and faces west, so its flat's edge falls on a vertex as the game's mesh has it.</summary>
    private static Func<float, float, float> Built()
    {
        const int x0 = 1580, z0 = -6400, size = 140;
        float[] heights = PlacedSiteGround.Build(x0, z0, size, size, Centre, Root, Yaw, Ops, (_, _) => Root + 3f);
        Func<float, float, float> grid = PlacedSiteGround.Sampler(heights, x0, z0, size, size);
        return (x, z) => grid(x, z) is float h && h < PlacedSiteGround.OffGrid ? h : Root + 3f;
    }

    private static PlacedSiteShape Shape() =>
        PlacedSiteShape.Of(Centre, Yaw, RoadStairRuns.FlatOf(Ops)!.Value, 17f, null, Ground, root: Root, prefab: "Mistlands_Excavation3");

    [Fact]
    public void ARoadOntoALevelFlatsLandingEndsAtItsEdgeNotInsideTheWall()
    {
        PlacedSiteShape shape = Shape();
        float reach = RoadSiteEntry.InsideReach(Width);
        var landings = shape.Landings(Ground, reach + RoadSiteEntry.EndSlack, RoadSiteEntry.RoadLandingTries,
            Width * 0.5f + PlacedSiteShape.BodyGap);
        Assert.NotEmpty(landings);
        foreach (var (at, _) in landings)
            Assert.True(Vector2.Distance(at, Centre) >= Wall,
                $"a landing at ({at.x:F0}, {at.y:F0}) is {Vector2.Distance(at, Centre):F1} m from the camp's centre, inside its {Wall} m wall");
    }

    /// <summary>With no level ground past the flat's edge less <see cref="PlacedSiteShape.LevelRadius"/>, the road aims at the
    /// flat's edge on its front axis (<see cref="PlacedSiteShape.LandingFor"/>), not at ground deeper in.</summary>
    [Fact]
    public void ALevelFlatWithNoLevelGroundAtItsEdgeHasNoLandingInsideIt()
    {
        var flat = new LevelOp(0f, 0f, 0f, 12f, false, 12f, 3f);
        // Level within the flat, a steep slope from 11 m out: nothing past 10 m is level over 2 m.
        float Steep(float x, float z) { float r = Mathf.Sqrt(x * x + z * z); return r <= 11f ? 50f : 50f + 2f * (r - 11f); }
        var shape = PlacedSiteShape.Of(new Vector2(0f, 0f), 0f, flat, 0f, null, Steep, root: 50f);
        Assert.All(shape.Landings(Steep, 1f), l => Assert.True(Vector2.Distance(l.at, new Vector2(0f, 0f)) >= 12f - PlacedSiteShape.LevelRadius,
            $"a landing {Vector2.Distance(l.at, new Vector2(0f, 0f)):F1} m in"));
        Assert.Equal(12f, Vector2.Distance(shape.LandingFor(1f), new Vector2(0f, 0f)), 3);
    }

    /// <summary>A sunk floor's landing is still the ground just past its pit (a frost cave's ledge): its edge is the pit's.</summary>
    [Fact]
    public void ASunkFloorsLandingIsStillJustPastItsPit()
    {
        var pit = new LevelOp(0f, 0f, -4f, 5f, false, 8f, 2f);
        float Flat(float x, float z) => Mathf.Sqrt(x * x + z * z) <= 8f ? 46f : 50f;
        var shape = PlacedSiteShape.Of(new Vector2(0f, 0f), 0f, pit, 10f, null, Flat, root: 50f);
        var landings = shape.Landings(Flat, 1f);
        Assert.NotEmpty(landings);
        Assert.All(landings, l => Assert.InRange(Vector2.Distance(l.at, new Vector2(0f, 0f)), 9f, 22f));
    }
}
