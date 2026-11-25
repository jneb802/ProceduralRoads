using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A structure's end stairs land by terrain contact, one owner (docs/OWNERS.md, terrain contact: <see cref="SupportGraph.Against"/>):
/// a run marches on until every point of its last step's exit edge is within the game's support growth
/// (<see cref="SupportGraph.Reach"/>, 0.15 m) of the ground or in it (decision 6k: the support growth, not a walker's step,
/// <see cref="StairLayout.Walkable"/>), and a step whose foot is over the ground by more than that growth where its carrier
/// stands is carried; the support rule grounds a piece at the same height.
/// </summary>
public sealed class BridgeEndLandingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public BridgeEndLandingTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>The deck as built, and the first step's foot under it.</summary>
    private const float Deck = 34.9f, Foot = 33.9f;

    /// <summary>
    /// A 12 m bridge along x, its deck ends at x = -6 and 6, its deck at <see cref="Deck"/> (the water half a metre under it, as
    /// the railed kits' reference). The ground rises to the deck ends (32.9 m there); out from them, by the distance from the
    /// deck's centre, <see cref="Out"/> from x = 7 on (the first step's middle; its exit edge at 8), straight from 32.9 m at the
    /// deck end to it. Symmetric, so both ends are alike.
    /// </summary>
    private sealed class EndWorld : WorldGenerator
    {
        public Func<float, float, float> Out = (_, _) => Foot;

        public override float GetHeight(float wx, float wy)
        {
            float a = Mathf.Abs(wx);
            if (a <= 2f) return 28.9f;
            if (a < 6f) return 28.9f + (a - 2f);
            if (a < 7f) return Mathf.Lerp(32.9f, Out(7f, wy), a - 6f);
            return Out(a, wy);
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    private (RoadCrossing c, EndWorld w) Bridge(Func<float, float, float> outward)
    {
        var w = new EndWorld { Out = outward };
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = Deck - BridgeLayout.DeckFreeboard;
        Assert.Equal(Deck, BridgeLayout.DeckHeight(c, w), 3);
        return (c, w);
    }

    /// <summary>The natural end stairs' steps at both ends (StructureEnds.Natural, which the network plans by).</summary>
    private static (int near, int far) NaturalSteps(RoadCrossing c, WorldGenerator w)
    {
        var (near, far) = StructureEnds.Natural(c, w);
        return (near.Steps, far.Steps);
    }

    /// <summary>
    /// The ground out from the deck ends lower than the first step's foot by <paramref name="below"/>, everywhere (under its
    /// middle and all along its exit edge): within the growth (0.14 m) the run lands on its first step, its step stands on no
    /// carrier (no Dvergr pile, no railed kit's columns) and the support rule grounds a black marble stair standing there;
    /// past it (0.16 m, and 0.19 m, which a walker steps: <see cref="StairLayout.Walkable"/>) the run takes a second step, the
    /// first is carried, and the support rule grounds no stair there. Every caller of the one contact owner alike.
    /// </summary>
    [Theory]
    [InlineData(0.14f, true)]
    [InlineData(0.16f, false)]
    [InlineData(0.19f, false)]
    public void AFootLandsWithinTheSupportGrowthNotAWalkersStep(float below, bool lands)
    {
        float ground = Foot - below;
        var (c, w) = Bridge((_, _) => ground);
        Assert.True(StairLayout.Walkable(Foot, ground), "the foot is within a walker's step of the ground");
        int steps = lands ? 1 : 2;
        Assert.Equal((steps, steps), NaturalSteps(c, w));

        // The Dvergr wood kit: a pile under the first step's middle where it is carried.
        List<StructurePiece> dvergr = DvergrWoodBridge.Solve(c, w, 1);
        bool PileUnderFirst(StructurePiece p) => p.Prefab == DvergrWoodBridge.Pile && Mathf.Abs(Mathf.Abs(p.Position.x) - 7f) < 0.01f;
        Assert.Equal(!lands, dvergr.Any(PileUnderFirst));

        // The railed kit: columns under the first step's edges at its low joint (8, +-2) where it is carried.
        List<StructurePiece> railed = BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail);
        bool ColumnUnderFirst(StructurePiece p) => p.Prefab == BridgeLayout.PostPrefab
            && Mathf.Abs(Mathf.Abs(p.Position.x) - 8f) < 0.01f && Mathf.Abs(Mathf.Abs(p.Position.z) - 2f) < 0.01f;
        Assert.Equal(!lands, railed.Any(ColumnUnderFirst));

        // The support rule: a black marble stair (its box from its foot) standing alone at the foot.
        var stair = new StructurePiece { Kind = StructurePieceKind.Stair, Prefab = DvergrViaduct.StepPrefab, Position = new Vector3(7f, Foot, 0f), YawDegrees = 90f };
        ViaductSupport.Held held = Assert.Single(StairSupport.Holding(new List<StructurePiece> { stair }, (_, _) => ground));
        Assert.Equal(lands ? SupportGraph.Marble.Max : 0f, held.Support);
    }

    /// <summary>
    /// The first step's middle on the ground, its exit edge 0.16 m under its foot (within a walker's step, past the support
    /// growth): the run does not land there but on its second step. The landed edge is terrain contact, not walking
    /// (decision 6k, 9 Nov 2026: the game's 0.15 m support growth).
    /// </summary>
    [Fact]
    public void AnExitEdgeWithinAStepButPastTheGrowthHasNotLanded()
    {
        var (c, w) = Bridge((a, _) => a <= 8f ? Mathf.Lerp(Foot, Foot - 0.16f, a - 7f) : Foot - 0.16f);
        Assert.Equal((2, 2), NaturalSteps(c, w));
    }

    /// <summary>
    /// Each lane's exit edge on the ground at its corners (0.05 m under the foot) but dipping between them, to 0.30 m under it at
    /// the lane's middle (a mesh vertex the corners do not read): the edge is read all along its length, so the run does not land
    /// on its first step but marches on to its second; the kits lay that run.
    /// </summary>
    [Fact]
    public void ARunWhoseExitEdgeDipsBetweenItsCornersMarchesOn()
    {
        float Edge(float z) => Foot - 0.05f - 0.25f * Mathf.Max(0f, 1f - Mathf.Abs(Mathf.Abs(z) - 1f));
        var (c, w) = Bridge((a, z) => a <= 8f ? Mathf.Lerp(Foot, Edge(z), a - 7f) : Edge(z));
        Assert.Equal((2, 2), NaturalSteps(c, w));
        List<StructurePiece> railed = BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail);
        Assert.Equal(8, railed.Count(p => p.Prefab == BridgeLayout.StairPrefab));
    }
}
