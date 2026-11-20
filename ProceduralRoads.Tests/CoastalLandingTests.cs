using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Xunit;

namespace ProceduralRoads.Tests;

public class CoastalLandingTests : IDisposable
{
    // Coastal landings are off for the release (RoadRules.CoastLandings false everywhere): these tests keep the search itself
    // under test by turning it on here; CoastalLandingsAreOffTests pins that none is made as shipped.
    private readonly Func<Heightmap.Biome, bool> _given = RoadCoastalLandings.GivenIn;
    public CoastalLandingTests() => RoadCoastalLandings.GivenIn = _ => true;
    public void Dispose() => RoadCoastalLandings.GivenIn = _given;

    private static (Island island, float[] heights) Coast(Func<float,float,float>? height = null, float spacing = 8)
    {
        const int size = 160;
        var island = new Island { CellSize=spacing, WorldOffset=80*spacing, ApproxArea=11_000_000 };
        var heights = new float[size*size];
        for (int x=0; x<size; x++)
            for (int z=0; z<size; z++)
            {
                float wx=(x-80)*spacing, wz=(z-80)*spacing;
                float h=height?.Invoke(wx,wz) ?? (wx<0 ? 25 : 33);
                heights[x*size+z]=h;
                if (h>=RoadWater.LandingFloor) island.Cells.Add(new Vector2Int(x,z));
            }
        return (island,heights);
    }

    private static List<Vector3> Find((Island island,float[] heights) coast,
        Heightmap.Biome biome=Heightmap.Biome.Meadows, RoadNetworkOptions? options=null) =>
        RoadCoastalLandings.Find(coast.island,coast.heights,160,(x,z)=>biome,options ?? new());

    /// <summary>As shipped (Tys, 18 Nov 2026: off for the release) no biome gives a coastal landing, so the search offers none on
    /// any shore, and no island carries one for the selection, the replacements or the record to take.</summary>
    [Fact]
    public void NoBiomeGivesACoastalLandingAsShipped()
    {
        RoadCoastalLandings.GivenIn = _given;
        foreach (Heightmap.Biome biome in Enum.GetValues(typeof(Heightmap.Biome)))
        {
            Assert.False(RoadRules.For(biome).CoastLandings, $"{biome}");
            Assert.False(RoadCoastalLandings.GivenIn(biome), $"{biome}");
            Assert.Empty(Find(Coast(), biome));
        }
        Assert.False(RoadRules.CoastLandings);
        Assert.Empty(Find(Coast()));
        // The same shore does give them once the search is on: the empty result is the rule, not a missing coast.
        RoadCoastalLandings.GivenIn = _ => true;
        Assert.NotEmpty(Find(Coast()));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void GentleCoastGetsSpacedLandingsOnGroundAtBothDetectorResolutions(float spacing)
    {
        var coast=Coast(spacing:spacing);
        var found=Find(coast);
        Assert.Equal(3,found.Count);
        Assert.All(found,p=> { Assert.InRange(p.x,0,32); Assert.Equal(33,p.y); });
        foreach (var p in found) foreach (var q in found.Where(q=>!q.Equals(p)))
            Assert.True(Vector3.Distance(p,q)>=RoadCoastalLandings.Separation);
        coast.island.Cells.Reverse();
        Assert.Equal(found,Find(coast));
        coast.island.ApproxArea=1_000_000;
        Assert.Single(Find(coast));
    }

    [Fact]
    public void GentleBeachCanIncludeDryStripBeforeWater() =>
        Assert.NotEmpty(Find(Coast((x,z)=>x<0 ? 25 : x<16 ? 30.5f : 33)));

    [Fact]
    public void ShallowWaterIsNotBoatAccess() =>
        Assert.Empty(Find(Coast((x,z)=>x<0 ? 29 : 33)));

    [Fact]
    public void NarrowWaterThenAnotherBankIsNotBoatAccess() =>
        Assert.Empty(Find(Coast((x,z)=>x<0 && x>-24 ? 25 : 33)));

    [Fact]
    public void SteepInlandOrHighCliffIsRejected()
    {
        Assert.Empty(Find(Coast((x,z)=>x<0 ? 25 : x<48 ? 33 : 50)));
        Assert.Empty(Find(Coast((x,z)=>x<0 ? 25 : 40)));
    }

    [Fact]
    public void MissingRasterOutsideWorldIsNotDeepWater() =>
        Assert.Empty(Find(Coast((x,z)=>x<0 ? float.MinValue : 33)));

    [Fact]
    public void BiomeScopeIsRespected()
    {
        RoadCoastalLandings.GivenIn=b=>b==Heightmap.Biome.Meadows;
        var coast=Coast();
        Assert.Empty(Find(coast,Heightmap.Biome.AshLands));
        Assert.Empty(Find(coast,Heightmap.Biome.Swamp));
        Assert.Empty(Find(coast,options:new() { ExcludedBiomes=Heightmap.Biome.Meadows }));
    }

    [Fact]
    public void LandingsTakeQuotaSlotsWithoutMutatingRealPoiList()
    {
        using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.NetworkOptions);
        RoadNetworkGenerator.NetworkOptions=new();
        var island=new Island { ApproxArea=6_000_000, CoastalLandings=new() { new Vector3(0,33,0),new Vector3(800,33,0) } };
        var pois=new List<(string name,Vector3 position,float radius)> {
            ("Crypt4",new Vector3(200,33,200),10), ("Crypt4",new Vector3(400,33,200),10),
            ("Eikthyrnir",new Vector3(600,33,200),10) };
        var method=typeof(RoadNetworkGenerator).GetMethod("SelectIslandDestinations",BindingFlags.NonPublic|BindingFlags.Static)!;
        List<(string name,Vector3 position,float radius)> Select() =>
            (List<(string name,Vector3 position,float radius)>)method.Invoke(null,new object[] {island,pois,4})!;
        var selected=Select();
        Assert.Equal(4,selected.Count);
        Assert.Equal(2,selected.Count(p=>p.name==RoadCoastalLandings.Name));
        Assert.Contains(selected,p=>p.name=="Eikthyrnir");
        Assert.Equal(3,pois.Count);
    }

    private sealed class CountingCoast : WorldGenerator
    {
        public int HeightReads;
        // Counted from every thread: the terrain is mapped on the island workers.
        public override float GetHeight(float x,float z) { System.Threading.Interlocked.Increment(ref HeightReads); return x<0 ? 25 : 33; }
        public override Heightmap.Biome GetBiome(float x,float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
    }

    [Fact]
    public void DetectorAddsLandingsWithoutExtraHeightQueries()
    {
        var world=new CountingCoast();
        RoadIslandDetector.Detect(world,worldRadius:256,minArea:1);
        int baseline=world.HeightReads;
        world.HeightReads=0;
        var islands=RoadIslandDetector.Detect(world,worldRadius:256,minArea:1,options:new());
        Assert.Equal(baseline,world.HeightReads);
        Assert.Contains(islands,i=>i.CoastalLandings.Count>0);
    }
}
