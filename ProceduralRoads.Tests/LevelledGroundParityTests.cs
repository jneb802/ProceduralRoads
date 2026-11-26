using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// One ground before our roads (RoadNetworkGenerator.GroundBeforeRoads): a single way onto a placed Location's landing over the
/// pad its levelling raises is judged on that one ground by every pre-road judge that reads it, and they give one verdict: the
/// way in planned as the road's own last stretch, the one list's ridge judge (RoadNetworkGenerator.Ridges), the road to a staircase's foot onto a Location placed first (RoadStairRuns.RidgeToFoot over
/// the ground before roads) and onto one placed after (over its expected ground, RoadNetworkGenerator.ExpectedGroundBeforeRoads),
/// and the ridge between the road's end and the Location (RoadNetworkGenerator.BehindRidge). The natural ground under the pad,
/// with a hump on its batter, reads otherwise: another verdict at the foot.
/// </summary>
public class LevelledGroundParityTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>Flat natural ground, with a hump <see cref="Hump"/> m high across the pad's batter, from <see cref="HumpFrom"/> to
    /// <see cref="HumpTo"/> m out (the Location's levelling buries it).</summary>
    private sealed class HumpWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f) return 0f;
            float r = new Vector2(wx, wy).magnitude;
            return r >= HumpFrom && r <= HumpTo ? Natural + Hump : Natural;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    private const float Natural = 40f, Pad = 5f, PadRadius = 18f, Batter = 1f, Footprint = 24f, Width = 4f;
    private const float Hump = 12f, HumpFrom = 19.5f, HumpTo = 21.5f;
    /// <summary>MountainCave02's floor, as in <see cref="LocationEntryPadFillTests"/>.</summary>
    private static readonly LevelOp CaveFloor = new(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f);
    private static readonly Vector2 Centre = new(0f, 0f);

    public LevelledGroundParityTests()
    {
        _scope.WithWorld(new HumpWorld());
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(Centre, Footprint) });
        PlacedLocations.Preload(Centre, PadGround);
    }

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadTerrainModifier.ResetForWorld();
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>The ground the Location leaves: a pad <see cref="Pad"/> m over the flat ground out to <see cref="PadRadius"/>, battered
    /// down at 1 in <see cref="Batter"/> over the hump, the cave's sunk floor in it.</summary>
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

    private static PlacedLocationShape Shape() => PlacedLocationShape.Of(Centre, 90f, CaveFloor, 0f, null, PadGround);

    private static LocationApproachRequest Request(Vector2 end, float height)
    {
        Vector2 before = end + end.normalized * 30f;
        return new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = StairLayout.Bearing(before, end), Centre = Centre,
            RoadTail = new List<Vector2> { before, end },
        };
    }

    [Fact]
    public void AWayInOverARaisedPadGetsOneVerdictOnOneGroundFromEveryPreRoadJudge()
    {
        WorldGenerator world = WorldGenerator.instance!;
        Func<Vector2, float> before = RoadNetworkGenerator.GroundBeforeRoads(world);
        Func<Vector2, float> natural = q => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world);
        var end = new Vector2(30f, 0f);

        // The one ground: the pad in the Location's footprint, the hump buried under it; the natural ground past it.
        Assert.Equal(PadGround(20.5f, 0f), before(new Vector2(20.5f, 0f)), 3);
        Assert.Equal(Natural + Hump, natural(new Vector2(20.5f, 0f)), 3);
        Assert.Equal(Natural, before(end), 3);

        // The way in, planned on the ground before roads as a way onto a placed-first Location is (the road's own last stretch,
        // its fill judged by the one ridge judge): accepted over that ground.
        RoadSpatialGrid.PlannedPath? way = RoadLocationEntry.Plan(Request(end, Natural), Shape(), Width,
            (x, z) => RoadNetworkGenerator.GroundBeforeRoads(x, z, world), out string why);
        Assert.True(way != null, why);
        var plans = new List<RoadSpatialGrid.PlannedPath> { way! };

        // The one list's ridge judge: no ridge nor trench (LocationEntryPadFillTests: over the flat natural ground, one).
        Assert.Empty(RoadNetworkGenerator.Ridges(plans, new List<RoadCrossing>()));

        // The road to a staircase's foot at the landing, onto a Location placed first (the ground before roads) and onto one placed
        // after (its expected ground over it): one verdict, the way in where it is cut down to the cave's sunk floor; over the
        // natural ground another.
        string Said(RoadRidges.Stretch? s) => s == null ? "none" : FormattableString.Invariant($"{s.Kind} from {s.Points[0]} for {s.Length:F1} m");
        string first = Said(RoadStairRuns.RidgeToFoot(way!.Points, way.Heights, footAtStart: false, before));
        string after = Said(RoadStairRuns.RidgeToFoot(way.Points, way.Heights, footAtStart: false, RoadNetworkGenerator.ExpectedGroundBeforeRoads(Centre, world)));
        Assert.Equal(first, after);
        Assert.NotEqual(first, Said(RoadStairRuns.RidgeToFoot(way.Points, way.Heights, footAtStart: false, natural)));

        // The ridge between the road's end and the Location's edge (its footprint's, on this side): read on the same ground, the
        // same verdict as the line to its arrival height over the ground before roads, none.
        Vector2 edge = Centre + (end - Centre).normalized * RoadLocationProtection.ArrivalRadiusAt(Centre, PadRadius);
        float level = RoadNetworkGenerator.ArrivalHeight(Centre, PadRadius, end)!.Value;
        bool behind = RoadNetworkGenerator.RidgeOver(end, Natural, edge, level, before) > RoadNetworkGenerator.RidgeRise;
        Assert.False(behind);
        Assert.Equal(behind, RoadNetworkGenerator.BehindRidge(Centre, PadRadius, end, Natural));
    }
}
