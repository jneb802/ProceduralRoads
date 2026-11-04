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
            RoadSpatialGrid.AddRoadPath(path, width, world);
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
    /// The whole width of the road is inside its clear areas, at every point
    /// and at any heading: straight along an axis off the old 4 m grid, and
    /// on the diagonal, where squares have the least reach across the road.
    /// </summary>
    [Theory]
    [InlineData(121.85f, -104f, 121.85f, -24f, 4f)]
    [InlineData(97f, -95f, 159f, -33f, 4f)]
    [InlineData(97f, -95f, 159f, -33f, 2f)]
    public void TheWholeWidthOfTheRoadIsInsideItsClearAreas(float x0, float z0, float x1, float z1, float width)
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
                for (float side = -0.45f; side <= 0.451f; side += 0.15f)
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
        }
    }
}
