using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed location's landing is held off the road's own earlier stretch as off the network's roads
/// (RoadNetworkGenerator.EntryMeetingRefusal, DoublesBackBesideItsRoad): read from a generated Mountains tower, whose landing
/// stands at 104.49. Its road came in from the south already at the landing's height (104.40 at -9294.4,1726.5), went on
/// down a dip to its end at -9294.4,1734.5 (102.71, passing 103.27 at 1730.5), and the staircase onto the landing from
/// that end doubled back up beside it, 2 m, to -9291.6,1727.3: its top a few metres from the road at the road's height.
/// </summary>
public class EntryDoublesBackTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public EntryDoublesBackTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
    }

    public void Dispose()
    {
        StairStructures.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private const float X = -9294.4f;
    private static readonly Vector2 End = new(X, 1734.5f), Top = new(-9291.6f, 1727.3f);

    /// <summary>The road as read: north along x -9294.4, level at 104.40 to 1726.5, down to 103.27 at 1730.5 and 102.71 at its end.</summary>
    private static List<RoadSpatialGrid.PlannedPath> Road(float to = 1734.5f)
    {
        static float H(float z) => z <= 1726.5f ? 104.40f
            : z <= 1730.5f ? Mathf.Lerp(104.40f, 103.27f, (z - 1726.5f) / 4f)
            : Mathf.Lerp(103.27f, 102.71f, (z - 1730.5f) / 4f);
        var points = new List<Vector2>();
        for (float z = 1694.5f; z <= to + 0.01f; z += 1f) points.Add(new Vector2(X, z));
        return new List<RoadSpatialGrid.PlannedPath>
        {
            new(points, points.ConvertAll(p => H(p.y)), points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth,
                to - 1694.5f, true),
        };
    }

    /// <summary>A staircase of eight walked stairs from <paramref name="foot"/> at <paramref name="footHeight"/> to
    /// <paramref name="top"/> at <paramref name="topHeight"/> (walking surfaces).</summary>
    private static RoadStairSpan Stairs(Vector2 foot, float footHeight, Vector2 top, float topHeight)
    {
        var plan = new StairPlan { Kit = "stone", Reached = true, End = new Vector3(top.x, topHeight, top.y) };
        for (int k = 0; k < 8; k++)
        {
            Vector2 at = Vector2.Lerp(foot, top, k / 7f);
            float surface = Mathf.Lerp(footHeight, topHeight, k / 7f);
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair",
                Position = new Vector3(at.x, surface - StairLayout.PieceRise / 2f, at.y), YawDegrees = StairLayout.Bearing(foot, top) });
        }
        return new RoadStairSpan { FromIndex = 0, ToIndex = 0, FromPoint = foot, FromHeight = footHeight, ToPoint = top, ToHeight = topHeight,
            Kit = StairKit.Stone, Plan = plan };
    }

    /// <summary>The staircase as laid, from the road's end 2 m up beside it: the network's meeting test alone passes it (the
    /// network has no road here), the road's own does not, so the way onto the landing is laid from further back along the road
    /// (EntryCutBacks), where the road is already at the landing's height: a road on from there straight onto the landing, or a
    /// short staircase that does not double back, is not refused.</summary>
    [Fact]
    public void AStaircaseDoublingBackLevelBesideItsRoadIsRefused()
    {
        var plans = Road();
        RoadStairSpan stubby = Stairs(End, 102.71f, Top, 104.71f);
        Assert.Null(RoadNetworkGenerator.MeetingRefusal(RoadNetworkGenerator.WithEntry(false, plans, End, null, stubby), out _));
        string? why = RoadNetworkGenerator.EntryMeetingRefusal(false, plans, End, null, stubby, network: true);
        Assert.NotNull(why);
        Assert.Contains(RoadNetworkGenerator.DoublesBackRefusal, why);
        // Cut back 12 m, its end at 1722.5 on the level stretch at 104.40, east onto the landing at 104.49.
        float back = RoadNetworkGenerator.EntryCutBacks[1];
        var cut = Road(1734.5f - back);
        var from = new Vector2(X, 1734.5f - back);
        var onto = new List<Vector2>();
        for (float x = X; x <= X + 12.01f; x += 1f) onto.Add(new Vector2(x, from.y));
        var road = new RoadSpatialGrid.PlannedPath(onto, onto.ConvertAll(p => Mathf.Lerp(104.40f, 104.49f, (p.x - X) / 12f)),
            onto.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 12f, true);
        Assert.Null(RoadNetworkGenerator.EntryMeetingRefusal(false, cut, from, road, null, network: true));
        Assert.Null(RoadNetworkGenerator.EntryMeetingRefusal(false, cut, from, null, Stairs(from, 104.40f, from + new Vector2(8f, 0f), 106.40f), network: true));
    }

    /// <summary>A staircase that doubles back the same way but climbs well above its road (its top 3.5 m over the road beside
    /// it) is not beside it at its height: a switchback up, allowed.</summary>
    [Fact]
    public void AStaircaseDoublingBackWellAboveItsRoadIsAllowed()
    {
        var plans = Road();
        Assert.Null(RoadNetworkGenerator.EntryMeetingRefusal(false, plans, End, null, Stairs(End, 102.71f, Top, 107.71f), network: true));
        // Nor one that comes down well below it.
        Assert.Null(RoadNetworkGenerator.EntryMeetingRefusal(false, plans, End, null, Stairs(End, 102.71f, Top, 100.71f), network: true));
    }
}
