using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Structure ends remembered while an island is planned (StructureEnds.MeetEndsRemembered, on with RoadSpatialGrid.RemembersPlans):
/// the same road and crossings met again on the same network are answered as they were met (the path, every field of each
/// crossing, the crossing unmet); asked otherwise, met afresh.
/// </summary>
[Collection("RoadStatics")]
public sealed class RememberedEndsTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly DvergrViaductEndStairTests.StepValley world = new() { FarDrop = _ => 1f };
    private static readonly (Vector2, Vector2)[] NoSteps = Array.Empty<(Vector2, Vector2)>();

    public RememberedEndsTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.WithWorld(world);
    }

    public void Dispose()
    {
        RoadSpatialGrid.RemembersPlans = false;
        RoadSpatialGrid.LevelledEnds = null;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private RoadCrossing Fresh()
    {
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out _)!;
        c.FromIndex = 1; c.ToIndex = 2;
        return c;
    }

    private static List<Vector2> Road(RoadCrossing c, float off = 10f) =>
        new() { new(-60f, 0f), c.FromBank, c.ToBank, c.ToBank + new Vector2(0f, off), new(80f, 30f) };

    private (List<Vector2> path, RoadCrossing crossing, RoadCrossing? unmet) Meet(float off = 10f, float cap = float.PositiveInfinity,
        Action<RoadCrossing>? change = null)
    {
        RoadCrossing c = Fresh();
        change?.Invoke(c);
        var path = Road(c, off);
        var unmet = StructureEnds.MeetEndsRemembered(path, new List<RoadCrossing> { c }, world, cap, NoSteps, (_, _) => false, RoadConstants.DefaultRoadWidth);
        return (path, c, unmet);
    }

    private static void SameAs((List<Vector2> path, RoadCrossing crossing, RoadCrossing? unmet) expected,
        (List<Vector2> path, RoadCrossing crossing, RoadCrossing? unmet) actual)
    {
        Assert.Equal(expected.path, actual.path);
        Assert.Equal(expected.unmet == null, actual.unmet == null);
        if (actual.unmet != null) Assert.Same(actual.crossing, actual.unmet);
        foreach (FieldInfo f in typeof(RoadCrossing).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? a = f.GetValue(expected.crossing), b = f.GetValue(actual.crossing);
            if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb && a is not string)
                Assert.Equal(System.Linq.Enumerable.Cast<object>(ea), System.Linq.Enumerable.Cast<object>(eb));
            else Assert.Equal(a, b);
        }
    }

    [Fact]
    public void EndsMetAgainOnTheSameNetworkAreAnsweredAsTheyWereMet()
    {
        var fresh = Meet();
        RoadSpatialGrid.RemembersPlans = true;
        var first = Meet();
        int before = StructureEnds.EndsRecalled;
        var again = Meet();
        Assert.Equal(before + 1, StructureEnds.EndsRecalled);
        SameAs(fresh, first);
        SameAs(fresh, again);
        Assert.NotSame(first.crossing, again.crossing);
    }

    /// <summary>The moved banks' test reads what the search gives it (a key compared by its Equals; here a stand-in tuple): ends
    /// asked for another search are met afresh, the same search's recalled, and a test whose inputs are not given never remembered.</summary>
    [Fact]
    public void EndsAskedForAnotherSearchAreMetAfresh()
    {
        RoadSpatialGrid.RemembersPlans = true;
        Func<RoadCrossing, RoadCrossing, bool> mayMove = (_, _) => true;
        object one = (new Vector2(-60f, 0f), new Vector2(80f, 30f), new Vector2(-60f, 0f), (Vector2?)null, 4f);
        object other = (new Vector2(-60f, 0f), new Vector2(80f, 30f), new Vector2(-60f, 0f), (Vector2?)new Vector2(80f, 30f), 4f);
        int Recalled(object? asked)
        {
            RoadCrossing c = Fresh();
            int before = StructureEnds.EndsRecalled;
            StructureEnds.MeetEndsRemembered(Road(c), new List<RoadCrossing> { c }, world, float.PositiveInfinity, NoSteps, (_, _) => false,
                RoadConstants.DefaultRoadWidth, mayMove, asked);
            return StructureEnds.EndsRecalled - before;
        }
        Assert.Equal(0, Recalled(one));
        Assert.Equal(1, Recalled(one));
        Assert.Equal(0, Recalled(other));
        Assert.Equal(0, Recalled(null));
        Assert.Equal(0, Recalled(null));
    }

    [Fact]
    public void AnEndNotMetIsAnsweredWithTheCrossingAskedAbout()
    {
        RoadSpatialGrid.RemembersPlans = false;
        RoadCrossing c0 = Fresh();
        var shortEnd = new List<Vector2> { new(-60f, 0f), c0.FromBank, c0.ToBank, c0.ToBank + c0.Direction };
        Assert.Same(c0, StructureEnds.MeetEndsRemembered(new List<Vector2>(shortEnd), new List<RoadCrossing> { c0 }, world, float.PositiveInfinity, NoSteps, (_, _) => false, RoadConstants.DefaultRoadWidth));
        RoadSpatialGrid.RemembersPlans = true;
        RoadCrossing c1 = Fresh(), c2 = Fresh();
        StructureEnds.MeetEndsRemembered(new List<Vector2>(shortEnd), new List<RoadCrossing> { c1 }, world, float.PositiveInfinity, NoSteps, (_, _) => false, RoadConstants.DefaultRoadWidth);
        int before = StructureEnds.EndsRecalled;
        Assert.Same(c2, StructureEnds.MeetEndsRemembered(new List<Vector2>(shortEnd), new List<RoadCrossing> { c2 }, world, float.PositiveInfinity, NoSteps, (_, _) => false, RoadConstants.DefaultRoadWidth));
        Assert.Equal(before + 1, StructureEnds.EndsRecalled);
    }

    [Fact]
    public void EndsAskedOtherwiseAreMetAfresh()
    {
        RoadSpatialGrid.RemembersPlans = true;
        Meet();
        int before = StructureEnds.EndsRecalled;
        // Another road to the far end.
        var moved = Meet(off: 14f);
        // Another earthwork cap.
        var capped = Meet(cap: RoadConstants.TerrainDeltaMax);
        // A field of the crossing.
        var lowered = Meet(change: c => c.RaiseAtMost = 0);
        // What the road being planned set on this thread.
        RoadSpatialGrid.LevelledEnds = new List<(Vector2 at, float height)> { (new Vector2(-60f, 0f), 61f) };
        Meet();
        RoadSpatialGrid.LevelledEnds = null;
        // The network.
        RoadNetworkGenerator.IslandNetworkChanged();
        Meet();
        Assert.Equal(before, StructureEnds.EndsRecalled);
        RoadSpatialGrid.RemembersPlans = false;
        SameAs(Meet(off: 14f), moved);
        SameAs(Meet(cap: RoadConstants.TerrainDeltaMax), capped);
        SameAs(Meet(change: c => c.RaiseAtMost = 0), lowered);
    }
    /// <summary>The ends are forgotten with the pieces as the island's planning is turned off (its end,
    /// RoadNetworkGenerator.GenerateRoutedRoads), so no world or crossing is left on an idle thread, and turned on again on the
    /// same network they are met afresh.</summary>
    [Fact]
    public void EndsAreForgottenWithThePiecesAsAnIslandEnds()
    {
        RoadSpatialGrid.RemembersPlans = true;
        Meet();
        Assert.True(StructureEnds.HoldsRememberedEnds);
        RoadSpatialGrid.RemembersPlans = false;
        Assert.False(StructureEnds.HoldsRememberedEnds);
        Assert.False(RoadSpatialGrid.HoldsRememberedPieces);
        RoadSpatialGrid.RemembersPlans = true;
        int before = StructureEnds.EndsRecalled;
        Meet();
        Assert.Equal(before, StructureEnds.EndsRecalled);
        // On a worker as on this thread: what an island's worker remembered is gone once its island ends.
        bool held = true, during = false;
        var worker = new System.Threading.Thread(() =>
        {
            RoadSpatialGrid.RemembersPlans = true;
            Meet();
            during = StructureEnds.HoldsRememberedEnds;
            RoadSpatialGrid.RemembersPlans = false;
            held = StructureEnds.HoldsRememberedEnds;
        });
        worker.Start();
        worker.Join();
        Assert.True(during);
        Assert.False(held);
    }

    /// <summary>Every field of a crossing is taken, compared and put back by the ends' typed snapshot
    /// (StructureEnds.CrossingFields): each field changed alone is another asking, and is put back as changed, its lists copied.
    /// A field added to RoadCrossing and left out of the snapshot fails here.</summary>
    [Fact]
    public void EveryFieldOfACrossingIsTakenComparedAndPutBack()
    {
        FieldInfo[] fields = typeof(RoadCrossing).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(fields);
        foreach (FieldInfo f in fields)
        {
            var plain = new RoadCrossing();
            var changed = new RoadCrossing();
            object? value = Other(f.FieldType, f.GetValue(changed), f.Name);
            f.SetValue(changed, value);
            Assert.False(StructureEnds.CrossingFields.Of(plain).Same(StructureEnds.CrossingFields.Of(changed)), $"{f.Name} is not compared");
            Assert.True(StructureEnds.CrossingFields.Of(changed).Same(StructureEnds.CrossingFields.Of(changed)), $"{f.Name} does not compare equal to itself");
            var put = new RoadCrossing();
            StructureEnds.CrossingFields.Of(changed).Put(put);
            object? back = f.GetValue(put);
            if (value is System.Collections.IList list)
            {
                Assert.NotSame(value, back);
                Assert.Equal(System.Linq.Enumerable.Cast<object>(list), System.Linq.Enumerable.Cast<object>((System.Collections.IEnumerable)back!));
            }
            else Assert.True(Equals(value, back), $"{f.Name} is not put back");
        }
    }

    /// <summary>A value of <paramref name="type"/> other than <paramref name="was"/>.</summary>
    private static object? Other(Type type, object? was, string name)
    {
        Type? under = Nullable.GetUnderlyingType(type);
        if (under != null && was != null) return null;
        Type t = under ?? type;
        if (t == typeof(int)) return (int)(was ?? 0) + 1;
        if (t == typeof(float)) return (float)(was ?? 0f) + 1.5f;
        if (t == typeof(bool)) return !(bool)(was ?? false);
        if (t == typeof(string)) return (string?)was + "x";
        if (t == typeof(Vector2)) return (Vector2)(was ?? default(Vector2)) + new Vector2(1f, 2f);
        if (t == typeof(StructureEnds.End)) return new StructureEnds.End(new Vector2(1f, 2f), new Vector2(0f, 1f), 3f, 2);
        if (t.IsEnum)
        {
            foreach (object v in Enum.GetValues(t)) if (!v.Equals(was)) return v;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (System.Collections.IList)Activator.CreateInstance(t)!;
            if (was is System.Collections.IList old) foreach (object? x in old) list.Add(x);
            list.Add(Activator.CreateInstance(t.GetGenericArguments()[0]));
            return list;
        }
        throw new Xunit.Sdk.XunitException($"RoadCrossing.{name} ({type}): a field the test does not know; add it to StructureEnds.CrossingFields and here");
    }
}
