using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A span across a valley (RoadStairRuns.SpanTheValley) is judged against the road over the whole stretch the two
/// staircases climb in place of it: its floors start on the first's flight, over the first's stretch, where no road is
/// built. Judged over the second's stretch alone, 7 of the 8 valleys tried were refused "on its road" (at
/// 2591,-6713 a floor over the first's last segment). Roads still may not cross: a span over another road is refused.
/// </summary>
public class ValleySpanRoadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly SharedFloorValleyWorld _world = new();

    /// <summary>The shared-floor valley (CantileverTests.SharedFloorValleyGround) as the world's ground.</summary>
    private sealed class SharedFloorValleyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => CantileverTests.SharedFloorValleyGround(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    public ValleySpanRoadTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The road through the shared-floor valley: from the west to the first's top at index 3, its last segment under the
    /// first flight's foot, the bottom at index 4, up the east face to the second's top at 6, on east.</summary>
    private static readonly List<Vector2> Road = new()
    {
        new(4360f, 6448f), new(4368f, 6448f), new(4376f, 6448f), new(4386f, 6448f),
        new(4392f, 6440f),
        new(4400f, 6440f), new(4408f, 6448f), new(4416f, 6448f), new(4424f, 6448f),
    };
    private const int FirstFrom = 3, Bottom = 4, SecondTo = 6;

    private bool Span(out StairPlan up, out string refusal)
    {
        var (below, second) = CantileverTests.SharedFloorValley();
        up = second;
        var first = new RoadStairSpan { FromIndex = FirstFrom, ToIndex = Bottom, Kit = StairKit.BlackMarble, Plan = below, ToPoint = Road[Bottom], ToHeight = 32.5f };
        var spans = new List<RoadStairSpan> { first };
        string why = "laid";
        bool spanned = RoadStairRuns.SpanTheValley(below, second, StairKit.BlackMarble, CantileverTests.SharedFloorValleyGround, 32.5f, 2f,
            FlatRunJoinTests.Relay(CantileverTests.SharedFloorValleyGround, CantileverTests.SharedFloorValleyTop, StairKit.BlackMarble),
            (p, at) =>
            {
                bool ok = RoadStairRuns.SpanUsable(p, Road, first, SecondTo, _world, spans, at);
                if (!ok) why = RoadStairRuns.LastRefusal.ToString();
                return ok;
            }, out StairPlan laid, out _);
        up = laid;
        refusal = why;
        return spanned;
    }

    private static float Across(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        return Vector2.Distance(p, a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude));
    }

    [Fact]
    public void ASpanOverTheFirstStaircasesStretchIsNotOnItsRoad()
    {
        Assert.True(Span(out StairPlan up, out string refusal), "refused: " + refusal);
        // The repro holds: a walked piece of the span stands over the first's last segment (index 3 -> 4), away from the
        // bottom's junction, so judged over the second's stretch alone (4..6) it was on its road.
        Assert.Contains(up.Pieces, p => p.Kind == StairPieceKind.Landing);
        Assert.Contains(up.Pieces, p =>
        {
            var at = new Vector2(p.Position.x, p.Position.z);
            return p.Kind != StairPieceKind.Support && Across(at, Road[FirstFrom], Road[Bottom]) < RoadStairRuns.OnRoad - 0.25f
                && Vector2.Distance(at, Road[Bottom]) > RoadStairRuns.JoiningStep;
        });
        Assert.True(RoadStairRuns.OverRoad(up, Road, Bottom, SecondTo, (x, z) => _world.GetHeight(x, z)), "judged over the second's stretch alone, not on its road");
    }

    [Fact]
    public void ASpanOverAnotherRoadIsStillRefused()
    {
        // Another road across the valley, north to south under the span's floors, clear of both flights' kept stairs.
        const float x = 4394.5f;
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(x, 6400f), new(x, 6420f), new(x, 6470f), new(x, 6490f) }, 4f, _world));
        RoadSpatialGrid.FinalizeRoadNetwork();
        var (below, second) = CantileverTests.SharedFloorValley();
        Assert.All(below.Pieces.Concat(second.Pieces).Where(p => p.Kind == StairPieceKind.Stair && p.Position.y > 34.4f),
            p => Assert.True(Mathf.Abs(p.Position.x - x) > RoadStairRuns.OnRoad, $"kept stair at {p.Position} on the other road"));
        Assert.False(Span(out StairPlan up, out string refusal));
        Assert.Equal(nameof(RoadStairRuns.Refusal.OnItsRoad), refusal);
        // Refused and put back as it was: no floors laid over the valley.
        Assert.DoesNotContain(up.Pieces, p => p.Kind == StairPieceKind.Landing && p.Position.y > 33f);
    }
}
