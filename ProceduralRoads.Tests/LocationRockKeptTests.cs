using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Which of a location's rocks an approach keeps (assistant-written, 6 Nov 2026): a large one, any at a location keeping every
/// rock, or any round its mouth. A location's shape splits them into its body (round its mouth) and the rocks kept beside its
/// approach; the rest are cleared out of the way.
/// </summary>
public class LocationRockKeptTests
{
    private static readonly Vector2 Centre = new(100f, -200f);

    /// <summary>A rock is kept for any one of the three reasons, the first holding named (large, then a location keeping every
    /// rock, then round its mouth), as the rock clearing's verdict words it; for none it is cleared.</summary>
    [Fact]
    public void ARockIsKeptForAnyOfItsThreeReasonsTheFirstNamed()
    {
        foreach (var (large, every, mouth, why) in new[]
                 {
                     (false, false, false, ""), (false, false, true, "round its mouth"), (false, true, false, "a location keeping every rock"),
                     (false, true, true, "a location keeping every rock"), (true, false, false, "large"), (true, false, true, "large"),
                     (true, true, false, "large"), (true, true, true, "large"),
                 })
        {
            bool kept = LocationRock.Kept(large, every, mouth, out string reason);
            Assert.Equal(why != "", kept);
            Assert.Equal(kept, LocationRock.Kept(large, every, mouth));
            Assert.Equal(why, reason);
        }
    }

    /// <summary>A 3 x 3 x 1.5 m rock turned 45 degrees is 4.5 m across its oriented box (kept as small), though the world box of
    /// its colliders is 6.2 m (large). The rock clearing's probe asks the box planning reads (<see cref="LocationRock.Of"/>) the
    /// one rule (<see cref="LocationRock.KeptAt"/>), and the middle of the oriented box, not the rock's pivot, is what is
    /// measured against the location's exterior radius.</summary>
    [Fact]
    public void ARockIsMeasuredByItsOrientedBoxAndItsMiddleAsPlanningDoes()
    {
        var local = new Bounds(new Vector3(2f, 0.75f, 0f), new Vector3(3f, 1.5f, 3f));
        LocationRock turned = LocationRock.Of(new Vector3(Centre.x + 20f, 10f, Centre.y), Quaternion.Euler(0f, 45f, 0f), new Vector3(1f, 1f, 1f), local, "r");
        Assert.False(turned.Large);
        Assert.InRange(Mathf.Sqrt(4f * turned.HalfX * turned.HalfX + 4f * turned.HalfZ * turned.HalfZ + 1.5f * 1.5f), 4.4f, 4.6f);
        Assert.False(turned.KeptAt(Centre, 10f, keepsEveryRock: false, out string why));
        Assert.Equal("", why);
        // The pivot is 20 m out, its middle 2 m further along its own x: round the mouth of a 21.5 m exterior radius by the pivot
        // alone, not by the middle.
        Assert.True(Vector2.Distance(turned.At, Centre) > 21.4f);
        Assert.False(turned.KeptAt(Centre, 21.4f, false, out _));
        Assert.True(turned.KeptAt(Centre, 22f, false, out why));
        Assert.Equal("round its mouth", why);
        LocationRock big = LocationRock.Of(new Vector3(Centre.x + 40f, 10f, Centre.y), Quaternion.identity, new Vector3(2f, 2f, 2f), local, "r");
        Assert.True(big.KeptAt(Centre, 10f, false, out why));
        Assert.Equal("large", why);
    }

    /// <summary>The shape's split over twelve rocks (9.999, 10 and 10.001 m from the centre, the two sides of the large-rock
    /// size, with and without a real top; each triple of tops numbered in that order), for a location with an exterior radius
    /// of 10 m and for one with none, each keeping the small rocks or not: its body is the rocks round its mouth, its kept
    /// rocks the large ones elsewhere (every one, at a location keeping every rock). Recorded from the shape as it is.</summary>
    [Theory]
    [InlineData(10f, false, "0 1 2 3 4 5 6 7", "10 11")]
    [InlineData(10f, true, "0 1 2 3 4 5 6 7", "8 9 10 11")]
    [InlineData(0f, false, "", "2 3 6 7 10 11")]
    [InlineData(0f, true, "", "0 1 2 3 4 5 6 7 8 9 10 11")]
    public void AShapeKeepsItsLargeRocksAndItsMouthsAsItsBody(float rockRadius, bool keepSmall, string body, string kept)
    {
        // Half a side at which a square rock's box is LargeRock across corner to corner, and either side of it.
        float edge = LocationRock.LargeRock / Mathf.Sqrt(8f);
        var rocks = new List<LocationRock>();
        int n = 0;
        foreach (float d in new[] { 9.999f, 10f, 10.001f })
            foreach (float half in new[] { edge - 1e-4f, edge + 1e-4f })
                foreach (bool top in new[] { false, true })
                {
                    Vector2 at = Centre + LocationRock.World(new Vector2(0f, d), 90f);
                    rocks.Add(new LocationRock(at, half, half, 0f, 0f, 0f, $"{n++}",
                        top ? new[] { 1f } : null, Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y), top ? 1 : 0));
                }
        PlacedLocationShape shape = PlacedLocationShape.Of(Centre, 0f, new LevelOp(0f, 0f, 0f, 4f, false), rockRadius, rocks,
            keepSmallRocks: keepSmall);

        Assert.Equal(body, string.Join(" ", shape.Body.Select(r => r.Name)));
        Assert.Equal(kept, string.Join(" ", shape.Rocks.Select(r => r.Name)));
    }
}
