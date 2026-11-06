using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Which of a place's rocks an approach keeps (assistant-written, 6 Nov 2026): a large one, any at a place keeping every
/// rock, or any round its mouth. A place's shape splits them into its body (round its mouth) and the rocks kept beside its
/// approach; the rest are cleared out of the way.
/// </summary>
public class SiteRockKeptTests
{
    private static readonly Vector2 Centre = new(100f, -200f);

    /// <summary>A rock is kept for any one of the three reasons, the first holding named (large, then a place keeping every
    /// rock, then round its mouth), as the rock clearing's verdict words it; for none it is cleared.</summary>
    [Fact]
    public void ARockIsKeptForAnyOfItsThreeReasonsTheFirstNamed()
    {
        foreach (bool large in new[] { false, true })
            foreach (bool every in new[] { false, true })
                foreach (bool mouth in new[] { false, true })
                {
                    bool kept = SiteRock.Kept(large, every, mouth, out string why);
                    Assert.Equal(large || every || mouth, kept);
                    Assert.Equal(kept, SiteRock.Kept(large, every, mouth));
                    Assert.Equal(large ? "large" : every ? "a place keeping every rock" : mouth ? "round its mouth" : "", why);
                }
    }

    /// <summary>The shape's split over rocks at its exterior radius's edge and either side of it, at the large-rock size
    /// and either side of it, with and without a real top, for a place with no exterior radius and for one keeping every
    /// rock: its body is the rocks round its mouth (less one covering the landing, its box only known), its kept rocks
    /// the large ones elsewhere (every one, at a place keeping every rock).</summary>
    [Theory]
    [InlineData(10f, false)]
    [InlineData(10f, true)]
    [InlineData(0f, false)]
    [InlineData(0f, true)]
    public void AShapeKeepsItsLargeRocksAndItsMouthsAsItsBody(float rockRadius, bool keepSmall)
    {
        // Half a side at which a square rock's box is LargeRock across corner to corner, and either side of it.
        float edge = SiteRock.LargeRock / Mathf.Sqrt(8f);
        var rocks = new List<SiteRock>();
        int n = 0;
        foreach (float d in new[] { 0f, 4f, 9.999f, 10f, 10.001f, 15f })
            foreach (float half in new[] { 0.5f, edge - 1e-4f, edge, edge + 1e-4f, 3f })
                foreach (bool top in new[] { false, true })
                    foreach (float bearing in new[] { 0f, 90f, 180f })
                    {
                        Vector2 at = Centre + SiteRock.World(new Vector2(0f, d), bearing);
                        rocks.Add(new SiteRock(at, half, half, 0f, 0f, 0f, $"rock{n++}",
                            top ? new[] { 1f } : null, Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y), top ? 1 : 0));
                    }
        PlacedSiteShape shape = PlacedSiteShape.Of(Centre, 0f, new LevelOp(0f, 0f, 0f, 4f, false), rockRadius, rocks,
            keepSmallRocks: keepSmall);

        var body = new List<string>();
        var kept = new List<string>();
        foreach (SiteRock rock in rocks)
        {
            bool mouth = rockRadius > 0f && Vector2.Distance(rock.At, Centre) <= rockRadius;
            if (mouth && (rock.Top != null || rock.Distance(shape.Landing) > 0f)) body.Add(rock.Name);
            if (!mouth && (rock.Large || keepSmall)) kept.Add(rock.Name);
        }
        Assert.Equal(body, shape.Body.Select(r => r.Name));
        Assert.Equal(kept, shape.Rocks.Select(r => r.Name));
        Assert.NotEmpty(kept);
        if (rockRadius > 0f) Assert.NotEmpty(body);
    }
}
