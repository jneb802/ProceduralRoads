using System;
using System.Linq;
using UnityEngine;
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
        WorldGenerator.instance = world;
        RoadSiteProtection.Set(sites);
        try { body(); }
        finally { RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>()); WorldGenerator.instance = null; }
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
        float saved = RoadPathfinder.SqueezeCorridor;
        try
        {
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
        finally { RoadPathfinder.SqueezeCorridor = saved; }
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
}
