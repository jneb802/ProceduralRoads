using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing across a deep pool whose bed runs on under the landing (in game a SunkenCrypt4: the
/// pool 26 m long and 1.57 m deep from the road's bank to the crypt, its natural ground under the landing 28.58, the crypt's
/// levelled ground there 30.25 and 30.64 on the porch behind it, falling to the pool by about 6 m out). The natural ground under
/// the landing is water, but the Location's own levelled ground is dry dirt: the road wades the pool at its levelled floor and
/// comes up the levelled ground, a cut, level with the landing.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The natural ground along the line out of the crypt's front, read in game every 2 m out from its landing
    /// (8.5 m out), the pool's deepest (28.43) under the porch, and the road's bank at 28.4 m out. Behind the crypt's centre the
    /// ground is dry (31.4, as in game).</summary>
    private static readonly (float r, float h)[] FordCryptNatural =
    {
        (0f, 29.7f), (5f, 28.43f), (8.5f, 28.58f), (10.5f, 29.03f), (12.5f, 29.51f), (14.5f, 29.56f), (16.5f, 29.43f), (18.5f, 29.36f),
        (20.5f, 28.95f), (22.5f, 29.28f), (24.5f, 29.76f), (26.5f, 30.1f), (28.4f, 30.54f), (30.5f, 30.93f), (200f, 30.93f),
    };

    /// <summary>The crypt's levelled ground on the same line, read in game: the porch, the landing, then down its blend to the pool.</summary>
    private static readonly (float r, float h)[] FordCryptLevelled =
    {
        (0f, 30.64f), (7.4f, 30.64f), (8.5f, 30.25f), (10.5f, 30.04f), (12.5f, 29.96f), (14.5f, 29.64f), (16.5f, 29.45f), (18.5f, 29.38f),
    };

    private static float Along((float r, float h)[] table, float r)
    {
        if (r <= table[0].r) return table[0].h;
        for (int k = 1; k < table.Length; k++)
            if (r <= table[k].r) return Mathf.Lerp(table[k - 1].h, table[k].h, (r - table[k - 1].r) / (table[k].r - table[k - 1].r));
        return table[table.Length - 1].h;
    }

    private static float FordCryptGround(float x, float z) => z <= 0f ? Along(FordCryptNatural, new Vector2(x, z).magnitude)
        : Mathf.Max(Along(FordCryptNatural, new Vector2(x, z).magnitude), Mathf.Lerp(29.7f, 31.4f, Mathf.Clamp01(z / 4f)));

    private static float FordCryptLevelledGround(float x, float z)
    {
        float r = new Vector2(x, z).magnitude;
        return r <= FordCryptLevelled[FordCryptLevelled.Length - 1].r ? Along(FordCryptLevelled, r) : FordCryptGround(x, z);
    }

    /// <summary>The crypt in its pool, the road's end on the dirt 40 m out in front: the way in wades the pool at its levelled floor
    /// and comes up the crypt's levelled ground to the landing, level with it within a step (StairLayout.StepUp).</summary>
    [Fact]
    public void AWayInFordsADeepPoolAndComesUpTheLocationsLevelledGroundToItsLanding()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(FordCryptGround);
        crypt.Ground = FordCryptLevelledGround;
        PlacedLocationShape door = PlacedLocationShape.Of(crypt.Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, crypt.Ground, false, crypt.Record.Root,
            "SunkenCrypt4", entrance: 0, bodyKnown: true);
        crypt.Shape = door;
        crypt.Entrances = new[] { door };
        PlacedLocations.Ready(crypt);
        Vector2 roadEnd = new(0f, -40f);
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        Vector2 end = RoadLocationEntry.WayInEnd(door, roadEnd, walker);
        Assert.True(RoadWater.Wet(FordCryptGround(end.x, end.y)), $"the landing's natural ground at {end} is in the pool");
        float landing = crypt.Ground(end.x, end.y);
        Assert.True(RoadWater.BuiltDry(landing) || landing > RoadConstants.SeaLevel, $"the landing's levelled ground {landing:F2} is out of the water");
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, walker);
        var request = new LocationApproachRequest { RoadEnd = roadEnd, RoadHeight = 30.93f, Centre = crypt.Centre, RoadTail = new List<Vector2> { roadEnd }, Reach = 64f };

        RoadSpatialGrid.PlannedPath? wayIn = RoadNetworkGenerator.PlanWayIn(RoadNetworkGenerator.WayInRoad.Alone(request), line, landing,
            crypt.Centre, RoadNetworkGenerator.RoadWidth, out string why, out RoadNetworkGenerator.WayInPlan? planned);

        Assert.True(wayIn != null && planned != null, why);
        int last = wayIn!.Points.Count - 1;
        Assert.True(Vector2.Distance(wayIn.Points[last], end) < 1f, $"the way in ends at {wayIn.Points[last]}, not at its landing {end}");
        Assert.True(Mathf.Abs(wayIn.Heights[last] - landing) <= StairLayout.StepUp,
            FormattableString.Invariant($"the way in ends at {wayIn.Heights[last]:F2}, the landing at {landing:F2}"));
        Assert.Contains(planned!.Crossings.Concat(planned.Found), c => c.Kind == CrossingKind.Ford && c.Style == FordStyle.Wade);
    }

    /// <summary>Where the road from the other Location starts: at that Location's ring, in its clearance band.</summary>
    private static readonly Vector2 FordCryptOther = new(0f, -119f);
    private const float FordCryptOtherRadius = 17.61f;

    /// <summary>The ground of <see cref="FordCryptGround"/> with a small pool (0.83 m deep, levelled at the wade floor) the road wades
    /// just past its start at the other Location's ring.</summary>
    private static float FordCryptGroundWithStartPool(float x, float z)
    {
        float h = FordCryptGround(x, z);
        float into = Mathf.Min(z + 100f, -90f - z);
        return into <= 0f ? h : Mathf.Min(h, Mathf.Lerp(30.93f, 29.17f, Mathf.Clamp01(into / 2f)));
    }

    /// <summary>
    /// The road as in game: from the other SunkenCrypt4's ring (inside its clearance band, as its own start lets it be), a short piece
    /// of land to a pool it wades (its first structure, levelled at the wade floor), the dirt, and its own wade into the crypt's pool to
    /// its ring end in the water. Its way onto the landing plans the road again from its last structure on: that is its first wade,
    /// whose stretch starts at the road's own start, so the stretch's first piece is the road's first and starts at the Location it
    /// leaves, as when it was planned (RoadNetworkGenerator.PlanPieces: the start's Location, Targets.Start). Planned without it, the
    /// piece out of the ring was refused "final road crosses a protected location" (in game the other crypt), every way in lost.
    /// </summary>
    [Fact]
    public void AWayInPlannedAgainFromTheRoadsFirstStructureKeepsTheLocationItLeaves()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(FordCryptGroundWithStartPool);
        crypt.Ground = FordCryptLevelledGround;
        PlacedLocationShape door = PlacedLocationShape.Of(crypt.Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, crypt.Ground, false, crypt.Record.Root,
            "SunkenCrypt4", entrance: 0, bodyKnown: true);
        crypt.Shape = door;
        crypt.Entrances = new[] { door };
        PlacedLocations.Ready(crypt);
        RoadLocationProtection.Set(new[]
        {
            new RoadLocationProtection.Footprint(crypt.Centre, Footprint),
            new RoadLocationProtection.Footprint(FordCryptOther, FordCryptOtherRadius),
        });
        // Waypoints a cell apart from the ring (18 m from the other's centre) to the ring end 22 m out in the crypt's pool.
        Vector2 At(float z) => new(0f, z);
        var path = new List<Vector2>();
        for (float z = -101f; z < -24f; z += RoadPathfinder.CellSize) path.Add(At(z));
        path.Add(At(-22f));
        int poolBank = path.FindIndex(p => p.y >= -29f);
        var first = new RoadCrossing
        {
            Kind = CrossingKind.Ford, Style = FordStyle.Wade, Shallow = true, FromIndex = 0, ToIndex = 3,
            FromBank = At(-100f), ToBank = path[3], RiverbedHeight = 29.17f, ShallowSite = At(-95f),
        };
        var tail = new RoadCrossing
        {
            Kind = CrossingKind.Ford, Style = FordStyle.Wade, Shallow = true, FromIndex = poolBank, ToIndex = path.Count - 1,
            FromBank = path[poolBank], ToBank = path[path.Count - 1], RiverbedHeight = 28.95f, ShallowSite = At(-25f),
        };
        RoadSpatialGrid.PlannedPath Piece(Vector2 a, Vector2 b, Func<Vector2, float> height, RoadCrossing? atStart, RoadCrossing? atEnd)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(a, b)));
            var points = Enumerable.Range(0, n + 1).Select(k => Vector2.Lerp(a, b, k / (float)n)).ToList();
            return new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(p => height(p)), points.ConvertAll(_ => default(RoadPointDebugInfo)),
                RoadNetworkGenerator.RoadWidth, points.Count - 1, false) { CrossingAtStart = atStart, CrossingAtEnd = atEnd };
        }
        float Land(Vector2 p) => RoadWater.AtLeastDryFloor(FordCryptGroundWithStartPool(p.x, p.y));
        var plans = new List<RoadSpatialGrid.PlannedPath>
        {
            Piece(path[0], first.FromBank, Land, null, first),
            Piece(first.FromBank, first.ToBank, _ => RoadConstants.SeaLevel - RoadConstants.FordWadeMaxDepth, first, first),
            Piece(first.ToBank, tail.FromBank, Land, first, tail),
            Piece(tail.FromBank, tail.ToBank, _ => RoadConstants.SeaLevel - RoadConstants.FordWadeMaxDepth, tail, tail),
        };
        plans[0].Locations = new RoadLocationProtection.Targets(FordCryptOther, null);
        plans[plans.Count - 1].Locations = new RoadLocationProtection.Targets(null, crypt.Centre);
        var crossings = new List<RoadCrossing> { first, tail };

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), crossings, RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why);

        Assert.DoesNotContain("protected location", why);
        Assert.True(entered && road != null, $"entered {entered}, staircase {span != null}: {why}");
        Vector2 last = road!.Points[road.Points.Count - 1];
        // Stopped at the landing's rim on its way toward the door (PlacedLocationShape.Ends), within the network's reach of it.
        // Its lip in the pool is wet (RoadWater.BuiltDry): then the dry landing itself.
        Assert.True((OnTheLip(door, last) || Vector2.Distance(last, door.Landing) < 0.5f) && Vector2.Distance(last, door.Landing) <= RoadNetworkGenerator.LandingReach,
            $"the way in ends at {last}, the landing {door.Landing}");
        Assert.True(RoadWater.Wet(FordCryptGround(last.x, last.y)), "the landing's natural ground is in the pool");
        float landing = crypt.Ground(last.x, last.y);
        Assert.True(Mathf.Abs(road.Heights[road.Heights.Count - 1] - landing) <= StairLayout.StepUp,
            FormattableString.Invariant($"the way in ends at {road.Heights[road.Heights.Count - 1]:F2}, the landing at {landing:F2}: ")
            + string.Join(" ", road.Points.Select((p, i) => FormattableString.Invariant($"({p.x:F1},{p.y:F1}) {road.Heights[i]:F2}"))) + $" follow {road.FollowTerrain}");
    }
}
