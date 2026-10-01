using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The height the mod plans on must be the height the game renders. Inside a
/// heightmap whose four corners share a biome, HeightmapBuilder builds every
/// vertex from THAT biome's formula - not from the biome at the vertex. A
/// patch of another biome smaller than a zone (a Black Forest tongue inside
/// Meadows) is therefore rendered at Meadows height, and a road planned on
/// WorldGenerator.GetHeight there was levelled to the forest formula instead:
/// a raised hump over ground that did not rise.
/// </summary>
public class BiomeBlendedHeightTests
{
    private const float MeadowsGround = 40f;
    private const float ForestGround = 46f;

    /// <summary>Two flat biome formulas, and a Black Forest tongue that lies
    /// inside the heightmap of zone (512,-128) without touching any of its
    /// corners (480..544 by -160..-96). GetHeight behaves as the game's does:
    /// the formula of the biome at the point.</summary>
    private sealed class TongueWorld : WorldGenerator
    {
        public bool Tongue = true;
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            Tongue && wx >= 500f && wx <= 530f && wy >= -150f && wy <= -110f
                ? Heightmap.Biome.BlackForest : Heightmap.Biome.Meadows;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask)
        {
            mask = default;
            return biome == Heightmap.Biome.BlackForest ? ForestGround : MeadowsGround;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
    }

    [Fact]
    public void APatchBetweenFourLikeCornersIsRenderedAtTheCornersHeight()
    {
        var world = new TongueWorld();
        // The world generator reads the patch's own formula ...
        Assert.Equal(ForestGround, world.GetHeight(515f, -130f));
        // ... the rendered heightmap, and so the road, does not.
        Assert.Equal(MeadowsGround, BiomeBlendedHeight.GetBlendedHeight(515f, -130f, world), 3);
        var info = BiomeBlendedHeight.GetBlendDebugInfo(515f, -130f, world);
        Assert.False(info.IsBiomeBoundary);
        Assert.Equal(Heightmap.Biome.BlackForest, info.PointBiome);
        Assert.Equal(-(ForestGround - MeadowsGround), info.HeightDifference, 3);
    }

    [Fact]
    public void AHeightmapThatIsOneBiomeThroughoutIsUnchanged()
    {
        var world = new TongueWorld();
        foreach (var p in new[] { new Vector2(0f, 0f), new Vector2(470f, -130f), new Vector2(600f, -130f) })
            Assert.Equal(world.GetHeight(p.x, p.y), BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world), 3);
    }

    [Fact]
    public void CornersThatDisagreeStillBlendByPositionNotByThePointsBiome()
    {
        // A boundary through the corners: Black Forest from x = 544 east. The
        // heightmap of zone (512,-128) has Black Forest on its east corners and
        // blends across, whatever the biome at the point itself.
        var world = new EastForest();
        float wx = 520f, wy = -128f;
        float t = (wx - 480f) / 64f;
        float tx = t * t * (3f - 2f * t);
        float expected = Mathf.Lerp(MeadowsGround, ForestGround, tx);
        Assert.Equal(expected, BiomeBlendedHeight.GetBlendedHeight(wx, wy, world), 3);
    }

    private sealed class EastForest : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            wx >= 544f ? Heightmap.Biome.BlackForest : Heightmap.Biome.Meadows;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask)
        {
            mask = default;
            return biome == Heightmap.Biome.BlackForest ? ForestGround : MeadowsGround;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
    }

    [Fact]
    public void ARoadAcrossAPatchStaysOnTheRenderedGround()
    {
        // End to end: the road crosses the tongue on ground the game renders
        // flat at 40 m. It must not be lifted onto the forest formula's 46 m.
        var world = new TongueWorld();
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            var path = new List<Vector2>();
            for (float x = 300f; x <= 740f; x += 8f)
                path.Add(new Vector2(x, -130f));
            RoadSpatialGrid.AddRoadPath(path, 4f, world);

            var points = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(515f, 0f, -130f), 30f);
            Assert.True(points.Count > 10, "too few road points on the tongue");
            foreach (var rp in points)
                Assert.True(Mathf.Abs(rp.h - MeadowsGround) < 0.05f,
                    $"road point at x={rp.p.x:F1} is {rp.h - MeadowsGround:F2} m off the rendered ground");
        }
        finally
        {
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = null;
        }
    }
}
