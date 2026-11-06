using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road's end on a road already laid is carried onto its centreline and each inside corner is filled up to a curve
/// (RoadJunctionFillet): the paint runs round the corner instead of to a point of grass between the two ribbons.
/// </summary>
[Collection("RoadStatics")]
public class JunctionFilletTests
{
    /// <summary>The fillet radius the mod lays (the suite runs with it off: PlainRoads).</summary>
    private const float Width = 4f, Height = 40f, Radius = 4f;
    /// <summary>The paint's solid core either side of a point (RoadProfile.PaintStrength).</summary>
    private const float Solid = Width * 0.5f * RoadConstants.RoadFlatCoreRatio;

    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Height;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static List<Vector2> Dense(Vector2 from, Vector2 to)
    {
        int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to)));
        var p = new List<Vector2>();
        for (int k = 0; k <= n; k++) p.Add(Vector2.Lerp(from, to, k / (float)n));
        return p;
    }

    private static RoadSpatialGrid.PlannedPath Piece(List<Vector2> p) =>
        new(p, p.ConvertAll(_ => Height), p.ConvertAll(_ => default(RoadPointDebugInfo)), Width, Vector2.Distance(p[0], p[p.Count - 1]), false);

    /// <summary>The trunk along x through the origin, stored; a branch arriving at <paramref name="degrees"/> to it from
    /// above, ending <paramref name="shortOf"/> metres along its own line short of the trunk's centreline; the fill
    /// with the given radius (0: none).</summary>
    private static (List<Vector2> trunk, List<Vector2> branch, List<Vector2> fill) Junction(float degrees, float shortOf, float radius)
    {
        var trunk = Dense(new Vector2(-40f, 0f), new Vector2(40f, 0f));
        Vector2 dir = new(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad));
        var branch = Dense(dir * 40f, dir * shortOf);
        var fill = new List<Vector2>();
        var world = new Flat();
        float before = RoadJunctionFillet.Radius;
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSiteProtection.Reset();
        RoadSpatialGrid.Clear();
        try
        {
            RoadJunctionFillet.Radius = radius;
            RoadSpatialGrid.Commit(Piece(trunk));
            foreach (var f in RoadJunctionFillet.For(new List<RoadSpatialGrid.PlannedPath> { Piece(branch) }, world))
            {
                Assert.Equal(0f, f.TotalLength);
                Assert.All(f.Heights, h => Assert.InRange(h, Height - 0.01f, Height + 0.01f));
                fill.AddRange(f.Points);
            }
        }
        finally { RoadJunctionFillet.Radius = before; RoadSpatialGrid.Clear(); }
        return (trunk, branch, fill);
    }

    private static bool Painted(Vector2 q, params List<Vector2>[] roads)
    {
        foreach (var road in roads)
            foreach (var p in road)
                if (Vector2.Distance(p, q) <= Solid) return true;
        return false;
    }

    /// <summary>Points of the inside corner between the branch (at <paramref name="degrees"/>) and the trunk's
    /// <paramref name="side"/> direction, between the two ribbons' solid paint and short of the fillet curve: a circle of
    /// centre-line radius <paramref name="centreRadius"/> tangent to both centrelines, its paint edge as far in as a road's.</summary>
    private static List<Vector2> InsideCorner(float degrees, float side, float centreRadius)
    {
        Vector2 own = new(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad)), other = new(side, 0f);
        float half = (float)System.Math.Acos(Vector2.Dot(own, other)) * 0.5f;
        Vector2 centre = (own + other).normalized * (centreRadius / Mathf.Sin(half));
        float edge = centreRadius - Solid, reach = centreRadius / (float)System.Math.Tan(half);
        var corner = new List<Vector2>();
        for (float x = -reach; x <= reach; x += 0.25f)
            for (float y = 0f; y <= reach; y += 0.25f)
            {
                Vector2 q = new(x, y);
                float acrossOther = q.y, acrossOwn = Mathf.Abs(own.x * q.y - own.y * q.x);
                bool between = Vector2.Dot(q, other) > 0f && (own.x * q.y - own.y * q.x) * (own.x * other.y - own.y * other.x) >= 0f;
                if (!between || acrossOther <= Solid || acrossOwn <= Solid) continue;
                if (Vector2.Dot(q, own) > reach || Vector2.Dot(q, other) > reach) continue;
                if (Vector2.Distance(q, centre) < edge + 0.25f) continue;
                corner.Add(q);
            }
        return corner;
    }

    [Fact]
    public void ATJunctionIsCarriedOntoTheTrunkAndBothInsideCornersAreRounded()
    {
        var (trunk, branch, none) = Junction(90f, 2f, 0f);
        Assert.Empty(none);
        var right = InsideCorner(90f, 1f, Radius + Width * 0.5f);
        var left = InsideCorner(90f, -1f, Radius + Width * 0.5f);
        Assert.NotEmpty(right);
        // Without the fill, the corners are bare up to the point where the two ribbons meet.
        Assert.Contains(right, q => !Painted(q, trunk, branch));

        var (_, _, fill) = Junction(90f, 2f, Radius);
        Assert.Contains(fill, q => q.magnitude < 0.5f);
        foreach (var q in right) Assert.True(Painted(q, trunk, branch, fill), $"{q} in the right-hand corner is bare");
        foreach (var q in left) Assert.True(Painted(q, trunk, branch, fill), $"{q} in the left-hand corner is bare");
        AssertNoSpur(fill);
        // Never inside the fillet curve: no fill point's paint reaches past it into the corner's grass.
        foreach (float side in new[] { 1f, -1f })
        {
            Vector2 centre = new(side * (Radius + Width * 0.5f), Radius + Width * 0.5f);
            Assert.All(fill, q => Assert.True(Vector2.Distance(q, centre) >= Radius + Width * 0.5f - 0.05f, $"{q} inside the curve"));
        }
    }

    [Fact]
    public void AnAcuteYJunctionFillsItsInsideCornerUpToTheCurve()
    {
        const float degrees = 35f;
        var (trunk, branch, none) = Junction(degrees, 3f, 0f);
        // The acute corner's fill runs at most three road widths along each road: its curve is that tight.
        float centreRadius = RoadJunctionFillet.MostReach * Width * (float)System.Math.Tan(degrees * 0.5f * Mathf.Deg2Rad);
        var acute = InsideCorner(degrees, 1f, centreRadius);
        Assert.NotEmpty(acute);
        Assert.Contains(acute, q => !Painted(q, trunk, branch, none));

        var (_, _, fill) = Junction(degrees, 3f, Radius);
        foreach (var q in acute) Assert.True(Painted(q, trunk, branch, fill), $"{q} in the acute corner is bare");
        // The obtuse side keeps its full radius.
        foreach (var q in InsideCorner(degrees, -1f, Radius + Width * 0.5f))
            Assert.True(Painted(q, trunk, branch, fill), $"{q} in the obtuse corner is bare");
        AssertNoSpur(fill);
    }

    [Fact]
    public void ARoadRunningOnPastTheTrunksEndGetsNoCornerThere()
    {
        // The trunk ends at the junction on its +x side: only the -x corner is filled.
        var trunk = Dense(new Vector2(-40f, 0f), new Vector2(0f, 0f));
        var branch = Dense(new Vector2(0f, 40f), new Vector2(0f, 2f));
        var world = new Flat();
        float before = RoadJunctionFillet.Radius;
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSiteProtection.Reset();
        RoadSpatialGrid.Clear();
        try
        {
            RoadJunctionFillet.Radius = Radius;
            RoadSpatialGrid.Commit(Piece(trunk));
            var fill = new List<Vector2>();
            foreach (var f in RoadJunctionFillet.For(new List<RoadSpatialGrid.PlannedPath> { Piece(branch) }, world)) fill.AddRange(f.Points);
            Assert.Contains(fill, q => q.x < -2.5f);
            Assert.DoesNotContain(fill, q => q.x > 1f);
        }
        finally { RoadJunctionFillet.Radius = before; RoadSpatialGrid.Clear(); }
    }

    /// <summary>A staircase's flight leaving the trunk 10 m from a 35 degree junction, up into its acute corner, clear of the
    /// branch's end: the corner's fill, which runs 12 m along the trunk, would lie under its upper stairs. No fill runs on or
    /// under a flight, the network's or one the road lays itself.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoFillRunsUnderAFlightLeavingTheTrunkNearTheJunction(bool own)
    {
        const float degrees = 35f;
        var trunk = Dense(new Vector2(-40f, 0f), new Vector2(40f, 0f));
        Vector2 dir = new(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad));
        var branch = Dense(dir * 40f, dir * 3f);
        var plan = new StairPlan();
        Vector2 foot = new(12.5f, 1.5f), top = new(10.4f, 4.5f);
        for (int k = 0; k <= 3; k++)
        {
            Vector2 at = Vector2.Lerp(foot, top, k / 3f);
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(at.x, Height + 0.5f * k, at.y) });
        }
        var world = new Flat();
        float before = RoadJunctionFillet.Radius;
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSiteProtection.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        try
        {
            RoadJunctionFillet.Radius = Radius;
            RoadSpatialGrid.Commit(Piece(trunk));
            var span = new RoadStairSpan { FromPoint = foot, ToPoint = top, FromHeight = Height, ToHeight = Height + 1.5f, Plan = plan };
            if (!own) StairStructures.Add(StairRun.FromPlan(foot, top, RoadStairRuns.Width, plan));
            var points = new List<Vector2>();
            var heights = new List<float>();
            foreach (var f in RoadJunctionFillet.For(new List<RoadSpatialGrid.PlannedPath> { Piece(branch) }, world, null,
                         own ? new List<RoadStairSpan> { span } : null))
            {
                points.AddRange(f.Points);
                heights.AddRange(f.Heights);
            }
            Assert.NotEmpty(points);
            var run = StairRun.FromPlan(foot, top, RoadStairRuns.Width, plan);
            StairStructures.Reset();
            StairStructures.Add(run);
            Assert.False(RoadStairRuns.OnALaidFlight(points, heights, out Vector2 at), $"fill at {at} under the flight");
        }
        finally { RoadJunctionFillet.Radius = before; RoadSpatialGrid.Clear(); StairStructures.Reset(); }
    }

    /// <summary>Nothing laid past the trunk's centreline: no spur on the junction's outside.</summary>
    private static void AssertNoSpur(List<Vector2> fill) => Assert.All(fill, q => Assert.True(q.y >= -0.05f, $"{q} past the trunk"));
}
