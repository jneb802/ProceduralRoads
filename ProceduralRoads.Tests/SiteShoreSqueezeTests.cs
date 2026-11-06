using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Ground squeezed between a place and the water is not road: the search goes
/// round, or the place goes unserved.
/// </summary>
public class SiteShoreSqueezeTests
{
    /// <summary>Flat land at 31 m; shallow water (28 m) east of <see cref="WaterX"/>.</summary>
    private sealed class Shore : WorldGenerator
    {
        public float WaterX = 20f;
        public override float GetHeight(float x, float z) => x > WaterX ? 28f : 31f;
    }

    private static void With(Shore world, RoadSiteProtection.Footprint[] sites, Action body)
    {
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadSiteProtection.Set(sites);
        try { body(); }
        finally { RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>()); }
    }

    [Fact]
    public void EdgeDistanceIsMeasuredFromTheFootprintsEdge()
    {
        With(new Shore(), new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 10f) }, () =>
        {
            Assert.Equal(5f, RoadSiteProtection.EdgeDistance(new Vector2(15f, 0f), 12f), 3);
            Assert.True(RoadSiteProtection.EdgeDistance(new Vector2(5f, 0f), 12f) < 0f);
            Assert.True(float.IsPositiveInfinity(RoadSiteProtection.EdgeDistance(new Vector2(40f, 0f), 12f)));
        });
    }

    [Fact]
    public void AStripBetweenAPlaceAndTheWaterIsSqueezed()
    {
        // The place's edge at x = 10, the water from x = 20: a 10 m strip.
        With(new Shore(), new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 10f) }, () =>
        {
            var finder = new RoadPathfinder(WorldGenerator.instance);
            Assert.True(finder.SiteShoreSqueeze(new Vector2(15f, 0f)));
            Assert.False(finder.SiteShoreSqueeze(new Vector2(-15f, 0f)), "the landward side is open");
            Assert.False(finder.SiteShoreSqueeze(new Vector2(15f, 60f)), "the shore away from any place is open");
            Assert.False(finder.SiteShoreSqueeze(new Vector2(5f, 0f)), "inside the footprint is the search's own business");
        });
        // With the water 30 m out the strip is wide enough.
        With(new Shore { WaterX = 40f }, new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 10f) }, () =>
            Assert.False(new RoadPathfinder(WorldGenerator.instance).SiteShoreSqueeze(new Vector2(15f, 0f))));
    }

    [Fact]
    public void TheSearchGoesRoundTheSqueezeInsteadOfThroughIt()
    {
        // A place on the shore and a row of places inland beside it: the short
        // way past is the 10 m strip between the first and the water.
        var sites = new[]
        {
            new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 10f),
            new RoadSiteProtection.Footprint(new Vector2(-30f, 0f), 20f),
            new RoadSiteProtection.Footprint(new Vector2(-70f, 0f), 20f),
            new RoadSiteProtection.Footprint(new Vector2(-110f, 0f), 20f),
            new RoadSiteProtection.Footprint(new Vector2(-150f, 0f), 20f),
        };
        bool InStrip(Vector2 p) => p.x > 10f && p.x < 20f && Mathf.Abs(p.y) < 12f;
        using var overrides = StaticOverride.Keep(() => RoadPathfinder.SqueezeCorridor);
        With(new Shore(), sites, () =>
        {
            RoadPathfinder.SqueezeCorridor = 0f;
            var through = new RoadPathfinder(WorldGenerator.instance).FindPath(new Vector2(8f, -120f), new Vector2(8f, 120f));
            Assert.NotNull(through);
            Assert.Contains(through!, InStrip);

            RoadPathfinder.SqueezeCorridor = 12f;
            var round = new RoadPathfinder(WorldGenerator.instance).FindPath(new Vector2(8f, -120f), new Vector2(8f, 120f));
            Assert.True(round == null || !round.Any(InStrip), "the road went through the squeezed strip");
        });
    }
    [Fact]
    public void ABridgeDeckAlongTheShorePastAPlaceIsSqueezed()
    {
        // Measured: a 104 m bridge ran along the shore 7 m from a troll cave's edge. Only steps
        // and landings were tested, so the deck flew over the strip the squeeze keeps road out of.
        With(new Shore { WaterX = 15f }, new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 12f) }, () =>
        {
            var finder = new RoadPathfinder(WorldGenerator.instance);
            // Along the shore at x = 19 (in the water from x = 15), 7 m from the place's edge at x = 12.
            Assert.True(finder.DeckSqueezed(new Vector2(19f, -50f), new Vector2(19f, 50f), out var at));
            Assert.InRange(at.y, -20f, 20f); // first sample within 12 m of the edge
            // The same deck 30 m out is clear of it.
            Assert.False(finder.DeckSqueezed(new Vector2(45f, -50f), new Vector2(45f, 50f), out _));
            // A deck heading straight out to sea from beyond the band is clear.
            Assert.False(finder.DeckSqueezed(new Vector2(30f, 40f), new Vector2(90f, 40f), out _));
            // A deck along the LANDWARD side (x = -19, dry) is the search's steps' business, not the deck's.
            Assert.False(finder.DeckSqueezed(new Vector2(-19f, -50f), new Vector2(-19f, 50f), out _));
        });
    }

    /// <summary>Land at <see cref="Land"/>; water <see cref="Depth"/> under the shallow line east of <see cref="WaterX"/>,
    /// a river there when <see cref="River"/>.</summary>
    private sealed class Shallows : WorldGenerator
    {
        public float WaterX = 20f, Depth = 1f, Land = 31f;
        public bool River;
        public override float GetHeight(float x, float z) => x > WaterX ? RoadConstants.ShallowWaterHeight - Depth : Land;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = River && x > WaterX ? 1f : 0f; width = 0f; }
    }

    private static void With(WorldGenerator world, RoadSiteProtection.Footprint[] sites, Action body)
    {
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadSiteProtection.Set(sites);
        try { body(); }
        finally { RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>()); }
    }

    /// <summary>Shallows beside a place are land the road builds out, not a squeeze. A Ruin1 on a shore
    /// (8654, -3667) had 406 steps refused beside it and stayed unreached.</summary>
    [Fact]
    public void ShallowsBesideAPlaceAreBuiltOutNotSqueezed()
    {
        var place = new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 10f) };
        With(new Shallows { Depth = 1f }, place, () =>
        {
            var finder = new RoadPathfinder(WorldGenerator.instance);
            Assert.False(finder.SiteShoreSqueeze(new Vector2(15f, 0f)), "water 1 m under the shallow line squeezed the strip");
            Assert.True(RoadShoreFill.LetThrough(WorldGenerator.instance, new Vector2(15f, 0f), out float gap));
            Assert.Equal(5f, gap, 3);
        });
        // Deeper than the fill depth: real water, squeezed as before.
        With(new Shallows { Depth = 2.5f }, place, () =>
            Assert.True(new RoadPathfinder(WorldGenerator.instance).SiteShoreSqueeze(new Vector2(15f, 0f))));
        // A river channel is never built out, however shallow.
        With(new Shallows { Depth = 0.5f, River = true }, place, () =>
            Assert.True(new RoadPathfinder(WorldGenerator.instance).SiteShoreSqueeze(new Vector2(15f, 0f))));
        // Nor where the fill would stand past the earthwork cap under the road.
        using var depth = StaticOverride.Keep(() => RoadShoreFill.FillDepth);
        using var cap = StaticOverride.Keep(() => RoadNetworkGenerator.EarthworkCap);
        RoadNetworkGenerator.EarthworkCap = 8f;
        With(new Shallows { Depth = 1f, Land = RoadConstants.ShallowWaterHeight + 9f }, place, () =>
            Assert.True(new RoadPathfinder(WorldGenerator.instance).SiteShoreSqueeze(new Vector2(15f, 0f))));
        // Off, every water is water.
        RoadShoreFill.FillDepth = 0f;
        With(new Shallows { Depth = 1f }, place, () =>
            Assert.True(new RoadPathfinder(WorldGenerator.instance).SiteShoreSqueeze(new Vector2(15f, 0f))));
    }

    [Fact]
    public void TheSearchTakesTheShoreOverBuiltOutShallowsAndSaysWhere()
    {
        // As TheSearchGoesRoundTheSqueezeInsteadOfThroughIt, the water 1 m deep: the strip is road.
        var sites = new[]
        {
            new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 10f),
            new RoadSiteProtection.Footprint(new Vector2(-30f, 0f), 20f),
            new RoadSiteProtection.Footprint(new Vector2(-70f, 0f), 20f),
            new RoadSiteProtection.Footprint(new Vector2(-110f, 0f), 20f),
            new RoadSiteProtection.Footprint(new Vector2(-150f, 0f), 20f),
        };
        bool InStrip(Vector2 p) => p.x > 10f && p.x < 20f && Mathf.Abs(p.y) < 12f;
        With(new Shallows { Depth = 1f }, sites, () =>
        {
            RoadPathfinder.ShoreFillCells.Clear();
            var path = new RoadPathfinder(WorldGenerator.instance).FindPath(new Vector2(8f, -120f), new Vector2(8f, 120f));
            Assert.NotNull(path);
            Assert.Contains(path!, InStrip);
            Assert.True(RoadPathfinder.ShoreFillCells.TryGetValue((0, 0), out int cells) && cells > 0);
            RoadPathfinder.ShoreFillCells.Clear();
        });
    }

    /// <summary>Where the search was let through on built-out shallows the road lays a shoulder over them,
    /// out to the squeeze corridor; without it the planner assumed land that was never built.</summary>
    [Fact]
    public void ARoadLetThroughOnShallowsLaysAShoulderOverThem()
    {
        var place = new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 10f) };
        using var spread = StaticOverride.Keep(() => RoadEarthworkNoise.FillSpread);
        RoadEarthworkNoise.FillSpread = 1f; // the mod's: no fill under a road on its own ground, so the shoulder alone
        With(new Shallows { Depth = 1f }, place, () =>
        {
            var world = WorldGenerator.instance;
            // A road along the strip at x = 15, 5 m off the place's edge, on the land's height.
            var road = Enumerable.Range(-10, 21).Select(i => new RoadSpatialGrid.RoadPoint(new Vector2(15f, i * 2f), 4f, 31f)).ToList();
            float[]? extra = RoadTerrainModifier.FillExtras(road, world);
            Assert.NotNull(extra);
            Assert.Equal(RoadEarthworkNoise.FillSpreadMax, extra![10], 3);
            // The shallows beside it are raised toward the road, out past the 12 m corridor: 5 m from the road's centre
            // (10 m off the place's edge) they stand out of the sea (30 m), with no shoulder they stay under it.
            float water = RoadConstants.ShallowWaterHeight - 1f;
            float shouldered = RoadTerrainModifier.ModelledHeight(road, new Vector2(20f, 0f), water, extra);
            Assert.True(shouldered > 30f, $"the shallows stayed at {shouldered:F2}");
            Assert.True(RoadTerrainModifier.ModelledHeight(road, new Vector2(21f, 0f), water, extra) > water + 0.2f);
            Assert.True(RoadTerrainModifier.ModelledHeight(road, new Vector2(20f, 0f), water, new float[road.Count]) < 30f);
            // A road away from any place lays no shoulder.
            var away = new List<RoadSpatialGrid.RoadPoint> { new(new Vector2(15f, 80f), 4f, 31f) };
            Assert.Equal(0f, RoadTerrainModifier.FillExtras(away, world)![0], 3);
        });
        // Over water too deep to build out the search squeezed, so no road there and no shoulder.
        With(new Shallows { Depth = 2.5f }, place, () =>
            Assert.Equal(0f, RoadTerrainModifier.FillExtras(new List<RoadSpatialGrid.RoadPoint> { new(new Vector2(15f, 0f), 4f, 31f) }, WorldGenerator.instance)![0], 3));
    }
}
