using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing over the pad its levelling raises: its fill is measured over the Location's levelled
/// ground (RoadNetworkGenerator.GroundBeforeRoads), not over the slope the pad buried, by the one ridge judge the road's final check
/// asks (RoadNetworkGenerator.AlongARidge), as the way in is planned and as the road is stored.
/// </summary>
public class LocationEntryPadFillTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : Natural;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    private const float Natural = 40f, Pad = 5f, PadRadius = 18f, Batter = 1f, Footprint = 24f, Width = 4f;
    /// <summary>MountainCave02's floor, as in <see cref="LocationEntryRoadTests"/>.</summary>
    private static readonly LevelOp CaveFloor = new(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f);

    public LocationEntryPadFillTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Preload(new Vector2(0f, 0f), PadGround);
    }

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadTerrainModifier.ResetForWorld();
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>The ground the Location leaves: a pad <see cref="Pad"/> m over the flat natural ground out to <see cref="PadRadius"/>,
    /// battered down to it at 1 in <see cref="Batter"/>, the cave's sunk floor in it.</summary>
    private static float PadGround(float x, float z)
    {
        float r = new Vector2(x, z).magnitude;
        float h = Natural + Mathf.Clamp(Pad - (r - PadRadius) / Batter, 0f, Pad);
        Vector2 floor = LocationLevelling.OffsetAt(CaveFloor, 90f);
        float d = Vector2.Distance(new Vector2(x, z), floor), level = Natural + Pad + CaveFloor.Height;
        if (d <= CaveFloor.Radius) h = level;
        if (d <= CaveFloor.SmoothRadius) h = Mathf.Lerp(h, level, 1f - Mathf.Pow(d / CaveFloor.SmoothRadius, CaveFloor.SmoothPower));
        return h;
    }

    private static float RawGround(float x, float z) => Natural;

    private static PlacedLocationShape Shape() => PlacedLocationShape.Of(new Vector2(0f, 0f), 90f, CaveFloor, 0f, null, PadGround);

    /// <summary>A road ending on the +x axis at <paramref name="endX"/> at <paramref name="height"/>.</summary>
    private static LocationApproachRequest Request(float endX, float height)
    {
        var end = new Vector2(endX, 0f);
        Vector2 before = end + end.normalized * 30f;
        return new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
    }

    /// <summary>
    /// The road ends on the natural ground 30 m out and climbs onto the pad, 5 m over the natural ground: its fill is measured over
    /// the pad's levelled ground, where it is small, not over the natural ground the pad buried, and the way in is accepted: the
    /// pad's levelling counts as the earthwork it joins.
    /// </summary>
    [Fact]
    public void AWayInOntoAPadRaisedOverTheRawGroundIsJudgedOverThePadAndAccepted()
    {
        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(Request(30f, Natural), Shape(), Width, PadGround, out string why);
        Assert.True(road != null, why);
        float overPad = RoadNetworkGenerator.EarthworkAcross(road!, q => PadGround(q.x, q.y), RoadNetworkGenerator.EarthworkLimit).Fill;
        float overRaw = RoadNetworkGenerator.EarthworkAcross(road, q => RawGround(q.x, q.y), RoadNetworkGenerator.EarthworkLimit).Fill;
        Assert.True(overPad < overRaw, FormattableString.Invariant($"fill {overPad:F2} over the pad, {overRaw:F2} over the raw ground"));
    }

    /// <summary>
    /// The way in planned over the pad is judged by the final check's ridge judge (RoadNetworkGenerator.AlongARidge,
    /// the one list's) on the same ground: no ridge, where the natural ground it no longer reads lies 5 m under it on both sides
    /// along the pad (RoadRidges.Find over the raw ground finds one).
    /// </summary>
    [Fact]
    public void TheWayInAndTheFinalChecksRidgeJudgeReadAWayInOnTheSameGround()
    {
        WorldGenerator world = WorldGenerator.instance!;
        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(Request(30f, Natural), Shape(), Width, PadGround, out string why);
        Assert.True(road != null, why);
        var plans = new List<RoadSpatialGrid.PlannedPath> { road! };
        Assert.NotEmpty(RoadRidges.Find(plans, q => RawGround(q.x, q.y)).FindAll(s => s.Kind == RoadRidges.Kind.Ridge));
        string? ridge = RoadNetworkGenerator.AlongARidge(plans, new List<RoadCrossing>(), out Vector2 at, out _);
        Assert.True(ridge == null, FormattableString.Invariant($"{ridge} at ({at.x:F1}, {at.y:F1})"));
    }
}
