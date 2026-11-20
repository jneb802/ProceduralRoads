using System.Collections.Generic;
using Xunit;
namespace ProceduralRoads.Tests;
public class RoadRockPolicyTests
{
    [Theory]
    [InlineData("rock1_mountain")]
    [InlineData("rock2_mountain")]
    [InlineData("rock3_mountain")]
    [InlineData("rock3_mountain_1")]
    [InlineData("rock2_heath")]
    [InlineData("rock4_heath")]
    [InlineData("rock4_forest")]
    [InlineData("Rock_3")]
    [InlineData("Rock_4")]
    [InlineData("Rock_4_plains")]
    [InlineData("rock4_coast")]
    [InlineData("HeathRockPillar")]
    public void NaturalBouldersClearOnlyInRequestedBiomes(string name)
    {
        // All the land biomes these boulders grow in; Mistlands rocks have
        // their own path, Ashlands and Deep North
        // and the sea are left alone.
        foreach (var biome in new[] { Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.BlackForest,
            Heightmap.Biome.Meadows, Heightmap.Biome.Swamp })
        {
            Assert.True(RoadRockPolicy.CanClear(name,true,biome,false,false,true,true,true));
            Assert.False(RoadRockPolicy.CanClear(name,false,biome,false,false,true,true,true));
            Assert.False(RoadRockPolicy.CanClear(name,true,biome,true,false,true,true,true));
            Assert.False(RoadRockPolicy.CanClear(name,true,biome,false,true,true,true,true));
        }
        foreach (var biome in new[] { Heightmap.Biome.None, Heightmap.Biome.Ocean, Heightmap.Biome.DeepNorth,
            Heightmap.Biome.AshLands, Heightmap.Biome.Mistlands })
            Assert.False(RoadRockPolicy.CanClear(name,true,biome,false,false,true,true,true));
    }

    /// <summary>A large rock on the road is the rock clearing's alone: Off keeps it, Carve carves it (whole when small),
    /// Remove removes it whole.</summary>
    [Fact]
    public void TheRockClearingSettingDecidesALargeRockOnTheRoad()
    {
        Assert.True(RoadRockPolicy.IsClearableRock("rock1_mountain"));
        Assert.False(RoadRockPolicy.IsClearableRock("Pickable_Stone"));
        Assert.False(RoadRockPolicy.Clears(RockClearingMode.Off));
        Assert.True(RoadRockPolicy.Clears(RockClearingMode.Carve) && RoadRockPolicy.Carves(RockClearingMode.Carve));
        Assert.True(RoadRockPolicy.Clears(RockClearingMode.Remove) && !RoadRockPolicy.Carves(RockClearingMode.Remove));
    }

    /// <summary>
    /// A zone generated with the road: PlaceVegetation asks the clear areas per vegetation entry. The road's keep trees
    /// and small stones off it, but a large rock the rock clearing takes there is placed as in a world without roads (the
    /// setting then carves or removes it). A location's own clear area, which comes first, still keeps it out.
    /// </summary>
    [Fact]
    public void AZoneGeneratedWithTheRoadKeepsItsLargeRocksForTheRockClearing()
    {
        var location = new ZoneSystem.ClearArea(new UnityEngine.Vector3(0f, 0f, 0f), 6f);
        var road = new ZoneSystem.ClearArea(new UnityEngine.Vector3(20f, 0f, 0f), 1.7f);
        var areas = new System.Collections.Generic.List<ZoneSystem.ClearArea> { location, road };
        var onRoad = new UnityEngine.Vector3(20f, 0f, 0.5f);
        var inLocation = new UnityEngine.Vector3(1f, 0f, 1f);
        System.Func<UnityEngine.Vector3, Heightmap.Biome> mountain = _ => Heightmap.Biome.Mountain;
        System.Func<UnityEngine.Vector3, Heightmap.Biome> mistlands = _ => Heightmap.Biome.Mistlands;
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 1, 2, onRoad, "Beech1", RockClearingMode.Carve, mountain));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 1, 2, onRoad, "Pickable_Stone", RockClearingMode.Carve, mountain));
        foreach (string rock in RoadRockPolicy.KnownRocks)
        {
            var biome = RoadRockPolicy.IsNaturalBoulder(rock) ? mountain : mistlands;
            Assert.False(RoadClearAreaManager.InsideClearArea(areas, 1, 2, onRoad, rock, RockClearingMode.Carve, biome), rock);
            Assert.False(RoadClearAreaManager.InsideClearArea(areas, 1, 2, onRoad, rock, RockClearingMode.Remove, biome), rock);
            Assert.True(RoadClearAreaManager.InsideClearArea(areas, 1, 2, inLocation, rock, RockClearingMode.Carve, biome), rock);
        }
        // Before the roads add theirs, every area is the game's.
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, int.MaxValue, int.MaxValue, onRoad, "rock1_mountain", RockClearingMode.Carve, mountain));
    }

    /// <summary>
    /// A rock the rock clearing would leave on the road -- clearing Off, a boulder in a biome it does not clear (Ashlands,
    /// Deep North), a Mistlands rock outside the Mistlands -- is kept off the road at generation as vegetation is, or it
    /// stands in the road for good.
    /// </summary>
    [Fact]
    public void ARockTheRockClearingWouldLeaveIsKeptOffTheRoadAtGeneration()
    {
        var road = new ZoneSystem.ClearArea(new UnityEngine.Vector3(20f, 0f, 0f), 1.7f);
        var areas = new System.Collections.Generic.List<ZoneSystem.ClearArea> { road };
        var onRoad = new UnityEngine.Vector3(20f, 0f, 0.5f);
        System.Func<UnityEngine.Vector3, Heightmap.Biome> In(Heightmap.Biome b) => _ => b;
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onRoad, "rock1_mountain", RockClearingMode.Off, In(Heightmap.Biome.Mountain)));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onRoad, "rock4_coast", RockClearingMode.Carve, In(Heightmap.Biome.AshLands)));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onRoad, "rock4_coast", RockClearingMode.Carve, In(Heightmap.Biome.DeepNorth)));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onRoad, "rock_mistlands1", RockClearingMode.Carve, In(Heightmap.Biome.Plains)));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onRoad, "cliff_mistlands1", RockClearingMode.Off, In(Heightmap.Biome.Mistlands)));
        Assert.False(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onRoad, "rock4_coast", RockClearingMode.Carve, In(Heightmap.Biome.Meadows)));
        Assert.False(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onRoad, "cliff_mistlands1", RockClearingMode.Carve, In(Heightmap.Biome.Mistlands)));
    }

    /// <summary>
    /// A staircase's or bridge's clear area keeps every rock out at generation, whatever the rock clearing would do: a
    /// boulder inside a structure's footprint is not the road's to carve.
    /// </summary>
    [Fact]
    public void AStructuresClearAreaKeepsEveryRockOutAtGeneration()
    {
        var road = new ZoneSystem.ClearArea(new UnityEngine.Vector3(20f, 0f, 0f), 1.7f);
        var stair = new ZoneSystem.ClearArea(new UnityEngine.Vector3(40f, 0f, 0f), 3f);
        var areas = new System.Collections.Generic.List<ZoneSystem.ClearArea> { road, stair };
        var onStair = new UnityEngine.Vector3(40f, 0f, 1f);
        foreach (string rock in RoadRockPolicy.KnownRocks)
            foreach (var mode in new[] { RockClearingMode.Carve, RockClearingMode.Remove, RockClearingMode.Off })
                Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, onStair, rock, mode,
                    _ => RoadRockPolicy.IsNaturalBoulder(rock) ? Heightmap.Biome.Mountain : Heightmap.Biome.Mistlands), rock);
    }

    /// <summary>The rule the generation and server clearings ask is the automatic pass's own: CanClear and
    /// CanClearMistlands for a rock no player or location holds, under a setting that clears.</summary>
    [Fact]
    public void ClearingTakesIsTheAutomaticPassRule()
    {
        var biomes = new[] { Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean };
        foreach (string rock in RoadRockPolicy.KnownRocks)
            foreach (var biome in biomes)
            {
                bool pass = RoadRockPolicy.CanClear(rock, true, biome, false, false, true, true, true) ||
                    RoadRockPolicy.CanClearMistlands(rock, RoadRockPolicy.PassClearsMistlandsRocks, RoadRockPolicy.PassClearsMistlandsCliffs,
                        true, biome, false, false, true, true, true);
                Assert.Equal(pass, RoadRockPolicy.ClearingTakes(rock, biome, RockClearingMode.Carve));
                Assert.Equal(pass, RoadRockPolicy.ClearingTakes(rock, biome, RockClearingMode.Remove));
                Assert.False(RoadRockPolicy.ClearingTakes(rock, biome, RockClearingMode.Off));
            }
    }

    /// <summary>
    /// Copper and silver deposits are carved like boulders (this replaces "resource rocks stay").
    /// Each is a Destructible whose first hit swaps it for a networked fractured MineRock5, which the carve handles
    /// as it does a boulder; tin is a MineRock_Tin with no fractured version and stays.
    /// </summary>
    [Theory]
    [InlineData("rock4_copper", Heightmap.Biome.BlackForest)]
    [InlineData("silvervein", Heightmap.Biome.Mountain)]
    [InlineData("rock3_silver", Heightmap.Biome.Mountain)]
    public void CopperAndSilverDepositsAreCarvedLikeBoulders(string name, Heightmap.Biome home)
    {
        Assert.True(RoadRockPolicy.IsClearableRock(name));
        Assert.Contains(name, RoadRockPolicy.KnownRocks);
        Assert.True(RoadRockPolicy.CanClear(name, true, home, false, false, true, true, true));
        Assert.False(RoadRockPolicy.CanClear(name, false, home, false, false, true, true, true));
        Assert.False(RoadRockPolicy.CanClear(name, true, home, true, false, true, true, true));
        Assert.False(RoadRockPolicy.CanClear(name, true, home, false, true, true, true, true));
        Assert.True(RoadRockPolicy.ClearingTakes(name, home, RockClearingMode.Carve));
        Assert.False(RoadRockPolicy.ClearingTakes(name, home, RockClearingMode.Off));
        Assert.False(RoadRockPolicy.ClearingTakes(name, Heightmap.Biome.Ocean, RockClearingMode.Carve));
        // Placed with the zone, left to the rock clearing rather than kept off the road with the vegetation.
        Assert.True(VegetationClearing.LeftToRockClearing(name.GetStableHashCode(), home, RockClearingMode.Carve));
        Assert.True(ServerBakePlanner.BringsRockAlive(name, playerCreated: false));
        // A fractured deposit (carved or mined) is judged as the deposit that fractures into it.
        var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
            { [name + "_frac"] = new() { name } };
        Assert.Equal(new[] { name }, RoadRockPolicy.PolicyNames(name + "_frac", map));
    }

    /// <summary>
    /// Only a deposit's chunks in the road are taken: carving cuts a deposit whatever its size (a small boulder goes
    /// whole), and a deposit is never removed whole, under the Remove setting or where the carve cannot cut it.
    /// </summary>
    [Theory]
    [InlineData("rock4_copper")]
    [InlineData("silvervein")]
    [InlineData("rock3_silver")]
    public void ADepositInTheRoadLosesOnlyItsChunksInTheRoad(string name)
    {
        Assert.True(RoadRockPolicy.IsResourceDeposit(name));
        Assert.False(RoadRockPolicy.CarveRemovesWhole(name, 3f));
        Assert.False(RoadRockPolicy.CarveRemovesWhole(name, RoadRockPolicy.CarveMinSize + 1f));
        Assert.False(RoadRockPolicy.MayRemoveWhole(name));
        foreach (string boulder in new[] { "rock1_mountain", "rock4_forest", "Rock_4" })
        {
            Assert.False(RoadRockPolicy.IsResourceDeposit(boulder));
            Assert.True(RoadRockPolicy.CarveRemovesWhole(boulder, 3f));
            Assert.False(RoadRockPolicy.CarveRemovesWhole(boulder, RoadRockPolicy.CarveMinSize + 1f));
            Assert.True(RoadRockPolicy.MayRemoveWhole(boulder));
        }
        // A fractured deposit is judged as its deposit.
        var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
            { [name + "_frac"] = new() { name } };
        Assert.Contains(RoadRockPolicy.PolicyNames(name + "_frac", map), RoadRockPolicy.IsResourceDeposit);
    }

    [Fact]
    public void TinIsNotCarved()
    {
        Assert.False(RoadRockPolicy.IsClearableRock("MineRock_Tin"));
        Assert.DoesNotContain("MineRock_Tin", RoadRockPolicy.KnownRocks);
        foreach (var biome in new[] { Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain })
            Assert.False(RoadRockPolicy.ClearingTakes("MineRock_Tin", biome, RockClearingMode.Carve));
    }

    [Theory]
    [InlineData("MineRock_Obsidian")]
    [InlineData("MineRock_Tin")]
    [InlineData("MineRock_Copper")]
    [InlineData("rock4_copper_frac")]
    [InlineData("silvervein_frac")]
    [InlineData("rock3_silver_frac")]
    [InlineData("rock1_mountain_frac")]
    [InlineData("rock2_heath_frac")]
    [InlineData("rock4_forest_frac")]
    [InlineData("Rock_3_frac")]
    [InlineData("Rock_3_deepnorth")]
    [InlineData("RockDolmen_1")]
    [InlineData("rock_mistlands1")]
    [InlineData("rock_3")]
    [InlineData("wood_floor")]
    public void TinFragmentsAndUnrecognisedSceneryStay(string name) =>
        Assert.False(RoadRockPolicy.CanClear(name,true,Heightmap.Biome.BlackForest,false,false,true,true,true));

    [Theory]
    [InlineData("rock1_mountain")]
    [InlineData("rock2_heath")]
    [InlineData("rock4_forest")]
    [InlineData("Rock_3")]
    public void ARemoteOrNotYetValidRockIsLeftForItsOwner(string name)
    {
        Assert.False(RoadRockPolicy.CanClear(name,true,Heightmap.Biome.BlackForest,false,false,true,true,false));
        Assert.False(RoadRockPolicy.CanClear(name,true,Heightmap.Biome.BlackForest,false,false,true,false,true));
        Assert.True(RoadRockPolicy.CanClear(name,true,Heightmap.Biome.BlackForest,false,false,false,false,false));
    }

    [Theory]
    [InlineData("rock_mistlands1", true, false)]
    [InlineData("rock_mistlands2", true, false)]
    [InlineData("cliff_mistlands1", false, true)]
    [InlineData("cliff_mistlands2", false, true)]
    public void MistlandsRocksAndCliffsClearSeparatelyOnlyInTheMistlands(string name, bool rocks, bool cliffs)
    {
        Assert.True(RoadRockPolicy.CanClearMistlands(name,rocks,cliffs,true,Heightmap.Biome.Mistlands,false,false,true,true,true));
        Assert.True(RoadRockPolicy.CanClearMistlands(name,true,true,true,Heightmap.Biome.Mistlands,false,false,true,true,true));
        Assert.False(RoadRockPolicy.CanClearMistlands(name,cliffs,rocks,true,Heightmap.Biome.Mistlands,false,false,true,true,true));
        Assert.False(RoadRockPolicy.CanClearMistlands(name,false,false,true,Heightmap.Biome.Mistlands,false,false,true,true,true));
        Assert.False(RoadRockPolicy.CanClearMistlands(name,true,true,true,Heightmap.Biome.Mountain,false,false,true,true,true));
        Assert.False(RoadRockPolicy.CanClearMistlands(name,true,true,false,Heightmap.Biome.Mistlands,false,false,true,true,true));
        Assert.False(RoadRockPolicy.CanClearMistlands(name,true,true,true,Heightmap.Biome.Mistlands,true,false,true,true,true));
        Assert.False(RoadRockPolicy.CanClearMistlands(name,true,true,true,Heightmap.Biome.Mistlands,false,true,true,true,true));
        Assert.False(RoadRockPolicy.CanClearMistlands(name,true,true,true,Heightmap.Biome.Mistlands,false,false,true,true,false));
        Assert.False(RoadRockPolicy.CanClear(name,true,Heightmap.Biome.Mistlands,false,false,true,true,true));
    }

    [Theory]
    [InlineData("rock1_mountain")]
    [InlineData("YggaShoot1")]
    [InlineData("Dverger")]
    [InlineData("cliff_mistlands1_creep")]
    public void TheMistlandsPathTakesNothingElse(string name) =>
        Assert.False(RoadRockPolicy.CanClearMistlands(name,true,true,true,Heightmap.Biome.Mistlands,false,false,true,true,true));

    [Theory]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(4, false)] [InlineData(5, false)]
    public void ARockBlockingUnderHalfTheRoadIsKept(int blocked, bool keep) =>
        Assert.Equal(keep, RoadRockPolicy.KeepPartial(blocked, 5));

    [Theory]
    [InlineData("rock4_forest")] [InlineData("Rock_3")] [InlineData("rock2_heath")]
    public void BouldersClearInMeadowsAndSwampToo(string name)
    {
        Assert.True(RoadRockPolicy.CanClear(name, true, Heightmap.Biome.Meadows, false, false, true, true, true));
        Assert.True(RoadRockPolicy.CanClear(name, true, Heightmap.Biome.Swamp, false, false, true, true, true));
        Assert.False(RoadRockPolicy.CanClear(name, true, Heightmap.Biome.Ocean, false, false, true, true, true));
    }

    [Fact]
    public void AFracturedRockIsJudgedAsTheRocksThatFractureIntoIt()
    {
        // A carved rock is its fractured copy; the next clearing pass must still
        // recognise it, or chunks left in the road are never finished (four
        // carved rocks once read "not a listed natural boulder").
        var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
        {
            ["rock1_mountain_frac"] = new() { "rock1_mountain" },
            ["rock4_forest_frac"] = new() { "rock4_forest", "rock_mistlands2" },
        };
        Assert.Equal(new[] { "rock1_mountain" }, RoadRockPolicy.PolicyNames("rock1_mountain_frac", map));
        Assert.Equal(new[] { "rock4_forest", "rock_mistlands2" }, RoadRockPolicy.PolicyNames("rock4_forest_frac", map));
        Assert.Equal(new[] { "rock4_copper_frac" }, RoadRockPolicy.PolicyNames("rock4_copper_frac", map));
        Assert.Equal(new[] { "rock2_mountain" }, RoadRockPolicy.PolicyNames("rock2_mountain", map));
        Assert.True(RoadRockPolicy.CanClear(RoadRockPolicy.PolicyNames("rock1_mountain_frac", map)[0], true,
            Heightmap.Biome.Mountain, false, false, true, true, true));
    }

    [Fact]
    public void ClearanceFacesAlongTheRoad()
    {
        // The clearance box is turned to the road's heading: toward the nearest
        // other stored point, ignoring duplicates (points are stored once per
        // grid cell they touch) and anything beyond 3 m.
        var here = new RoadSpatialGrid.RoadPoint(new UnityEngine.Vector2(10f, 10f), 4f, 0f);
        var all = new System.Collections.Generic.List<RoadSpatialGrid.RoadPoint>
        {
            here,
            new(new UnityEngine.Vector2(10.1f, 10f), 4f, 0f),   // a duplicate of this point
            new(new UnityEngine.Vector2(10f, 11f), 4f, 0f),     // the next point, north
            new(new UnityEngine.Vector2(14f, 10f), 4f, 0f),     // too far
        };
        var dir = RoadRockPolicy.Heading(here, all);
        Assert.Equal(0f, dir.x, 3);
        Assert.Equal(1f, dir.y, 3);
    }

    /// <summary>A 4 m road's clearance: its solid paint, 3.4 m.</summary>
    private static readonly float Clearance4m = 4f * RoadConstants.RoadPaintOuterRatio;

    private static System.Collections.Generic.List<int> Open(float width, params int[][] strips)
    {
        var blockers = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyCollection<int>>();
        foreach (var s in strips) blockers.Add(s);
        return RoadRockPolicy.ChunksInTheWay(blockers, width, new System.Collections.Generic.HashSet<int>());
    }

    /// <summary><paramref name="count"/> strips; chunk <paramref name="chunk"/> in strips <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static int[][] Strips(int count, params (int chunk, int from, int to)[] rocks)
    {
        var strips = new int[count][];
        for (int s = 0; s < count; s++)
        {
            var here = new System.Collections.Generic.List<int>();
            foreach (var (chunk, from, to) in rocks) if (s >= from && s <= to) here.Add(chunk);
            strips[s] = here.ToArray();
        }
        return strips;
    }

    [Fact]
    public void ACarveLeavesTheSmallestWayAPlayerFitsThrough()
    {
        // The smallest possible hole in the rock the player can
        // reliably fit through. The player is 0.98 m wide; the way left is 1.5 m.
        Assert.True(RoadRockPolicy.MinOpening > RoadRockPolicy.PlayerWidth + 0.4f);
        Assert.True(RoadRockPolicy.ClearanceHeight > RoadRockPolicy.PlayerHeight);
        // A 4 m road's 3.4 m clearance in 20 strips of 0.17 m: the way is the 10
        // strips reaching within 0.75 m of the centre line, 1.7 m, as wide as
        // the open run it was, and centred.
        Assert.Equal((5, 14), RoadRockPolicy.WayStrips(20, Clearance4m));
        var rock = new int[20][];
        for (int s = 0; s < 20; s++) rock[s] = new[] { s };
        var taken = Open(Clearance4m, rock);
        Assert.Equal(new[] { 9, 10, 8, 11, 7, 12, 6, 13, 5, 14 }, taken);   // from the middle out
        // A clearance no wider than the way is opened in full.
        Assert.Equal((0, 19), RoadRockPolicy.WayStrips(20, 1.5f));
        Assert.Equal((0, 9), RoadRockPolicy.WayStrips(10, 1f));
        // The way is centred on every width a road or staircase has.
        for (float w = 1.6f; w <= 8f; w += 0.1f)
        {
            var (first, last) = RoadRockPolicy.WayStrips(20, w);
            Assert.Equal(19 - last, first);
            Assert.True((last - first + 1) * w / 20 >= RoadRockPolicy.MinOpening - 1e-3f);
        }
    }

    [Fact]
    public void ARockOnTheCentreLineLosesItsChunksThereThoughTheRoadBesideItIsOpen()
    {
        // in game: copper deposits kept chunks 0.3-2 m over the road's
        // centre line, the other half of the road open beside them. Chunk 1
        // reaches from the left edge to the centre line (strips 0-9 of 20),
        // chunk 2 from the right edge to 0.5 m short of it (strips 13-19).
        var taken = Open(Clearance4m, Strips(20, (1, 0, 9), (2, 13, 19)));
        Assert.Equal(new[] { 1, 2 }, taken);
        // Alone, with the right half of the road open, chunk 1 still goes.
        Assert.Equal(new[] { 1 }, Open(Clearance4m, Strips(20, (1, 0, 9))));
    }

    [Fact]
    public void AnEdgeIntrusionBesideTheWayStays()
    {
        // Chunk 1 covers the left 5 of 20 strips, chunk 2 the right 4: both
        // stop short of the way, nothing goes.
        Assert.Empty(Open(Clearance4m, Strips(20, (1, 0, 4), (2, 16, 19))));
        // One strip into the way is enough to go.
        Assert.Equal(new[] { 1 }, Open(Clearance4m, Strips(20, (1, 0, 5))));
    }

    [Fact]
    public void AChunkAlreadyTakenIsNotTakenAgain()
    {
        var removed = new System.Collections.Generic.HashSet<int> { 1 };
        var blockers = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyCollection<int>>();
        foreach (var s in Strips(20, (1, 8, 11), (2, 10, 10))) blockers.Add(s);
        Assert.Equal(new[] { 2 }, RoadRockPolicy.ChunksInTheWay(blockers, Clearance4m, removed));
        Assert.Equal(2, removed.Count);
    }

    [Fact]
    public void AWholeRockStandingInTheWayIsNotPutBack()
    {
        // in game: two copper deposits were swapped for their fractured
        // copies, no chunk of either was in the way, and the whole rock was put
        // back with its collider 0.3-2 m over the road's centre line.
        Assert.False(RoadRockPolicy.RestoresWholeRock(0, wholeRockInTheWay: true));
        Assert.True(RoadRockPolicy.RestoresWholeRock(0, wholeRockInTheWay: false));
        Assert.False(RoadRockPolicy.RestoresWholeRock(3, wholeRockInTheWay: false));
    }

    [Fact]
    public void AZoneBeingGeneratedProbesTheRoadNextDoorAtTheRoadsHeight()
    {
        // Loaded zones: no ground means the terrain is not built yet; skip, look again.
        Assert.False(RoadRockPolicy.ClearanceSurface(false, 0f, 139.7f, false, out _));
        // A zone generated for a peer has only its own terrain and is never looked
        // at again: a point next door stands on the road's stored height.
        Assert.True(RoadRockPolicy.ClearanceSurface(false, 0f, 139.7f, true, out float surface));
        Assert.Equal(139.7f, surface);
        // Ground, where there is any, always wins.
        Assert.True(RoadRockPolicy.ClearanceSurface(true, 141f, 139.7f, true, out surface));
        Assert.Equal(141f, surface);
        Assert.True(RoadRockPolicy.ClearanceSurface(true, 141f, 139.7f, false, out surface));
        Assert.Equal(141f, surface);
    }

    private static UnityEngine.Vector2 V(float x, float z) => new UnityEngine.Vector2(x, z);

    [Fact]
    public void ARockReachingOutOfItsZoneWidensTheProbe()
    {
        // Measured on a server-generated world: rock1_mountain rooted at
        // (9374.75, 1452.07), zone (146,23), collider x 9363.25-9394.85,
        // z 1440.77-1463.87, over the road centre at (9376.53, 1442.44) ..
        // (9383.27, 1458.90), which lie in zone (147,23): outside the terrain
        // its zone is generated with. It was never judged.
        var rockZone = ZoneSystem.GetZone(new UnityEngine.Vector3(9374.75f, 0f, 1452.07f));
        Assert.Equal(new Vector2s(146, 23), rockZone);
        var c3 = ZoneSystem.GetZonePos(rockZone);
        var centre = V(c3.x, c3.z);
        foreach (var p in new[] { V(9376.53f, 1442.44f), V(9383.27f, 1458.90f) })
            Assert.NotEqual(rockZone, ZoneSystem.GetZone(new UnityEngine.Vector3(p.x, 0f, p.y)));
        float reach = RoadRockPolicy.OwnObjectsReach(centre, new[] { (V(9363.25f, 1440.77f), V(9394.85f, 1463.87f)) }, 46f, 4f);
        Assert.True(reach > 60f && reach < 66f, $"reach {reach}");
        // A zone whose rocks all stand well inside keeps the corner radius.
        Assert.Equal(46f, RoadRockPolicy.OwnObjectsReach(centre, new[] { (V(9330f, 1460f), V(9350f, 1480f)) }, 46f, 4f));
        Assert.Equal(46f, RoadRockPolicy.OwnObjectsReach(centre, new (UnityEngine.Vector2, UnityEngine.Vector2)[0], 46f, 4f));
    }

    [Fact]
    public void TheRoadUnderARockBeyondTheCornerRadiusIsReached()
    {
        // The second measured rock: rooted at (9062.70, 1189.81), zone (142,19),
        // over the road at (9066.65, 1174.68) and (9058.61, 1178.70), zone
        // (142,18): 46.5 and 47.5 m from its zone's centre, beyond the 46 m the
        // pass looked. Its collider holds at least its root and reaches those
        // points (the smallest bounds that can be).
        var rockZone = ZoneSystem.GetZone(new UnityEngine.Vector3(9062.70f, 0f, 1189.81f));
        Assert.Equal(new Vector2s(142, 19), rockZone);
        var c3 = ZoneSystem.GetZonePos(rockZone);
        var centre = V(c3.x, c3.z);
        var road = new[] { V(9066.65f, 1174.68f), V(9058.61f, 1178.70f) };
        foreach (var p in road) Assert.True((p - centre).magnitude > 46f);
        float reach = RoadRockPolicy.OwnObjectsReach(centre, new[] { (V(9058.61f, 1174.68f), V(9066.65f, 1189.81f)) }, 46f, 4f);
        foreach (var p in road) Assert.True((p - centre).magnitude <= reach, $"{p.x},{p.y} beyond {reach}");
    }

    [Fact]
    public void EveryRoadPointWhoseClearanceMeetsTheBoundsIsWithinReach()
    {
        // The clearance is paint-wide (0.85 of the width) and 1.2 m long: for
        // roads up to 8 m it lies within 4 m of its point, so a point whose
        // clearance can touch the bounds is within 4 m of them.
        var rng = new System.Random(7);
        var centre = V(0f, 0f);
        for (int n = 0; n < 2000; n++)
        {
            float x0 = (float)(rng.NextDouble() * 64 - 32), z0 = (float)(rng.NextDouble() * 64 - 32);
            float x1 = x0 + (float)(rng.NextDouble() * 35), z1 = z0 + (float)(rng.NextDouble() * 35);
            float reach = RoadRockPolicy.OwnObjectsReach(centre, new[] { (V(x0, z0), V(x1, z1)) }, 46f, 4f);
            float w = (float)(1 + rng.NextDouble() * 7);
            float corner = (float)System.Math.Sqrt(System.Math.Pow(w * 0.5f * RoadConstants.RoadPaintOuterRatio, 2) + 0.36);
            Assert.True(corner <= 4f);
            // A point just touching the bounds from outside, anywhere round them.
            float px = (float)(x0 - corner + rng.NextDouble() * (x1 - x0 + 2 * corner));
            float pz = (float)(z0 - corner + rng.NextDouble() * (z1 - z0 + 2 * corner));
            Assert.True(V(px, pz).magnitude <= reach + 1e-3f, $"({px},{pz}) beyond {reach}");
        }
    }

    [Fact]
    public void EveryKnownRockWidensTheProbeAndNothingElse()
    {
        foreach (string name in RoadRockPolicy.KnownRocks) Assert.True(RoadRockPolicy.IsClearableRock(name), name);
        foreach (string name in new[] { "rock1_mountain_frac", "MineRock_Tin", "Beech1", "cliff_mistlands1_creep" })
            Assert.False(RoadRockPolicy.IsClearableRock(name), name);
    }

    /// <summary>
    /// Rocks in a zone explored before the road network are the players' landscape and are never carved or removed,
    /// by the zone's own pass or by a neighbour's that reaches over the border. The physics pass itself
    /// (RoadRockClearing, RoadRockProbe) asks this one question; it cannot run offline, so it is checked in game.
    /// </summary>
    [Fact]
    public void ACutoffZoneKeepsItsRocks()
    {
        using var world = new Valheim.Testing.Doubles.ValheimWorldScope().WithZoneSystem();
        NetworkCutoff.Forget();
        try
        {
            NetworkCutoff.SnapshotWorld(new[] { new Vector2s(0, 0) });
            NetworkCutoff.UseLoadedNetwork(7, null);
            Assert.False(RoadRockPolicy.MayTouch(new UnityEngine.Vector3(10f, 30f, -20f)), "a boulder in the explored zone");
            Assert.False(RoadRockPolicy.MayTouch(new UnityEngine.Vector3(-31f, 30f, 31f)), "its corner");
            Assert.True(RoadRockPolicy.MayTouch(new UnityEngine.Vector3(70f, 30f, 0f)), "a boulder next door, generated with the network");
        }
        finally { NetworkCutoff.Forget(); }
    }

    /// <summary>
    /// A fractured rock as boxes on flat ground at height 0, each box its own real shape:
    /// x, y (up) and z from-to. A box stands on the ground with its bottom within
    /// GroundTouch of 0; two touch when no gap between them on any axis is over FragmentTouch.
    /// </summary>
    private static int[] CutOff(float[][] boxes, int[] standing, int[] taken)
    {
        bool Touch(int a, int b)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float gap = System.Math.Max(boxes[a][2 * axis], boxes[b][2 * axis]) - System.Math.Min(boxes[a][2 * axis + 1], boxes[b][2 * axis + 1]);
                if (gap > RoadRockPolicy.FragmentTouch) return false;
            }
            return true;
        }
        var cut = RoadRockPolicy.CutOff(standing, new System.Collections.Generic.HashSet<int>(taken), i => boxes[i][2] <= RoadRockPolicy.GroundTouch, Touch);
        cut.Sort();
        return cut.ToArray();
    }

    private static float[] Box(float x0, float x1, float y0, float y1) => new[] { x0, x1, y0, y1, 0f, 1f };

    /// <summary>
    /// A fractured rock carved from below: the road takes its bottom chunks (0 and 3).
    /// </summary>
    private static readonly float[][] Carved =
    {
        Box(0f, 2f, 0f, 1f),      // 0: in the road, on the ground
        Box(0f, 2f, 1.05f, 2f),   // 1: sat on 0 alone
        Box(0f, 2f, 2f, 3f),      // 2: on 1
        Box(3f, 5f, 0f, 1.5f),    // 3: in the road
        Box(3f, 5f, 1.5f, 2.5f),  // 4: over 3, hanging from 5 above it
        Box(3f, 5f, 2.55f, 3.5f), // 5: leaning on 6 from the side
        Box(5.05f, 6f, 0f, 3.5f), // 6: a pillar on the ground beside the road
        Box(8f, 9f, 0.2f, 1f),    // 7: on the ground on its own
        Box(12f, 13f, 5f, 6f),    // 8: never on the ground: perched on a rock not in this list
        Box(2.05f, 2.5f, 0f, 2f), // 9: another rock's chunk, beside 1
    };
    private static readonly int[] CarvedRock = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
    private static readonly int[] Road = { 0, 3 };

    [Fact]
    public void AChunkLeftTouchingNothingGoesAndTheChainOnItWithIt()
    {
        // 1 touched only 0; 2 touches 1, which goes too, so 2 has nothing left either.
        Assert.Equal(new[] { 1, 2 }, CutOff(Carved, CarvedRock, Road));
        Assert.Empty(CutOff(Carved, CarvedRock, new int[0]));
    }

    [Fact]
    public void AChunkHangingFromOneAboveItIsKept()
    {
        // 4 lost 3 under it but hangs from 5, which leans on the pillar 6: both stay.
        var cut = CutOff(Carved, CarvedRock, Road);
        Assert.DoesNotContain(4, cut);
        Assert.DoesNotContain(5, cut);
        // Take the pillar as well and nothing holds them any more.
        Assert.Equal(new[] { 1, 2, 4, 5 }, CutOff(Carved, CarvedRock, new[] { 0, 3, 6 }));
    }

    [Fact]
    public void AChunkTouchingAStayingOneAtTheSideIsKept()
    {
        // The same rock with 9 as its own: 1 touches it at the side, and 2 rests on 1.
        Assert.Empty(CutOff(Carved, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, Road));
    }

    [Fact]
    public void NothingOutsideTheCarvedRockIsTouched()
    {
        // 9 belongs to another rock: it holds nothing of this one up, and is never named.
        var cut = CutOff(Carved, CarvedRock, Road);
        Assert.Contains(1, cut);
        Assert.DoesNotContain(9, cut);
        // Chunks still joined to the ground, and one the world perched on another rock, stay.
        foreach (int kept in new[] { 6, 7, 8 }) Assert.DoesNotContain(kept, cut);
        // The chunks taken are the carve's, not cut off.
        foreach (int taken in Road) Assert.DoesNotContain(taken, cut);
    }

    /// <summary>A fragment as its eight corners: a quadrilateral in x (east) and y (up), its bottom edge x0..x1 at y0
    /// and its top edge x2..x3 at y1, the same from z0 to z1.</summary>
    private static UnityEngine.Vector3[] Slab(float x0, float x1, float y0, float x2, float x3, float y1, float z0, float z1) => new[]
    {
        new UnityEngine.Vector3(x0, y0, z0), new UnityEngine.Vector3(x1, y0, z0), new UnityEngine.Vector3(x0, y0, z1), new UnityEngine.Vector3(x1, y0, z1),
        new UnityEngine.Vector3(x2, y1, z0), new UnityEngine.Vector3(x3, y1, z0), new UnityEngine.Vector3(x2, y1, z1), new UnityEngine.Vector3(x3, y1, z1),
    };

    [Fact]
    public void FragmentsTouchWhereTheyShareACorner()
    {
        var corners = new System.Collections.Generic.Dictionary<int, UnityEngine.Vector3[]>
        {
            [0] = Slab(0f, 2f, 0f, 0f, 2f, 1f, 0f, 1f),
            [1] = Slab(0f, 2f, 1f + RoadRockPolicy.FragmentTouch * 0.5f, 0f, 2f, 2f, 0f, 1f),   // sat on 0
            [2] = Slab(0f, 2f, 2f + RoadRockPolicy.FragmentTouch * 2f, 0f, 2f, 3f, 0f, 1f),     // a gap over 1
        };
        var touching = RoadRockPolicy.TouchingFragments(corners);
        Assert.Equal(new[] { 1 }, touching[0]);
        Assert.Equal(new[] { 0 }, touching[1]);
        Assert.False(touching.ContainsKey(2));
    }

    /// <summary>
    /// In game: a fractured Mistlands cliff at (1806.7, 94.1, -6619.4), carved where a staircase's
    /// foot (1808, -6616) meets its road on ground at 92.7, kept slanted fragments hanging over the road with nothing
    /// under them. Here: the fragment in the way (0) is taken, a slanted fragment (2) rested on it alone, and a slanted
    /// fragment beside the way (1) stands on the ground 0.36 m from 2. Their boxes meet both ways, so a touch read from
    /// bounds held 2 up; read from their corners, nothing does, and 2 is cut off with the road's fragment.
    /// </summary>
    [Fact]
    public void AFragmentWhoseBoxOnlyReachesAStandingOneIsCutOff()
    {
        const float ground = 92.7f, z0 = -6620f, z1 = -6618f;
        var corners = new System.Collections.Generic.Dictionary<int, UnityEngine.Vector3[]>
        {
            [0] = Slab(1807f, 1809f, ground, 1807f, 1809f, 95.7f, z0, z1),         // in the way, on the ground
            [1] = Slab(1803f, 1805f, ground - 0.2f, 1805.5f, 1807.5f, 97.5f, z0, z1), // beside the way, leaning over it
            [2] = Slab(1807f, 1809f, 95.7f, 1809.5f, 1811.5f, 100.7f, z0, z1),     // on 0, leaning the same way
        };
        float Lowest(int i) { float y = float.MaxValue; foreach (var v in corners[i]) y = System.Math.Min(y, v.y); return y; }
        // A corner of fragment i inside the box of fragment boxOf, grown by FragmentTouch.
        bool CornerIn(int i, int boxOf)
        {
            float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
            foreach (var v in corners[boxOf])
            {
                float[] c = { v.x, v.y, v.z };
                for (int a = 0; a < 3; a++) { lo[a] = System.Math.Min(lo[a], c[a]); hi[a] = System.Math.Max(hi[a], c[a]); }
            }
            foreach (var v in corners[i])
            {
                float[] c = { v.x, v.y, v.z };
                bool inside = true;
                for (int a = 0; a < 3; a++)
                    inside &= c[a] >= lo[a] - RoadRockPolicy.FragmentTouch && c[a] <= hi[a] + RoadRockPolicy.FragmentTouch;
                if (inside) return true;
            }
            return false;
        }
        // The escape: each of 1 and 2 reaches into the other's box.
        Assert.True(CornerIn(2, 1), "a corner of 2 in the box of 1");
        Assert.True(CornerIn(1, 2), "a corner of 1 in the box of 2");

        var touching = RoadRockPolicy.TouchingFragments(corners);
        bool Touch(int a, int b) => touching.TryGetValue(a, out var t) && t.Contains(b);
        Assert.True(Touch(0, 2), "2 rests on 0");
        Assert.False(Touch(1, 2), "1 and 2 are a gap apart");
        var cut = RoadRockPolicy.CutOff(new[] { 0, 1, 2 }, new System.Collections.Generic.HashSet<int> { 0 },
            i => Lowest(i) <= ground + RoadRockPolicy.GroundTouch, Touch);
        Assert.Equal(new[] { 2 }, cut);
    }

    /// <summary>
    /// In game: a mountain boulder and a Mistlands cliff, each on a zone corner, carved before the zone next door loaded,
    /// kept chunks hanging with nothing under them ("+ 0 cut off"). A fragment's ground is read off the loaded terrain only
    /// where that zone's terrain is ready; anywhere else it is the finished ground. A column over a zone not loaded (no
    /// terrain read), or loaded but not yet cut (the ungraded ground right under the fragment), is judged on the finished
    /// ground the road cut leaves, and what the carve leaves joined to nothing goes; an arch still standing on the ground at
    /// both ends stays.
    /// </summary>
    [Fact]
    public void AFragmentOverAZoneNotReadyIsJudgedOnTheFinishedGround()
    {
        const float cut = 3f;   // how far the road's cut leaves the finished ground under the ungraded ground
        // Per fragment: its underside, and over which zone (ready or not) it stands.
        float[] underside = { 0f, 1f, 2f, 0f, 1.5f, 0f };
        bool[] readyZone = { true, true, false, true, true, false };
        bool[] answers = { true, true, false, true, true, true };   // a zone not loaded has no terrain to read
        const float terrain = 0f;  // the loaded ground, uncut where its zone is not ready
        float Finished(int i) => readyZone[i] ? terrain : terrain - cut;
        bool OnGround(int i) =>
            RoadRockPolicy.FragmentGround(readyZone[i], answers[i], terrain, Finished(i), out float g) && RoadRockPolicy.StandsOn(underside[i], g);
        // 0 in the way on the ground; 1 sits on 0 and leans on 2; 2 over a zone not loaded, joined to nothing else.
        // 3 and 5 hold up 4, an arch: 5 over a zone loaded but not yet cut, reaching a metre into the ground the cut leaves.
        underside[5] = terrain - cut - 1f;
        bool Touch(int a, int b) => (System.Math.Min(a, b), System.Math.Max(a, b)) is (0, 1) or (1, 2) or (3, 4) or (4, 5);
        var cutOff = RoadRockPolicy.CutOff(new[] { 0, 1, 2, 3, 4, 5 }, new System.Collections.Generic.HashSet<int> { 0 }, OnGround, Touch);
        cutOff.Sort();
        Assert.Equal(new[] { 1, 2 }, cutOff);

        // The ground each is judged on.
        Assert.True(RoadRockPolicy.FragmentGround(true, true, terrain, terrain - cut, out float g0) && g0 == terrain, "a ready zone: its terrain");
        Assert.True(RoadRockPolicy.FragmentGround(false, true, terrain, terrain - cut, out float g1) && g1 == terrain - cut, "a zone not cut yet: the finished ground");
        Assert.True(RoadRockPolicy.FragmentGround(false, false, 0f, terrain - cut, out float g2) && g2 == terrain - cut, "a zone not loaded: the finished ground");
        Assert.True(RoadRockPolicy.FragmentGround(true, false, 0f, terrain - cut, out float g3) && g3 == terrain - cut, "no terrain answers: the finished ground");
        Assert.False(RoadRockPolicy.FragmentGround(false, false, 0f, float.NaN, out _), "nothing answers: not judged");
        // Standing on the ground: within GroundTouch over it, or under it.
        Assert.True(RoadRockPolicy.StandsOn(terrain + RoadRockPolicy.GroundTouch, terrain));
        Assert.False(RoadRockPolicy.StandsOn(terrain + RoadRockPolicy.GroundTouch + 0.01f, terrain));
        Assert.True(RoadRockPolicy.StandsOn(terrain - cut, terrain));
        // Read as before (the uncut ground over a zone not ready, and no read where none answers, which held it up),
        // fragment 2 stood and held 1: nothing was cut off.
        bool Before(int i) => !answers[i] || RoadRockPolicy.StandsOn(underside[i], terrain);
        Assert.Empty(RoadRockPolicy.CutOff(new[] { 0, 1, 2, 3, 4, 5 }, new System.Collections.Generic.HashSet<int> { 0 }, Before, Touch));
    }

    /// <summary>How many carves one frame applies when each costs <paramref name="carveMs"/>, from the pass's queue.</summary>
    private static List<int> CarvesPerFrame(int queued, double carveMs, double budgetMs)
    {
        var frames = new List<int>();
        while (queued > 0)
        {
            int done = 0;
            double elapsed = 0;
            while (queued > 0 && RoadRockPolicy.CarvesMoreThisFrame(done, elapsed, budgetMs)) { elapsed += carveMs; done++; queued--; }
            frames.Add(done);
        }
        return frames;
    }

    /// <summary>
    /// Measured in world: a carve costs ~18 ms of the main thread, and a pass carving its rocks in its own frame held
    /// it for 57 to 131 ms. Spread over the frames after the pass under the bake's 8 ms frame budget, every frame
    /// carves one rock (a carve cannot be split), and cheap carves (a rock left whole) share a frame.
    /// </summary>
    [Fact]
    public void CarvesSpreadOneExpensiveRockPerFrame()
    {
        Assert.Equal(new List<int> { 1, 1, 1, 1, 1, 1, 1 }, CarvesPerFrame(7, 18.0, 8.0));
        Assert.Equal(new List<int> { 4, 4, 2 }, CarvesPerFrame(10, 2.0, 8.0));
        // The first carve of a frame always goes, so a queue never stalls behind one over the budget.
        Assert.True(RoadRockPolicy.CarvesMoreThisFrame(0, 50.0, 8.0));
        Assert.False(RoadRockPolicy.CarvesMoreThisFrame(1, 8.0, 8.0));
    }

    /// <summary>
    /// The game moves a StaticPhysics object only inside the active area round the player. Measured in an explored world:
    /// zones held 26 to 54 such objects outside it, and the pass waited for them every two seconds for hours. An object
    /// the game does not simulate never holds a zone; one it simulates still does, and so does one with no move left.
    /// </summary>
    [Fact]
    public void AnObjectTheGameDoesNotSimulateNeverHoldsAZone()
    {
        Assert.False(RoadRockPolicy.HoldsZone(movesOnNextCheck: true, simulatedNow: false));
        Assert.True(RoadRockPolicy.HoldsZone(movesOnNextCheck: true, simulatedNow: true));
        Assert.False(RoadRockPolicy.HoldsZone(movesOnNextCheck: false, simulatedNow: true));
        Assert.False(RoadRockPolicy.HoldsZone(movesOnNextCheck: false, simulatedNow: false));

        int holding = 0, unsimulated = 0;
        for (int i = 0; i < 26; i++)
        {
            if (RoadRockPolicy.HoldsZone(true, false)) holding++;
            else unsimulated++;
        }
        Assert.Equal(0, holding);
        Assert.Equal(RoadRockPolicy.ZoneVerdict.WaitForPlayerZone, RoadRockPolicy.JudgeZone(0, false, holding, unsimulated));
    }

    /// <summary>A simulated mover still holds the zone on the timer, whatever else waits outside the area; so do road
    /// points without ground and a rock owned elsewhere. With nothing left the zone is done.</summary>
    [Fact]
    public void ASimulatedMoverStillHoldsAZoneOnTheTimer()
    {
        Assert.Equal(RoadRockPolicy.ZoneVerdict.RetryOnTimer, RoadRockPolicy.JudgeZone(0, false, 1, 25));
        Assert.Equal(RoadRockPolicy.ZoneVerdict.RetryOnTimer, RoadRockPolicy.JudgeZone(0, false, 3, 0));
        Assert.Equal(RoadRockPolicy.ZoneVerdict.RetryOnTimer, RoadRockPolicy.JudgeZone(2, false, 0, 26));
        Assert.Equal(RoadRockPolicy.ZoneVerdict.RetryOnTimer, RoadRockPolicy.JudgeZone(0, true, 0, 26));
        Assert.Equal(RoadRockPolicy.ZoneVerdict.Done, RoadRockPolicy.JudgeZone(0, false, 0, 0));
    }

    /// <summary>
    /// A zone whose only movers are outside the active area, with the player standing still for many retry periods: it
    /// is looked at once and not again on the timer. When the player crosses into another zone it is looked at once
    /// more; once its objects are inside the area and the game has moved them, it is done. A network change releases
    /// it too.
    /// </summary>
    [Fact]
    public void AZoneWithOnlyUnsimulatedMoversWaitsForThePlayersZoneNotTheTimer()
    {
        var standing = new Vector2s(-2, 1);
        int looks = 0;
        bool waiting = false, done = false;
        Vector2s waitedFrom = default;
        int waitedVersion = 0;
        // One retry period per step: the player stands still for 100 of them.
        for (int step = 0; step < 100 && !done; step++)
        {
            if (waiting && RoadRockPolicy.ReleasesWaiting(waitedFrom, waitedVersion, standing, 1)) waiting = false;
            if (waiting) continue;
            looks++;
            var verdict = RoadRockPolicy.JudgeZone(0, false, holding: 0, unsimulated: 26);
            Assert.Equal(RoadRockPolicy.ZoneVerdict.WaitForPlayerZone, verdict);
            waiting = true; waitedFrom = standing; waitedVersion = 1;
        }
        Assert.Equal(1, looks);
        Assert.False(done);

        // The player walks one zone over: looked at again, now simulated and moved, so done.
        var moved = new Vector2s(-1, 1);
        Assert.True(RoadRockPolicy.ReleasesWaiting(waitedFrom, waitedVersion, moved, 1));
        Assert.Equal(RoadRockPolicy.ZoneVerdict.Done, RoadRockPolicy.JudgeZone(0, false, 0, 0));

        // A network change releases a waiting zone; the same zone and network keep it waiting.
        Assert.True(RoadRockPolicy.ReleasesWaiting(standing, 1, standing, 2));
        Assert.False(RoadRockPolicy.ReleasesWaiting(standing, 1, new Vector2s(-2, 1), 1));
    }
}
