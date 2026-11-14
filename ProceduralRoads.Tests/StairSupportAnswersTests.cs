using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// What holds up each piece a road's stretch's layouts try is chosen once on the stretch's ground (StairSupport.Answers,
/// RoadStairRuns.StretchAnswers): StairSupport.Choose reads nothing but its kit, its walk and the ground, so a layout that
/// asks the answers a layout before it gave is the layout chosen afresh, field for field, and the answers it was given are
/// not chosen again. Before, every layout of a stretch (held to its road's headings, then free of them, of its cells, of its
/// facing) chose again what holds up most of the pieces the one before it had tried: about half of a generation's choices.
/// </summary>
public class StairSupportAnswersTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly bool _low = GenerationMemory.Low;

    private sealed class SharedFloorValleyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => CantileverTests.SharedFloorValleyGround(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 76;
    }

    /// <summary>The road through a valley (StairRowLayTests.ValleyRoad): down to the bottom at index 4, up to index 6.</summary>
    private static readonly List<Vector2> ValleyRoad = new()
    {
        new(4360f, 6448f), new(4368f, 6448f), new(4376f, 6448f), new(4386f, 6448f),
        new(4392f, 6440f),
        new(4400f, 6440f), new(4408f, 6448f), new(4416f, 6448f), new(4424f, 6448f),
    };

    public StairSupportAnswersTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        GenerationMemory.Low = _low;
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The ends of the shared climb (StairLayoutTests.Face, the plan laid with no answers).</summary>
    private static readonly Vector2 FaceFoot = new(-2061.41f, 1504.82f), FaceTop = new(-2125f, 1460f);

    /// <summary>
    /// The climb up a real Mountain face, laid after another layout of the same ends (held to a heading, as a road's first
    /// layout is) has given its answers: the same plan as laid with none, every field, and some of what holds it up answered,
    /// not chosen again.
    /// </summary>
    [Fact]
    public void ALayoutAskingAnotherLayoutsAnswersIsTheLayoutChosenAfresh()
    {
        var (fresh, ground) = StairLayoutTests.Face.Value;
        Assert.True(fresh.Reached, fresh.Reason);

        var answers = new StairSupport.Answers();
        StairLayout.Build(FaceFoot, FaceTop, ground, arriving: 200f, answers: answers);
        long before = StairSupport.AnswersReused;
        StairPlan asked = StairLayout.Build(FaceFoot, FaceTop, ground, answers: answers);
        long reused = StairSupport.AnswersReused - before;

        var differences = StairRowLayTests.Differences(fresh, asked);
        Assert.True(differences.Count == 0, string.Join("; ", differences));
        Assert.True(reused > 0, "nothing the first layout chose was answered for the second");
    }

    /// <summary>A layout asking answers of its own, empty to begin with, is the layout chosen afresh; asked again on them, it is
    /// answered whole: every piece it tries was chosen the first time.</summary>
    [Fact]
    public void ALayoutLaidAgainOnItsOwnAnswersIsTheSameAndChoosesNothing()
    {
        var (fresh, ground) = StairLayoutTests.Face.Value;
        var answers = new StairSupport.Answers();
        StairPlan first = StairLayout.Build(FaceFoot, FaceTop, ground, answers: answers);
        int chosen = answers.On(null, StairKit.Stone).Chosen;
        StairPlan again = StairLayout.Build(FaceFoot, FaceTop, ground, answers: answers);

        Assert.True(StairRowLayTests.Differences(fresh, first).Count == 0, string.Join("; ", StairRowLayTests.Differences(fresh, first)));
        Assert.True(StairRowLayTests.Differences(fresh, again).Count == 0, string.Join("; ", StairRowLayTests.Differences(fresh, again)));
        Assert.True(chosen > 0, "the layout chose nothing");
        Assert.Equal(chosen, answers.On(null, StairKit.Stone).Chosen);
    }

    /// <summary>Answers belong to the ground they were given on: past a road's end the search reads the ground levelled to the
    /// road there (RoadEndRing.RoadsGround), and another kit is held up its own way, so each has its own.</summary>
    [Fact]
    public void ARingAndAKitHaveAnswersOfTheirOwn()
    {
        var answers = new StairSupport.Answers();
        var ring = new RoadEndRing(new Vector2(1f, 2f), 90f, 40f);
        var moved = new RoadEndRing(new Vector2(1f, 2.5f), 90f, 40f);
        StairSupport.Answered plain = answers.On(null, StairKit.Stone);
        Assert.Same(plain, answers.On(null, StairKit.Stone));
        Assert.NotSame(plain, answers.On(ring, StairKit.Stone));
        Assert.Same(answers.On(ring, StairKit.Stone), answers.On(new RoadEndRing(new Vector2(1f, 2f), 90f, 40f), StairKit.Stone));
        Assert.NotSame(answers.On(ring, StairKit.Stone), answers.On(moved, StairKit.Stone));
        Assert.NotSame(plain, answers.On(null, StairKit.BlackMarble));
    }

    private static List<RoadStairSpan>? LayValley(WorldGenerator world)
    {
        var steps = new List<(Vector2 from, Vector2 to)> { (ValleyRoad[3], ValleyRoad[4]), (ValleyRoad[4], ValleyRoad[5]), (ValleyRoad[5], ValleyRoad[6]) };
        RoadStairRuns.ResetCache();
        return RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
    }

    /// <summary>A road's staircases laid with each stretch's answers kept (the generation keeping memory for speed) are those
    /// laid choosing every piece afresh (keeping less), field for field; kept, some of a stretch's layouts were answered.</summary>
    [Fact]
    public void ARoadsStaircasesLaidOnTheirStretchesAnswersAreThoseChosenAfresh()
    {
        var world = new SharedFloorValleyWorld();
        _scope.WithWorld(world);
        GenerationMemory.Low = true;
        long before = StairSupport.AnswersReused;
        List<RoadStairSpan>? afresh = LayValley(world);
        Assert.Equal(before, StairSupport.AnswersReused);
        GenerationMemory.Low = false;
        List<RoadStairSpan>? answered = LayValley(world);
        Assert.NotNull(afresh);
        var differences = StairRowLayTests.Differences(afresh, answered);
        Assert.True(differences.Count == 0, string.Join("; ", differences));
        Assert.True(StairSupport.AnswersReused > before, "no stretch's layout was answered");
    }
}
