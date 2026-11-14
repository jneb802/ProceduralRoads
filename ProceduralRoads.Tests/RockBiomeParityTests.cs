using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A rock is judged in one biome on every path that judges it: the game's biome at its position (RoadBiome.At, the
/// WorldGenerator's), on the rock probe that carves it (RoadRockProbe, through RoadRockHit.Judged) and on the vegetation
/// clearing that leaves it to the probe (VegetationClearing.LeftToRockClearing, given its biome by the zone spawn and the
/// server bake) (the owner's decision of 13 Nov 2026). The probe used to read the heightmap's biome (Heightmap.FindBiome,
/// its corners' blend), which differs from the world's near a biome edge, so the two could judge one rock in two biomes.
/// </summary>
[Collection("RoadStatics")]
public sealed class RockBiomeParityTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public void Dispose() => _scope.Dispose();

    /// <summary>The Mistlands west of x = 0, the Mountains east of it.</summary>
    private sealed class EdgeWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx < 0f ? Heightmap.Biome.Mistlands : Heightmap.Biome.Mountain;
    }

    /// <summary>Every path that hands a rock's biome to its judge, and the call it must make for it.</summary>
    private static readonly (string file, string call)[] Paths =
    {
        ("Roads/RoadRockProbe.cs", "RoadBiome.At(root.transform.position)"),
        ("Patches/ZoneSystem_Patch.cs", "BiomeAt(Vector3 point) => RoadBiome.At(point)"),
        ("Roads/ServerTerrainBake.cs", "BiomeAt(Vector3 position) => RoadBiome.At(position)"),
    };

    [Fact]
    public void EveryPathJudgesARockInTheGamesBiomeAtItsPosition()
    {
        string root = Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");
        foreach (var (file, call) in Paths)
        {
            string code = OwnerLiteralTests.Code(File.ReadAllText(Path.Combine(root, file)));
            Assert.True(code.Contains(call), $"{file} does not give a rock's judge the biome by {call}");
            Assert.False(Regex.IsMatch(code, @"\bFindBiome\s*\("), $"{file} reads the heightmap's biome");
        }
    }

    /// <summary>On both sides of an edge, the probe's verdict and the clearing's agree, each in the biome RoadBiome.At gives
    /// the rock's position: a Mistlands rock is the probe's west of the edge and the vegetation's east of it, a natural
    /// boulder the other way round.</summary>
    [Theory]
    [InlineData("rock_mistlands1", -0.5f, true)]
    [InlineData("rock_mistlands1", 0.5f, false)]
    [InlineData("rock1_mountain", -0.5f, false)]
    [InlineData("rock1_mountain", 0.5f, true)]
    public void TheProbeAndTheClearingJudgeARockAlikeAtAnEdge(string name, float x, bool probeTakes)
    {
        _scope.WithWorld(new EdgeWorld());
        var at = new Vector3(x, 40f, 3f);
        Heightmap.Biome biome = RoadBiome.At(at);
        bool mist = RoadRockPolicy.PassClearsMistlandsRocks, cliffs = RoadRockPolicy.PassClearsMistlandsCliffs;
        RoadRockHit hit = RoadRockHit.Judged(name, 20f, new[] { name }, biome, new HashSet<string> { name }, mist, cliffs,
            locationChild: false, inLocationArea: false, keptOnApproach: null, piece: false, hasView: true, validView: true, owned: true);
        Assert.Equal(probeTakes, hit.Clears);
        Assert.Equal(probeTakes, VegetationClearing.LeftToRockClearing(name.GetStableHashCode(), RoadBiome.At(at)));
    }
}
