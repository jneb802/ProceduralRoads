using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase onto a Location that climbs onto ground the walk to the landing reaches without stairs ends there: no second
/// staircase is laid over that ground down onto the landing (RoadStairRuns.Turn's crest, judged by RoadStairRuns.FlatsFrom).
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The mountain cave in game: its road arriving up a steep face, the Location's levelled ground at the top of the face
    /// (153.5), its entrance's landing 1.8 m lower on that ground (151.7), 13 m in from the face's top and rising gently to it.
    /// Here: a mound with a top at 62 out to 16 m, a face falling 0.5 in 1 outside it, its inside a bowl falling 1.8 m to 8 m
    /// from the centre (0.225 in 1, ground a player walks), the crypt's entrance's landing in the bowl. The rim stands 1.8 m over
    /// the landing, a crest by the ground alone (RoadStairRuns.CrestRise). The first flight up the face makes it onto ground the
    /// walk to the landing needs no stairs on; nothing more is laid past it, down onto the landing.</summary>
    [Fact]
    public void AStaircaseUpOntoGroundWalkedToTheLandingLaysNoSecondStaircaseOntoIt()
    {
        const float top = 12f, bowl = 1.8f;
        MoundWorld world = Mound(Heightmap.Biome.Mountain, 0.5f, top);
        world.Ground = (x, z) =>
        {
            float r = new Vector2(x, z).magnitude;
            return 50f + Mathf.Clamp(top - 0.5f * (r - 16f), 0f, top) - bowl * Mathf.Clamp01((16f - r) / 8f);
        };
        PlacedLocation crypt = CryptOnAMound(world, top - bowl);
        var (entered, _, span, why, arrival, _, _) = StairsOntoTheCrypt(world, crypt, 36f);
        Assert.True(entered, why);
        Assert.NotNull(span);
        var walked = span!.Plan.Pieces.Where(p => p.Kind is StairPieceKind.Stair or StairPieceKind.Landing).ToList();
        string laid = string.Join("; ", walked.Select(p => FormattableString.Invariant($"{p.Kind} ({p.Position.x:F1}, {p.Position.y:F2}, {p.Position.z:F1})")))
            + $"; arrival {(arrival == null ? "none" : EntranceArrivals.Name(arrival.Target))}";
        // Up the face only: one staircase, its legs climbing no more than it rises (a crest pair's legs climb both ways).
        Assert.True(span.Plan.LegClimb <= Mathf.Abs(span.Plan.Rise) + 0.01f,
            FormattableString.Invariant($"its legs climb {span.Plan.LegClimb:F2} m, it rises {span.Plan.Rise:F2} m: {laid}"));
        // Past its highest piece nothing climbs again (a second staircase up from the landing to meet it), and at most the end
        // rule's one reversed stair steps down off its top (StairLayout.FinishTop).
        float highest = walked.Max(p => p.Position.y);
        int topAt = walked.FindIndex(p => p.Position.y >= highest - 0.01f);
        var past = walked.Skip(topAt).ToList();
        for (int i = 1; i < past.Count; i++)
            Assert.True(past[i].Position.y <= past[i - 1].Position.y + 0.01f, "a staircase climbs back up past its top: " + laid);
        Assert.True(past.Skip(1).Count(p => p.Kind == StairPieceKind.Stair) <= 1, "more than one stair laid down off its top: " + laid);
    }
}
