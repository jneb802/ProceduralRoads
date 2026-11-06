using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Rebuilding a placed cave's ground without its zone (server pre-visit)
/// against what the game drew: road_site_ground dumps of a
/// frost cave turned 157.5 and Hildir's cave turned 225, trimmed to 26 m round each.
/// Each has the cave's turn and root, its terrain modifiers with their prefab-local
/// offsets, and per vertex the game's base height, its final height, and the mod's
/// own height (equal to the base at every vertex).
/// </summary>
public class PlacedSiteGroundTests
{
    private sealed class Dump
    {
        public Vector2 Centre;
        public float Root, Yaw;
        public readonly List<LevelOp> Ops = new();
        public readonly Dictionary<(int, int), (float natural, float game)> Vertices = new();
    }

    private static Dump Read(string name)
    {
        float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);
        var dump = new Dump();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))
        {
            string[] c = line.Split(',');
            if (c[0] == "location")
            {
                dump.Centre = new Vector2(F(c[2]), F(c[4]));
                dump.Root = F(c[3]);
                dump.Yaw = F(c[6]);
            }
            else if (c[0] == "modifier" && c[1] != "order")
            {
                // localX localY localZ at 6-8; level 13, offset 14, radius 15, square 16, smooth 17, radius 18, power 19; sortOrder 11.
                bool level = c[13] == "1", smooth = c[17] == "1";
                dump.Ops.Add(new LevelOp(F(c[6]), F(c[8]), F(c[7]) + F(c[14]), level ? F(c[15]) : 0f, c[16] == "1",
                    smooth ? F(c[18]) : 0f, F(c[19]), int.Parse(c[11], CultureInfo.InvariantCulture)));
            }
            else if (c[0] == "vertex" && c[1] != "x")
                dump.Vertices[(int.Parse(c[1]), int.Parse(c[2]))] = (F(c[5]), F(c[4]));
        }
        return dump;
    }

    [Theory]
    [InlineData("siteground-mountaincave02.csv", 7)]
    [InlineData("siteground-hildircave.csv", 8)]
    public void APlacedCavesGroundIsRebuiltFromItsTurnAndPrefabModifiers(string fixture, int modifiers)
    {
        Dump dump = Read(fixture);
        Assert.Equal(modifiers, dump.Ops.Count);
        int x0 = dump.Vertices.Keys.Min(k => k.Item1), z0 = dump.Vertices.Keys.Min(k => k.Item2);
        int w = dump.Vertices.Keys.Max(k => k.Item1) - x0 + 1, h = dump.Vertices.Keys.Max(k => k.Item2) - z0 + 1;

        float[] rebuilt = PlacedSiteGround.Build(x0, z0, w, h, dump.Centre, dump.Root, dump.Yaw, dump.Ops,
            (x, z) => dump.Vertices.TryGetValue((x, z), out var v) ? v.natural : float.NaN);

        float worst = 0f, worstNatural = 0f;
        foreach (var kv in dump.Vertices)
        {
            float here = rebuilt[(kv.Key.Item2 - z0) * w + (kv.Key.Item1 - x0)];
            worst = Mathf.Max(worst, Mathf.Abs(here - kv.Value.game));
            worstNatural = Mathf.Max(worstNatural, Mathf.Abs(kv.Value.natural - kv.Value.game));
        }
        Assert.True(worst <= 0.01f, $"rebuilt ground off by {worst:F3} m somewhere");
        // The cave's modifiers move the ground metres: the check is not passing on natural ground.
        Assert.True(worstNatural > 5f, $"natural ground only {worstNatural:F2} m off");
    }

    [Fact]
    public void TurnedWrongTheRebuildIsNotTheGame()
    {
        Dump dump = Read("siteground-mountaincave02.csv");
        int x0 = dump.Vertices.Keys.Min(k => k.Item1), z0 = dump.Vertices.Keys.Min(k => k.Item2);
        int w = dump.Vertices.Keys.Max(k => k.Item1) - x0 + 1, h = dump.Vertices.Keys.Max(k => k.Item2) - z0 + 1;
        float[] rebuilt = PlacedSiteGround.Build(x0, z0, w, h, dump.Centre, dump.Root, dump.Yaw + 22.5f, dump.Ops,
            (x, z) => dump.Vertices.TryGetValue((x, z), out var v) ? v.natural : float.NaN);
        float worst = dump.Vertices.Max(kv => Mathf.Abs(rebuilt[(kv.Key.Item2 - z0) * w + (kv.Key.Item1 - x0)] - kv.Value.game));
        Assert.True(worst > 0.5f, $"a turn one step off still matched to {worst:F3} m");
    }

    [Fact]
    public void TheSamplerJoinsVerticesAndWallsOffTheRest()
    {
        var heights = new[] { 0f, 2f, 4f, 6f };
        var ground = PlacedSiteGround.Sampler(heights, 10, 20, 2, 2);
        Assert.Equal(3f, ground(10.5f, 20.5f), 3);
        Assert.Equal(10000f, ground(12f, 20f));
    }
}
