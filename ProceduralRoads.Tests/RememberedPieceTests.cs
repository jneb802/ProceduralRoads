using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The pieces of road remembered while an island is planned (RoadSpatialGrid.RemembersPlans): a piece asked again on the same
/// network, with the same asking and the same state of the road being planned, is answered exactly as it was planned; asked
/// otherwise, it is planned afresh.
/// </summary>
[Collection("RoadStatics")]
public class RememberedPieceTests : IDisposable
{
    /// <summary>Meadows climbing at <see cref="Grade"/> along x, flat at 40 m west of x = 0.</summary>
    private sealed class Climb : WorldGenerator
    {
        public float Grade = 0.25f;
        public override float GetHeight(float x, float z) => 40f + Mathf.Max(0f, x) * Grade;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }

    private readonly ValheimWorldScope _scope = new ValheimWorldScope();
    private readonly Climb world = new();

    public RememberedPieceTests()
    {
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Source = null;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
    }

    public void Dispose()
    {
        RoadSpatialGrid.RemembersPlans = false;
        RoadSpatialGrid.LevelledEnds = null;
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Reset();
        _scope.Dispose();
    }

    private static List<Vector2> Line(float from, float to) =>
        Enumerable.Range(0, 9).Select(i => new Vector2(Mathf.Lerp(from, to, i / 8f), 3f * Mathf.Sin(i))).ToList();

    private static void SameAs(RoadSpatialGrid.PlannedPath? expected, RoadSpatialGrid.PlannedPath? actual)
    {
        Assert.Equal(expected == null, actual == null);
        if (expected == null) return;
        Assert.Equal(expected.Points, actual!.Points);
        Assert.Equal(expected.Heights, actual.Heights);
        Assert.Equal(expected.Widths, actual.Widths);
        Assert.Equal(expected.Grades, actual.Grades);
        Assert.Equal(expected.Held, actual.Held);
        Assert.Equal(expected.Landed, actual.Landed);
        Assert.Equal(expected.TotalLength, actual.TotalLength);
    }

    private (RoadSpatialGrid.PlannedPath? plan, string? why, Vector2? at, int rests) Plan(List<Vector2> path, float? endGround = null)
    {
        int rests = RoadPitches.RestsRefusals;
        var plan = RoadSpatialGrid.PlanRoadPath(path, 4f, world, 40f, endGround);
        return (plan, RoadSpatialGrid.LastRefusal, RoadSpatialGrid.RefusedAt, RoadPitches.RestsRefusals - rests);
    }

    [Fact]
    public void APieceAskedAgainOnTheSameNetworkIsAnsweredAsItWasPlanned()
    {
        PlainRoads.WithModDefaults(() =>
        {
            var path = Line(-60f, 60f);
            var fresh = Plan(path);
            RoadSpatialGrid.RemembersPlans = true;
            var first = Plan(path);
            int before = RoadSpatialGrid.PiecesRecalled;
            var again = Plan(path);
            Assert.Equal(before + 1, RoadSpatialGrid.PiecesRecalled);
            SameAs(fresh.plan, again.plan);
            SameAs(first.plan, again.plan);
            Assert.Equal(fresh.why, again.why);
            Assert.Equal(fresh.at, again.at);
            // The caller's plan is its own: changing it changes nothing remembered.
            again.plan!.Heights[0] += 5f;
            SameAs(fresh.plan, Plan(path).plan);
        });
    }

    [Fact]
    public void APieceRefusedForAClimbTooSteepForItsRestsIsAnsweredWithItsRefusalAndCounted()
    {
        PlainRoads.WithModDefaults(() =>
        {
            world.Grade = 0.6f;
            var path = Line(-20f, 60f);
            var fresh = Plan(path);
            Assert.Null(fresh.plan);
            RoadSpatialGrid.RemembersPlans = true;
            Plan(path);
            RoadSpatialGrid.LastRefusal = "something else";
            RoadSpatialGrid.RefusedAt = null;
            int before = RoadSpatialGrid.PiecesRecalled;
            var again = Plan(path);
            Assert.Equal(before + 1, RoadSpatialGrid.PiecesRecalled);
            Assert.Null(again.plan);
            Assert.Equal(fresh.why, again.why);
            Assert.Equal(fresh.at, again.at);
            Assert.Equal(fresh.rests, again.rests);
        });
    }

    [Fact]
    public void APieceAskedOtherwiseIsPlannedAfresh()
    {
        PlainRoads.WithModDefaults(() =>
        {
            var path = Line(-60f, 60f);
            RoadSpatialGrid.RemembersPlans = true;
            Plan(path);
            int before = RoadSpatialGrid.PiecesRecalled;

            // Another end to meet.
            var otherEnd = Plan(path, 50f);
            RoadSpatialGrid.RemembersPlans = false;
            SameAs(Plan(path, 50f).plan, otherEnd.plan);
            RoadSpatialGrid.RemembersPlans = true;
            Plan(path);
            before = RoadSpatialGrid.PiecesRecalled;

            // A waypoint moved.
            var moved = new List<Vector2>(path);
            moved[4] += new Vector2(0f, 1f);
            Plan(moved);
            // What the road being planned set on this thread: a structure's end levelled.
            RoadSpatialGrid.LevelledEnds = new List<(Vector2 at, float height)> { (path[0], 41f) };
            var levelled = Plan(path);
            RoadSpatialGrid.RemembersPlans = false;
            SameAs(Plan(path).plan, levelled.plan);
            RoadSpatialGrid.LevelledEnds = null;
            RoadSpatialGrid.RemembersPlans = true;
            Plan(path);
            // The last resort's pitch.
            RoadPitches.AsLastResort("a test", () => Plan(path));
            // A trial's plan.
            RoadGrade.Trial(() => Plan(path));
            Assert.Equal(before, RoadSpatialGrid.PiecesRecalled);
        });
    }

    [Fact]
    public void AWriteToTheNetworkForgetsEveryPiece()
    {
        PlainRoads.WithModDefaults(() =>
        {
            var path = Line(-60f, 60f);
            RoadSpatialGrid.RemembersPlans = true;
            Plan(path);
            RoadNetworkGenerator.IslandNetworkChanged();
            int before = RoadSpatialGrid.PiecesRecalled;
            Plan(path);
            Assert.Equal(before, RoadSpatialGrid.PiecesRecalled);
        });
    }

    [Fact]
    public void NotRememberingEveryPieceIsPlannedAfresh()
    {
        PlainRoads.WithModDefaults(() =>
        {
            var path = Line(-60f, 60f);
            int before = RoadSpatialGrid.PiecesRecalled;
            Plan(path);
            Plan(path);
            Assert.Equal(before, RoadSpatialGrid.PiecesRecalled);
        });
    }

    /// <summary>A copy (what a remembered piece hands out) holds every field of the plan: a field added to PlannedPath and not
    /// copied fails here.</summary>
    [Fact]
    public void ACopyOfAPlanHoldsEveryFieldOfIt()
    {
        PlainRoads.WithModDefaults(() =>
        {
            var plan = RoadSpatialGrid.PlanRoadPath(Line(-60f, 60f), 4f, world, 40f, null)!;
            plan.Widths = plan.Points.Select(_ => 4f).ToList();
            plan.Unpainted = plan.Points.Select(_ => true).ToList();
            plan.Junctions ??= new List<RoadSpatialGrid.Junction>();
            plan.Mound = true;
            plan.LocationLandingStart = plan.LocationLandingEnd = true;
            plan.Grades ??= plan.Points.Select(_ => 0.3f).ToList();
            plan.Held ??= plan.Points.Select(_ => false).ToList();
            plan.Landed = (true, true);
            plan.Locations = new RoadLocationProtection.Targets(new Vector2(1f, 2f), new Vector2(3f, 4f));
            var copy = plan.Copy();
            foreach (FieldInfo f in typeof(RoadSpatialGrid.PlannedPath).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? a = f.GetValue(plan), b = f.GetValue(copy);
                if (a is IList la)
                {
                    Assert.NotSame(a, b);
                    Assert.Equal(la.Cast<object>(), ((IList)b!).Cast<object>());
                }
                else Assert.Equal(a, b);
            }
        });
    }
}
