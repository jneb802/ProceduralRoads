using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase is built in the kit its own pieces stand in (StairKit.ForPlan), not the kit its road's line guesses: the
/// staircase from (-9344, -792) to (-9224, -792) on one world turned south at (-9253, -804) through Black Forest and
/// Mountain and came out black marble. The game's own biome along the straight line between its ends is 26 % Mistlands (a
/// band 30 m wide at the Mistlands' edge, north of where it turned), under the 40 % that makes black marble: its kit was
/// judged over a line other than its own.
/// </summary>
public class StairKitOwnPiecesTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A face of 0.8 from 40 m at x = 0 to 64 m at x = 30: too steep for a flight straight up, so a staircase
    /// winds across it, off its road. Sea past 200 m. The biome is <see cref="Biome"/>. With <see cref="Ramp"/>, the ground
    /// climbs to the face's foot over the last 4 m before it at that grade.</summary>
    private sealed class FaceWorld : WorldGenerator
    {
        public Func<float, float, Heightmap.Biome> Biome = (x, z) => Heightmap.Biome.Mountain;
        public float Ramp;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * 0.8f, 0f, 24f) + Ramp * Mathf.Clamp(wx, -4f, 0f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome(wx, wy);
        public override int GetSeed() => 77;
    }

    public StairKitOwnPiecesTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>Its road straight up the face along z = 0, the Mistlands (or everything but them) a strip 2 m wide along
    /// the road alone: the road's line is all one biome, the staircase winding across the face mostly the other.</summary>
    [Theory]
    [InlineData(true, "stone")]
    [InlineData(false, "blackmarble")]
    public void AStaircaseIsBuiltInTheKitItsOwnPiecesStandInNotItsRoads(bool mistlandsAlongTheRoad, string kit)
    {
        var world = new FaceWorld();
        Heightmap.Biome along = mistlandsAlongTheRoad ? Heightmap.Biome.Mistlands : Heightmap.Biome.Mountain;
        Heightmap.Biome off = mistlandsAlongTheRoad ? Heightmap.Biome.Mountain : Heightmap.Biome.Mistlands;
        world.Biome = (x, z) => Mathf.Abs(z) < 1f ? along : off;
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (int x = -48; x <= 64; x += 8) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2, Vector2)>();
        for (int x = 0; x < 24; x += 8) steps.Add((new Vector2(x, 0f), new Vector2(x + 8, 0f)));
        // The road's own line guesses the other kit.
        Assert.NotEqual(kit, StairKit.ForLine(path.GetRange(6, 4), (x, z) => world.GetBiome(x, z)).Name);

        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);

        Assert.NotNull(spans);
        RoadStairSpan span = Assert.Single(spans!);
        var walked = span.Plan.Pieces.Where(p => p.Kind != StairPieceKind.Support).ToList();
        int offTheRoad = walked.Count(p => Mathf.Abs(p.Position.z) >= 1f);
        Assert.True(offTheRoad >= RoadBiome.Share * walked.Count && walked.Count - offTheRoad < RoadBiome.Share * walked.Count,
            $"{offTheRoad} of {walked.Count} walked pieces off the road's strip: the case needs the staircase mostly off it");
        Assert.Equal(kit, span.Kit.Name);
        // Laid in it, not only named so.
        Assert.Equal(kit, span.Plan.Kit);
        Assert.All(walked.Where(p => p.Kind == StairPieceKind.Stair), p => Assert.Equal(span.Kit.Stair, p.Prefab));
        Assert.Equal(1, RoadStairRuns.KitsRelaid);
    }

    /// <summary>A lay a kit's relay throws away is not counted: the feet started lower, lowered, carried down to a crossing's
    /// end and the aprons cut at them are those of the staircases laid, as many as the same staircases laid in their own kit
    /// at once. Before, the first lay's were counted as well. The ground climbs to the face's foot steeper than the road may,
    /// so its staircase starts lower: set off from the ring at the road's end (RoadEndRing), a foot on level road no longer
    /// has an apron cut against the face beside it, which the case counted before.</summary>
    [Fact]
    public void ALayARelayThrowsAwayIsNotCounted()
    {
        var world = new FaceWorld { Ramp = 0.7f };
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (int x = -48; x <= 64; x += 8) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2, Vector2)>();
        for (int x = 0; x < 24; x += 8) steps.Add((new Vector2(x, 0f), new Vector2(x + 8, 0f)));
        (int startedLower, int lowered, int toEnds, int aprons, int relaid, string kit) Laid(Func<float, float, Heightmap.Biome> biome)
        {
            RoadNetworkGenerator.Reset();
            world.Biome = biome;
            RoadStairSpan span = Assert.Single(RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world)!);
            return (RoadStairRuns.FeetStartedLower, RoadStairRuns.FeetLowered, RoadStairRuns.FeetLoweredToEnds, RoadStairRuns.FootAprons,
                RoadStairRuns.KitsRelaid, span.Kit.Name);
        }
        var once = Laid((x, z) => Heightmap.Biome.Mountain);
        var relaid = Laid((x, z) => Mathf.Abs(z) < 1f ? Heightmap.Biome.Mistlands : Heightmap.Biome.Mountain);
        Assert.Equal((0, "stone"), (once.relaid, once.kit));
        Assert.Equal((1, "stone"), (relaid.relaid, relaid.kit));
        Assert.True(once.startedLower + once.lowered + once.toEnds + once.aprons > 0, "the case needs a foot started lower, lowered or an apron cut");
        Assert.Equal((once.startedLower, once.lowered, once.toEnds, once.aprons), (relaid.startedLower, relaid.lowered, relaid.toEnds, relaid.aprons));
    }

    /// <summary>The rule counts walked pieces only: a staircase's supports reach down and out to the ground, wherever that
    /// is, and are not where anyone walks.</summary>
    [Fact]
    public void AStaircasesKitCountsItsWalkedPiecesNotItsSupports()
    {
        var plan = new StairPlan();
        for (int i = 0; i < 6; i++) plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Position = new Vector3(2f * i, 0f, 0f) });
        for (int i = 0; i < 20; i++) plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Support, Position = new Vector3(2f * i, 0f, 50f) });
        Heightmap.Biome At(float x, float z) => z > 10f ? Heightmap.Biome.Mistlands : Heightmap.Biome.Mountain;

        Assert.Same(StairKit.Stone, StairKit.ForPlan(plan, At));
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Position = new Vector3(0f, 0f, 50f) });
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Position = new Vector3(2f, 0f, 50f) });
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Filler, Position = new Vector3(4f, 0f, 50f) });
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Filler, Position = new Vector3(6f, 0f, 50f) });
        // 4 of 10 walked pieces in the Mistlands: black marble.
        Assert.Same(StairKit.BlackMarble, StairKit.ForPlan(plan, At));
    }
}
