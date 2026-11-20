using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class ShoreFillRadiiTests
{
    private sealed class Shore : WorldGenerator
    {
        public float WaterX, Depth;
        public override float GetHeight(float x, float z) =>
            x > WaterX ? RoadConstants.ShallowWaterHeight - Depth : 31f;
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2.5f)]
    public void TinyReachSamplesOnlyTheHalfAndFullRadiiAcrossCallers(float depth)
    {
        using var corridor = StaticOverride.Keep(() => RoadPathfinder.SqueezeCorridor);
        using var fill = StaticOverride.Keep(() => RoadShoreFill.FillDepth);
        using var spread = StaticOverride.Keep(() => RoadEarthworkNoise.FillSpread);
        RoadPathfinder.SqueezeCorridor = 12f;
        RoadShoreFill.FillDepth = 2f;
        RoadEarthworkNoise.FillSpread = 1f;
        var world = new Shore { Depth = depth };
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 10f) });
        try
        {
            Vector2 p = new(21.999f, 0f);
            float reach = RoadPathfinder.SqueezeCorridor - RoadLocationProtection.EdgeDistance(p, RoadPathfinder.SqueezeCorridor);
            Assert.InRange(reach, 0f, 0.002f);
            world.WaterX = p.x + reach;
            // The old tolerance admitted many samples beyond this full radius.
            Assert.False(RoadShoreFill.Squeezed(world, p, buildable: false, out _));
            Assert.False(RoadShoreFill.Squeezed(world, p, buildable: true, out _));
            Assert.False(new RoadPathfinder(world).LocationShoreSqueeze(p));
            Assert.False(RoadShoreFill.LetThrough(world, p, out _));
            float width = reach * 0.5f;
            Assert.Equal(0f, RoadShoreFill.Reach(world, p, width));
            var points = new List<RoadSpatialGrid.RoadPoint> { new(p, width, 31f) };
            Assert.Equal(0f, RoadTerrainModifier.FillExtras(points, world)![0]);

            // Both intended radii are still read, including water at the full radius only.
            foreach (float fraction in new[] { 0.25f, 0.75f })
            {
                world.WaterX = p.x + reach * fraction;
                Assert.True(RoadShoreFill.Squeezed(world, p, buildable: false, out _));
                bool unbuildable = depth > RoadShoreFill.FillDepth;
                Assert.Equal(unbuildable, RoadShoreFill.Squeezed(world, p, buildable: true, out _));
                Assert.Equal(unbuildable, new RoadPathfinder(world).LocationShoreSqueeze(p));
                Assert.Equal(!unbuildable, RoadShoreFill.LetThrough(world, p, out _));
                float expected = unbuildable ? 0f : reach - width * 0.5f;
                Assert.Equal(expected, RoadShoreFill.Reach(world, p, width));
                Assert.Equal(expected, RoadTerrainModifier.FillExtras(points, world)![0]);
            }
        }
        finally { RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>()); }
    }
}
