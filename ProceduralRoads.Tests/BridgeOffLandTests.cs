using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A deck end over land that stands above the deck is walked in to the last
/// road ground before the water; a shore that only dips is left alone.
/// </summary>
public class BridgeOffLandTests
{
    /// <summary>Along x from the bank at 0: a dip, then a hump peaking 36.2 m
    /// at 18 m, then water from 30 m out; flat across z.</summary>
    private sealed class HumpShore : WorldGenerator
    {
        public bool Hump = true;
        public override float GetHeight(float x, float z)
        {
            if (x < 0f) return 31.4f;
            if (x < 6f) return 31.4f - 0.4f * Mathf.Sin(x / 6f * Mathf.PI);                 // dips to 31.0
            if (x < 30f) return Hump ? 31.2f + 5f * Mathf.Sin((x - 6f) / 24f * Mathf.PI) : 31.2f;   // up to ~36.2
            return 27f;                                                                         // the water
        }
    }

    [Fact]
    public void AHumpUnderTheDeckEndMovesTheEndIn()
    {
        var world = new HumpShore();
        using (new ValheimWorldScope().WithWorld(world))
        {
            Vector2 bank = new(0f, 0f), other = new(60f, 0f);
            Vector2 moved = RoadCrossingDetector.OffLand(bank, other, 31.6f, world);
            Assert.InRange(moved.x, 26f, 30f);
            Assert.True(BiomeBlendedHeight.GetBlendedHeight(moved.x, moved.y, world) >= RoadPathfinder.LandingFloor);
        }
    }

    [Fact]
    public void AShoreThatOnlyDipsKeepsItsBank()
    {
        var world = new HumpShore { Hump = false };
        using (new ValheimWorldScope().WithWorld(world))
        {
            Vector2 bank = new(0f, 0f);
            Assert.Equal(bank, RoadCrossingDetector.OffLand(bank, new Vector2(60f, 0f), 31.6f, world));
        }
    }
}
