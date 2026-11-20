using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing that stands in a swamp's shallows (in game a SunkenCrypt4 at (1345.8, 2799.0): its
/// landing at (1342, 2809), the shallows from the road's side 25.6 m on under the landing to the crypt's centre, the crypt's own
/// ground there 27.50). The road's detector measured the whole pool beyond the landing, as at a through crossing, so a wade was
/// judged against the crypt's ground past the landing, refused, and a span put across the pool from the road's bank to the crypt's
/// centre ("[ENDS] near end of Ford/Span (1345.79, 2799.13) -> (1336.01, 2822.74), 25.6 m not met"): a bridge end the road does
/// not meet, the crypt lost. The way in and its crossing end at the landing; the detector judges only the stretch the road walks.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The swamp round a sunken crypt: the shallows (<see cref="SwampShallows"/>) from 30 m out in to 5 m, the crypt's own
    /// low ground under them within 5 m (out of the water, short of the dry line: no bank a wade climbs onto), dry dirt
    /// (<see cref="CryptDirt"/>) from 34 m out.</summary>
    private static float SunkenSwamp(float x, float z)
    {
        float r = new Vector2(x, z).magnitude;
        return r < 5f ? RoadConstants.SeaLevel + 0.2f : r < 30f ? SwampShallows : Mathf.Lerp(SwampShallows, CryptDirt, Mathf.Clamp01((r - 30f) / 4f));
    }

    /// <summary>The crypt's landing in the shallows, the road's end on the dirt 50 m out in front: the way in wades the shallows from the
    /// road's bank and ends at its landing (RoadLocationEntry.WayInEnd); its crossing's banks are the road's and the landing, none
    /// past the landing toward the crypt's centre.</summary>
    [Fact]
    public void AWayInToALandingInTheShallowsWadesToTheLandingAndNoFurther()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(SunkenSwamp);
        Vector2 roadEnd = new(0f, -50f);
        PlacedLocationShape door = crypt.Entrances[0];
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        Vector2 end = RoadLocationEntry.WayInEnd(door, roadEnd, walker);
        Assert.True(RoadWater.Wet(crypt.Ground(end.x, end.y)), $"the landing at {end} is in the shallows");
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, walker);
        var request = new LocationApproachRequest { RoadEnd = roadEnd, RoadHeight = CryptDirt, Centre = crypt.Centre, RoadTail = new List<Vector2> { roadEnd }, Reach = 64f };

        RoadSpatialGrid.PlannedPath? wayIn = RoadNetworkGenerator.PlanWayIn(RoadNetworkGenerator.WayInRoad.Alone(request), line, crypt.Ground(end.x, end.y),
            crypt.Centre, RoadNetworkGenerator.RoadWidth, out string why, out RoadNetworkGenerator.WayInPlan? planned);

        Assert.True(wayIn != null && planned != null, why);
        Vector2 last = wayIn!.Points[wayIn.Points.Count - 1];
        Assert.True(Vector2.Distance(last, end) < 1f, $"the way in ends at {last}, not at its landing {end}");
        foreach (RoadCrossing c in planned!.Crossings)
            Assert.True(c.FromBank.magnitude >= end.magnitude - 0.5f && c.ToBank.magnitude >= end.magnitude - 0.5f,
                FormattableString.Invariant($"a {c.Kind} {c.Style} from ({c.FromBank.x:F1}, {c.FromBank.y:F1}) to ({c.ToBank.x:F1}, {c.ToBank.y:F1}) past the landing at {end.magnitude:F1} m"));
        Assert.True(wayIn.FollowTerrain, "the way in onto a landing in the shallows is the road's own wade, the bed's own surface");
    }
}
