using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>Where the sway (RoadWiggle.Apply) was lost for nothing, and how its losses are counted (RoadSpatialGrid.PlanRoadPath).</summary>
public class SwayLossTests : IDisposable
{
    private readonly Func<IEnumerable<RoadLocationProtection.Footprint>?>? previousSource = RoadLocationProtection.Source;

    public SwayLossTests()
    {
        RoadLocationProtection.Source = null;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
    }

    public void Dispose()
    {
        RoadLocationProtection.Source = previousSource;
        RoadLocationProtection.Reset();
    }

    private static List<Vector2> Straight(float x0, float x1, float step = 8f)
    {
        var path = new List<Vector2>();
        for (float x = x0; x <= x1; x += step) path.Add(new Vector2(x, 0f));
        return path;
    }

    private static float Offset(Vector2 p, IReadOnlyList<Vector2> line)
    {
        float best = float.MaxValue;
        for (int j = 1; j < line.Count; j++)
        {
            Vector2 a = line[j - 1], ab = line[j] - a;
            float l2 = ab.sqrMagnitude;
            float t = l2 > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
            best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
        }
        return best;
    }

    [Fact]
    public void ACornerTheResampleCutsIntoALocationsClearanceLosesTheSwayThereOnly()
    {
        // A generated world's 912 m piece from a Black Forest Location to the swamp, as the sway was handed it (the searched
        // line, pulled), and every Location footprint within 40 m of it. The line turns at (-2920,-3272) round ShipSetting01's
        // footprint (-2948.3,-3264.7), radius 24; the 12 m resample's leg (-2920.0,-3262.2) to (-2921.1,-3273.9) cuts that
        // corner into its clearance, though the searched line is clear. The whole piece was then left unswayed (it was
        // planned with up to 9.5 m of sway). Now the corner is kept and the sway held round it, and only there.
        var path = new List<Vector2>
        {
            new(-3065.9316f, -3077.3694f), new(-3048, -3104), new(-3016, -3136), new(-2992, -3160), new(-2976, -3192),
            new(-2952, -3208), new(-2928, -3240), new(-2920, -3256), new(-2920, -3272), new(-2944, -3312), new(-2976, -3336),
            new(-3008, -3360), new(-3032, -3392), new(-3048, -3424), new(-3056, -3464), new(-3064, -3496), new(-3088, -3536),
            new(-3112, -3576), new(-3120, -3608), new(-3152, -3624), new(-3192, -3640), new(-3240, -3640), new(-3288, -3640),
            new(-3304, -3632), new(-3320, -3624), new(-3336, -3632), new(-3352, -3640), new(-3364.4443f, -3646.2222f),
        };
        var footprints = new (float x, float z, float r)[]
        {
            (-3055.9f, -3144.2f, 9f), (-3016f, -3195.4f, 30f), (-2962.8f, -3181f, 10.7f), (-2948.3f, -3264.7f, 24f), (-2944f, -3392f, 32f),
            (-3054.4f, -3369f, 10.3f), (-3009.8f, -3454.7f, 24f), (-3089.9f, -3433.5f, 8f), (-3136.4f, -3600.9f, 8f), (-3078.8f, -3648.9f, 24f),
            (-3199.9f, -3578.5f, 24f), (-3189.9f, -3667.1f, 10f), (-3264f, -3584f, 32f), (-3318.1f, -3587.8f, 8f), (-3347.4f, -3655.5f, 5f),
        };
        RoadLocationProtection.Set(footprints.Select(f => new RoadLocationProtection.Footprint(new Vector2(f.x, f.z), f.r)).ToArray());
        Vector2 start = path[0], end = path[path.Count - 1], corner = new(-2920, -3272);
        Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(path, 4f, null, start, end, default));
        var resampled = RoadWiggle.Resample(path, RoadWiggle.Spacing);
        Assert.True(RoadLocationProtection.FirstBlockedLeg(resampled, 4f, null, start, end, default) > 0, "the resample no longer cuts the corner");

        List<Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(path, _ => 60f, _ => false, _ => Heightmap.Biome.Meadows, 4f));

        Assert.Equal(RoadWiggle.Outcome.SwayedRoundCorner, RoadWiggle.LastOutcome);
        Assert.Contains(corner, swayed);
        // Never into a location's clearance.
        Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(swayed, 4f, null, start, end, default));
        float near = 0f, away = 0f;
        foreach (var p in swayed)
        {
            float d = Vector2.Distance(p, corner);
            if (d < 6f) near = Mathf.Max(near, Offset(p, path));
            else if (d > RoadWiggle.HoldRadius + RoadWiggle.ShareFade + RoadWiggle.Spacing) away = Mathf.Max(away, Offset(p, path));
        }
        Assert.True(near < 0.5f, $"at the corner the road is {near:F2} m off the searched line");
        Assert.True(away > 5f, $"away from the corner the road stayed within {away:F2} m of the searched line");
    }

    [Fact]
    public void ACornerKeptOnALineWithNothingToSwayIsNotCountedASwayKeptRoundACorner()
    {
        // The same piece through the Mistlands (no point may sway): the resample cuts the corner round the footprint, the corner
        // is kept as before, and the line is the resample with it. Nothing swayed, so it is not labelled a sway kept round a corner.
        var path = new List<Vector2>
        {
            new(-2992, -3160), new(-2976, -3192), new(-2952, -3208), new(-2928, -3240), new(-2920, -3256), new(-2920, -3272),
            new(-2944, -3312), new(-2976, -3336), new(-3008, -3360), new(-3032, -3392), new(-3048, -3424),
        };
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(-2948.3f, -3264.7f), 24f) });
        Vector2 start = path[0], end = path[path.Count - 1], corner = new(-2920, -3272);
        Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(path, 4f, null, start, end, default));
        Assert.True(RoadLocationProtection.FirstBlockedLeg(RoadWiggle.Resample(path, RoadWiggle.Spacing), 4f, null, start, end, default) > 0,
            "the resample no longer cuts the corner");
        List<Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(path, _ => 60f, _ => false, _ => Heightmap.Biome.Mistlands, 4f));
        Assert.Equal(RoadWiggle.Outcome.ResampledNothingSwayed, RoadWiggle.LastOutcome);
        Assert.Contains(corner, swayed);
        Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(swayed, 4f, null, start, end, default));
        RoadLocationProtection.Set(System.Array.Empty<RoadLocationProtection.Footprint>());
    }

    [Fact]
    public void WhereNothingMaySwayThePathIsStillResampledAsBefore()
    {
        // A Mistlands road (the meander's biome factor there is none) is resampled though no point of it sways: it comes back on
        // the resample's line, not untouched (an untouched return changed which valley roads were planned).
        var path = new List<Vector2> { new(0, 0), new(37, 0), new(70, 23), new(130, 23), new(171, 60) };
        List<Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(path, _ => 60f, _ => false, _ => Heightmap.Biome.Mistlands, 4f));
        Assert.NotSame(path, swayed);
        Assert.Equal(RoadWiggle.Outcome.ResampledNothingSwayed, RoadWiggle.LastOutcome);
        Assert.Equal(RoadWiggle.Resample(path, RoadWiggle.Spacing), swayed);
    }

    [Fact]
    public void ATooShortPathComesBackUntouchedAndSaysSo()
    {
        var path = Straight(0f, 40f);
        List<Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(path, _ => 60f, _ => false, _ => Heightmap.Biome.Meadows, 4f));
        Assert.Same(path, swayed);
        Assert.Equal(RoadWiggle.Outcome.TooShort, RoadWiggle.LastOutcome);
    }

    [Fact]
    public void ALineWhoseOwnLegCutsALocationsClearanceIsLeftAsItWas()
    {
        // The search's own line runs through a location, wider than any sway: no waypoint of it to keep, the sway dropped.
        var path = Straight(0f, 200f);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(100f, 0f), 12f) });
        List<Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(path, _ => 60f, _ => false, _ => Heightmap.Biome.Meadows, 4f));
        Assert.Same(path, swayed);
        Assert.Equal(RoadWiggle.Outcome.DroppedByKeepClear, RoadWiggle.LastOutcome);
    }

    [Fact]
    public void TheSecondHalfIsHeldOnALineWithAnOddCountOfHoldPoints()
    {
        // Seven hold points: each half holds four. Compared by count, the second half looked like nothing new and was never
        // planned; compared by the points, it is.
        var path = Straight(0f, 144f, 12f);
        var first = RoadWiggle.HalfOf(path, second: false);
        var second = RoadWiggle.HalfOf(path, second: true);
        Assert.Equal(7, RoadWiggle.Resample(path, RoadWiggle.HoldRadius).Count);
        Assert.Equal(first.Count, second.Count);
        Assert.False(RoadWiggle.HoldsNothingNew(second, first));
        // Still nothing new: no point, or every point already held.
        Assert.True(RoadWiggle.HoldsNothingNew(new List<Vector2>(), first));
        Assert.True(RoadWiggle.HoldsNothingNew(new List<Vector2>(first), first));
        Assert.True(RoadWiggle.HoldsNothingNew(first.GetRange(1, 2), first));
        Assert.False(RoadWiggle.HoldsNothingNew(first, null));
    }

    private sealed class BiomeWorld : SyntheticWorld
    {
        public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
    }

    private static void Counted(Heightmap.Biome biome, List<Vector2> path, RoadLocationProtection.Footprint[] footprints, Action check)
    {
        var world = new BiomeWorld { HasRiver = false, HasMountain = false, Biome = biome };
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Set(footprints);
        try
        {
            PlainRoads.WithModDefaults(() =>
            {
                RoadSpatialGrid.SwayKept = RoadSpatialGrid.SwayKeptHeld = RoadSpatialGrid.SwayRefused = RoadSpatialGrid.SwayMoreEarth = 0;
                RoadSpatialGrid.SwayPlainRefusedToo = RoadSpatialGrid.SwayTooShort = 0;
                RoadSpatialGrid.SwayDroppedByKeepClear = RoadSpatialGrid.SwayKeptRoundCorner = 0;
                RoadSpatialGrid.PlanRoadPath(path, 4f, world, terrainHeight: _ => 60f);
                check();
            });
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void ARoadRefusedUnswayedTooIsNotASwayRefusal()
    {
        // Straight through a location: refused swayed and plain. The sway lost nothing; it was counted "dropped as refused".
        Counted(Heightmap.Biome.Meadows, Straight(-150f, 150f), new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 5f) }, () =>
        {
            Assert.Equal(1, RoadSpatialGrid.SwayPlainRefusedToo);
            Assert.Equal(0, RoadSpatialGrid.SwayRefused);
            Assert.Equal(0, RoadSpatialGrid.SwayKept);
        });
    }

    [Fact]
    public void ALineTheSwayLeftAsItWasIsNotCountedKept()
    {
        Counted(Heightmap.Biome.Meadows, Straight(0f, 40f), Array.Empty<RoadLocationProtection.Footprint>(), () =>
        {
            Assert.Equal(1, RoadSpatialGrid.SwayTooShort);
            Assert.Equal(0, RoadSpatialGrid.SwayKept);
        });
        Counted(Heightmap.Biome.Meadows, Straight(-150f, 150f), Array.Empty<RoadLocationProtection.Footprint>(), () =>
        {
            Assert.Equal(1, RoadSpatialGrid.SwayKept);
            Assert.Equal(0, RoadSpatialGrid.SwayTooShort + RoadSpatialGrid.SwayDroppedByKeepClear + RoadSpatialGrid.SwayPlainRefusedToo);
        });
    }
}
