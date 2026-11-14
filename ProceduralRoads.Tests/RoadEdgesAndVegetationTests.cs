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
    public void AZoneSeesPointsAsFarAsTheirFitReaches(float width, float reach)
    {
        // The gather includes the fit's rounding allowance, even with plain earthworks.
        using (PlainEarthworks.Apply().And(() => RoadTerrainModifier.BatterPerMetre, 0f))
            Assert.Equal(Mathf.Max(width, RoadTerrainModifier.FitReach(width)), RoadSpatialGrid.ZoneReach(width), 5);
        // With the earthworks a zone sees as far as they can reach.
        Assert.Equal(Mathf.Max(width, RoadTerrainModifier.GatherRadius(width)), RoadSpatialGrid.ZoneReach(width), 5);
        Assert.True(RoadSpatialGrid.ZoneReach(width) >= reach);
    }

    [Fact]
    public void AZoneARoadOnlyBrushesHasNothingToWriteAndIsStampedWithNothingSaved()
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

            // Nothing saved, but final for this network: stamped with the network and the writer.
            TerrainComp west = Write(new Vector2s(0, 0));
            Assert.Equal(RoadTerrainModifier.WriteOutcome.NothingToWrite, RoadTerrainModifier.LastWriteOutcome);
            Assert.Equal(0, west.SaveCount);
            Assert.Null(west.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(west));
            Assert.Equal(RoadTerrainModifier.WriterVersion, west.m_nview.GetZDO().GetInt(RoadTerrainModifier.WriterVersionHash));

            TerrainComp east = Write(new Vector2s(1, 0));
            Assert.Equal(RoadTerrainModifier.WriteOutcome.Written, RoadTerrainModifier.LastWriteOutcome);
            Assert.Equal(1, east.SaveCount);
            // Stamped with the writer that wrote it, beside the network.
            Assert.Equal(RoadTerrainModifier.WriterVersion, east.m_nview.GetZDO().GetInt(RoadTerrainModifier.WriterVersionHash));
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A loaded zone a road only brushes is written once, finds nothing to write and is stamped: no path that
    /// reads the stamp writes it again for the same network, in this session or after a reload (the sweep, a compiler
    /// coming alive, a zone spawn; unstamped it was written again on every load). A new network writes it again, and a
    /// road of the new one that reaches into it is written.</summary>
    [Fact]
    public void AZoneWhoseWriteFoundNothingToWriteIsNotWrittenAgainUntilTheNetworkChanges()
    {
        var world = new SlopeWorld();
        using var scope = new ValheimWorldScope().WithWorld(world);
        using var plain = PlainEarthworks.Apply()
            .And(() => RoadTerrainModifier.BatterPerMetre, 0f)
            .And(() => RoadProfile.FlatCoreRatio, RoadConstants.RoadFlatCoreRatio);
        try
        {
            RoadSpatialGrid.Clear();
            RoadTerrainModifier.ResetForWorld();
            RoadNorthSouth(35.9f, 4f, world);
            var zone = new Vector2s(0, 0);
            Heightmap hm = scope.RegisterHeightmap(zone, 64);
            RoadTerrainModifier.OnZoneSpawned(zone, RoadSpatialGrid.GetRoadPointsInZone(zone));
            Assert.Equal(RoadTerrainModifier.WriteOutcome.NothingToWrite, RoadTerrainModifier.LastWriteOutcome);
            TerrainComp tc = hm.m_terrainComp!;
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));

            // In this session, and after a reload (the session's sweep record forgotten, the saved compiler coming alive
            // again, the zone spawning again): nothing writes it.
            for (int load = 0; load < 2; load++)
            {
                if (load == 1) RoadTerrainModifier.ResetForWorld();
                RoadTerrainModifier.LastWriteOutcome = RoadTerrainModifier.WriteOutcome.None;
                Assert.Equal(0, RoadTerrainModifier.SweepUnstamped());
                RoadTerrainModifier.OnTerrainCompilerReady(tc);
                RoadTerrainModifier.OnZoneSpawned(zone, RoadSpatialGrid.GetRoadPointsInZone(zone));
                Assert.Equal(RoadTerrainModifier.WriteOutcome.None, RoadTerrainModifier.LastWriteOutcome);
                Assert.Equal(0, tc.SaveCount);
            }

            // A new network whose road reaches into the zone: the stamp is stale and the zone is written.
            int old = RoadSpatialGrid.RoadNetworkVersion;
            RoadSpatialGrid.Clear();
            RoadNorthSouth(34.5f, 2f, world);
            Assert.NotEqual(old, RoadSpatialGrid.RoadNetworkVersion);
            Assert.False(RoadTerrainModifier.CarriesCurrentRoads(tc));
            RoadTerrainModifier.OnTerrainCompilerReady(tc);
            Assert.Equal(RoadTerrainModifier.WriteOutcome.Written, RoadTerrainModifier.LastWriteOutcome);
            Assert.Equal(1, tc.SaveCount);
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
        }
        finally { RoadTerrainModifier.ResetForWorld(); RoadSpatialGrid.Clear(); }
    }

    /// <summary>The server bake's write of a zone the roads only brush: nothing to write, reported so, the compiler kept
    /// stamped and empty, and the bake's plan for its saved compiler then leaves it alone (a dropped compiler would be made
    /// again and written on every load).</summary>
    [Fact]
    public void TheBakeKeepsAZoneWithNothingToWriteStampedAndEmpty()
    {
        var world = new SlopeWorld();
        using var scope = new ValheimWorldScope().WithWorld(world).WithZdos();
        using var plain = PlainEarthworks.Apply()
            .And(() => RoadTerrainModifier.BatterPerMetre, 0f)
            .And(() => RoadProfile.FlatCoreRatio, RoadConstants.RoadFlatCoreRatio);
        try
        {
            RoadSpatialGrid.Clear();
            RoadTerrainModifier.ResetForWorld();
            RoadNorthSouth(35.9f, 4f, world);
            var zone = new Vector2s(0, 0);
            Heightmap hm = scope.RegisterHeightmap(zone, 64);
            TerrainComp tc = hm.m_terrainComp!;
            RoadTerrainModifier.LastWriteOutcome = RoadTerrainModifier.WriteOutcome.None;
            RoadTerrainModifier.BakeRoadTerrain(zone, hm, tc);
            bool carries = RoadTerrainModifier.CarriesCurrentRoads(tc);
            Assert.True(carries);
            Assert.Equal(ServerBakePlanner.WriteReport.NothingToWrite,
                ServerBakePlanner.ReportOf(carries, RoadTerrainModifier.LastWriteOutcome));
            Assert.Null(tc.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));

            int applied = tc.m_nview.GetZDO().GetInt(RoadTerrainModifier.AppliedVersionHash);
            var saved = new[] { new ServerBakePlanner.Compiler(applied, 0, false, RoadTerrainModifier.CarriesCurrentRoads(zone, applied)) };
            Assert.Equal(ServerBakePlanner.Action.AlreadyCurrent,
                ServerBakePlanner.Decide(RoadSpatialGrid.RoadNetworkVersion, true, false, false, saved, 1));
            RoadTerrainModifier.LastWriteOutcome = RoadTerrainModifier.WriteOutcome.None;
            RoadTerrainModifier.BakeRoadTerrain(zone, hm, tc);
            Assert.Equal(RoadTerrainModifier.WriteOutcome.None, RoadTerrainModifier.LastWriteOutcome);
        }
        finally { RoadTerrainModifier.ResetForWorld(); RoadSpatialGrid.Clear(); }
    }

    private static readonly int Tree = "Beech1".GetStableHashCode();
    private static readonly int Wall = "stone_wall_1x1".GetStableHashCode();
    private static readonly HashSet<int> Vegetation = new() { Tree };
    /// <summary>A road's clear area at (10, 10).</summary>
    private static readonly VegetationClearing.ZoneAreas Areas = RoadSquare(new Vector2(10f, 10f), 2.4f);

    private static VegetationClearing.ZoneAreas RoadSquare(Vector2 centre, float radius) =>
        new(new List<ZoneSystem.ClearArea> { new(new Vector3(centre.x, 0f, centre.y), radius) });

    /// <summary>VegetationClearing.InsideClearArea for vegetation (not a rock) at a ground point.</summary>
    private static bool Inside(VegetationClearing.ZoneAreas areas, Vector2 p) =>
        VegetationClearing.InsideClearArea(areas, new Vector3(p.x, 0f, p.y), Tree, null);
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

    /// <summary>Generation's clear-area test and the server's answer alike: the game's strict square, on and either side of
    /// an edge, in a corner, on negative coordinates, at radius zero and for a NaN point.</summary>
    [Theory]
    [InlineData(10f, 10f, 2f, 10f, 10f, true)]
    [InlineData(10f, 10f, 2f, 11.999f, 10f, true)]
    [InlineData(10f, 10f, 2f, 12f, 10f, false)]
    [InlineData(10f, 10f, 2f, 8f, 10f, false)]
    [InlineData(10f, 10f, 2f, 8.001f, 10f, true)]
    [InlineData(10f, 10f, 2f, 10f, 12f, false)]
    [InlineData(10f, 10f, 2f, 11.9f, 11.9f, true)]
    [InlineData(-40f, -1200f, 2f, -38.001f, -1200f, true)]
    [InlineData(-40f, -1200f, 2f, -38f, -1200f, false)]
    [InlineData(-40f, -1200f, 2f, -40f, -1202f, false)]
    [InlineData(10f, 10f, 0f, 10f, 10f, false)]
    [InlineData(10f, 10f, 2f, float.NaN, 10f, false)]
    [InlineData(10f, 10f, 2f, 10f, float.NaN, false)]
    public void GenerationAndTheServerTestTheGamesSquareAlike(float cx, float cz, float r, float px, float pz, bool inside)
    {
        // One test for both (VegetationClearing.InsideClearArea), as a road's area; and a location's (the game's own).
        var square = new List<ZoneSystem.ClearArea> { new(new Vector3(cx, 0f, cz), r) };
        Assert.Equal(inside, Inside(new VegetationClearing.ZoneAreas(square), new Vector2(px, pz)));
        Assert.Equal(inside, VegetationClearing.InGameArea(new VegetationClearing.ZoneAreas(square, 1, 1), new Vector2(px, pz)));
    }

    [Fact]
    public void TheClearAreaIsTheGamesStrictSquare()
    {
        // In the square's corner, outside the inscribed circle: generation
        // leaves it out, so clearing takes it.
        Assert.True(Inside(Areas, new Vector2(12.2f, 12.2f)));
        // On the edge: the game's test is strict.
        Assert.False(Inside(Areas, new Vector2(12.4f, 10f)));
    }

    /// <summary>Within a location's radius, where its parts cannot be read, the server cannot tell its own trees from the
    /// game's vegetation: it leaves them (VegetationClearing.Decide).</summary>
    [Fact]
    public void ObjectsWithinALocationWhosePartsAreUnknownAreLeftToTheLocation()
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
        var onTheBorder = RoadSquare(new Vector2(33f, 0f), 2.4f);
        var hutNextDoor = new List<VegetationClearing.Footprint> { new(new Vector2(31f, 0f), VegetationClearing.PlayerGroundRadius) };
        Assert.False(VegetationClearing.ShouldRemove(Tree, new Vector2(33f, 0f), false, Vegetation, onTheBorder, NoLocations, hutNextDoor));

        // A diagonal zone corner is the same story.
        var atTheCorner = RoadSquare(new Vector2(33f, 33f), 2.4f);
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
    private static VegetationClearing.ZoneAreas RoadAreas(Vector2s zone) => new(RoadClearAreaManager.GetOrCreateClearAreas(zone));

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

            VegetationClearing.ZoneAreas areas = RoadAreas(zone);
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
            VegetationClearing.ZoneAreas areas = RoadAreas(new Vector2s(2, -1));
            var vegetation = new HashSet<int> { SmallBeech };
            foreach (float side in new[] { -1f, 1f })
            {
                var middle = new Vector2(x + side * 0.43f * width, -65f);
                var edge = new Vector2(x + side * 0.48f * width, -65f);
                Assert.True(Inside(areas, middle));
                Assert.True(VegetationClearing.ShouldRemove(SmallBeech, middle, false, vegetation, areas, NoLocations, NoPlayerGround));
                Assert.False(Inside(areas, edge));
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
    private static bool PlacedOffTheRoad(Vector2s zone, Vector2 at, string prefab, float scale)
    {
        float yaw = 0f;
        return !RoadClearAreaManager.OnRoad(new VegetationClearing.ZoneAreas(RoadClearAreaManager.GetOrCreateClearAreas(zone), 0, int.MaxValue), RoadCentres(zone),
            new Vector3(at.x, 40f, at.y), prefab, scale, _ => Heightmap.Biome.Meadows, ref yaw);
    }

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
                Assert.False(Inside(RoadAreas(zone), edge));

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
            Assert.False(Inside(RoadAreas(zone), tree));
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
                     "shrub_2", "stubbe", "FirTree_oldLog", "SwampTree2_log", "Pickable_Stone", "Pickable_Branch" })
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
    /// vegetation pass never takes it. A boulder the rock clearing would leave -- a biome it does not clear --
    /// goes with the vegetation, as generation keeps it off the road.
    /// </summary>
    [Fact]
    public void ASmallStoneGoesWithTheVegetationAndALargeRockIsLeftToTheRockClearing()
    {
        var vegetation = new HashSet<int> { Tree, Stone, Boulder, MistlandsCliff };
        var centre = new Vector2(10f, 10f);
        System.Func<Vector3, Heightmap.Biome> In(Heightmap.Biome b) => _ => b;
        Assert.True(VegetationClearing.ShouldRemove(Stone, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            biomeAt: In(Heightmap.Biome.Mountain)));
        Assert.False(VegetationClearing.ShouldRemove(Boulder, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            biomeAt: In(Heightmap.Biome.Mountain)));
        Assert.False(VegetationClearing.ShouldRemove(MistlandsCliff, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            biomeAt: In(Heightmap.Biome.Mistlands)));
        Assert.True(VegetationClearing.ShouldRemove(Boulder, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            biomeAt: In(Heightmap.Biome.AshLands)));
        Assert.True(VegetationClearing.ShouldRemove(Boulder, centre, false, vegetation, Areas, NoLocations, NoPlayerGround,
            biomeAt: In(Heightmap.Biome.DeepNorth)));
        foreach (string rock in RoadRockPolicy.KnownRocks)
        {
            Assert.True(VegetationClearing.IsRock(rock.GetStableHashCode()), rock);
        }
        Assert.False(VegetationClearing.IsRock(Stone));
        Assert.False(VegetationClearing.IsRock(Tree));
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
            VegetationClearing.ZoneAreas areas = RoadAreas(zone);
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
                    Assert.True(Inside(areas, point), $"({point.x:F2}, {point.y:F2}) on the road is outside its clear areas");
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
        using var scope = new ValheimWorldScope().WithWorld(world).WithZdos().WithNetwork(server: true);
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
