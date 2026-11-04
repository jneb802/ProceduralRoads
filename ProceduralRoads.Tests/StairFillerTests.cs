using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The pieces that fill a turn's wedge are chosen turn by turn (maybe a single large 2x2, or 2x1s side by side,
/// or a 1x1 on the outer part snapped to the upslope stairs; the layout chooses).
/// Geometry here is worked out apart from <see cref="StairFiller"/>.
/// </summary>
public class StairFillerTests
{
    private static readonly Vector2 Pivot = new(10f, 20f);

    private static Vector2 F(float h) => StairLayout.Forward(h);
    private static Vector2 R(float h) => StairLayout.Right(h);

    private static bool InRect(Vector2 p, Vector2 back, float heading, float across, float depth)
    {
        Vector2 d = p - back;
        float along = Vector2.Dot(d, F(heading)), side = Vector2.Dot(d, R(heading));
        return along >= -1e-3f && along <= depth + 1e-3f && Mathf.Abs(side) <= across / 2f + 1e-3f;
    }

    /// <summary>Points on a 5 cm grid inside the wedge, kept 5 cm in from its edges.</summary>
    private static List<Vector2> WedgePoints(float h0, int steps, float width)
    {
        float s = Math.Sign(steps);
        Vector2 o0 = Pivot - R(h0) * (s * width), o1 = Pivot - R(h0 + steps * StairLayout.HeadingStep) * (s * width);
        var points = new List<Vector2>();
        for (float a = 0f; a <= 1f; a += 0.01f)
            for (float b = 0f; a + b <= 1f; b += 0.01f)
            {
                Vector2 p = Pivot + (o0 - Pivot) * a + (o1 - Pivot) * b;
                // Distance in from each edge, through the barycentric weights.
                float inset = 0.05f / width;
                if (a < inset || b < inset || 1f - a - b < inset * 0.5f) continue;
                points.Add(p);
            }
        return points;
    }

    /// <summary>Square metres of the pieces that lie outside the clean corner (the
    /// outer sides of both pieces carried on until they meet), the stair above and a floor below.</summary>
    private static float Shows(List<FillerPiece> pieces, float h0, int steps, float width, bool belowIsFloor)
    {
        float s = Math.Sign(steps), h1 = h0 + steps * StairLayout.HeadingStep;
        Vector2 o0 = Pivot - R(h0) * (s * width), o1 = Pivot - R(h1) * (s * width);
        // Walk the old outer side forward in 1 mm steps until it passes outside the new one.
        Vector2 corner = o0;
        float Outside(Vector2 p) => s * Vector2.Dot(p - o1, R(h1)) * -1f;
        while (Outside(corner) < 0f && Vector2.Distance(corner, o0) < 4f * width) corner += F(h0) * 0.001f;
        Vector2 aboveBack = Pivot - R(h1) * (s * width / 2f);
        Vector2 belowBack = Pivot - R(h0) * (s * width / 2f) - F(h0) * 2f;
        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            // Same side of each edge as the opposite corner.
            static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
            return (d1 >= -1e-3f && d2 >= -1e-3f && d3 >= -1e-3f) || (d1 <= 1e-3f && d2 <= 1e-3f && d3 <= 1e-3f);
        }
        bool InWedge(Vector2 p) => InTriangle(p, Pivot, o0, corner) || InTriangle(p, Pivot, corner, o1);
        float shown = 0f;
        const float cell = 0.05f;
        foreach (FillerPiece piece in pieces)
            for (float along = cell / 2f; along < piece.Depth; along += cell)
                for (float side = -piece.Across / 2f + cell / 2f; side < piece.Across / 2f; side += cell)
                {
                    Vector2 p = piece.Back + F(piece.Heading) * along + R(piece.Heading) * side;
                    if (InWedge(p) || InRect(p, aboveBack, h1, width, 2f) || (belowIsFloor && InRect(p, belowBack, h0, width, 2f))) continue;
                    shown += cell * cell;
                }
        return shown;
    }

    private static List<FillerPiece> Fan(float h0, int steps, float width)
    {
        float s = Math.Sign(steps);
        var fan = new List<FillerPiece>();
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            float h = StairLayout.Wrap(h0 + s * i * StairLayout.HeadingStep);
            for (float offset = 0f; offset < width; offset += 2f)
                fan.Add(new FillerPiece("stone_wall_2x1", Pivot - R(h) * (s * (offset + 1f)), h, 2f, 1f));
        }
        return fan;
    }

    public static IEnumerable<object[]> Turns()
    {
        foreach (string kit in new[] { "stone", "wood", "blackmarble" })
            foreach (float width in new[] { 2f, 4f })
                foreach (int steps in new[] { 1, 2, 3, 4, -1, -2, -3, -4 })
                    foreach (bool floor in new[] { false, true })
                        yield return new object[] { kit, 22.5f, steps, width, floor };
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void EveryWedgeIsCoveredOnTheGridAndShowsNoMoreThanTheFan(string kitName, float h0, int steps, float width, bool belowIsFloor)
    {
        StairKit kit = StairKit.Named(kitName)!;
        List<FillerPiece> chosen = StairFiller.Choose(Pivot, h0, steps, width, belowIsFloor, kit.Fillers);

        Assert.NotEmpty(chosen);
        Assert.All(WedgePoints(h0, steps, width), p =>
            Assert.True(chosen.Any(f => InRect(p, f.Back, f.Heading, f.Across, f.Depth)),
                $"wedge point {p} is open under {string.Join(", ", chosen.Select(c => $"{c.Prefab}@{c.Heading} {c.Back}"))}"));
        Assert.All(chosen, f => Assert.True(StairLayout.Wrap(f.Heading) % StairLayout.HeadingStep < 1e-3f, $"filler heading {f.Heading} is off the hammer's grid"));
        Assert.All(chosen, f => Assert.Contains(f.Prefab, kit.Fillers.Select(k => k.Prefab)));
        // The fan of blocks the layout used before covers a 2 m wedge (not a 4 m one, whose
        // outer corner reaches past its 1 m blocks); what is chosen never shows more.
        if (width == 2f && kit != StairKit.Wood)
            Assert.True(Shows(chosen, h0, steps, width, belowIsFloor) <= Shows(Fan(h0, steps, width), h0, steps, width, belowIsFloor) + 0.02f);
    }

    /// <summary>A turn under the summit: a stair 45 degrees off the stair
    /// below, where the fan of two blocks stuck out past the new flight. A
    /// third option fills it: a 2x1 across the old edge and a 1x1 tucked
    /// behind the new one at its outer end.</summary>
    [Fact]
    public void AFortyFiveDegreeStairTurnShowsLessThanTheFan()
    {
        List<FillerPiece> chosen = StairFiller.Choose(Pivot, 157.5f, 2, 2f, belowIsFloor: false);
        float fan = Shows(Fan(157.5f, 2, 2f), 157.5f, 2, 2f, false);

        Assert.True(Shows(chosen, 157.5f, 2, 2f, false) < fan / 2f,
            $"chose {string.Join(", ", chosen.Select(c => $"{c.Prefab}@{c.Heading}"))} showing {Shows(chosen, 157.5f, 2, 2f, false):F2} m2 against the fan's {fan:F2}");
        Assert.Equal(new[] { "stone_wall_1x1", "stone_wall_2x1" }, chosen.Select(c => c.Prefab).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>A right-angle turn off a landing: its clean corner is one 2x2 square.</summary>
    [Fact]
    public void ARightAngleTurnIsOneFloor()
    {
        FillerPiece floor = Assert.Single(StairFiller.Choose(Pivot, 0f, 4, 2f, belowIsFloor: true));
        Assert.Equal(StairKit.Stone.Floor, floor.Prefab);
    }
}

/// <summary>Stairs are built of the biome's pieces.</summary>
public class StairKitTests
{
    [Fact]
    public void EachBiomeBuildsItsOwnMaterial()
    {
        Assert.Same(StairKit.Stone, StairKit.ForSite(Heightmap.Biome.Mountain, new Vector2(5000f, 0f), 1));
        Assert.Same(StairKit.BlackMarble, StairKit.ForSite(Heightmap.Biome.Mistlands, new Vector2(5000f, 0f), 1));
        Assert.Same(StairKit.Wood, StairKit.ForSite(Heightmap.Biome.Meadows, new Vector2(5000f, 0f), 1));
    }

    /// <summary>"the stone should resolve to 0 in the early centre game, just
    /// wood stairs should never not resolve - probility just lowers".</summary>
    [Fact]
    public void TheBlackForestIsWoodAtTheCentreAndNeverAllStoneOutward()
    {
        int Stone(float radius)
        {
            int n = 0;
            for (int i = 0; i < 400; i++)
            {
                float a = i * 0.0157f;
                var at = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
                if (StairKit.ForSite(Heightmap.Biome.BlackForest, at, 1234) == StairKit.Stone) n++;
            }
            return n;
        }
        Assert.Equal(0, Stone(800f));
        Assert.Equal(0, Stone(StairKit.ForestStoneFrom));
        int middle = Stone(2100f), outer = Stone(6000f);
        Assert.InRange(middle, 80, 320);
        Assert.InRange(outer, 280, 360); // about four in five; wood still turns up
        Assert.True(outer > middle);
    }

    /// <summary>A staircase that starts in the Mistlands but is 90% in the mountains uses stone; black marble only if it is 50%+ in the
    /// Mistlands, then 40%.</summary>
    [Theory]
    [InlineData(10f, "stone")]
    [InlineData(35f, "stone")]
    [InlineData(45f, "blackmarble")]
    [InlineData(60f, "blackmarble")]
    public void AStaircaseIsBuiltOfTheBiomeMostOfItLiesIn(float mistMetres, string kit)
    {
        var line = new List<Vector2> { new(0f, 0f), new(0f, 100f) };
        Heightmap.Biome At(float x, float z) => z < mistMetres ? Heightmap.Biome.Mistlands : Heightmap.Biome.Mountain;

        Assert.Equal(kit, StairKit.ForLine(line, At, 1).Name);
    }

    [Fact]
    public void AWoodStaircasesFloorsAreBoardsAtTheWalkingSurface()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 30), (x, z) => 0.3f * z, kit: StairKit.Wood);
        Assert.True(plan.Reached, plan.Reason);
        Assert.All(plan.Pieces, p => Assert.StartsWith("wood_", p.Prefab));
        StairPiece floor = plan.Pieces.First(p => p.Kind == StairPieceKind.Landing);
        StairPiece stairBelow = plan.Pieces.Last(p => p.Kind == StairPieceKind.Stair && plan.Pieces.IndexOf(p) < plan.Pieces.IndexOf(floor));
        // A board's origin is its walking surface: level with the top of the stair below.
        Assert.Equal(stairBelow.Position.y + StairLayout.PieceRise, floor.Position.y, 3);
    }
}
