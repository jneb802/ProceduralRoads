using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ProceduralRoads.Tests;

/// <summary>
/// The suite runs on plain roads: the route and shape features that the mod
/// turns on by default (the meander, the sway, the pull, turn and slope
/// prices, switchback and bend shaping, pitches, following the ground, joins,
/// the earthwork cap, the bridge-end rules) are off, so a test of one
/// mechanism sees that mechanism and a straight fixture road stays straight.
/// A test of a feature turns it on and restores the plain value. The mod's
/// own defaults are captured before they are replaced, and
/// <see cref="DefaultRoadsTests"/> runs with them.
/// </summary>
internal static class PlainRoads
{
    private static readonly List<(string name, Func<object> get, Action<object> set, object plain)> Fields = new()
    {
        ("slope weight", () => RoadPathfinder.DefaultSlopeMultiplier, v => RoadPathfinder.DefaultSlopeMultiplier = (float)v, 10f),   // the price before the route work
        ("road reuse", () => RoadPathfinder.DefaultReuseFactor, v => RoadPathfinder.DefaultReuseFactor = (float)v, 1f),
        ("turn limit", () => RoadPathfinder.DefaultTurnLimit, v => RoadPathfinder.DefaultTurnLimit = (float)v, 0f),
        ("turn weight", () => RoadPathfinder.DefaultTurnWeight, v => RoadPathfinder.DefaultTurnWeight = (float)v, 0f),
        ("meander", () => RoadPathfinder.DefaultMeander, v => RoadPathfinder.DefaultMeander = (float)v, 0f),
        ("quadratic variance", () => RoadTerrainSamples.DefaultQuadVariance, v => RoadTerrainSamples.DefaultQuadVariance = (bool)v, false),
        ("stair turns", () => RoadSwitchbacks.StairTurns, v => RoadSwitchbacks.StairTurns = (bool)v, false),
        ("bend radius", () => RoadSwitchbacks.BendRadius, v => RoadSwitchbacks.BendRadius = (float)v, 0f),
        ("switchback fallback", () => RoadSwitchbacks.Fallback, v => RoadSwitchbacks.Fallback = (bool)v, false),
        ("tight switchbacks", () => RoadSwitchbacks.TightSwitchbacks, v => RoadSwitchbacks.TightSwitchbacks = (bool)v, false),
        ("follow ground", () => RoadSpatialGrid.FollowGround, v => RoadSpatialGrid.FollowGround = (bool)v, false),
        ("junction match", () => RoadSpatialGrid.JunctionMatch, v => RoadSpatialGrid.JunctionMatch = (bool)v, false),
        ("curve checks", () => RoadSpatialGrid.CurveChecks, v => RoadSpatialGrid.CurveChecks = (bool)v, false),
        ("pitches", () => RoadPitches.Enabled, v => RoadPitches.Enabled = (bool)v, false),
        ("junction landing", () => RoadPitches.JunctionLanding, v => RoadPitches.JunctionLanding = (float)v, 0f),
        ("pull", () => RoadPathPull.Enabled, v => RoadPathPull.Enabled = (bool)v, false),
        ("pull tolerance", () => RoadPathPull.Tolerance, v => RoadPathPull.Tolerance = (float)v, 0f),
        ("sway", () => RoadWiggle.Amplitude, v => RoadWiggle.Amplitude = (float)v, 0f),
        ("road snap", () => RoadNetworkGenerator.RoadSnap, v => RoadNetworkGenerator.RoadSnap = (float)v, 0f),
        ("corridor snap", () => RoadNetworkGenerator.CorridorSnap, v => RoadNetworkGenerator.CorridorSnap = (float)v, 0f),
        ("one branch", () => RoadNetworkGenerator.OneBranch, v => RoadNetworkGenerator.OneBranch = (bool)v, false),
        ("earthwork cap", () => RoadNetworkGenerator.EarthworkCap, v => RoadNetworkGenerator.EarthworkCap = (float)v, 0f),
        ("bridge ends to water", () => RoadCrossingDetector.ToWater, v => RoadCrossingDetector.ToWater = (bool)v, false),
        ("decline tall climbs", () => RoadCrossingDetector.DeclineTallClimbs, v => RoadCrossingDetector.DeclineTallClimbs = (float)v, 0f),
    };

    /// <summary>The mod's own values, captured before the plain ones replaced them.</summary>
    internal static readonly Dictionary<string, object> ModDefaults = new();

    [ModuleInitializer]
    internal static void Apply()
    {
        foreach (var (name, get, set, plain) in Fields)
        {
            ModDefaults[name] = get();
            set(plain);
        }
    }

    /// <summary>Run <paramref name="body"/> with the mod's own defaults, then go back to plain roads.</summary>
    internal static void WithModDefaults(Action body)
    {
        foreach (var (name, _, set, _) in Fields) set(ModDefaults[name]);
        try { body(); }
        finally { foreach (var (_, _, set, plain) in Fields) set(plain); }
    }
}
