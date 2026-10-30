using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A run's placed places, written and read back for offline tools: the turn the game drew
/// when it placed the zone cannot be had any other way.
/// </summary>
public class PlacedSitesTests
{
    private static PlacedSiteRecord Cave() => new()
    {
        Prefab = "MountainCave02", Centre = new Vector2(-2560f, 1536f), Yaw = 202.5f, Root = 159.57391f, RockRadius = 12.25f,
        KeepSmallRocks = false, X0 = -2610, Z0 = 1490, W = 81, H = 101,
        Ops = new List<LevelOp> { new(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f, 1), new(0f, 0f, 0f, 0f, true, 12.5f, 2.5f, 0) },
        Rocks = new List<SiteRock>
        {
            new(new Vector2(-2559.185f, 1567.148f), 9.75f, 6.5f, 265f, 136.9f, 156.3f, "rock2_mountain",
                new[] { 150.1f, float.NaN, 151.25f, 149.875f, float.NaN, 152.5f }, -2562, 1565, 3),
            new(new Vector2(-2553.61f, 1570.032f), 20f, 15f, 357f, 140f, 166.9f, "rock1_mountain"),
        },
    };

    [Fact]
    public void APlacedSiteIsReadBackExactlyAsWritten()
    {
        PlacedSiteRecord before = Cave();

        PlacedSiteRecord after = Assert.Single(PlacedSites.Read(PlacedSites.Write(new[] { before })));

        Assert.Equal(before.Prefab, after.Prefab);
        Assert.Equal(before.Centre, after.Centre);
        Assert.Equal((before.Yaw, before.Root, before.RockRadius, before.KeepSmallRocks), (after.Yaw, after.Root, after.RockRadius, after.KeepSmallRocks));
        Assert.Equal((before.X0, before.Z0, before.W, before.H), (after.X0, after.Z0, after.W, after.H));
        Assert.Equal(before.Ops, after.Ops);
        Assert.Equal(before.Rocks.Count, after.Rocks.Count);
        for (int i = 0; i < before.Rocks.Count; i++)
        {
            SiteRock a = before.Rocks[i], b = after.Rocks[i];
            Assert.Equal((a.Name, a.At, a.HalfX, a.HalfZ, a.Yaw, a.MinY, a.MaxY), (b.Name, b.At, b.HalfX, b.HalfZ, b.Yaw, b.MinY, b.MaxY));
            Assert.Equal((a.TopX0, a.TopZ0, a.TopW), (b.TopX0, b.TopZ0, b.TopW));
            Assert.Equal(a.Top, b.Top);
        }
        // The ground rebuilt from either is the same, to the bit.
        float Natural(int x, int z) => 150f + 0.25f * (x + 2560) - 0.125f * (z - 1536);
        Assert.Equal(before.Ground(Natural), after.Ground(Natural));
    }

    [Fact]
    public void SeveralPlacesAreKeptByTheirCentres()
    {
        PlacedSites.Reset();
        PlacedSiteRecord cave = Cave();
        var tower = new PlacedSiteRecord { Prefab = "Mistlands_GuardTower1_new", Centre = new Vector2(3527.4f, 6471.8f), Yaw = 90f, Root = 60f, X0 = 3480, Z0 = 6420, W = 90, H = 90 };

        PlacedSites.Load(PlacedSites.Read(PlacedSites.Write(new[] { cave, tower })));

        Assert.Equal(2, PlacedSites.Count);
        Assert.True(PlacedSites.TryGet(new Vector2(3527f, 6472f), out PlacedSiteRecord found));
        Assert.Equal("Mistlands_GuardTower1_new", found.Prefab);
        Assert.Empty(found.Ops);
        Assert.Empty(found.Rocks);
        PlacedSites.Reset();
    }
}
