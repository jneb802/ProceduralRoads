using System;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// RoadSurface: dirt around spawn, the game's paving further out, handed over
/// in 10 m around a wobbled 1000 m ring, never letting the ground show through.
/// </summary>
public class RoadSurfaceTests
{
    /// <summary>A point at a distance from the centre along a compass heading in degrees.</summary>
    private static (float x, float z) At(float distance, float degrees)
    {
        double a = degrees * Math.PI / 180.0;
        return ((float)(distance * Math.Sin(a)), (float)(distance * Math.Cos(a)));
    }

    /// <summary>Headings where the game's WorldAngle is +1 and -1 (sin(20 × angle)).</summary>
    private const float OuterLobe = 4.5f;   // 20 × 4.5° = 90°
    private const float InnerLobe = 13.5f;  // 20 × 13.5° = 270°

    private static void AssertMask(Color expected, Color actual, string what) =>
        Assert.True(Mathf.Abs(actual.r - expected.r) < 1e-5f && Mathf.Abs(actual.g - expected.g) < 1e-5f
                    && Mathf.Abs(actual.b - expected.b) < 1e-5f,
            $"{what} is ({actual.r:F3}, {actual.g:F3}, {actual.b:F3}), expected ({expected.r:F3}, {expected.g:F3}, {expected.b:F3})");

    [Fact]
    public void LobesAreWhereTheGameWobblesTheRing()
    {
        var (ox, oz) = At(1000f, OuterLobe);
        var (ix, iz) = At(1000f, InnerLobe);
        Assert.True(WorldGenerator.WorldAngle(ox, oz) > 0.999f);
        Assert.True(WorldGenerator.WorldAngle(ix, iz) < -0.999f);
    }

    [Fact]
    public void DirtAroundSpawn()
    {
        // The handover ring comes no closer than 900 m, and its band starts 5 m before it.
        AssertMask(Heightmap.m_paintMaskDirt, RoadSurface.MaskAt(0f, 0f), "the world centre");
        for (float degrees = 0f; degrees < 360f; degrees += 1f)
        {
            foreach (float distance in new[] { 600f, 894f })
            {
                var (x, z) = At(distance, degrees);
                AssertMask(Heightmap.m_paintMaskDirt, RoadSurface.MaskAt(x, z), $"{distance} m at {degrees}°");
            }
        }
    }

    [Fact]
    public void HandoverTakesTenMetresOnTheWobbledRing()
    {
        // 1000 m + 100 m × WorldAngle: 1100 m on an outer lobe, 900 m on an
        // inner one; dirt 5 m before, stone 5 m after, a blend in between.
        foreach (var (degrees, ring) in new[] { (OuterLobe, 1100f), (InnerLobe, 900f) })
        {
            var (x, z) = At(ring - 5.5f, degrees);
            AssertMask(Heightmap.m_paintMaskDirt, RoadSurface.MaskAt(x, z), $"{ring - 5.5f} m at {degrees}°");
            (x, z) = At(ring, degrees);
            AssertMask(new Color(1f, 0f, 1f, 1f), RoadSurface.MaskAt(x, z), $"the ring itself, {ring} m at {degrees}°");
            (x, z) = At(ring + 5.5f, degrees);
            AssertMask(Heightmap.m_paintMaskPaved, RoadSurface.MaskAt(x, z), $"{ring + 5.5f} m at {degrees}°");
        }
    }

    [Fact]
    public void StoneBeyondTheHandover()
    {
        for (float degrees = 0f; degrees < 360f; degrees += 1f)
        {
            foreach (float distance in new[] { 1106f, 2000f, 9000f })
            {
                var (x, z) = At(distance, degrees);
                AssertMask(Heightmap.m_paintMaskPaved, RoadSurface.MaskAt(x, z), $"{distance} m at {degrees}°");
            }
        }
    }

    [Fact]
    public void ChangeIsSmoothAndOnlyTowardStone()
    {
        foreach (float degrees in new[] { 0f, OuterLobe, InnerLobe, 37f, 211f })
        {
            float previous = 0f;
            for (float distance = 0f; distance <= 1200f; distance += 0.25f)
            {
                var (x, z) = At(distance, degrees);
                float share = RoadSurface.StoneShare(x, z);
                Assert.True(share >= previous - 1e-6f, $"stone share falls at {distance} m, {degrees}°");
                // Smoothstep over 10 m rises at most 1.5/10 per metre.
                Assert.True(share - previous < 0.15f * 0.25f + 1e-4f, $"stone share jumps at {distance} m, {degrees}°");
                previous = share;
            }
            Assert.Equal(1f, previous);
        }
    }

    [Fact]
    public void GroundNeverShowsThroughTheChange()
    {
        // One channel is always full, so the road covers the ground and the
        // game clears grass on it (a channel above 0.5) all the way through.
        for (float stone = 0f; stone <= 1f; stone += 0.01f)
        {
            Color mask = RoadSurface.Mask(stone);
            Assert.Equal(1f, Mathf.Max(mask.r, mask.b));
            Assert.Equal(0f, mask.g);
            Assert.InRange(mask.r, 0f, 1f);
            Assert.InRange(mask.b, 0f, 1f);
        }
        // Half way it is paving laid over dirt.
        AssertMask(new Color(1f, 0f, 1f, 1f), RoadSurface.Mask(0.5f), "the mid-point");
    }

    [Fact]
    public void PavingComesInBeforeTheDirtGoes()
    {
        // Paving rises over the first half of the handover; the dirt under it
        // fades over the second.
        Color first = RoadSurface.Mask(0.25f);
        Assert.Equal(1f, first.r);
        Assert.True(Mathf.Abs(first.b - 0.5f) < 1e-6f);
        Color second = RoadSurface.Mask(0.75f);
        Assert.Equal(1f, second.b);
        Assert.True(Mathf.Abs(second.r - 0.5f) < 1e-6f);
    }
}
