using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;
namespace ProceduralRoads.Tests;

// Real writer against authored sloped inputs; expectations across the core,
// release and outside are declared directly, not read back from RoadProfile
// or RoadSurface.
public class TerrainPaintMatrixTests
{
    [Theory]
    [InlineData(0, .25f, 3f, 2f, false)]
    [InlineData(0, -.25f, -3f, 4f, false)]
    [InlineData(40, .25f, 3f, 8f, false)]
    [InlineData(40, -.25f, -3f, 4f, false)]
    [InlineData(0, .25f, 20f, 4f, false)]
    [InlineData(40, -.25f, -20f, 4f, false)]
    [InlineData(0, .25f, 3f, 4f, true)]
    [InlineData(40, -.25f, -3f, 4f, true)]
    public void SlopedGroundPreservesVergePaintAndVegetationWhileCoreChanges(int zoneX,float slope,float lift,float width,bool paintOnly)
    {
        using var scope=new ValheimWorldScope();
        using var overrides = StaticOverride.Keep(() => RoadEarthworkNoise.Amplitude).AndKeep(() => RoadEarthworkNoise.FillSpread).AndKeep(() => RoadTerrainModifier.BatterPerMetre);
        try
        {
            RoadTerrainModifier.ResetDebugCounters();RoadSpatialGrid.Clear();
            scope.WithWorld(new SyntheticWorld()).WithZdos();
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadEarthworkNoise.Amplitude=0;RoadEarthworkNoise.FillSpread=0;RoadTerrainModifier.BatterPerMetre=0;
            var zone=new Vector2s(zoneX,0);var hm=scope.RegisterHeightmap(zone);
            hm.AuthoredHeight=(_,z)=>64+slope*z;var tc=hm.m_terrainComp!;
            var original=new Color(.2f,.4f,.6f,.3f);
            for(int i=0;i<tc.m_paintMask.Length;i++)tc.m_paintMask[i]=original;
            hm.RebuildTerrain();
            var road=new List<RoadSpatialGrid.RoadPoint>();
            for(int x=-16;x<=16;x++)if(width!=8 || x==0)road.Add(new RoadSpatialGrid.RoadPoint(new Vector2(zoneX*64+x,0),width,64+lift,paintOnly:paintOnly));
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone,road,hm,tc);
            int centre=32*65+32;
            float expected=paintOnly?64:64+Math.Max(-8,Math.Min(8,lift));
            Assert.InRange(Math.Abs(hm.LastRenderedHeights![centre]-expected),0,.001f);
            var color=tc.m_paintMask[centre];
            Assert.InRange(Math.Abs(color.r-(zoneX==0?1:0)),0,.001f);
            Assert.InRange(Math.Abs(color.g),0,.001f);
            Assert.InRange(Math.Abs(color.b-(zoneX==0?0:1)),0,.001f);
            if(width==8)
            {
                // One point: at 3m, t=(3-2.4)/(3.4-2.4)=.6 and
                // paint strength=1-(3t²-2t³)=.352. No repeated disc blending.
                var edge=tc.m_paintMask[35*65+32];
                float red=zoneX==0?1:0,blue=zoneX==0?0:1;
                Assert.InRange(Math.Abs(edge.r-(.2f+(red-.2f)*.352f)),0,.001f);
                Assert.InRange(Math.Abs(edge.g-.2592f),0,.001f);
                Assert.InRange(Math.Abs(edge.b-(.6f+(blue-.6f)*.352f)),0,.001f);
            }
            // Height across the road, declared from the cross-section design
            // rather than RoadProfile's code: the core (|z| <= w/2) is levelled
            // fully, a smoothstep release runs to zero over the 2 m margin
            // (noise, fill spread and batter are off), and the ±8 m limit is
            // on each vertex's total delta. All road points share one height
            // on z=0, so the road surface is 64+lift and the nearest point, at
            // distance |z|, sets the blend. On this 1 m grid the only release
            // vertex is |z|=w/2+1, the margin's midpoint: 1-(3·.5²-2·.5³)=.5.
            // So the core keeps no cross-fall unless the clamp binds.
            float half=width/2,surface=64+lift;
            for(int z=-12;z<=12;z++)
            {
                int i=(z+32)*65+32;
                float ground=64+slope*z,distance=Math.Abs(z);
                float blend=distance<=half?1:distance==half+1?.5f:0;
                float height=paintOnly?ground:ground+Math.Max(-8,Math.Min(8,blend*(surface-ground)));
                Assert.InRange(Math.Abs(hm.LastRenderedHeights[i]-height),0,.001f);
                Assert.Equal(original.a,tc.m_paintMask[i].a);
                if(Math.Abs(z)>=width/2)
                {
                    Assert.False(tc.m_modifiedPaint[i]);
                    Assert.Equal(original.r,tc.m_paintMask[i].r);Assert.Equal(original.g,tc.m_paintMask[i].g);Assert.Equal(original.b,tc.m_paintMask[i].b);
                }
            }
            var savedPaint=(Color[])tc.m_paintMask.Clone();var savedHeights=hm.LastRenderedHeights.ToArray();int saves=tc.SaveCount;
            RoadTerrainModifier.ApplyRoadTerrainMods(zone,road);
            Assert.Equal(saves,tc.SaveCount);
            Assert.Equal(savedHeights,hm.LastRenderedHeights);
            for(int i=0;i<savedPaint.Length;i++)Assert.Equal(savedPaint[i],tc.m_paintMask[i]);
        }
        finally { RoadTerrainModifier.ResetDebugCounters(); RoadSpatialGrid.Clear(); }
    }
}
