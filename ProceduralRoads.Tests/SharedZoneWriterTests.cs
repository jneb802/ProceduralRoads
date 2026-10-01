using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;
namespace ProceduralRoads.Tests;

public class SharedZoneWriterTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RealWriterMeetsAcrossZonesAndPreservesPreexistingVerge(bool reverse)
    {
        using var scope=new ValheimWorldScope();
        using var overrides = StaticOverride.Keep(() => RoadEarthworkNoise.Amplitude).AndKeep(() => RoadEarthworkNoise.FillSpread).AndKeep(() => RoadTerrainModifier.BatterPerMetre);
        var plane=new PlaneTerrain(64,0,.25f);
        var paint=new PaintRgba(.2f,.4f,.6f,.3f);
        var state=new TerrainWorldState();
        for(int zone=0;zone<2;zone++) state.Add(zone,0,new TerrainZoneState(
            new TerrainGrid<float>(65,65,zone*64-32,-32,1,plane.GetHeight),
            // This mod's compiler double uses 65x65. Native texture layout is
            // separate; the shared fixture does not prescribe either layout.
            new TerrainGrid<PaintRgba>(65,65,zone*64-32,-32,1,(x,z)=>paint)));
        var before=state.Clone();
        try
        {
            scope.WithTerrain(plane).WithZdos();
            RoadTerrainModifier.ResetDebugCounters(); RoadSpatialGrid.Clear(); RoadSpatialGrid.FinalizeRoadNetwork();
            RoadEarthworkNoise.Amplitude=0; RoadEarthworkNoise.FillSpread=0; RoadTerrainModifier.BatterPerMetre=0;
            var points=new List<RoadSpatialGrid.RoadPoint>();
            for(int x=16;x<=48;x++) points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x,0),4,67));
            foreach(int zone in reverse ? new[]{1,0} : new[]{0,1})
            {
                var hm=scope.RegisterHeightmap(new Vector2s(zone,0));
                hm.AuthoredHeight=plane.GetHeight; var tc=hm.m_terrainComp!;
                for(int z=0;z<65;z++) for(int x=0;x<65;x++)
                {
                    var p=state[zone,0].Paint[x,z];tc.m_paintMask[z*65+x]=new Color(p.R,p.G,p.B,p.A);
                }
                // An earlier writer raised this off-road vertex by 1m.
                int verge=44*65+(zone==0?64:0);tc.m_levelDelta[verge]=1;tc.m_modifiedHeight[verge]=true;
                hm.RebuildTerrain();
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(new Vector2s(zone,0),points,hm,tc);
                for(int z=0;z<65;z++) for(int x=0;x<65;x++)
                {
                    state[zone,0].Heights[x,z]=hm.LastRenderedHeights![z*65+x];
                    var p=tc.m_paintMask[z*65+x];state[zone,0].Paint[x,z]=new PaintRgba(p.r,p.g,p.b,p.a);
                }
                var once=state[zone,0].Clone();int saves=tc.SaveCount;
                RoadTerrainModifier.ApplyRoadTerrainMods(new Vector2s(zone,0),points);
                Assert.Equal(saves,tc.SaveCount);
                for(int z=0;z<65;z++) for(int x=0;x<65;x++)
                {
                    Assert.Equal(once.Heights[x,z],hm.LastRenderedHeights![z*65+x]);
                    var p=tc.m_paintMask[z*65+x];Assert.Equal(once.Paint[x,z],new PaintRgba(p.r,p.g,p.b,p.a));
                }
            }
            Assert.Equal(67,state[0,0].Heights[64,32],3);Assert.Equal(67,state[1,0].Heights[0,32],3);
            Assert.Equal(new PaintRgba(1,0,0,.3f),state[0,0].Paint[64,32]);
            Assert.Equal(state[0,0].Paint[64,32],state[1,0].Paint[0,32]);
            for(int zone=0;zone<2;zone++)
            {
                int edge=zone==0?64:0;
                Assert.Equal(68,state[zone,0].Heights[edge,44],3); // 64+.25*12 + earlier 1m
                Assert.Equal(paint,state[zone,0].Paint[edge,44]);
                Assert.Equal(64,before[zone,0].Heights[edge,32]);
                Assert.Equal(paint,before[zone,0].Paint[edge,32]);
            }
        }
        finally { RoadTerrainModifier.ResetDebugCounters(); RoadSpatialGrid.Clear(); }
    }
}
