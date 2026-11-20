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

    [Theory]
    [InlineData("silvervein")]
    [InlineData("rock3_silver")]
    [InlineData("rock4_copper")]
    [InlineData("MineRock_Obsidian")]
    [InlineData("MineRock_Tin")]
    [InlineData("rock1_mountain_frac")]
    [InlineData("rock2_heath_frac")]
    [InlineData("rock4_forest_frac")]
    [InlineData("Rock_3_frac")]
    [InlineData("Rock_3_deepnorth")]
    [InlineData("RockDolmen_1")]
    [InlineData("rock_mistlands1")]
    [InlineData("rock_3")]
    [InlineData("wood_floor")]
    public void OreFragmentsAndUnrecognisedSceneryStay(string name) =>
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

    private static System.Collections.Generic.List<int> Open(float edge, bool[]? other, params int[][] strips)
    {
        var blockers = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyCollection<int>>();
        foreach (var s in strips) blockers.Add(s);
        var held = new System.Collections.Generic.List<bool>(other ?? new bool[strips.Length]);
        return RoadRockPolicy.ChunksToOpen(blockers, held, edge, new System.Collections.Generic.HashSet<int>());
    }

    [Fact]
    public void ACarveLeavesTheSmallestWayAPlayerFitsThrough()
    {
        // The smallest possible hole in the rock the player can
        // reliably fit through. The player is 0.98 m wide; the way left is 1.5 m.
        Assert.True(RoadRockPolicy.MinOpening > RoadRockPolicy.PlayerWidth + 0.4f);
        Assert.True(RoadRockPolicy.ClearanceHeight > RoadRockPolicy.PlayerHeight);
        // A 4 m road's clearance is its solid paint, 3.4 m: of 10 strips, a rock
        // keeps all but 5 (1.7 m), where it kept all but 7 under the old 30%.
        float road = 4f * RoadConstants.RoadPaintOuterRatio;
        var rock = new int[10][];
        for (int s = 0; s < 10; s++) rock[s] = new[] { s };
        var taken = Open(RoadRockPolicy.EdgeAllowance(road), null, rock);
        taken.Sort();
        Assert.Equal(5, taken.Count);
        Assert.Equal(taken[0] + 4, taken[4]);   // one run, from the middle out
        Assert.InRange(taken[0], 2, 3);
        // A clearance no wider than the way is opened in full.
        Assert.Equal(0f, RoadRockPolicy.EdgeAllowance(1.5f));
        Assert.Equal(0f, RoadRockPolicy.EdgeAllowance(1f));
    }

    [Fact]
    public void AnEdgeIntrusionWithinTheAllowanceStays()
    {
        // Chunk 1 covers the left 3 of 10 strips: 30% is allowed, nothing goes.
        Assert.Empty(Open(0.3f, null, new[] { 1 }, new[] { 1 }, new[] { 1 }, new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0]));
        // Both sides, 20% + 10%: the open run is still 7 strips.
        Assert.Empty(Open(0.3f, null, new[] { 1 }, new[] { 1 }, new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new[] { 2 }));
    }

    [Fact]
    public void TooMuchIntrusionLosesTheChunksNearestTheCentre()
    {
        // Chunk 1 covers strips 0-2, chunk 3 strip 3: 40% blocked. Chunk 3 is
        // nearer the centre and alone is enough.
        Assert.Equal(new[] { 3 }, Open(0.3f, null, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 3 }, new int[0], new int[0], new int[0], new int[0], new int[0], new int[0]));
        // A chunk in the middle always goes: it splits the road into two short runs.
        Assert.Equal(new[] { 5 }, Open(0.3f, null, new int[0], new int[0], new int[0], new int[0], new[] { 5 }, new int[0], new int[0], new int[0], new int[0], new int[0]));
    }

    [Fact]
    public void WhatIsNotOursLeavesNothingToKeep()
    {
        // A tree holds strips 7-9 (30%): the rock at strip 0 must go, and if the
        // run can never be long enough every removable chunk is taken.
        var other = new[] { false, false, false, false, false, false, false, true, true, true };
        Assert.Equal(new[] { 1 }, Open(0.3f, other, new[] { 1 }, new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0]));
        var wall = new[] { false, false, false, false, true, false, false, false, false, false };
        Assert.Equal(2, Open(0.3f, wall, new[] { 1 }, new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new[] { 2 }).Count);
        // Allowance 0 clears the whole width.
        Assert.Equal(new[] { 1 }, Open(0f, null, new[] { 1 }, new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0], new int[0]));
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
        foreach (string name in new[] { "rock1_mountain_frac", "rock4_copper", "silvervein", "Beech1", "cliff_mistlands1_creep" })
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
}
