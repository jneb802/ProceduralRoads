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
/// A road meets the network level at grade, never on another road's pitch (RoadNetworkGenerator.MeetingRefusal, asked by
/// the one refusal list), on every path that plans a road: generation's (GenerateRoad), an approach trial's (TrialBuilds),
/// a marked manual road's (ManualRoads.Prepare) and a staircase end floor's (BuiltRoadRefusal), each for the three ways a
/// road meets one: arriving on it, crossing it (refused as any crossing at grade is), and riding it from its landing then
/// leaving it mid-pitch. The road met is
/// the junction the owner found in game (a branch meeting a road in the middle of its pitch, the road falling 5 m in 11 m
/// beside it): here 3 m over 6 m, 50%, between landings at 40 and 43 m, on ground that follows it. Meeting it on its
/// landing is built on every path.
/// </summary>
[Collection("RoadStatics")]
public sealed class JunctionPitchEveryPathTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _grade = GradeCap.At(0.35f);
    private readonly IDisposable _landing = StaticOverride.Keep(() => RoadPitches.JunctionLanding);

    public JunctionPitchEveryPathTests()
    {
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        NetworkCutoff.Forget();
    }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        _landing.Dispose();
        _grade.Dispose();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>The road met: level at 40 m west of x = -3, at 43 m east of x = 3, a 50% pitch between; the ground follows it
    /// everywhere (it depends on x alone). Water past 100 m.</summary>
    private sealed class PitchedRoadGround : WorldGenerator
    {
        public static float Profile(float x) => x <= -3f ? 40f : x >= 3f ? 43f : 40f + (x + 3f) * 0.5f;
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 100f || Mathf.Abs(z) > 100f ? 0f : Profile(x);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 7;
    }

    private static Island Everywhere()
    {
        var i = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-96, -96), Max = new Vector2(96, 96) };
        for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }

    /// <summary>The world with the pitched road on it, stored and finished as a network road.</summary>
    private PitchedRoadGround Begin()
    {
        var world = new PitchedRoadGround();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        var road = new List<Vector2>();
        var heights = new List<float>();
        for (int x = -60; x <= 60; x++) { road.Add(new Vector2(x, 0f)); heights.Add(PitchedRoadGround.Profile(x)); }
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, heights, road.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 120f, false));
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        return world;
    }

    public enum Meets { Arrives, Crosses, RidesThenLeaves }

    /// <summary>The marks of each way of meeting the road at <paramref name="x"/>: arriving from 40 m north; crossing from
    /// 40 m north to 40 m south; riding it from 12 m west of the meeting along it, then leaving north.</summary>
    private static Vector2[] Marks(Meets how, float x) => how switch
    {
        Meets.Arrives => new[] { new Vector2(x, 40f), new Vector2(x, 0f) },
        Meets.Crosses => new[] { new Vector2(x, 40f), new Vector2(x, -40f) },
        _ => new[] { new Vector2(x - 12f, 0f), new Vector2(x, 0f), new Vector2(x, 40f) },
    };

    /// <summary>The road through <paramref name="marks"/>, a point a metre, at the height of the ground under it (the road
    /// met where it rides it).</summary>
    private static List<Vector2> Line(Vector2[] marks, float step = 1f)
    {
        var line = new List<Vector2> { marks[0] };
        for (int i = 1; i < marks.Length; i++)
        {
            int n = Mathf.CeilToInt(Vector2.Distance(marks[i - 1], marks[i]) / step);
            for (int k = 1; k <= n; k++) line.Add(Vector2.Lerp(marks[i - 1], marks[i], k / (float)n));
        }
        return line;
    }

    private static RoadSpatialGrid.PlannedPath Planned(List<Vector2> line) =>
        new(line, line.Select(p => PitchedRoadGround.Profile(p.x)).ToList(), line.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f,
            line.Zip(line.Skip(1), Vector2.Distance).Sum(), false);

    private static RoadPathfinder Finder(WorldGenerator world) =>
        new(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false, Stairs = false };

    /// <summary>Whether a road was stored meeting the pitched road on its pitch: a point of it within a road's width of the
    /// met road between x = -3 and 3, but those of the met road itself.</summary>
    private static bool StoredOnThePitch() =>
        RoadSpatialGrid.SnapshotAllRoadPoints().Any(p => p.IsRoad && Mathf.Abs(p.p.x) < 3f && Mathf.Abs(p.p.y) > 0.5f && Mathf.Abs(p.p.y) <= RoadNetworkGenerator.RoadWidth);

    /// <summary>Generation, an approach trial, a marked road and an end floor, each asked for the road meeting the pitched road
    /// at <paramref name="x"/>: whether each built it.</summary>
    private (bool generation, bool trial, bool manual, bool endFloor, string why) Built(Meets how, float x)
    {
        Vector2[] marks = Marks(how, x);
        var why = new List<string>();

        WorldGenerator world = Begin();
        SetPathfinder(Finder(world));
        bool generated = RoadNetworkGenerator.GenerateRoad(marks[0], 0f, marks[marks.Length - 1], 0f, RoadNetworkGenerator.RoadWidth, "meets the pitched road");
        // Built onto the network away from the pitch is a road the rule lets through; one stored on the pitch is not.
        bool generation = generated && StoredOnThePitch();
        if (!generated) why.Add("generation: " + RoadSpatialGrid.LastRefusal);
        SetPathfinder(null);

        world = Begin();
        // The searched path a cell at a time through every mark (the road turns where it leaves the road it rides).
        var cells = Line(marks, RoadPathfinder.CellSize);
        bool trial = RoadNetworkGenerator.TrialBuilds(Finder(world), cells, marks[0], 0f, marks[marks.Length - 1], 0f,
            false, false, false, false, RoadNetworkGenerator.RoadWidth, 0f, 0f);

        Begin();
        bool manual;
        try { ManualRoads.Prepare(marks, false, new[] { Everywhere() }); manual = true; }
        catch (InvalidOperationException e) { manual = false; why.Add("manual: " + e.Message); }

        Begin();
        string? endFloor = RoadNetworkGenerator.BuiltRoadRefusal(new List<RoadSpatialGrid.PlannedPath> { Planned(Line(marks)) }, new List<RoadCrossing>(), out _);
        if (endFloor != null) why.Add("end floor: " + endFloor);
        return (generation, trial, manual, endFloor == null, string.Join("; ", why));
    }

    /// <summary>Arriving on the pitch, or riding the road from its landing and leaving it mid-pitch, is refused on every path
    /// for the junction's grade (generation finds its way onto the landing instead, or none); crossing it there is refused
    /// on every path too, as any road crossing another at grade is. Riding then leaving, the end floor's road rides the
    /// pitch to the corner where it leaves it, 0.5 m up over the 1.4 m between its stretches either side of that corner: the
    /// one list names that step first (WalkStep comes before the meeting), and the meeting rule refuses the same road for
    /// the junction's grade on its own.</summary>
    [Theory]
    [InlineData(Meets.Arrives)]
    [InlineData(Meets.Crosses)]
    [InlineData(Meets.RidesThenLeaves)]
    public void NoPathBuildsARoadMeetingAnotherOnItsPitch(Meets how)
    {
        var (generation, trial, manual, endFloor, why) = Built(how, 0f);
        Assert.False(generation, $"{how}: generation stored a road meeting it on its pitch; {why}");
        Assert.False(trial, $"{how}: an approach trial built it; {why}");
        Assert.False(manual, $"{how}: the marked road was planned; {why}");
        Assert.False(endFloor, $"{how}: the end floor's road passed the list; {why}");
        if (how == Meets.Arrives) Assert.Contains(RoadNetworkGenerator.JunctionOverCapRefusal, why);
        if (how == Meets.RidesThenLeaves)
        {
            Assert.Contains("end floor: " + RoadNetworkGenerator.WalkStepRefusal, why);
            Begin();
            string? meeting = RoadNetworkGenerator.MeetingRefusal(new List<RoadSpatialGrid.PlannedPath> { Planned(Line(Marks(how, 0f))) }, out _);
            Assert.StartsWith(RoadNetworkGenerator.JunctionOverCapRefusal, meeting);
        }
    }

    /// <summary>The control: arriving on the road's landing, or riding it and leaving from its landing, is built on every
    /// path.</summary>
    [Theory]
    [InlineData(Meets.Arrives)]
    [InlineData(Meets.RidesThenLeaves)]
    public void EveryPathBuildsARoadMeetingAnotherOnItsLanding(Meets how)
    {
        var (_, trial, manual, endFloor, why) = Built(how, 20f);
        Assert.True(trial, $"{how}: an approach trial refused it; {why}");
        Assert.True(manual, $"{how}: the marked road was refused; {why}");
        Assert.True(endFloor, $"{how}: the end floor's road was refused; {why}");
        Assert.DoesNotContain(RoadNetworkGenerator.JunctionOverCapRefusal, why);
    }
}
