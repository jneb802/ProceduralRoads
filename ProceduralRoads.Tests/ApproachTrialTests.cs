using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An approach trial (RoadNetworkGenerator.PlanFromPath's: the whole road with an approach a candidate proposes, planned by
/// PlanCandidate and judged by BuiltRoadRefusal) made again exactly while one road is planned is decided as it was: nothing is
/// stored while one road is planned. Remembered from the road's first search to its last (RoadNetworkGenerator.OneRoad, as
/// GenerateRoadCore begins it) and forgotten as it ends; the key is everything the trial reads (RoadNetworkGenerator.ApproachTrial).
/// </summary>
[Collection("RoadStatics")]
public sealed class ApproachTrialTests : IDisposable
{
    /// <summary>A ridge east of the location at the origin (LocationApproachAcceptanceTests): the approach from the east goes round it.</summary>
    private sealed class Hillside : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            60f + 8f * (float)Math.Exp(-Math.Pow((x - 28f) / 16f, 2) - Math.Pow(z / 7f, 2));
    }

    private readonly WorldGenerator _world = new Hillside();
    private readonly ValheimWorldScope _scope;
    private readonly IDisposable _source = StaticOverride.Keep(() => LocationLevelling.Source);
    private readonly IDisposable _grade = GradeCap.At(0.35f);
    private readonly RoadPathfinder _finder;

    public ApproachTrialTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope = new ValheimWorldScope().WithWorld(_world);
        _finder = new RoadPathfinder(_world);
        // The location levels the ground round its centre, so its approach is improved.
        LocationLevelling.Source = c => c.magnitude < 0.5f ? new[] { new LevelOp(0, 0, 0f, 8, false) } : null;
        // Another road south of the ridge: the best-ranked approach round it crosses that road and is refused, the one north
        // of it is built (LocationApproachAcceptanceTests).
        var other = new List<Vector2>();
        for (float t = 0f; t <= 16f; t += 4f) other.Add(new Vector2(32f, -12f - t));
        RoadSpatialGrid.Commit(RoadSpatialGrid.PlanRoadPath(other, 4, _world)!);
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _grade.Dispose();
        _source.Dispose();
        _scope.Dispose();
    }

    /// <summary>The road from 200 m east to the location's keep-out edge, planned as generation plans it.</summary>
    private RoadNetworkGenerator.RoadPlan Planned()
    {
        var path = new List<Vector2>();
        for (int x = 200; x >= 0; x -= 8) path.Add(new Vector2(x, 0));
        return RoadNetworkGenerator.PlanFromPath(_finder, path, path[0], 0f, new Vector2(0f, 0f), 16f, false, false, true, false, 4f);
    }

    /// <summary>A plan as it decides the road: where it stops, its path, and every planned piece's points and heights.</summary>
    private static string Said(RoadNetworkGenerator.RoadPlan plan) =>
        plan.Stop + " | " + string.Join(" ", plan.Path.Select(p => FormattableString.Invariant($"({p.x:R},{p.y:R})"))) + " | "
        + (plan.Plans == null ? "none" : string.Join(" / ", plan.Plans.Select(pp =>
            string.Join(" ", pp.Points.Select((p, i) => FormattableString.Invariant($"({p.x:R},{p.y:R},{pp.Heights[i]:R})"))))));

    /// <summary>
    /// The same road planned twice within one road (as a search again plans it): the second's trials are the first's, each
    /// decided as it was, and the road is planned the same, as it is planned outside a road (nothing remembered) and after the
    /// road ends (forgotten).
    /// </summary>
    [Fact]
    public void ATrialMadeAgainWithinOneRoadIsDecidedAsItWasAndTheRoadIsPlannedTheSame()
    {
        int before = RoadNetworkGenerator.TrialsRepeated;
        string alone = Said(Planned());
        Assert.Contains("Planned", alone);
        Said(Planned());
        Assert.Equal(before, RoadNetworkGenerator.TrialsRepeated);
        string first, second;
        int afterFirst;
        using (RoadNetworkGenerator.OneRoad())
        {
            first = Said(Planned());
            afterFirst = RoadNetworkGenerator.TrialsRepeated;
            // A road's searches again begin no trials of their own: they are the same road's.
            using (RoadNetworkGenerator.OneRoad()) second = Said(Planned());
        }
        Assert.True(RoadNetworkGenerator.TrialsRepeated > afterFirst, "no trial was decided as it was");
        Assert.Equal(alone, first);
        Assert.Equal(alone, second);
        int ended = RoadNetworkGenerator.TrialsRepeated;
        Assert.Equal(alone, Said(Planned()));
        Assert.Equal(ended, RoadNetworkGenerator.TrialsRepeated);
    }

    /// <summary>
    /// A trial is decided again only where it is the same trial: one more piece of network (the island's network version
    /// moved on, as every store moves it), or another search's stair steps, is another trial.
    /// </summary>
    [Fact]
    public void ATrialIsDecidedAsItWasOnlyOnTheSameNetwork()
    {
        using (RoadNetworkGenerator.OneRoad())
        {
            Planned();
            int made = RoadNetworkGenerator.TrialsRepeated;
            RoadNetworkGenerator.IslandNetworkChanged();
            Planned();
            Assert.Equal(made, RoadNetworkGenerator.TrialsRepeated);
        }
    }

    /// <summary>
    /// An approach trial is judged by the one list with its location end, as generation's road is (RoadNetworkGenerator.RoadRefusal,
    /// its location ends' refusals first): the road from the east to the location, with that very arrival already a way into the
    /// location (LocationWaysIn.Record). Generation's own test of its end (RoadNetworkGenerator.ReEndOnAWayIn) sends that road to
    /// end on the way's road instead, refused as built so; a trial of it (RoadNetworkGenerator.TrialBuilds, what PlanFromPath
    /// offers each candidate) is refused too. On the parent the trial asked none of the location-end refusals (no way onto a
    /// landing, the meeting with a landing on a flight, the re-end, an end not level): it passed, and a candidate so judged
    /// could push out an approach the road itself was then refused for.
    /// </summary>
    [Fact]
    public void AnApproachTrialIsRefusedForItsLocationEndAsGenerationRefusesIt()
    {
        // One way in up a face (LocationWaysIn) is the mod's default.
        using var snap = StaticOverride.Keep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
        // Clear of the ridge east of the location (the road along its crest is refused for the ridge, judged with or
        // without an earthwork cap): from the east 24 m north of it, then in from the north.
        var path = new List<Vector2>();
        for (int x = 200; x >= 0; x -= 8) path.Add(new Vector2(x, 24f));
        path.Add(new Vector2(0f, 16f)); path.Add(new Vector2(0f, 8f)); path.Add(new Vector2(0f, 0f));
        Vector2 location = new(0f, 0f);
        bool Trial()
        {
            using (RoadNetworkGenerator.OneRoad())
                return RoadNetworkGenerator.TrialBuilds(_finder, path, path[0], 0f, location, 16f, false, false, true, false, 4f, 0f, 16f);
        }
        Assert.True(Trial(), RoadSpatialGrid.LastRefusal);
        RoadNetworkGenerator.RoadPlan plan = RoadNetworkGenerator.PlanCandidate(_finder, path, path[0], 0f, location, 16f, false, false, true, false, 4f);
        Assert.True(plan.Plans != null && plan.EndAtLocation, RoadSpatialGrid.LastRefusal);
        LocationWaysIn.Way way = LocationWaysIn.Of(plan.Plans!.SelectMany(p => p.Points).ToList(), plan.Plans!.SelectMany(p => p.Heights).ToList(), location)!;
        Assert.NotNull(way);
        LocationWaysIn.Record(location, way);
        try
        {
            Assert.NotNull(RoadNetworkGenerator.ReEndOnAWayIn(location, way));
            Assert.False(Trial(), "the trial passed a road generation sends to end on the location's way in");
        }
        finally { LocationWaysIn.Reset(); }
    }

    private static readonly ConstructorInfo Ctor = typeof(RoadNetworkGenerator.ApproachTrial).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();

    private object?[] Arguments() => Ctor.GetParameters().Select(p => p.Name switch
    {
        "finder" => (object?)_finder,
        "road" => new List<Vector2> { new(200f, 0f), new(100.5f, 2.25f), new(20f, 1f) },
        "steps" => new List<(Vector2 from, Vector2 to)> { (new Vector2(100f, 0f), new Vector2(104f, 0f)), (new Vector2(104f, 0f), new Vector2(108f, 4f)) },
        "fords" => true,
        "bridges" => true,
        "stairs" => true,
        "startCenter" => new Vector2(200f, 0f),
        "startRadius" => 0f,
        "endCenter" => new Vector2(0f, 0f),
        "endRadius" => 16f,
        "startPlaced" => false,
        "endPlaced" => false,
        "startReached" => true,
        "endReached" => false,
        "width" => 4f,
        "world" => _world,
        "network" => 3,
        "ground" => 5,
        "unpricedLedge" => false,
        "shaping" => 0,
        _ => throw new InvalidOperationException($"{p.Name}: not covered by this test"),
    }).ToArray();

    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
    private static float Float(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

    private IEnumerable<(string what, object? value)> Others(string name, object? value)
    {
        switch (value)
        {
            case bool b: yield return (name, !b); break;
            case int i: yield return (name, i + 1); break;
            case float f: yield return (name, f + 0.25f); yield return (name + " by an ulp", Float(Bits(f) + 1)); break;
            case Vector2 v: yield return (name + ".x", new Vector2(v.x + 0.25f, v.y)); yield return (name + ".y", new Vector2(v.x, v.y + 0.25f)); break;
            case RoadPathfinder: yield return (name, new RoadPathfinder(_world)); break;
            case WorldGenerator: yield return (name, new Hillside()); yield return (name + " none", null); break;
            case List<Vector2> road:
                yield return (name + " a point fewer", road.Take(road.Count - 1).ToList());
                yield return (name + " a point more", road.Concat(new[] { new Vector2(10f, 0f) }).ToList());
                for (int i = 0; i < road.Count; i++)
                {
                    var moved = new List<Vector2>(road);
                    moved[i] = new Vector2(road[i].x, road[i].y + 0.25f);
                    yield return ($"{name}[{i}]", moved);
                }
                break;
            case List<(Vector2 from, Vector2 to)> steps:
                yield return (name + " none", new List<(Vector2, Vector2)>());
                yield return (name + " a step fewer", steps.Take(steps.Count - 1).ToList());
                for (int i = 0; i < steps.Count; i++)
                {
                    var from = new List<(Vector2 from, Vector2 to)>(steps);
                    from[i] = (steps[i].from + new Vector2(0f, 0.25f), steps[i].to);
                    yield return ($"{name}[{i}].from", from);
                    var to = new List<(Vector2 from, Vector2 to)>(steps);
                    to[i] = (steps[i].from, steps[i].to + new Vector2(0f, 0.25f));
                    yield return ($"{name}[{i}].to", to);
                }
                break;
            default: throw new InvalidOperationException($"{name}: {value?.GetType().Name ?? "null"} not covered by this test");
        }
    }

    /// <summary>The key is everything the trial reads: every field one of its arguments, and two trials differing in any one
    /// of them, by as little as a float's last bit, are two trials.</summary>
    [Fact]
    public void TwoTrialsDifferingInAnyOneInputAreTwoTrials()
    {
        object?[] args = Arguments();
        var key = (RoadNetworkGenerator.ApproachTrial)Ctor.Invoke(args);
        ParameterInfo[] ps = Ctor.GetParameters();
        var fields = typeof(RoadNetworkGenerator.ApproachTrial).GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name.ToLowerInvariant()).OrderBy(n => n);
        Assert.Equal(ps.Select(p => p.Name!.ToLowerInvariant()).OrderBy(n => n), fields);
        var again = (RoadNetworkGenerator.ApproachTrial)Ctor.Invoke(Arguments());
        Assert.True(key.Equals(again) && key.GetHashCode() == again.GetHashCode(), "the same trial is not the same key");
        int variants = 0;
        for (int i = 0; i < ps.Length; i++)
            foreach (var (what, value) in Others(ps[i].Name!, args[i]))
            {
                object?[] changed = (object?[])args.Clone();
                changed[i] = value;
                Assert.False(key.Equals((RoadNetworkGenerator.ApproachTrial)Ctor.Invoke(changed)), $"a trial differing only in {what} shares the key");
                variants++;
            }
        Assert.True(variants > ps.Length, $"{variants} variants");
    }
}
