using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The rock clearing takes every listed rock in every biome alike (the owner's decision of 14 Nov 2026): no biome is read
/// to judge a rock, so a rock at a biome border is cleared as its road's. A 50 m rock2_heath centred just inside the
/// Mistlands was once left whole beside a Plains road, and Mistlands cliffs centred just inside the Mountains were kept
/// while the cliffs round them were carved, when the clearing took each kind only in the biomes its centre stood in.
/// </summary>
[Collection("RoadStatics")]
public sealed class RockBorderTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public void Dispose() => _scope.Dispose();

    /// <summary>The Mistlands west of x = 0, the given biome (the Mountains by default) east of it.</summary>
    private sealed class EdgeWorld : WorldGenerator
    {
        private readonly Heightmap.Biome _east;
        public EdgeWorld(Heightmap.Biome east = Heightmap.Biome.Mountain) => _east = east;
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx < 0f ? Heightmap.Biome.Mistlands : _east;
    }

    /// <summary>The files that judge a rock, none of which reads a biome to do it.</summary>
    private static readonly string[] RockJudges =
        { "Roads/RoadRockPolicy.cs", "Roads/RoadRockHit.cs", "Roads/RoadRockProbe.cs", "Roads/VegetationClearing.cs" };

    [Fact]
    public void NoPathReadsABiomeToJudgeARock()
    {
        string root = Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");
        foreach (string file in RockJudges)
        {
            string code = OwnerLiteralTests.Code(File.ReadAllText(Path.Combine(root, file)));
            Assert.False(Regex.IsMatch(code, @"\bHeightmap\.Biome\b|\bRoadBiome\b|\b(Find|Get)Biome\s*\("), $"{file} reads a biome");
        }
    }

    /// <summary>On both sides of an edge the probe's verdict and the vegetation clearing's agree: the rock is the probe's.</summary>
    [Theory]
    [InlineData("rock_mistlands1", -0.5f)]
    [InlineData("rock_mistlands1", 0.5f)]
    [InlineData("rock1_mountain", -0.5f)]
    [InlineData("rock1_mountain", 0.5f)]
    public void TheProbeAndTheClearingJudgeARockAlikeAtAnEdge(string name, float x)
    {
        _scope.WithWorld(new EdgeWorld());
        Assert.Equal(x < 0f ? Heightmap.Biome.Mistlands : Heightmap.Biome.Mountain, RoadBiome.At(new Vector3(x, 40f, 3f)));
        RoadRockHit hit = Judge(name);
        Assert.True(hit.Clears, hit.Verdict);
        Assert.True(VegetationClearing.LeftToRockClearing(name.GetStableHashCode()));
    }

    /// <summary>A natural boulder whose centre stands just inside the Mistlands, beside a road in the Plains, is cleared.</summary>
    [Fact]
    public void ANaturalBoulderInTheMistlandsByAPlainsRoadIsCleared()
    {
        _scope.WithWorld(new EdgeWorld(Heightmap.Biome.Plains));
        Assert.Equal(Heightmap.Biome.Plains, RoadBiome.At(new Vector3(12f, 40f, 3f)));
        Assert.Equal(Heightmap.Biome.Mistlands, RoadBiome.At(new Vector3(-0.5f, 40f, 3f)));
        RoadRockHit hit = Judge("rock2_heath");
        Assert.True(hit.Clears, hit.Verdict);
        Assert.Equal("boulder: clear", hit.Verdict);
        Assert.True(VegetationClearing.LeftToRockClearing("rock2_heath".GetStableHashCode()));
    }

    /// <summary>The reverse: a Mistlands rock or cliff standing outside the Mistlands is cleared as it is in them.</summary>
    [Theory]
    [InlineData("rock_mistlands1")]
    [InlineData("cliff_mistlands1")]
    [InlineData("cliff_mistlands2")]
    public void AMistlandsRockOrCliffInThePlainsIsCleared(string name)
    {
        _scope.WithWorld(new EdgeWorld(Heightmap.Biome.Plains));
        Assert.Equal(Heightmap.Biome.Plains, RoadBiome.At(new Vector3(0.5f, 40f, 3f)));
        RoadRockHit hit = Judge(name);
        Assert.True(hit.Clears, hit.Verdict);
        Assert.True(VegetationClearing.LeftToRockClearing(name.GetStableHashCode()));
    }

    /// <summary>The clearing takes every listed rock: boulders, deposits, Mistlands rocks and cliffs.</summary>
    [Fact]
    public void NoBiomeGatesTheRockClearing()
    {
        foreach (string rock in RoadRockPolicy.KnownRocks)
        {
            Assert.True(RoadRockPolicy.ClearingTakes(rock), rock);
            Assert.True(VegetationClearing.LeftToRockClearing(rock.GetStableHashCode()), rock);
        }
    }

    /// <summary>A listed rock the clearing would take is kept for the one reason that holds it, named as that reason: one the
    /// game has not loaded (no valid network view) is not blamed on the vegetation rule, and one that is not the world's
    /// vegetation is.</summary>
    [Fact]
    public void AKeptRockNamesTheReasonThatKeepsIt()
    {
        const string name = "rock2_heath";
        RoadRockHit unloaded = RoadRockHit.Judged(name, 20f, new[] { name }, new HashSet<string> { name },
            RoadRockPolicy.PassClearsMistlandsRocks, RoadRockPolicy.PassClearsMistlandsCliffs, locationChild: false, inLocationArea: false,
            keptOnApproach: null, piece: false, hasView: true, validView: false, owned: true);
        Assert.False(unloaded.Clears);
        Assert.Equal("keep: not loaded (no valid network view)", unloaded.Verdict);
        RoadRockHit notVegetation = RoadRockHit.Judged(name, 20f, new[] { name }, new HashSet<string>(),
            RoadRockPolicy.PassClearsMistlandsRocks, RoadRockPolicy.PassClearsMistlandsCliffs, locationChild: false, inLocationArea: false,
            keptOnApproach: null, piece: false, hasView: true, validView: true, owned: true);
        Assert.False(notVegetation.Clears);
        Assert.Equal("keep: vegetation rule", notVegetation.Verdict);
    }

    /// <summary>A rock of the world's vegetation no location, player or other peer holds, judged by the automatic pass.</summary>
    private static RoadRockHit Judge(string name) =>
        RoadRockHit.Judged(name, 20f, new[] { name }, new HashSet<string> { name }, RoadRockPolicy.PassClearsMistlandsRocks,
            RoadRockPolicy.PassClearsMistlandsCliffs, locationChild: false, inLocationArea: false, keptOnApproach: null, piece: false,
            hasView: true, validView: true, owned: true);
}
