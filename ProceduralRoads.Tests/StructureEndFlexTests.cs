using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An end's steps are a look, not a rule (StructureEnds.MeetEnds): an end starts from the look the bank gives it, flush or
/// stepped; where its road does not fit (past the earthwork cap as every road's earthwork is measured, across its width,
/// RoadNetworkGenerator.EarthworkAcross), the other is tried within the structure's limits before the crossing is given
/// up, both ways. A viaduct on a sidehill whose flush end's road runs on under the deck's end over the valley's rim at
/// its downhill edge is met by a step instead; an end whose step's road is cut past the cap into a bank at its uphill edge
/// is met flush.
/// </summary>
public sealed class StructureEndFlexTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StructureEndFlexTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>A Mistlands world whose ground is <see cref="Height"/>; sea past |x|, |z| = 200.</summary>
    private sealed class Valley : WorldGenerator
    {
        public Func<float, float, float> Height = (_, _) => 60f;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : Height(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 91;
    }

    private const float Top = 60f, Depth = 16f, Cliff = 10f;

    /// <summary>The ground down from a flat at <paramref name="top"/> whose rim is at <paramref name="rim"/> (metres
    /// <paramref name="inward"/> of it are over the valley), a cliff of <see cref="Cliff"/> to the floor
    /// <see cref="Depth"/> under <see cref="Top"/>.</summary>
    private static float Down(float top, float inward) => Mathf.Max(Top - Depth, top - Mathf.Clamp(inward, 0f, 100f) * Cliff);

    /// <summary>The deck's two end edges (x) of the valley jump from -24 to 24 along z = 0: the module grid's, the same on
    /// any valley without water.</summary>
    private (float near, float far) Edges()
    {
        var probe = new Valley { Height = (x, _) => Mathf.Abs(x) > 20f ? Top : Top - Depth };
        _scope.WithWorld(probe);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), probe, out string why)
            ?? throw new InvalidOperationException(why);
        var (near, far) = DvergrViaduct.EdgesOf(c, probe);
        return (near.x, far.x);
    }

    /// <summary>The viaduct across <paramref name="world"/> from -24 to 24 along z = 0, its ends met by the one owner from
    /// the road along the line (StructureEnds.MeetEnds, the earthwork cap): its two ends as met, or null where one is not.</summary>
    private (StructureEnds.End near, StructureEnds.End far)? Met(Valley world)
    {
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)
            ?? throw new InvalidOperationException(why);
        c.FromIndex = 1; c.ToIndex = 2;
        var path = new List<Vector2> { new(-90f, 0f), c.FromBank, c.ToBank, new(90f, 0f) };
        if (StructureEnds.MeetEnds(path, new List<RoadCrossing> { c }, world, RoadConstants.TerrainDeltaMax, null) != null) return null;
        return BridgeStructures.Ends(c, world);
    }

    /// <summary>The road an end is met by as it is stored there: run on under the joining piece from its joint to its foot,
    /// then levelled over its apron, at the meeting height, every metre; how many of its points are past the cap as every
    /// road's earthwork is measured (RoadNetworkGenerator.EarthworkAcross, the road's width).</summary>
    private static int PastAcross(StructureEnds.End end, Valley world)
    {
        var points = new List<Vector2>();
        for (float d = StructureEnds.JoinOverlap; d >= -StructureEnds.ApronLength - 1e-3f; d -= 1f) points.Add(end.Meet + end.Up * d);
        var heights = points.ConvertAll(_ => end.MeetHeight);
        var plan = new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), RoadConstants.DefaultRoadWidth, 0f, false);
        return RoadNetworkGenerator.EarthworkAcross(plan, RoadNetworkGenerator.GroundBeforeRoads(world), RoadConstants.TerrainDeltaMax).Past;
    }

    /// <summary>The end and the ground beside its road's joint and apron, for a failure's message.</summary>
    private static string Describe(StructureEnds.End end, Valley world)
    {
        var parts = new List<string> { FormattableString.Invariant($"edge {end.Edge} up {end.Up} height {end.Height:F2} steps {end.Steps}") };
        for (float d = StructureEnds.JoinOverlap; d >= -2f; d -= 1f)
        {
            Vector2 p = end.Meet + end.Up * d;
            parts.Add(FormattableString.Invariant($"({p.x:F1}: {world.GetHeight(p.x, -2f):F1}/{world.GetHeight(p.x, 0f):F1}/{world.GetHeight(p.x, 2f):F1})"));
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// A viaduct whose banks the road arrives level with, the valley's far rim slanting across the road: on the road's line
    /// it lies 2.5 m in from the deck's end, at the road's downhill edge 1 m. The flush end's road, run on 2 m under the
    /// deck's end (StructureEnds.JoinOverlap), stands over the rim's cliff at that edge, past the cap (the finished road
    /// would be refused for it, RoadNetworkGenerator.EarthworkRefusal); met by its one step down for looks, the road meets
    /// the step's foot on the bank, within the cap. Before, the end was met flush on the road's centre alone.
    /// </summary>
    [Fact]
    public void AFlushEndPastTheCapAtItsDownhillEdgeIsMetByAStep()
    {
        var (ne, fe) = Edges();
        float nearRim = ne + 2.5f, farRim = fe - 2.5f;
        // The far rim slants: 0.75 m further out for every metre across, to the road's downhill edge (z = 2) 1 m from the
        // deck's end.
        var world = new Valley
        {
            Height = (x, z) => x < 0f ? Down(Top, x - nearRim) : Down(Top, farRim + 0.75f * z - x),
        };
        _scope.WithWorld(world);
        var flush = DvergrViaduct.Ends(DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out _)!, world).far;
        Assert.Equal(0, flush.Steps);
        Assert.True(PastAcross(flush, world) > 0, $"the flush end's road is within the cap: the world does not set the case; {Describe(flush, world)}");

        var met = Met(world);
        Assert.True(met.HasValue, "the viaduct's ends are not met");
        var (near, far) = met!.Value;
        Assert.Equal(0, near.Steps);
        Assert.True(far.Steps >= 1, $"the far end met with {far.Steps} steps: its road is past the cap at its downhill edge");
        Assert.Equal(0, PastAcross(far, world));
    }

    /// <summary>
    /// The other way round: a viaduct whose far flat lies a metre under its deck, an end the bank takes by one step
    /// (StructureEnds.Decide), low (StructureEnds.Low), met by it for nothing. A bank rises steeply beside the road's uphill
    /// edge for the step's apron, 7.5 m over the deck: the step's road, a metre under the deck, is cut 8.5 m into it there,
    /// past the cap; flush with the deck, 7.5 m. The end is met flush. Before, it was met by its step on the road's centre
    /// alone.
    /// </summary>
    [Fact]
    public void AStepPastTheCapAtItsUphillEdgeIsMetFlush()
    {
        var (ne, fe) = Edges();
        float nearRim = ne + 2.5f, farRim = fe - 2.5f;
        float Bank(float x, float z) => x >= fe - 3f && x <= fe + StructureEnds.ApronLength + 0.5f ? 8.5f * Mathf.Clamp01(z - 1f) : 0f;
        var world = new Valley
        {
            Height = (x, z) => x < 0f ? Down(Top, x - nearRim) : Down(Top - 1f, farRim - x) + Bank(x, z),
        };
        _scope.WithWorld(world);
        var stepped = DvergrViaduct.Ends(DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out _)!, world).far;
        Assert.Equal(1, stepped.Steps);
        Assert.True(PastAcross(stepped, world) > 0, $"the step's road is within the cap: the world does not set the case; {Describe(stepped, world)}");

        var met = Met(world);
        Assert.True(met.HasValue, "the viaduct's ends are not met");
        var far = met!.Value.far;
        Assert.Equal(0, far.Steps);
        Assert.Equal(0, PastAcross(far, world));
    }

    /// <summary>The most steps an end may be met by: a viaduct's within its end stairs' most (DvergrViaduct.MaxEndStairRise,
    /// 3 m: three), every other kit's the most a run marches (BridgeLayout.MaxStairSteps).</summary>
    [Fact]
    public void TheMostStepsAreEachStructuresOwn()
    {
        var world = new Valley { Height = (x, _) => Mathf.Abs(x) > 20f ? Top : Top - Depth };
        _scope.WithWorld(world);
        RoadCrossing viaduct = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)
            ?? throw new InvalidOperationException(why);
        Assert.Equal(3, StructureEnds.MostSteps(viaduct, world));
        RoadCrossing bridge = RoadCrossing.Between(new Vector2(-24f, 0f), new Vector2(24f, 0f), Top - Depth, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        bridge.Kit = BridgeStructures.Kit.WoodRail;
        Assert.Equal(BridgeLayout.MaxStairSteps, StructureEnds.MostSteps(bridge, world));
    }
}
