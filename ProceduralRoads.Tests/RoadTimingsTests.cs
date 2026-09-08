using System;
using System.Threading;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// RoadTimings is the measuring stick for the validation-cycle work: these
/// pin its aggregation (count / total / longest with label), counters, wait
/// reasons, marks, frame stalls, the summary text and the JSON shape.
/// Tests share the static instance, so each resets first and the class is
/// kept off the parallel collections.
/// </summary>
[Collection("RoadTimings")]
public class RoadTimingsTests
{
    [Fact]
    public void StageAggregatesCountTotalAndLongestWithLabel()
    {
        RoadTimings.Reset("t1");
        RoadTimings.Record("gen.pathfind", 5.0, "a");
        RoadTimings.Record("gen.pathfind", 20.0, "b");
        RoadTimings.Record("gen.pathfind", 7.5, "c");

        Assert.True(RoadTimings.TryGetStage("gen.pathfind", out var stat));
        Assert.Equal(3, stat.Count);
        Assert.Equal(32.5, stat.TotalMs, 6);
        Assert.Equal(20.0, stat.MaxMs, 6);
        Assert.Equal("b", stat.MaxLabel);
        Assert.False(RoadTimings.TryGetStage("gen.other", out _));
    }

    [Fact]
    public void ScopeMeasuresElapsedTime()
    {
        RoadTimings.Reset();
        using (RoadTimings.Stage("sleep", "20ms"))
            Thread.Sleep(20);

        Assert.True(RoadTimings.TryGetStage("sleep", out var stat));
        Assert.Equal(1, stat.Count);
        Assert.InRange(stat.TotalMs, 15.0, 2000.0);
        Assert.Equal("20ms", stat.MaxLabel);
    }

    [Fact]
    public void CountersWaitsAndMarksAccumulate()
    {
        RoadTimings.Reset("t3");
        RoadTimings.Count("terrain.vertices_modified", 40);
        RoadTimings.Count("terrain.vertices_modified", 2);
        RoadTimings.Count("terrain.zones_written");
        RoadTimings.Wait("saved compiler not alive");
        RoadTimings.Wait("saved compiler not alive");
        RoadTimings.Mark("load.player_spawn");

        Assert.Equal(42, RoadTimings.GetCount("terrain.vertices_modified"));
        Assert.Equal(1, RoadTimings.GetCount("terrain.zones_written"));
        Assert.Equal(0, RoadTimings.GetCount("never"));
        Assert.Equal(2, RoadTimings.GetWaits("saved compiler not alive"));
        Assert.Equal(0, RoadTimings.GetWaits("never"));
        var marks = RoadTimings.Marks;
        Assert.Single(marks);
        Assert.Equal("load.player_spawn", marks[0].Name);
        Assert.InRange(marks[0].MsSinceStart, 0.0, 60000.0);
        Assert.InRange((DateTime.UtcNow - marks[0].Utc).TotalMinutes, -1.0, 1.0);
    }

    [Fact]
    public void FramesCountStallsAndKeepTheLongest()
    {
        RoadTimings.Reset();
        RoadTimings.Frame(16.0);
        RoadTimings.Frame(250.0);
        RoadTimings.Frame(120.0);
        RoadTimings.Frame(RoadTimings.StallFrameMs); // equal to the threshold is not a stall

        Assert.Equal(4, RoadTimings.Frames);
        Assert.Equal(2, RoadTimings.StallFrames);
        Assert.Equal(250.0, RoadTimings.MaxFrameMs, 6);
    }

    [Fact]
    public void ResetClearsEverythingAndNamesTheRun()
    {
        RoadTimings.Reset("old");
        RoadTimings.Record("s", 1.0);
        RoadTimings.Count("c");
        RoadTimings.Wait("w");
        RoadTimings.Mark("m");
        RoadTimings.Frame(500.0);

        RoadTimings.Reset("new");

        Assert.Equal("new", RoadTimings.RunId);
        Assert.False(RoadTimings.TryGetStage("s", out _));
        Assert.Equal(0, RoadTimings.GetCount("c"));
        Assert.Equal(0, RoadTimings.GetWaits("w"));
        Assert.Empty(RoadTimings.Marks);
        Assert.Equal(0, RoadTimings.Frames);
        Assert.Equal(0.0, RoadTimings.MaxFrameMs);
        Assert.InRange(RoadTimings.ElapsedMs, 0.0, 10000.0);

        RoadTimings.Reset();
        Assert.Equal("", RoadTimings.RunId);
    }

    [Fact]
    public void SummaryListsStagesCountersWaitsMarksAndFrames()
    {
        RoadTimings.Reset("r7");
        RoadTimings.Record("gen.total", 12345.0, "global");
        RoadTimings.Record("terrain.zone", 3.0, "(1,2)");
        RoadTimings.Count("terrain.zones_written", 9);
        RoadTimings.Wait("zone spawn: saved terrain compiler not alive yet");
        RoadTimings.Mark("gen.done");
        RoadTimings.Frame(300.0);

        string text = RoadTimings.Summary();

        Assert.StartsWith("run=r7 started=", text);
        Assert.Contains("gen.total", text);
        Assert.Contains("12.3s", text);
        Assert.Contains("(global)", text);
        Assert.Contains("terrain.zone", text);
        Assert.Contains("terrain.zones_written", text);
        Assert.Contains("wait     1x zone spawn: saved terrain compiler not alive yet", text);
        Assert.Contains("mark gen.done", text);
        Assert.Contains("frames=1 stalls>100ms=1 longest=300ms", text);
        // stages come out sorted, so gen.* precede terrain.*
        Assert.True(text.IndexOf("gen.total", StringComparison.Ordinal) < text.IndexOf("terrain.zone", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonCarriesEveryTableAndEscapesLabels()
    {
        RoadTimings.Reset("run \"q\"");
        RoadTimings.Record("gen.pathfind", 1.5, "Start -> Eikthyr\\Camp");
        RoadTimings.Count("gen.pathfind_iterations", 777);
        RoadTimings.Wait("pathfind failed: max iterations reached");
        RoadTimings.Mark("gen.start");
        RoadTimings.Frame(150.0);

        string json = RoadTimings.ToJson();

        Assert.StartsWith("{\"run_id\":\"run \\\"q\\\"\"", json);
        Assert.Contains("\"stages\":{\"gen.pathfind\":{\"count\":1,\"total_ms\":1.5,\"max_ms\":1.5,\"max_label\":\"Start -> Eikthyr\\\\Camp\"}}", json);
        Assert.Contains("\"counters\":{\"gen.pathfind_iterations\":777}", json);
        Assert.Contains("\"waits\":{\"pathfind failed: max iterations reached\":1}", json);
        Assert.Contains("\"marks\":[{\"name\":\"gen.start\",\"utc\":\"", json);
        Assert.Contains("\"frames\":{\"count\":1,\"stalls\":1,\"stall_threshold_ms\":100.0,\"longest_ms\":150.0,\"longest_at_ms\":", json);
        Assert.EndsWith("}}", json);
        Assert.Equal(json.Length, json.Replace("\n", "").Length);
    }

    [Fact]
    public void JsonOfAnEmptyRunIsStillWellFormed()
    {
        RoadTimings.Reset();
        string json = RoadTimings.ToJson();
        Assert.Contains("\"stages\":{}", json);
        Assert.Contains("\"counters\":{}", json);
        Assert.Contains("\"waits\":{}", json);
        Assert.Contains("\"marks\":[]", json);
    }
}

[CollectionDefinition("RoadTimings", DisableParallelization = true)]
public class RoadTimingsCollection { }
