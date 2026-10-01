using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Two edges of the road terrain. (1) A zone and the zone next door hold the
/// same border vertices, each in its own compiler; for roads narrower than 4 m
/// a zone missed road points that reached its border, the two computed
/// different heights there, and the road stepped at the seam. (2) Zones
/// generated before their road keep their trees on it; the server removes what
/// generation would have left out, once. The removing is Unity work in
/// ServerTerrainBake; what is removed, and when, is decided here.
/// </summary>
public class RoadEdgesAndVegetationTests
{
    /// <summary>Ground rising 0.5 m per metre eastward, so a road on it is cut and filled across its width.</summary>
    private sealed class SlopeWorld : SyntheticWorld
    {
        public SlopeWorld()
        {
            HasRiver = false;
            HasMountain = false;
        }

        public override float GetHeight(float wx, float wy) => 40f + 0.5f * wx;
        public override Heightmap.Biome GetBiome(float wx, float wy) => Heightmap.Biome.Meadows;
    }

    private static void RoadNorthSouth(float x, float width, WorldGenerator world)
    {
        var path = new List<Vector2>();
        for (float z = -40f; z <= 40f; z += 8f)
            path.Add(new Vector2(x, z));
        RoadSpatialGrid.AddRoadPath(path, width, world);
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    private static TerrainComp Write(Vector2s zone)
    {
        Heightmap hm = Heightmap.CreateForZone(zone, 64);
        List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.GetRoadPointsInZone(zone);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, hm.m_terrainComp!);
        return hm.m_terrainComp!;
    }

    [Fact]
    public void ANarrowRoadBesideABorderGivesBothZonesTheSameBorderHeights()
    {
        var world = new SlopeWorld();
        WorldGenerator.instance = world;
        try
        {
            RoadSpatialGrid.Clear();
            // A 2 m road 2.5 m east of x = 32, the border between zones (0,0)
            // and (1,0). It levels terrain out to 1 + 2 = 3 m, so it reaches
            // the border vertices, which both zones hold.
            RoadNorthSouth(34.5f, 2f, world);
            TerrainComp west = Write(new Vector2s(0, 0));
            TerrainComp east = Write(new Vector2s(1, 0));

            const int pitch = 65;
            bool reached = false;
            for (int vz = 0; vz < pitch; vz++)
            {
                float fromEast = east.m_levelDelta[vz * pitch + 0];  // x = 32 in zone (1,0)
                float fromWest = west.m_levelDelta[vz * pitch + 64]; // x = 32 in zone (0,0)
                Assert.Equal(fromEast, fromWest, 4);
                if (Mathf.Abs(fromEast) > 0.01f)
                    reached = true;
            }
            Assert.True(reached, "the road did not reach the border, so this shows nothing");
        }
        finally
        {
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = null;
        }
    }

    [Theory]
    [InlineData(2f, 3f)]
    [InlineData(3f, 3.5f)]
    [InlineData(4f, 4f)]
    [InlineData(10f, 10f)]
    public void AZoneSeesPointsAsFarAsTheyReachAndFromFourMetresUpAsBefore(float width, float reach)
    {
        // The plain cross-section's reach; the earthworks widen it (below).
        float savedBatter = RoadTerrainModifier.BatterPerMetre;
        try
        {
            PlainEarthworks.Begin();
            RoadTerrainModifier.BatterPerMetre = 0f;
            Assert.Equal(reach, RoadSpatialGrid.ZoneReach(width), 5);
        }
        finally { PlainEarthworks.End(); RoadTerrainModifier.BatterPerMetre = savedBatter; }
        // With the earthworks a zone sees as far as they can reach.
        Assert.Equal(Mathf.Max(width, RoadTerrainModifier.GatherRadius(width)), RoadSpatialGrid.ZoneReach(width), 5);
        Assert.True(RoadSpatialGrid.ZoneReach(width) >= reach);
    }

    [Fact]
    public void AZoneARoadOnlyBrushesHasNothingToWriteAndIsNotStamped()
    {
        // The plain cross-section (60 % flat core, no earthwork widening),
        // whose reach the 3.9 m below is set against.
        var world = new SlopeWorld();
        WorldGenerator.instance = world;
        float savedBatter = RoadTerrainModifier.BatterPerMetre, savedCore = RoadProfile.FlatCoreRatio;
        PlainEarthworks.Begin();
        RoadTerrainModifier.BatterPerMetre = 0f;
        RoadProfile.FlatCoreRatio = RoadConstants.RoadFlatCoreRatio;
        try
        {
            RoadSpatialGrid.Clear();
            // A 4 m road 3.9 m east of the border: zone (0,0) sees its points
            // (within 4 m), but at 3.9 m the level blend is all but gone and
            // its paint does not reach.
            RoadNorthSouth(35.9f, 4f, world);
            Assert.NotEmpty(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0)));

            TerrainComp west = Write(new Vector2s(0, 0));
            Assert.Equal(RoadTerrainModifier.WriteOutcome.NothingToWrite, RoadTerrainModifier.LastWriteOutcome);
            Assert.Equal(0, west.SaveCount);
            Assert.False(RoadTerrainModifier.CarriesCurrentRoads(west));

            TerrainComp east = Write(new Vector2s(1, 0));
            Assert.Equal(RoadTerrainModifier.WriteOutcome.Written, RoadTerrainModifier.LastWriteOutcome);
            Assert.Equal(1, east.SaveCount);
        }
        finally
        {
            PlainEarthworks.End();
            RoadTerrainModifier.BatterPerMetre = savedBatter;
            RoadProfile.FlatCoreRatio = savedCore;
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = null;
        }
    }

    private static readonly int Tree = "Beech1".GetStableHashCode();
    private static readonly int Wall = "stone_wall_1x1".GetStableHashCode();
    private static readonly HashSet<int> Vegetation = new() { Tree };
    private static readonly List<VegetationClearing.Area> Areas = new() { new(new Vector2(10f, 10f), 2.4f) };
    private static readonly List<VegetationClearing.Footprint> NoLocations = new();
    private static readonly List<VegetationClearing.Footprint> NoPlayerGround = new();

    [Fact]
    public void OnlyTheGamesVegetationInsideTheRoadsClearAreasGoes()
    {
        Assert.True(VegetationClearing.ShouldRemove(Tree, new Vector2(10f, 10f), false, Vegetation, Areas, NoLocations, NoPlayerGround));
        // A player's build is not vegetation, road or no road.
        Assert.False(VegetationClearing.ShouldRemove(Wall, new Vector2(10f, 10f), false, Vegetation, Areas, NoLocations, NoPlayerGround));
        // Off the road.
        Assert.False(VegetationClearing.ShouldRemove(Tree, new Vector2(13f, 10f), false, Vegetation, Areas, NoLocations, NoPlayerGround));
    }

    [Fact]
    public void TheClearAreaIsTheGamesStrictSquare()
    {
        // In the square's corner, outside the inscribed circle: generation
        // leaves it out, so clearing takes it.
        Assert.True(VegetationClearing.InsideClearArea(Areas, new Vector2(12.2f, 12.2f)));
        // On the edge: the game's test is strict.
        Assert.False(VegetationClearing.InsideClearArea(Areas, new Vector2(12.4f, 10f)));
    }

    [Fact]
    public void ObjectsWithinALocationAreLeftToTheLocation()
    {
        var locations = new List<VegetationClearing.Footprint> { new(new Vector2(0f, 10f), 12f) };
        Assert.False(VegetationClearing.ShouldRemove(Tree, new Vector2(10f, 10f), false, Vegetation, Areas, locations, NoPlayerGround));
    }

    // ---- what the clearing must not take: somebody's own work ----
    //
    // A grown tree carries nothing in the save that says a player planted it
    // (vanilla's PlaceVegetation writes only a scale, and Plant.Grow carries no
    // creator onto the grown prefab), so these are the two facts that CAN be
    // honoured: an object stamped with a creator, and vegetation standing on
    // ground a player has built on.

    [Fact]
    public void AnObjectAPlayerMadeIsNeverTaken()
    {
        // Same prefab, same spot on the road, but it carries a creator.
        Assert.True(VegetationClearing.ShouldRemove(Tree, new Vector2(10f, 10f), false, Vegetation, Areas, NoLocations, NoPlayerGround));
        Assert.False(VegetationClearing.ShouldRemove(Tree, new Vector2(10f, 10f), true, Vegetation, Areas, NoLocations, NoPlayerGround));
    }

    /// <summary>
    /// A build at (31, 0) and a tree at (33, 0) are two metres apart with a
    /// zone border between them. Gathering a player's objects from the tree's
    /// own zone only made the advertised 8 m radius shrink to nothing at every
    /// zone edge, so the tree beside somebody's hut was still eligible.
    /// </summary>
    [Fact]
    public void ABuildJustOverAZoneBorderStillProtectsTheTreeBesideIt()
    {
        var onTheBorder = new List<VegetationClearing.Area> { new(new Vector2(33f, 0f), 2.4f) };
        var hutNextDoor = new List<VegetationClearing.Footprint> { new(new Vector2(31f, 0f), VegetationClearing.PlayerGroundRadius) };
        Assert.False(VegetationClearing.ShouldRemove(Tree, new Vector2(33f, 0f), false, Vegetation, onTheBorder, NoLocations, hutNextDoor));

        // A diagonal zone corner is the same story.
        var atTheCorner = new List<VegetationClearing.Area> { new(new Vector2(33f, 33f), 2.4f) };
        var hutDiagonally = new List<VegetationClearing.Footprint> { new(new Vector2(31f, 31f), VegetationClearing.PlayerGroundRadius) };
        Assert.False(VegetationClearing.ShouldRemove(Tree, new Vector2(33f, 33f), false, Vegetation, atTheCorner, NoLocations, hutDiagonally));

        // Control: a build in the neighbouring zone but outside the radius
        // protects nothing, so the road is still cleared.
        var hutFarOff = new List<VegetationClearing.Footprint> { new(new Vector2(20f, 0f), VegetationClearing.PlayerGroundRadius) };
        Assert.True(VegetationClearing.ShouldRemove(Tree, new Vector2(33f, 0f), false, Vegetation, onTheBorder, NoLocations, hutFarOff));
    }

    /// <summary>
    /// Why the eight neighbours are enough to gather: nothing outside that
    /// block can reach into the middle zone, because the radius is smaller
    /// than a zone step.
    /// </summary>
    [Fact]
    public void TheProtectionRadiusFitsInsideOneZoneStep()
    {
        // Half a zone is the real bound: a point inside the middle zone is at
        // most this far from its centre, so nothing beyond the 3x3 block can
        // come within the radius of it.
        Assert.True(VegetationClearing.PlayerGroundRadius <= RoadConstants.HalfZoneSize);
    }

    [Fact]
    public void VegetationOnGroundAPlayerBuiltOnStays()
    {
        // A hut 3 m away: the tree beside it is left alone.
        var nearBuild = new List<VegetationClearing.Footprint> { new(new Vector2(13f, 10f), VegetationClearing.PlayerGroundRadius) };
        Assert.False(VegetationClearing.ShouldRemove(Tree, new Vector2(10f, 10f), false, Vegetation, Areas, NoLocations, nearBuild));

        // The same build far off protects nothing: a natural tree on the road still goes.
        var farBuild = new List<VegetationClearing.Footprint> { new(new Vector2(60f, 60f), VegetationClearing.PlayerGroundRadius) };
        Assert.True(VegetationClearing.ShouldRemove(Tree, new Vector2(10f, 10f), false, Vegetation, Areas, NoLocations, farBuild));
    }

    [Fact]
    public void AZoneIsClearedOncePerNetwork()
    {
        VegetationClearing.Reset();
        try
        {
            var zone = new Vector2s(3, 4);
            Assert.False(VegetationClearing.IsCleared(zone, 111));
            VegetationClearing.MarkCleared(zone, 111);
            Assert.True(VegetationClearing.IsCleared(zone, 111));

            // Another network puts its roads elsewhere: every zone starts again.
            Assert.False(VegetationClearing.IsCleared(zone, 222));
            VegetationClearing.MarkCleared(new Vector2s(5, 5), 222);
            Assert.False(VegetationClearing.IsCleared(zone, 222));

            // A saved record of another network is not taken.
            VegetationClearing.Load(111, new[] { zone }, currentVersion: 222);
            Assert.False(VegetationClearing.IsCleared(zone, 222));
            Assert.Equal(0, VegetationClearing.Version);
        }
        finally
        {
            VegetationClearing.Reset();
        }
    }

    [Fact]
    public void TheClearedZonesSurviveASaveAndLoadWithTheNetwork()
    {
        var world = new SlopeWorld();
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        RoadNetworkGenerator.Reset();
        try
        {
            RoadNorthSouth(10f, 4f, world);
            int version = RoadSpatialGrid.RoadNetworkVersion;
            RoadNetworkPersistence.EnsureMetadataInstance();
            typeof(RoadNetworkGenerator).GetField("m_roadsGenerated", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
            RoadNetworkGenerator.SaveGlobalRoadData();
            var zone = new Vector2s(0, 0);
            VegetationClearing.MarkCleared(zone, version);
            RoadNetworkGenerator.SaveClearedZones();

            // Next session.
            RoadNetworkGenerator.Reset();
            Assert.False(VegetationClearing.IsCleared(zone, version));
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
            Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
            Assert.True(VegetationClearing.IsCleared(zone, version));
        }
        finally
        {
            RoadNetworkGenerator.Reset();
            ZDOMan.instance = null;
            WorldGenerator.instance = null;
        }
    }
}
