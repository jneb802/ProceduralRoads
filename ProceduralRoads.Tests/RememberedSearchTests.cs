using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The searches a pathfinder remembers while an island is planned (RoadPathfinder.RemembersSearches), and the step fast paths
/// that read less per step (RoadLocationProtection.NoneWithin): each answers exactly as the search made afresh does.
/// </summary>
public class RememberedSearchTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    public RememberedSearchTests() { RoadLocationProtection.Reset(); RoadLocationProtection.Source = null; }
    public void Dispose() { PlacedLocations.Reset(); RoadLocationProtection.Reset(); RoadLocationProtection.Source = null; RoadSpatialGrid.Clear(); _world.Dispose(); }

    private static readonly Vector2 From = new(-400f, 100f), To = new(-100f, -300f);

    private static void SameAs(RoadPathfinder expected, List<Vector2>? expectedPath, RoadPathfinder actual, List<Vector2>? actualPath)
    {
        Assert.Equal(expectedPath == null, actualPath == null);
        if (expectedPath != null) Assert.Equal(expectedPath, actualPath!);
        Assert.Equal(expected.LastPathCost, actual.LastPathCost);
        Assert.Equal(expected.LastStairSteps, actual.LastStairSteps);
        Assert.Equal(expected.LastViaductJumps, actual.LastViaductJumps);
        Assert.Equal(expected.LastViaductGrounds, actual.LastViaductGrounds);
        Assert.Equal(expected.LastStartIndex, actual.LastStartIndex);
        Assert.Equal(expected.LastEndIndex, actual.LastEndIndex);
    }

    [Fact]
    public void ASearchAskedAgainOnTheSameNetworkIsAnsweredAsItWasMade()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = true };
        var fresh = new RoadPathfinder(world);
        var expected = fresh.FindPath(From, To);
        var remembering = new RoadPathfinder(world) { RemembersSearches = true };
        var first = remembering.FindPath(From, To);
        int before = RoadPathfinder.Recalled;
        var again = remembering.FindPath(From, To);
        Assert.Equal(before + 1, RoadPathfinder.Recalled);
        SameAs(fresh, expected, remembering, again);
        Assert.Equal(first, again);
        // The caller's list is its own: changing it changes nothing remembered.
        again!.Clear();
        Assert.Equal(expected, remembering.FindPath(From, To));
    }

    [Fact]
    public void ASearchAskedOtherwiseIsMadeAfresh()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = true };
        var remembering = new RoadPathfinder(world) { RemembersSearches = true };
        remembering.FindPath(From, To);
        int before = RoadPathfinder.Recalled;

        // Priced up round a point on its way: the search reads its price-ups.
        remembering.PenalizeAround(new Vector2(-250f, -100f), 40f);
        var penalised = remembering.FindPath(From, To);
        var fresh = new RoadPathfinder(world);
        fresh.PenalizeAround(new Vector2(-250f, -100f), 40f);
        SameAs(fresh, fresh.FindPath(From, To), remembering, penalised);

        // A setting of its own.
        remembering.ClearEarthworkPenalty();
        remembering.MaxGrade = 0.2f;
        var capped = remembering.FindPath(From, To);
        var freshCapped = new RoadPathfinder(world) { MaxGrade = 0.2f };
        SameAs(freshCapped, freshCapped.FindPath(From, To), remembering, capped);
        Assert.Equal(before, RoadPathfinder.Recalled);
    }

    [Fact]
    public void AWriteToTheNetworkForgetsEverySearch()
    {
        var world = new SyntheticWorld { HasRiver = false };
        var remembering = new RoadPathfinder(world) { RemembersSearches = true };
        remembering.FindPath(From, To);
        RoadNetworkGenerator.IslandNetworkChanged();
        int before = RoadPathfinder.Recalled;
        remembering.FindPath(From, To);
        Assert.Equal(before, RoadPathfinder.Recalled);
    }

    [Fact]
    public void APathfinderNotRememberingSearchesEveryTime()
    {
        var world = new SyntheticWorld { HasRiver = false };
        var finder = new RoadPathfinder(world);
        int before = RoadPathfinder.Recalled;
        finder.FindPath(From, To);
        finder.FindPath(From, To);
        Assert.Equal(before, RoadPathfinder.Recalled);
    }

    /// <summary>Every field of a pathfinder is either read into what a remembered search is asked (its settings and closings), or
    /// what a search leaves, or a memo of terrain facts or a search's scratch. A field added is a fact a search might read: it
    /// fails here until it is put in one of these lists, and into RoadPathfinder.SettingsNow or the asking where a search reads it.</summary>
    [Fact]
    public void EveryFieldOfAPathfinderIsAskedOrKnownNotToChangeASearch()
    {
        string[] asked =
        {
            "SlopeMultiplier", "RiverPenalty", "WaterPenalty", "SteepSlopePenalty", "SteepSlopeThreshold", "TerrainVariancePenalty",
            "TerrainVarianceThreshold", "BaseCost", "StandingWaterPenalty", "Fords", "Bridges", "BridgeCostFixed", "BridgeCostPerMeter", "BridgeHeightShare",
            "Viaducts", "StopsAtItsGoal", "ViaductCostFixed", "ViaductCostPerMeter", "ViaductEndStairCostPerMeter", "ViaductApproachReach", "ViaductApproachFactor", "Stairs", "StairCostFixed",
            "StairCostPerMeterClimbed", "MaxGrade", "ReuseFactor", "LocationClearance", "TurnLimitDegrees", "Meander", "TurnWeight",
            "m_worldGen", "m_noStairs", "m_stairOpen", "m_noJumps", "m_noViaducts", "m_gridOnly", "m_earthworkPenalty",
        };
        string[] left =
        {
            "<LastStairSteps>k__BackingField", "<LastViaductJumps>k__BackingField", "<LastViaductGrounds>k__BackingField", "<LastPathCost>k__BackingField",
            "<AlreadyOnNetwork>k__BackingField", "<LastStartIndex>k__BackingField", "<LastEndIndex>k__BackingField", "m_lastIterations",
            "m_lastFailure", "m_squeezeHits", "m_shoreFills", "Activity",
        };
        string[] memos =
        {
            // Terrain facts for the pathfinder's life (QuadVariance is asked through m_terrain's setting).
            "m_terrain", "m_squeezed", "m_viaducts", "m_viaductRow", "m_viaductCell", "m_crossings", "m_waterRows", "m_waterRow", "m_waterCell",
            // A search's own, set at its start.
            "m_searchStart", "m_searchEnd", "m_crossingDestination", "m_locations", "m_spareState", "m_fromRoadCell", "m_fromRoadKnown",
            "m_fromOnRoad", "m_fromClearCell", "m_fromClearKnown", "m_fromClear",
            // The memory itself.
            "m_remembers", "m_remembered", "m_rememberedNetwork",
        };
        var known = new HashSet<string>(asked.Concat(left).Concat(memos));
        var fields = typeof(RoadPathfinder).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.Name).ToList();
        Assert.Empty(fields.Where(f => !known.Contains(f)));
        Assert.Empty(known.Where(f => !fields.Contains(f)));
    }

    /// <summary>A point no footprint comes near (RoadLocationProtection.NoneWithin, at the clearance and the longest step): no
    /// step of the search from it is refused for a footprint, as BlocksRoute refuses them one by one.</summary>
    [Fact]
    public void NoStepFromAPointNoFootprintComesNearIsRefusedForOne()
    {
        var rng = new System.Random(7);
        var footprints = new List<RoadLocationProtection.Footprint>();
        for (int i = 0; i < 40; i++)
            footprints.Add(new RoadLocationProtection.Footprint(new Vector2(rng.Next(-600, 600), rng.Next(-600, 600)), 5f + rng.Next(0, 40)));
        RoadLocationProtection.Set(footprints);
        const float clearance = 6f, cell = RoadPathfinder.CellSize;
        float reach = clearance + Mathf.Sqrt(5f) * cell + 1f;
        int clear = 0, near = 0;
        for (int i = 0; i < 4000; i++)
        {
            var p = new Vector2(rng.Next(-700, 700) * 1.03f, rng.Next(-700, 700) * 0.97f);
            if (!RoadLocationProtection.NoneWithin(p, reach)) { near++; continue; }
            clear++;
            foreach (Vector2Int d in RoadPathfinder.Directions)
                Assert.False(RoadLocationProtection.BlocksRoute(p, p + new Vector2(d.x, d.y) * cell, clearance, default));
        }
        Assert.True(clear > 100 && near > 100, $"{clear} clear, {near} near");
        // Right by a footprint's rim, it is never clear.
        Assert.False(RoadLocationProtection.NoneWithin(footprints[0].Centre + new Vector2(footprints[0].Radius + reach - 0.01f, 0f), reach));
    }
}
