using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Characterization tests for warp-71's GenerateReachableRoads — the single
/// strategy that replaces Chain and MST. It grows a tree outward from the
/// root, only ever expanding from nodes a road actually reached, remembers
/// failed edges, biases edge choice by location priority, and deliberately
/// starts a new disconnected component when nothing else is reachable.
/// </summary>
public class ReachableRoadsTests
{
    private static readonly Regex SuccessRe = new(@"^Generated road: (.+) -> (.+) \(\d+ waypoints\)$");
    private static readonly Regex FailureRe = new(@"^Could not find path: (.+) -> (.+)$");

    private sealed class Harness : IDisposable
    {
        public readonly List<string> Logs = new();
        public readonly RoadPathfinder Pathfinder;

        private static readonly FieldInfo PathfinderField =
            typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo? ReachableMethod =
            typeof(RoadNetworkGenerator).GetMethod("GenerateReachableRoads", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>False on pre-warp-71 bases; these tests no-op there.</summary>
        public static bool Available => ReachableMethod != null;

        public Harness(SyntheticWorld world)
        {
            WorldGenerator.instance = world;
            RoadSpatialGrid.Clear();
            Pathfinder = new RoadPathfinder(world);
            PathfinderField.SetValue(null, Pathfinder);
            ManualLogSource.Captured = Logs;
        }

        public void Run(Vector3 start, List<(string name, Vector3 position, float radius)> locations) =>
            ReachableMethod!.Invoke(null, new object[] { start, 0f, locations, "Start" });

        public List<(string from, string to)> SuccessEdges => Parse(SuccessRe);
        public List<(string from, string to)> FailedEdges => Parse(FailureRe);

        private List<(string, string)> Parse(Regex re) =>
            Logs.Select(l => re.Match(l))
                .Where(m => m.Success)
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
                .ToList();

        public void Dispose()
        {
            ManualLogSource.Captured = null;
            PathfinderField.SetValue(null, null);
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = null;
        }
    }

    private static List<(string name, Vector3 position, float radius)> StarLayout(float cx, float cy) => new()
    {
        ("Hub", new Vector3(cx, 0, cy), 8f),
        ("West", new Vector3(cx - 140, 0, cy), 8f),
        ("East", new Vector3(cx + 140, 0, cy), 8f),
        ("North", new Vector3(cx, 0, cy + 140), 8f),
        ("South", new Vector3(cx, 0, cy - 140), 8f),
    };

    [Fact]
    public void FormsMstLikeTreeOnStarLayout()
    {
        if (!Harness.Available) return; // strategy arrives with warp-71
        // With all-equal priorities the growth degenerates to nearest-to-tree
        // (Prim) — so the hub/spoke shape of the old MST strategy survives.
        using var h = new Harness(new SyntheticWorld { HasRiver = false, HasMountain = false });

        h.Run(new Vector3(-300, 0, 0), StarLayout(0, 0));

        var edges = h.SuccessEdges;
        Assert.Equal(5, edges.Count);
        Assert.Empty(h.FailedEdges);

        int hubDegree = edges.Count(e => e.from == "Hub" || e.to == "Hub");
        Assert.True(hubDegree >= 3, $"Expected Hub degree >= 3, got {hubDegree}");
    }

    [Fact]
    public void HighPriorityLocationIsConnectedBeforeCloserOnes()
    {
        if (!Harness.Available) return; // strategy arrives with warp-71
        // Priority bias: a boss location (priority 100 => bonus 2000) beats
        // plain locations that are physically closer to the tree.
        using var h = new Harness(new SyntheticWorld { HasRiver = false, HasMountain = false });

        var layout = new List<(string name, Vector3 position, float radius)>
        {
            ("Near1", new Vector3(-200, 0, 60), 8f),
            ("Near2", new Vector3(-180, 0, -80), 8f),
            ("Bonemass", new Vector3(150, 0, 0), 8f), // farthest, but priority 100
        };

        h.Run(new Vector3(-300, 0, 0), layout);

        Assert.Equal(3, h.SuccessEdges.Count);
        Assert.Equal("Bonemass", h.SuccessEdges[0].to);
    }

    [Fact]
    public void RetriesAlternativesAndPromotesComponentAcrossRiver()
    {
        if (!Harness.Available) return; // strategy arrives with warp-71
        // Same river scenario the old strategies orphaned silently. The new
        // algorithm tries every cross-river edge, records each failure, then
        // deliberately starts a second component on the far side.
        using var h = new Harness(new SyntheticWorld { HasRiver = true, HasMountain = false });

        var layout = new List<(string name, Vector3 position, float radius)>
        {
            ("NearWest", new Vector3(-160, 0, 60), 8f),
            ("FarEast", new Vector3(300, 0, 0), 8f),
            ("FarEast2", new Vector3(380, 0, 120), 8f),
        };

        h.Run(new Vector3(-280, 0, 0), layout);

        // Same two physical roads as the old strategies...
        Assert.Contains(("Start", "NearWest"), h.SuccessEdges);
        Assert.Contains(("FarEast", "FarEast2"), h.SuccessEdges);
        Assert.Equal(2, h.SuccessEdges.Count);

        // ...but it exhausted every cross-river edge first (old strategies
        // tried exactly one), and the far-side component is explicit.
        Assert.True(h.FailedEdges.Count >= 3,
            $"Expected >= 3 failed cross-river attempts, got {h.FailedEdges.Count}");
        Assert.Contains(h.Logs, l => l.Contains("Started disconnected road component"));
    }

    [Fact]
    public void RendersReachableNetwork()
    {
        if (!Harness.Available) return; // strategy arrives with warp-71
        // Visual: star west of the river (tree growth, cross shape) and the
        // component-promotion scenario spanning the river below it.
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };

        var paths = new List<(List<Vector2>, byte, byte, byte)>();
        var markers = new List<(Vector2, byte, byte, byte)>();

        void Draw(Vector3 start, List<(string name, Vector3 position, float radius)> layout,
            byte r, byte g, byte b)
        {
            using var h = new Harness(world);
            h.Run(start, layout);

            var positions = layout.ToDictionary(
                l => l.name, l => new Vector2(l.position.x, l.position.z));
            positions["Start"] = new Vector2(start.x, start.z);

            foreach (var (from, to) in h.SuccessEdges)
            {
                var path = h.Pathfinder.FindPath(positions[from], positions[to]);
                if (path != null)
                    paths.Add((path, r, g, b));
            }

            foreach (var pos in positions.Values)
                markers.Add((pos, 255, 255, 255));
        }

        Draw(new Vector3(-380, 0, 180), StarLayout(-180, 180), 40, 190, 220);

        Draw(new Vector3(-280, 0, -200), new List<(string, Vector3, float)>
        {
            ("NearWest", new Vector3(-160, 0, -140), 8f),
            ("FarEast", new Vector3(300, 0, -200), 8f),
            ("FarEast2", new Vector3(380, 0, -80), 8f),
        }, 220, 90, 30);

        string output = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(ReachableRoadsTests).Assembly.Location)!,
            "debug-reachable.bmp");

        WorldRenderer.Render(world, paths, markers, output, -700f, 700f, 2f);
        Assert.True(System.IO.File.Exists(output));
    }
}
