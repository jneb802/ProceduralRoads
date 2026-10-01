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
    public void KnownRocksListsExactlyTheClearableNames()
    {
        // road_rock_kinds reports on this list; a name the predicates accept
        // but the list lacks would never be checked for chunks.
        foreach (string name in RoadRockPolicy.KnownRocks)
            Assert.True(RoadRockPolicy.IsNaturalBoulder(name) || RoadRockPolicy.IsMistlandsRock(name, true, true), name);
        Assert.Equal(12, System.Array.FindAll(RoadRockPolicy.KnownRocks, RoadRockPolicy.IsNaturalBoulder).Length);
        Assert.Equal(4, System.Array.FindAll(RoadRockPolicy.KnownRocks, n => RoadRockPolicy.IsMistlandsRock(n, true, true)).Length);
        Assert.Equal(RoadRockPolicy.KnownRocks.Length, new System.Collections.Generic.HashSet<string>(RoadRockPolicy.KnownRocks).Count);
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

    [Theory]
    [InlineData(RockClearingMode.Off, false, false)]
    [InlineData(RockClearingMode.Remove, true, false)]
    [InlineData(RockClearingMode.Carve, true, true)]
    public void TheSettingSaysWhetherRocksAreClearedAndHow(RockClearingMode mode, bool clears, bool carves)
    {
        Assert.Equal(clears, RoadRockPolicy.Clears(mode));
        Assert.Equal(carves, RoadRockPolicy.Carves(mode));
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
}
