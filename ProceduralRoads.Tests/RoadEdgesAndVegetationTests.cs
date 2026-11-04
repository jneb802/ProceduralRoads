using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
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
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
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
        using var scope = new ValheimWorldScope().WithWorld(world);
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
        using (PlainEarthworks.Apply().And(() => RoadTerrainModifier.BatterPerMetre, 0f))
            Assert.Equal(reach, RoadSpatialGrid.ZoneReach(width), 5);
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
        using var scope = new ValheimWorldScope().WithWorld(world);
        using var plain = PlainEarthworks.Apply()
            .And(() => RoadTerrainModifier.BatterPerMetre, 0f)
            .And(() => RoadProfile.FlatCoreRatio, RoadConstants.RoadFlatCoreRatio);
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
        finally { RoadSpatialGrid.Clear(); }
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

    // ---- the road's clear areas, as generation and the clearing both use them ----

    /// <summary>Flat meadow: the road lies where it is drawn.</summary>
    private sealed class FlatWorld : SyntheticWorld
    {
        public FlatWorld()
        {
            HasRiver = false;
            HasMountain = false;
        }

        public override float GetHeight(float wx, float wy) => 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
    }

    private static readonly int SmallBeech = "Beech_small1".GetStableHashCode();

    /// <summary>The zone's road clear areas as the clearing tests them (ServerTerrainBake.ClearAreasFor's road part).</summary>
    private static List<VegetationClearing.Area> RoadAreas(Vector2s zone)
    {
        var areas = new List<VegetationClearing.Area>();
        foreach (ZoneSystem.ClearArea area in RoadClearAreaManager.GetOrCreateClearAreas(zone))
            areas.Add(new VegetationClearing.Area(new Vector2(area.m_center.x, area.m_center.z), area.m_radius));
        return areas;
    }

    private static void WithRoad(List<Vector2> path, float width, System.Action body)
    {
        var world = new FlatWorld();
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadClearAreaManager.ClearCache();
        try
        {
            Assert.True(RoadSpatialGrid.AddRoadPath(path, width, world));
            RoadSpatialGrid.FinalizeRoadNetwork();
            body();
        }
        finally
        {
            RoadClearAreaManager.ClearCache();
            RoadSpatialGrid.Clear();
        }
    }

    /// <summary>
    /// Seen in game near spawn: a wild Beech_small1 0.59 m from the centre of
    /// a 4 m road at (122.42, -65.06), zone (2,-1), a zone generated before the
    /// network. The road there runs north-south at x = 121.85. The clear
    /// areas were centred on a 4 m grid, not on the road (x 121.85 snapped to
    /// 120), so their east edge stood at 120 + 2.4 = 122.4 and the eastern
    /// half of the road had no clear area at all: generation planted on it
    /// and the clearing, testing the same areas, kept what stood there.
    /// </summary>
    [Fact]
    public void AWildSmallTreeOnTheRoadInAZoneGeneratedBeforeItGoes()
    {
        var path = new List<Vector2>();
        for (float z = -104f; z <= -24f; z += 8f)
            path.Add(new Vector2(121.85f, z));
        WithRoad(path, 4f, () =>
        {
            var zone = new Vector2s(2, -1);
            var tree = new Vector2(122.42f, -65.06f);
            Assert.Equal(zone, ZoneSystem.GetZone(new Vector3(tree.x, 0f, tree.y)));
            float nearest = float.MaxValue;
            foreach (RoadSpatialGrid.RoadPoint point in RoadSpatialGrid.GetRoadPointsInZone(zone))
                nearest = Mathf.Min(nearest, Mathf.Abs(point.p.x - tree.x));
            Assert.True(nearest < 0.7f, $"the road is {nearest} m from the tree, not under it");

            List<VegetationClearing.Area> areas = RoadAreas(zone);
            var vegetation = new HashSet<int> { SmallBeech };
            Assert.True(VegetationClearing.ShouldRemove(SmallBeech, tree, false, vegetation, areas, NoLocations, NoPlayerGround));
            // A sapling a player planted there carries its creator: it stays.
            Assert.False(VegetationClearing.ShouldRemove(SmallBeech, tree, true, vegetation, areas, NoLocations, NoPlayerGround));
            // A wild one beside the road, past its clear area, stays too.
            Assert.False(VegetationClearing.ShouldRemove(SmallBeech, new Vector2(125f, -65.06f), false, vegetation, areas, NoLocations, NoPlayerGround));
        });
    }

    /// <summary>
    /// Vegetation may creep onto the outer 10 % of the road: on a 4 m road
    /// along an axis the clear areas reach 1.8 m from its centre line. A bush
    /// on the outer edge stays; one in the middle goes, in a zone generated
    /// with the road (the game's own square test of the same areas) and in
    /// one the server clears afterwards alike.
    /// </summary>
    [Fact]
    public void VegetationMayCreepOntoTheOuterEdgeOfTheRoad()
    {
        const float x = 121.85f, width = 4f;
        var path = new List<Vector2>();
        for (float z = -104f; z <= -24f; z += 8f)
            path.Add(new Vector2(x, z));
        WithRoad(path, width, () =>
        {
            List<VegetationClearing.Area> areas = RoadAreas(new Vector2s(2, -1));
            var vegetation = new HashSet<int> { SmallBeech };
            foreach (float side in new[] { -1f, 1f })
            {
                var middle = new Vector2(x + side * 0.43f * width, -65f);
                var edge = new Vector2(x + side * 0.48f * width, -65f);
                Assert.True(VegetationClearing.InsideClearArea(areas, middle));
                Assert.True(VegetationClearing.ShouldRemove(SmallBeech, middle, false, vegetation, areas, NoLocations, NoPlayerGround));
                Assert.False(VegetationClearing.InsideClearArea(areas, edge));
                Assert.False(VegetationClearing.ShouldRemove(SmallBeech, edge, false, vegetation, areas, NoLocations, NoPlayerGround));
            }
        });
    }

    private static readonly int SwampTree = "SwampTree1".GetStableHashCode();
    private static readonly int BigSwampTree = "SwampTree2".GetStableHashCode();
    private static readonly int Pine = "Pinetree_01".GetStableHashCode();
    private static readonly int Bush = "Bush01_0".GetStableHashCode();

    /// <summary>The zone's road points as the grown-tree rule reads them (ServerTerrainBake.RoadCentresFor).</summary>
    private static List<VegetationClearing.RoadCentre> RoadCentres(Vector2s zone) => RoadClearAreaManager.GetOrCreateRoadCentres(zone);

    /// <summary>Generation's test of one candidate (ZoneSystem_PlaceVegetation_Patch: the road's clear areas after none of
    /// the game's, the zone's road centres, the drawn scale).</summary>
    private static bool PlacedOffTheRoad(Vector2s zone, Vector2 at, string prefab, float scale) =>
        !RoadClearAreaManager.OnRoad(RoadClearAreaManager.GetOrCreateClearAreas(zone), 0, int.MaxValue, RoadCentres(zone),
            new Vector3(at.x, 40f, at.y), prefab, scale, RockClearingMode.Carve, _ => Heightmap.Biome.Meadows);

    /// <summary>The server's test of one saved object.</summary>
    private static bool Cleared(Vector2s zone, Vector2 at, int prefab, float scale) =>
        VegetationClearing.ShouldRemove(prefab, at, false, new HashSet<int> { prefab }, RoadAreas(zone), NoLocations, NoPlayerGround,
            RoadCentres(zone), scale);

    /// <summary>
    /// Seen in a swamp: a SwampTree1 2.68 m from the centre of a 4 m road, its origin just past the clear areas (1.8 m),
    /// its trunk -- 2.41 m from the origin at scale 1 -- over the road. A grown tree whose trunk reaches the road goes, in a
    /// zone generated with the road and in one the server clears alike; a bush on the same spot is creep and stays; a grown
    /// tree whose trunk ends short of the road stays, and how far that is follows the tree's scale and trunk.
    /// </summary>
    [Fact]
    public void AGrownTreeWhoseTrunkReachesTheRoadGoesAndABushThereMayStay()
    {
        const float x = 121.85f, width = 4f;
        var path = new List<Vector2>();
        for (float z = -104f; z <= -24f; z += 8f)
            path.Add(new Vector2(x, z));
        WithRoad(path, width, () =>
        {
            var zone = new Vector2s(2, -1);
            foreach (float side in new[] { -1f, 1f })
            {
                var edge = new Vector2(x + side * 2.68f, -65f);
                Assert.False(VegetationClearing.InsideClearArea(RoadAreas(zone), edge));

                // 2 + 2.41 + 0.25 = 4.66 m: the swamp tree's trunk reaches the road.
                Assert.False(PlacedOffTheRoad(zone, edge, "SwampTree1", 1f));
                Assert.True(Cleared(zone, edge, SwampTree, 1f));
                // A bush there is creep.
                Assert.True(PlacedOffTheRoad(zone, edge, "Bush01_0", 1f));
                Assert.False(Cleared(zone, edge, Bush, 1f));
                Assert.True(PlacedOffTheRoad(zone, edge, "Beech_small1", 1.5f));
                Assert.False(Cleared(zone, edge, SmallBeech, 1.5f));
                // A pine's trunk is 0.33 m: at 2.68 m it ends short of the road (2 + 0.33 + 0.25 = 2.58 m).
                Assert.True(PlacedOffTheRoad(zone, edge, "Pinetree_01", 1f));
                Assert.False(Cleared(zone, edge, Pine, 1f));

                // Well clear: 5 m off, past the swamp tree's 4.66 m at scale 1; at scale 1.3 it reaches 5.38 m.
                var clear = new Vector2(x + side * 5f, -65f);
                Assert.True(PlacedOffTheRoad(zone, clear, "SwampTree1", 1f));
                Assert.False(Cleared(zone, clear, SwampTree, 1f));
                Assert.False(PlacedOffTheRoad(zone, clear, "SwampTree1", 1.3f));
                Assert.True(Cleared(zone, clear, SwampTree, 1.3f));
                var farOff = new Vector2(x + side * 9f, -65f);
                Assert.True(PlacedOffTheRoad(zone, farOff, "SwampTree2", 2f));
                Assert.False(Cleared(zone, farOff, BigSwampTree, 2f));
            }
        });
    }

    /// <summary>
    /// A grown tree may reach a road outside its zone: an AshlandsTree6 at scale 2.5 inside the zone, 10.5 m from a
    /// road beyond the zone's border, has its trunk on that road. The zone's road centres reach as far as the widest
    /// trunk at the largest scale.
    /// </summary>
    [Fact]
    public void AGrownTreeIsTestedAgainstARoadBeyondItsZone()
    {
        const float x = 170f;
        var path = new List<Vector2>();
        for (float z = -104f; z <= -24f; z += 8f)
            path.Add(new Vector2(x, z));
        WithRoad(path, 4f, () =>
        {
            var zone = new Vector2s(2, -1);
            var tree = new Vector2(159.5f, -65f);
            Assert.Equal(zone, ZoneSystem.GetZone(new Vector3(tree.x, 0f, tree.y)));
            Assert.NotEqual(zone, ZoneSystem.GetZone(new Vector3(x, 0f, tree.y)));
            Assert.False(VegetationClearing.InsideClearArea(RoadAreas(zone), tree));
            // 2 + 4.32 x 2.5 + 0.25 = 13.05 m.
            Assert.False(PlacedOffTheRoad(zone, tree, "AshlandsTree6", 2.5f));
            Assert.True(Cleared(zone, tree, "AshlandsTree6".GetStableHashCode(), 2.5f));
            Assert.True(VegetationClearing.TreeReach >= 4.32f * VegetationClearing.LargestTreeScale);
        });
    }

    /// <summary>
    /// Creep is the small vegetation: saplings, small trees, bushes, stumps, logs and small stones are not grown trees,
    /// nor are the large rocks, which are the rock clearing's.
    /// </summary>
    [Fact]
    public void OnlyFullSizeTreesAreGrownTrees()
    {
        foreach (string tree in new[] { "Beech1", "Birch1", "Birch2_aut", "Oak1", "FirTree", "Pinetree_01", "SwampTree1", "SwampTree2", "YggaShoot1", "AshlandsTree6" })
        {
            Assert.True(VegetationClearing.IsGrownTree(tree, out float reach), tree);
            Assert.True(reach > 0f, tree);
            Assert.True(VegetationClearing.IsGrownTree(tree.GetStableHashCode(), out float byHash) && byHash == reach, tree);
        }
        foreach (string creep in new[] { "Beech_small1", "Beech_small2", "FirTree_small", "FirTree_small_dead", "YggaShoot_small1", "Bush01_0",
                     "shrub_2", "stubbe_0", "FirTree_oldLog", "SwampTree2_log", "Pickable_Stone", "Pickable_Branch" })
            Assert.False(VegetationClearing.IsGrownTree(creep, out _), creep);
        foreach (string rock in RoadRockPolicy.KnownRocks)
            Assert.False(VegetationClearing.IsGrownTree(rock, out _), rock);
    }

    private static readonly int Stone = "Pickable_Stone".GetStableHashCode();
    private static readonly int Boulder = "rock1_mountain".GetStableHashCode();
    private static readonly int MistlandsCliff = "cliff_mistlands1".GetStableHashCode();

    /// <summary>
    /// The game's vegetation list holds stones and boulders alike. A small stone in the middle of the road goes with the
    /// vegetation; a boulder there that the rock clearing takes is the rock clearing's alone (Carve carves it), so the
    /// vegetation pass never takes it. A boulder the rock clearing would leave -- clearing Off, a biome it does not clear --
    /// goes with the vegetation, as generation keeps it off the road.
    /// </summary>
    [Fact]
    public void ASmallStoneGoesWithTheVegetationAndALargeRockIsLeftToTheRockClearing()
    {
        var vegetation = new HashSet<int> { Tree, Stone, Boulder, MistlandsCliff };
        var centre = new Vector2(10f, 10f);
        System.Func<Vector2, Heightmap.Biome> In(Heightmap.Biome b) => _ => b;
        Assert.True(VegetationClearing.ShouldRemove(Stone, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            rockClearing: RockClearingMode.Carve, biomeAt: In(Heightmap.Biome.Mountain)));
        Assert.False(VegetationClearing.ShouldRemove(Boulder, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            rockClearing: RockClearingMode.Carve, biomeAt: In(Heightmap.Biome.Mountain)));
        Assert.False(VegetationClearing.ShouldRemove(MistlandsCliff, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            rockClearing: RockClearingMode.Carve, biomeAt: In(Heightmap.Biome.Mistlands)));
        Assert.True(VegetationClearing.ShouldRemove(Boulder, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            rockClearing: RockClearingMode.Off, biomeAt: In(Heightmap.Biome.Mountain)));
        Assert.True(VegetationClearing.ShouldRemove(Boulder, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            rockClearing: RockClearingMode.Carve, biomeAt: In(Heightmap.Biome.AshLands)));
        Assert.True(VegetationClearing.ShouldRemove(Boulder, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            rockClearing: RockClearingMode.Carve, biomeAt: In(Heightmap.Biome.DeepNorth)));
        foreach (string rock in RoadRockPolicy.KnownRocks)
        {
            Assert.True(VegetationClearing.IsRock(rock.GetStableHashCode()), rock);
            Assert.False(VegetationClearing.LeftToRockClearing(rock.GetStableHashCode(), Heightmap.Biome.Mountain, RockClearingMode.Off), rock);
        }
        Assert.False(VegetationClearing.IsRock(Stone));
        Assert.False(VegetationClearing.IsRock(Tree));
    }

    /// <summary>A structure's clear area (a staircase, a bridge) takes every rock off on the server, whatever the rock
    /// clearing would do with it on a road; a player's own rock still stays.</summary>
    [Fact]
    public void AStructuresClearAreaTakesEveryRockOnTheServer()
    {
        var vegetation = new HashSet<int> { Boulder, MistlandsCliff };
        var onStair = new Vector2(40f, 0f);
        var stair = new List<VegetationClearing.Area> { new VegetationClearing.Area(onStair, 3f) };
        foreach (var mode in new[] { RockClearingMode.Carve, RockClearingMode.Remove, RockClearingMode.Off })
        {
            Assert.True(VegetationClearing.ShouldRemove(Boulder, onStair, false, vegetation, Areas, NoLocations, NoPlayerGround,
                structureAreas: stair, rockClearing: mode, biomeAt: _ => Heightmap.Biome.Mountain));
            Assert.True(VegetationClearing.ShouldRemove(MistlandsCliff, onStair, false, vegetation, Areas, NoLocations, NoPlayerGround,
                structureAreas: stair, rockClearing: mode, biomeAt: _ => Heightmap.Biome.Mistlands));
        }
        Assert.False(VegetationClearing.ShouldRemove(Boulder, onStair, true, vegetation, Areas, NoLocations, NoPlayerGround,
            structureAreas: stair, rockClearing: RockClearingMode.Carve, biomeAt: _ => Heightmap.Biome.Mountain));
    }

    /// <summary>
    /// The middle 90 % of the road is inside its clear areas, at every point
    /// and at any heading: straight along an axis off the old 4 m grid, where
    /// squares have the least reach across the road, and on the diagonal.
    /// </summary>
    [Theory]
    [InlineData(121.85f, -104f, 121.85f, -24f, 4f)]
    [InlineData(97f, -95f, 159f, -33f, 4f)]
    [InlineData(97f, -95f, 159f, -33f, 2f)]
    public void TheMiddleOfTheRoadIsInsideItsClearAreas(float x0, float z0, float x1, float z1, float width)
    {
        var from = new Vector2(x0, z0);
        var to = new Vector2(x1, z1);
        var path = new List<Vector2>();
        for (int i = 0; i <= 10; i++)
            path.Add(Vector2.Lerp(from, to, i / 10f));
        WithRoad(path, width, () =>
        {
            var zone = new Vector2s(2, -1);
            List<VegetationClearing.Area> areas = RoadAreas(zone);
            Vector2 along = (to - from).normalized;
            var across = new Vector2(-along.y, along.x);
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            int checkedPoints = 0;
            // Inside the zone, clear of its border, where every road point that matters is gathered.
            for (float t = 0f; t <= (to - from).magnitude; t += 0.25f)
            {
                Vector2 onRoad = from + along * t;
                if (Mathf.Abs(onRoad.x - centre.x) > 28f || Mathf.Abs(onRoad.y - centre.z) > 28f)
                    continue;
                for (float side = -0.44f; side <= 0.441f; side += 0.11f)
                {
                    Vector2 point = onRoad + across * (side * width);
                    Assert.True(VegetationClearing.InsideClearArea(areas, point), $"({point.x:F2}, {point.y:F2}) on the road is outside its clear areas");
                    checkedPoints++;
                }
            }
            Assert.True(checkedPoints > 100);
        });
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
        using var scope = new ValheimWorldScope().WithWorld(world).WithZdos();
        RoadNetworkGenerator.Reset();
        try
        {
            RoadNorthSouth(10f, 4f, world);
            int version = RoadSpatialGrid.RoadNetworkVersion;
            RoadNetworkPersistence.EnsureMetadataInstance();
            typeof(RoadNetworkGenerator).GetField("m_roadsGenerated", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true); typeof(RoadNetworkGenerator).GetField("m_networkDirty", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
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
        }
    }
}
