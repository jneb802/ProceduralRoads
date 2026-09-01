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
/// Characterization tests for the two existing island network strategies in
/// RoadNetworkGenerator (upstream master): Chain (greedy nearest-neighbor
/// walk) and MST (Prim's minimum spanning tree). Strategies are private, so
/// they are invoked via reflection and observed through the log seam —
/// no mod code is modified.
/// </summary>
public class RoadTopologyTests
{
    private static readonly Regex SuccessRe = new(@"^Generated road: (.+) -> (.+) \(\d+ waypoints\)$");
    private static readonly Regex FailureRe = new(@"^Could not find path: (.+) -> (.+)$");

    private sealed class Harness : IDisposable
    {
        public readonly List<string> Logs = new();
        public readonly RoadPathfinder Pathfinder;

        public Harness(SyntheticWorld world)
        {
            WorldGenerator.instance = world;
            RoadSpatialGrid.Clear();
            Pathfinder = new RoadPathfinder(world);
            PathfinderField.SetValue(null, Pathfinder);
            ManualLogSource.Captured = Logs;
        }

        private static readonly FieldInfo PathfinderField =
            typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo? ChainMethod =
            typeof(RoadNetworkGenerator).GetMethod("GenerateChainRoads", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo? MstMethod =
            typeof(RoadNetworkGenerator).GetMethod("GenerateMSTRoads", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>
        /// False once upstream warp-71 (PR #16) is merged — it replaces both
        /// legacy strategies with GenerateReachableRoads. The legacy
        /// characterization tests below no-op in that case.
        /// </summary>
        public static bool LegacyStrategiesAvailable => ChainMethod != null && MstMethod != null;

        public void RunChain(Vector3 start, List<(string name, Vector3 position, float radius)> locations) =>
            ChainMethod!.Invoke(null, new object[] { start, 0f, locations });

        public void RunMst(Vector3 start, List<(string name, Vector3 position, float radius)> locations) =>
            MstMethod!.Invoke(null, new object[] { start, 0f, locations });

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

    private static Dictionary<string, int> Degrees(IEnumerable<(string from, string to)> edges)
    {
        var deg = new Dictionary<string, int>();
        foreach (var (from, to) in edges)
        {
            deg.TryGetValue(from, out int df);
            deg[from] = df + 1;
            deg.TryGetValue(to, out int dt);
            deg[to] = dt + 1;
        }
        return deg;
    }

    [Fact]
    public void MstFormsHubAndSpokeOnStarLayout()
    {
        if (!Harness.LegacyStrategiesAvailable) return; // removed by warp-71
        using var h = new Harness(new SyntheticWorld { HasRiver = false, HasMountain = false });

        h.RunMst(new Vector3(-300, 0, 0), StarLayout(0, 0));

        var edges = h.SuccessEdges;
        Assert.Equal(5, edges.Count); // spanning tree: one edge per location
        Assert.Empty(h.FailedEdges);

        // The central node collects the satellites — hub/spoke emerges.
        var degrees = Degrees(edges);
        Assert.True(degrees["Hub"] >= 3, $"Expected Hub degree >= 3, got {degrees["Hub"]}");
    }

    [Fact]
    public void ChainStaysASinglePathOnStarLayout()
    {
        if (!Harness.LegacyStrategiesAvailable) return; // removed by warp-71
        using var h = new Harness(new SyntheticWorld { HasRiver = false, HasMountain = false });

        h.RunChain(new Vector3(-300, 0, 0), StarLayout(0, 0));

        var edges = h.SuccessEdges;
        Assert.Equal(5, edges.Count); // one hop per location

        // A chain never branches: every node has degree <= 2,
        // and each node is the source of at most one road.
        var degrees = Degrees(edges);
        Assert.All(degrees, kv => Assert.True(kv.Value <= 2, $"{kv.Key} has degree {kv.Value}"));
        var sources = edges.GroupBy(e => e.from);
        Assert.All(sources, g => Assert.Single(g));
    }

    [Fact]
    public void ChainVisitsInNearestNeighborOrder()
    {
        if (!Harness.LegacyStrategiesAvailable) return; // removed by warp-71
        using var h = new Harness(new SyntheticWorld { HasRiver = false, HasMountain = false });

        var line = new List<(string name, Vector3 position, float radius)>
        {
            ("C", new Vector3(0, 0, 0), 8f),
            ("A", new Vector3(-200, 0, 0), 8f),
            ("D", new Vector3(100, 0, 0), 8f),
            ("B", new Vector3(-100, 0, 0), 8f),
        };

        h.RunChain(new Vector3(-300, 0, 0), line);

        // Greedy walk sorts the line regardless of input order.
        Assert.Equal(
            new[] { ("Start", "A"), ("A", "B"), ("B", "C"), ("C", "D") },
            h.SuccessEdges);
    }

    [Fact]
    public void ChainGreedyWalksFartherThanMstOnAsymmetricLayout()
    {
        if (!Harness.LegacyStrategiesAvailable) return; // removed by warp-71
        // Classic greedy pitfall: points on both sides of the start. The chain
        // commits east and must double back west; MST connects west directly.
        var layout = new List<(string name, Vector3 position, float radius)>
        {
            ("NearEast", new Vector3(60, 0, 0), 8f),
            ("FarEast", new Vector3(120, 0, 0), 8f),
            ("West", new Vector3(-120, 0, 0), 8f),
        };
        var start = new Vector3(0, 0, 0);

        float ChainLength()
        {
            using var h = new Harness(new SyntheticWorld { HasRiver = false, HasMountain = false });
            h.RunChain(start, new(layout));
            return NetworkLength(h.SuccessEdges, layout, start);
        }

        float MstLength()
        {
            using var h = new Harness(new SyntheticWorld { HasRiver = false, HasMountain = false });
            h.RunMst(start, new(layout));
            return NetworkLength(h.SuccessEdges, layout, start);
        }

        float chain = ChainLength();
        float mst = MstLength();
        Assert.True(chain > mst,
            $"Expected greedy chain ({chain:F0}m straight-line) to exceed MST ({mst:F0}m)");
    }

    [Fact]
    public void BothStrategiesKeepRoutingFromNodesTheyFailedToReach()
    {
        if (!Harness.LegacyStrategiesAvailable) return; // removed by warp-71
        // Characterization of a real quirk: neither strategy reacts to a failed
        // road. The chain moves its cursor to the unreachable node anyway, and
        // MST emits child edges of an unreachable parent — producing orphan
        // road segments disconnected from the start. warp-71's
        // GenerateReachableRoads exists upstream to fix exactly this.
        var layout = new List<(string name, Vector3 position, float radius)>
        {
            ("NearWest", new Vector3(-160, 0, 60), 8f),
            ("FarEast", new Vector3(300, 0, 0), 8f),   // across the river
            ("FarEast2", new Vector3(380, 0, 120), 8f), // also across the river
        };
        var start = new Vector3(-280, 0, 0);

        foreach (bool useMst in new[] { false, true })
        {
            using var h = new Harness(new SyntheticWorld { HasRiver = true, HasMountain = false });
            if (useMst) h.RunMst(start, new(layout));
            else h.RunChain(start, new(layout));

            string label = useMst ? "MST" : "Chain";

            Assert.Contains(("NearWest", "FarEast"), h.FailedEdges);

            // The orphan: a road between the two far-side nodes still gets
            // built even though nothing connects them to the start.
            Assert.Contains(("FarEast", "FarEast2"), h.SuccessEdges);

            Assert.Contains(("Start", "NearWest"), h.SuccessEdges);
            Assert.Equal(2, h.SuccessEdges.Count);

            // (label kept for readability of future failures)
            Assert.NotNull(label);
        }
    }

    [Fact]
    public void RendersTopologyComparison()
    {
        if (!Harness.LegacyStrategiesAvailable) return; // removed by warp-71
        // Visual artifact: identical star layouts, Chain on the south half of
        // the island, MST on the north half. Chain renders as one snaking
        // path; MST as spokes around the hub.
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };

        var paths = new List<(List<Vector2>, byte, byte, byte)>();
        var markers = new List<(Vector2, byte, byte, byte)>();

        void Run(bool useMst, float cy, byte r, byte g, byte b)
        {
            using var h = new Harness(world);
            var layout = StarLayout(0, cy);
            var start = new Vector3(-300, 0, cy);
            if (useMst) h.RunMst(start, layout);
            else h.RunChain(start, layout);

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

        Run(useMst: false, cy: -230, r: 220, g: 90, b: 30);  // Chain: orange
        Run(useMst: true, cy: 230, r: 40, g: 190, b: 220);   // MST: cyan

        string output = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(RoadTopologyTests).Assembly.Location)!,
            "debug-topology.bmp");

        WorldRenderer.Render(world, paths, markers, output, -700f, 700f, 2f);
        Assert.True(System.IO.File.Exists(output));
    }

    private static float NetworkLength(
        List<(string from, string to)> edges,
        List<(string name, Vector3 position, float radius)> layout,
        Vector3 start)
    {
        var positions = layout.ToDictionary(l => l.name, l => l.position);
        positions["Start"] = start;
        return edges.Sum(e => Vector3.Distance(positions[e.from], positions[e.to]));
    }
}
