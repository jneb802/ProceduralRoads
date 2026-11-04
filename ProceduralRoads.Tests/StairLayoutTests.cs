using System;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A stone staircase between two points, laid out on synthetic ground where
/// the right answer is known. The rules come from a hand-built example: 2 m stone stairs that rise 1 m, level landings where the
/// ground is gentler, contour-following on ground steeper than a flight,
/// corner pivots by turn type, fillers in the wedge a convex turn opens, and
/// a pillar column under every piece that does not rest on the ground.
/// </summary>
public class StairLayoutTests
{
    private static Func<float, float, float> PlaneAlongZ(float grade) => (x, z) => grade * z;

    private sealed class EndGuardOff : IDisposable
    {
        public EndGuardOff() => StairLayout.EndGuardReach = 0f;
        public void Dispose() => StairLayout.EndGuardReach = StairLayout.DefaultEndGuardReach;
    }

    private static StairPiece[] Walked(StairPlan plan) =>
        plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).ToArray();

    /// <summary>Ground that rises exactly as a flight does gets one straight
    /// flight up the fall line: no landings, no turns, nothing to prop up.</summary>
    [Fact]
    public void GroundAtFlightGradeIsOneStraightFlight()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), PlaneAlongZ(0.5f));

        Assert.True(plan.Reached, plan.Reason);
        Assert.Equal(10, plan.Stairs);
        Assert.Equal(0, plan.Landings);
        Assert.Equal(0, plan.Turns);
        Assert.DoesNotContain(plan.Pieces, p => p.Kind == StairPieceKind.Support);
        Assert.All(Walked(plan), p => Assert.Equal(180f, p.YawDegrees)); // climbing +z, so the piece faces -z
        Assert.Equal(10f, plan.Rise, 3);
    }

    /// <summary>Each stair sits one rise above the last, and a piece's origin is its foot.</summary>
    [Fact]
    public void StairsStepUpOneRiseAtATime()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 10), PlaneAlongZ(0.5f));
        float[] feet = Walked(plan).Select(p => p.Position.y).ToArray();

        for (int i = 0; i < feet.Length; i++) Assert.Equal(i * StairLayout.PieceRise, feet[i], 3);
        Assert.Equal(1f, Walked(plan)[0].Position.z, 3); // centred one metre along from the start edge
    }

    /// <summary>Gentle ground climbs the same height but rests on landings in between.</summary>
    [Fact]
    public void GentleGroundMixesLandingsIntoTheFlight()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 24), PlaneAlongZ(0.25f));

        Assert.True(plan.Reached, plan.Reason);
        Assert.InRange(plan.Rise, 5.5f, 6.5f);
        Assert.True(plan.Landings > 0, "a 0.25 slope should take landings");
        Assert.True(plan.MaxBuried <= 1f, $"buried {plan.MaxBuried:F2}");
    }

    /// <summary>Ground twice as steep as a flight cannot be climbed straight:
    /// the stair crosses the slope, so it walks at least twice the rise.
    /// The goal is off to the side, so one traverse reaches it.</summary>
    [Fact]
    public void SteepGroundIsClimbedAcrossTheContours()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(17, 10), PlaneAlongZ(1f));

        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.WalkedLength >= 2f * (plan.Rise - StairLayout.ReachHeight),
            $"walked {plan.WalkedLength} for a rise of {plan.Rise}");
        Assert.Contains(Walked(plan), p => p.Kind == StairPieceKind.Stair && p.YawDegrees != 180f);
        Assert.True(plan.MaxBuried <= 1.5f, $"buried {plan.MaxBuried:F2}");
    }

    /// <summary>Asked downhill, the same staircase is laid from the bottom.</summary>
    [Fact]
    public void DownhillRequestIsLaidFromTheBottom()
    {
        StairPlan up = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), PlaneAlongZ(0.5f));
        StairPlan down = StairLayout.Build(new Vector2(0, 20), new Vector2(0, 0), PlaneAlongZ(0.5f));

        Assert.False(up.Reversed);
        Assert.True(down.Reversed);
        Assert.Equal(up.Pieces.Select(p => (p.Prefab, p.Position, p.YawDegrees)), down.Pieces.Select(p => (p.Prefab, p.Position, p.YawDegrees)));
    }

    [Fact]
    public void SameInputSamePieces()
    {
        Func<float, float, float> hill = (x, z) => 0.8f * z + 2f * Mathf.Sin(x * 0.2f);
        StairPlan a = StairLayout.Build(new Vector2(-3, 0), new Vector2(4, 12), hill);
        StairPlan b = StairLayout.Build(new Vector2(-3, 0), new Vector2(4, 12), hill);

        Assert.Equal(a.Pieces.Select(p => (p.Prefab, p.Position, p.YawDegrees)), b.Pieces.Select(p => (p.Prefab, p.Position, p.YawDegrees)));
    }

    /// <summary>Four metres wide is two pieces abreast, one either side of the line.</summary>
    [Fact]
    public void FourMetresWideIsTwoPiecesAbreast()
    {
        StairPlan narrow = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), PlaneAlongZ(0.5f));
        StairPlan wide = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), PlaneAlongZ(0.5f), width: 4f);

        Assert.Equal(2 * narrow.Stairs, wide.Stairs);
        Assert.Equal(new[] { -1f, 1f }, Walked(wide).Select(p => p.Position.x).Distinct().OrderBy(x => x).ToArray());
    }

    /// <summary>Every turn, up the slope or down it, pivots on the inside
    /// corner and leaves a wedge on the outside for a filler.</summary>
    [Theory]
    [InlineData(0.3f, 0.15f)]   // climbing +z the right is uphill, and the goal is to the right
    [InlineData(0.5f, -0.3f)]   // climbing +z the right is downhill
    public void EveryTurnPivotsOnTheInsideCornerAndFillsTheWedge(float alongZ, float alongX)
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(8, 12), (x, z) => alongZ * z + alongX * x);

        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.Turns > 0);
        Assert.Contains(plan.Pieces, p => p.Kind == StairPieceKind.Filler);
    }

    /// <summary>No two pieces you walk on overlap: stairs overlapped
    /// on the inside of turns that pivoted on the outside corner. Fillers
    /// sit under the next stair by design and are left out.</summary>
    [Theory]
    [InlineData("spur")]
    [InlineData("hollow")]
    [InlineData("steep")]
    [InlineData("gully")]
    [InlineData("hill")]
    public void WalkedPiecesNeverOverlap(string ground)
    {
        Func<float, float, float> g = ground switch
        {
            "spur" => (x, z) => 0.5f * z - 0.3f * x,
            "hollow" => (x, z) => 0.3f * z + 0.15f * x,
            "steep" => (x, z) => z,
            "gully" => Gully,
            _ => (x, z) => 0.8f * z + 2f * Mathf.Sin(x * 0.2f),
        };
        Vector2 to = ground == "gully" ? new Vector2(0, 16) : ground == "steep" ? new Vector2(17, 10) : new Vector2(8, 12);
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), to, g);
        StairPiece[] walked = Walked(plan);

        for (int i = 0; i < walked.Length; i++)
            for (int j = i + 1; j < walked.Length; j++)
            {
                // Only pieces at overlapping heights can collide: a flight passing over another is fine.
                if (Math.Abs(walked[i].Position.y - walked[j].Position.y) >= 1f - 1e-3f) continue;
                // Floors may overlap each other at a turn (it looks better); stairs never.
                if (walked[i].Kind == StairPieceKind.Landing && walked[j].Kind == StairPieceKind.Landing) continue;
                Assert.False(FootprintsOverlap(walked[i], walked[j]),
                    $"{walked[i].Prefab} at {walked[i].Position} yaw {walked[i].YawDegrees} overlaps {walked[j].Prefab} at {walked[j].Position} yaw {walked[j].YawDegrees}");
            }
    }

    /// <summary>Separating-axis test on two 2 x 2 m footprints, shrunk by a
    /// few centimetres so pieces that only share an edge or corner do not count.</summary>
    private static bool FootprintsOverlap(StairPiece a, StairPiece b)
    {
        Vector2[] Corners(StairPiece p)
        {
            Vector2 c = new Vector2(p.Position.x, p.Position.z), f = StairLayout.Forward(p.YawDegrees), r = StairLayout.Right(p.YawDegrees);
            const float h = StairLayout.PieceRun / 2f - 0.05f;
            return new[] { c + f * h + r * h, c + f * h - r * h, c - f * h - r * h, c - f * h + r * h };
        }
        Vector2[] ca = Corners(a), cb = Corners(b);
        foreach (Vector2 axis in new[] { StairLayout.Forward(a.YawDegrees), StairLayout.Right(a.YawDegrees), StairLayout.Forward(b.YawDegrees), StairLayout.Right(b.YawDegrees) })
        {
            float aMin = ca.Min(p => Vector2.Dot(p, axis)), aMax = ca.Max(p => Vector2.Dot(p, axis));
            float bMin = cb.Min(p => Vector2.Dot(p, axis)), bMax = cb.Max(p => Vector2.Dot(p, axis));
            if (aMax <= bMin || bMax <= aMin) return false;
        }
        return true;
    }

    /// <summary>Where the stair crosses a dip, pillars reach down into the ground.</summary>
    [Fact]
    public void PiecesOverADipStandOnPillarsBuriedInTheGround()
    {
        Func<float, float, float> dip = (x, z) => 0.5f * z - (z > 6f && z < 12f ? 5f : 0f);
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), dip);

        Assert.True(plan.Reached, plan.Reason);
        // Columns, and a cantilever's strip columns (its arches and blocks are held by them).
        StairPiece[] supports = plan.Pieces.Where(p => p.Kind == StairPieceKind.Support && p.Prefab == StairKit.Stone.Support).ToArray();
        Assert.NotEmpty(supports);
        foreach (var column in supports.GroupBy(p => (p.Position.x, p.Position.z)))
        {
            float bottom = column.Min(p => p.Position.y) - StairKit.Stone.SupportHeight / 2f;
            Assert.True(bottom <= dip(column.Key.x, column.Key.z) - StairLayout.SupportBury + 1e-3f,
                $"column at {column.Key} ends at {bottom:F2}, ground {dip(column.Key.x, column.Key.z):F2}");
        }
    }

    /// <summary>A gully up a face twice as steep as a flight, 12 m wide
    /// between rock walls: too narrow to curve round, so the stair zigzags,
    /// turning back on switchback landings.</summary>
    private static float Gully(float x, float z) => z + (Math.Abs(x) > 6f ? 50f : 0f);

    [Fact]
    public void NarrowSteepGullyZigzagsOnSwitchbacks()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 16), Gully);

        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.Switchbacks > 0, "expected at least one switchback");
        Assert.True(plan.MaxBuried <= 1.5f, $"buried {plan.MaxBuried:F2}");
    }

    /// <summary>A hand-built switchback: the flight arrives on one floor, a second
    /// floor lies one stair-width beside it, and the next flight leaves that
    /// floor heading back the way it came, from the same height.</summary>
    [Fact]
    public void SwitchbackIsTwoFloorsAbreastAndTheFlightTurnsBack()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 16), Gully);
        StairPiece[] walked = Walked(plan);
        int i = Enumerable.Range(1, Math.Max(0, walked.Length - 3)).FirstOrDefault(k =>
            walked[k - 1].Kind == StairPieceKind.Stair && walked[k].Kind == StairPieceKind.Landing &&
            walked[k + 1].Kind == StairPieceKind.Landing && walked[k + 2].Kind == StairPieceKind.Stair &&
            Vector2.Distance(new Vector2(walked[k].Position.x, walked[k].Position.z), new Vector2(walked[k + 1].Position.x, walked[k + 1].Position.z)) > 1.5f &&
            walked[k].YawDegrees == walked[k + 1].YawDegrees);
        Assert.True(i > 0, "no stair - floor - floor - stair switchback");

        StairPiece arrive = walked[i - 1], a = walked[i], b = walked[i + 1], leave = walked[i + 2];
        Assert.Equal(2f, Vector2.Distance(new Vector2(a.Position.x, a.Position.z), new Vector2(b.Position.x, b.Position.z)), 3);
        Assert.Equal(a.Position.y, b.Position.y, 3);
        Assert.Equal(arrive.Position.y + StairLayout.PieceRise, leave.Position.y, 3);
        float bend = Math.Abs(StairLayout.Wrap(leave.YawDegrees - arrive.YawDegrees - 180f + 180f) - 180f);
        Assert.True(bend <= StairLayout.MaxSwitchbackBendSteps * StairLayout.HeadingStep + 1e-3f,
            $"leaves {bend} degrees off the reverse heading");
    }

    /// <summary>A 22.5 degree turn on a stair is filled with a flat
    /// 1 m block at the old heading, level with the stair top before it.</summary>
    [Fact]
    public void ShallowTurnIsFilledWithAFlatBlock()
    {
        Func<float, float, float> uphillRight = (x, z) => 0.3f * z + 0.15f * x;
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(8, 12), uphillRight);

        StairPiece[] blocks = plan.Pieces.Where(p => p.Prefab == "stone_wall_2x1").ToArray();
        Assert.All(blocks, block => Assert.Equal(StairPieceKind.Filler, block.Kind));
        Assert.All(plan.Pieces.Where(p => p.Kind == StairPieceKind.Filler), f =>
            Assert.True(f.YawDegrees % StairLayout.HeadingStep < 1e-3f, $"filler yaw {f.YawDegrees} is off the hammer's grid"));
    }

    /// <summary>Every piece walked on has its body and 2 m of headroom kept
    /// clear of rock: two stretches per stair, following the steps up.</summary>
    [Fact]
    public void EveryWalkedPieceHasAClearanceFromItsUndersideToHeadroom()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 24), PlaneAlongZ(0.25f));
        int walkedPieces = plan.Pieces.Count(p => p.Kind != StairPieceKind.Support);
        int stairs = plan.Pieces.Count(p => p.Prefab == StairKit.Stone.Stair);

        Assert.Equal(walkedPieces + stairs * (StairLayout.ClearanceStretches - 1), plan.Clearances.Count);
        Assert.All(plan.Clearances, c =>
        {
            Assert.Equal(c.Surface + StairLayout.ClearanceHeight, c.Centre.y + c.Half.y, 3);
            Assert.True(c.Centre.y - c.Half.y <= c.Surface, "the box starts at or below the walking surface");
        });
        // The first stair's two stretches step up a half rise apart.
        Assert.Equal(0.5f, plan.Clearances[1].Surface - plan.Clearances[0].Surface, 3);
    }

    /// <summary>Real ground: an 82 m climb up a Mountain face of 1:1 to 2:1,
    /// sampled in game on a 2 m grid. The first search drifted onto a gentle
    /// bench beside it and ran out of steps after 30 stairs (a much longer run is wanted).</summary>
    [Fact]
    public void LongClimbUpARealMountainFaceIsReached()
    {
        Func<float, float, float> face = FixtureGround("mountain-face-2m.txt");
        StairPlan plan = StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), face);

        Assert.True(plan.Reached, plan.Reason);
        Assert.InRange(plan.Rise, 80f, 84f);
        Assert.True(plan.MaxBuried <= 1.5f, $"buried {plan.MaxBuried:F2}");
        // Mostly hugging the terrain: some lift is fine, a row of tall pillars is not.
        Assert.True(plan.MaxFloat <= 2.5f, $"floats {plan.MaxFloat:F2} m at worst");
        // A small earthwork exposes a buried stretch, but hugging comes first: never deeper than
        // the deepest burial the search accepts, plus the cut's depth under the steps.
        Assert.True(plan.MaxCut <= StairCover.RoadStaircase + StairLayout.CutBelowSurface + 0.05f, $"cuts {plan.MaxCut:F2} m deep at worst");
        Assert.All(plan.Pieces, p => Assert.True(p.YawDegrees % StairLayout.HeadingStep < 1e-3f,
            $"{p.Kind} {p.Prefab} yaw {p.YawDegrees} is off the hammer's grid"));
        // Every turn's wedge gets fillers (StairFillerTests checks they cover it).
        Assert.Contains(plan.Pieces, p => p.Kind == StairPieceKind.Filler);
    }

    /// <summary>A landing lines up with the stair below it; the turn comes
    /// as the next stair leaves it. A landing turned 45 degrees off the stair
    /// below left a gap to the stair above.</summary>
    [Theory]
    [InlineData("face")]
    [InlineData("gentle")]
    [InlineData("hill")]
    public void LandingLinesUpWithTheStairBelowIt(string ground)
    {
        StairPlan plan = ground switch
        {
            "face" => StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), FixtureGround("mountain-face-2m.txt")),
            "gentle" => StairLayout.Build(new Vector2(0, 0), new Vector2(0, 24), PlaneAlongZ(0.25f)),
            _ => StairLayout.Build(new Vector2(-3, 0), new Vector2(14, 22), (x, z) => 0.4f * z + 2f * Mathf.Sin(x * 0.2f)),
        };
        StairPiece[] walked = Walked(plan);
        Assert.Contains(walked, p => p.Kind == StairPieceKind.Landing);
        for (int i = 1; i < walked.Length; i++)
        {
            // A floor after a stair lines up with it (a stair faces back down its flight); a floor
            // after a floor may turn, overlapping it.
            if (walked[i].Kind != StairPieceKind.Landing || walked[i - 1].Kind != StairPieceKind.Stair) continue;
            Assert.Equal(StairLayout.Wrap(walked[i - 1].YawDegrees + 180f), walked[i].YawDegrees, 3);
        }
    }

    /// <summary>A road arriving at the foot going the other way from the climb:
    /// the staircase sets off within 45 degrees of the road and switches back,
    /// and finishes lined up with the road leaving the top (a mini switchback at the end so the direction of the
    /// road mostly lines up with the direction of the stairs).</summary>
    [Fact]
    public void ItsEndsLineUpWithTheRoadAtThem()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), PlaneAlongZ(0.5f), arriving: 180f, leaving: 90f);

        Assert.True(plan.Reached, plan.Reason);
        float Off(float a, float b) => Math.Abs(StairLayout.Wrap(a - b + 180f) - 180f);
        Assert.True(Off(plan.StartHeading, 180f) <= 45.01f, $"sets off at {plan.StartHeading}");
        Assert.True(Off(plan.EndHeading, 90f) <= 45.01f, $"finishes at {plan.EndHeading}");
        Assert.True(plan.Switchbacks > 0 || plan.Turns > 0);
        // The first piece really faces the way the plan says (a stair faces back down its flight).
        StairPiece first = Walked(plan)[0];
        Assert.Equal(plan.StartHeading, first.Kind == StairPieceKind.Stair ? StairLayout.Wrap(first.YawDegrees + 180f) : first.YawDegrees, 3);
    }

    /// <summary>A piece sitting on the ground hovers a few centimetres above it in
    /// game and falls: every walked piece not sunk SupportBury into the ground
    /// stands on a column (a black marble staircase lost a quarter of its pieces).</summary>
    [Fact]
    public void EveryPieceNotSunkIntoTheGroundStandsOnAColumn()
    {
        // Real ground: on an exact 0.5 slope a stair is sunk half a metre at its
        // middle and needs nothing; floors and fillers on a rough face do.
        Func<float, float, float> ground = FixtureGround("mountain-face-2m.txt");
        StairPlan plan = StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), ground);
        var columns = plan.Pieces.Where(p => p.Kind == StairPieceKind.Support && !p.Cantilevered).ToList();
        var arches = plan.Pieces.Where(p => p.Prefab == StairKit.Stone.Cantilever!.Arch).ToList();

        Assert.NotEmpty(columns);
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
        {
            float underside = piece.Prefab == StairKit.Stone.Stair ? piece.Position.y : piece.Position.y + StairKit.Stone.FloorTop - StairKit.Stone.FloorThickness;
            float g = ground(piece.Position.x, piece.Position.z);
            if (underside <= g - StairLayout.SupportBury) continue;
            // A turn's filler off a cantilevered piece rests on the turn's arch (CantileverTests: the hand-built turns).
            if (piece.Cantilevered && piece.Kind == StairPieceKind.Filler) continue;
            if (piece.Cantilevered)
            {
                // Held out from the face: two arches under it, their tops at its underside (CantileverTests).
                Assert.True(2 <= arches.Count(a => Vector2.Distance(new Vector2(a.Position.x, a.Position.z), new Vector2(piece.Position.x, piece.Position.z)) < 0.6f
                    && Math.Abs(a.Position.y + StairKit.Stone.Cantilever!.ArchHeight / 2f - underside) < 0.01f));
                continue;
            }
            Assert.Contains(columns, c => Math.Abs(c.Position.x - piece.Position.x) < 0.01f && Math.Abs(c.Position.z - piece.Position.z) < 0.01f
                && c.Position.y - StairKit.Stone.SupportHeight / 2f <= g - StairLayout.SupportBury + 1e-3f);
        }
    }

    /// <summary>Ground from an x z height grid file, bilinear between samples, clamped at its edges.</summary>
    internal static Func<float, float, float> FixtureGround(string name)
    {
        var heights = new System.Collections.Generic.Dictionary<(int, int), float>();
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue, step = 2;
        foreach (string line in System.IO.File.ReadLines(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))
        {
            if (line.StartsWith("#") || line.Trim().Length == 0) continue;
            string[] f = line.Split(' ');
            int x = (int)Math.Round(float.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture));
            int z = (int)Math.Round(float.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture));
            heights[(x, z)] = float.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        float H(int x, int z) => heights[(Math.Min(Math.Max(x, minX), maxX), Math.Min(Math.Max(z, minZ), maxZ))];
        return (x, z) =>
        {
            int x0 = (int)Math.Floor(x / step) * step, z0 = (int)Math.Floor(z / step) * step;
            float fx = Mathf.Clamp01((x - x0) / step), fz = Mathf.Clamp01((z - z0) / step);
            return H(x0, z0) * (1 - fx) * (1 - fz) + H(x0 + step, z0) * fx * (1 - fz) + H(x0, z0 + step) * (1 - fx) * fz + H(x0 + step, z0 + step) * fx * fz;
        };
    }

    /// <summary>Where the ground covers a walking surface, the plan lowers it to
    /// just under the steps; where the stair stands on or above the ground,
    /// nothing is cut (a small earthwork to expose a buried stretch).</summary>
    [Fact]
    public void BuriedStretchesGetASmallCutAndExposedOnesNone()
    {
        // The burial rule alone: the end guard (TreadsNearEitherEndAreKeptClearOfEarthwork) cuts every row near the ends.
        using var guard = new EndGuardOff();
        StairPlan onGround = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), (x, z) => 0.5f * z - 0.2f);
        Assert.Empty(onGround.Cuts);

        // A mound 0.6 m high across the fall line between z 8 and 12 buries part of the flight.
        Func<float, float, float> mound = (x, z) => 0.5f * z + (z > 8f && z < 12f ? 0.6f : 0f);
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), mound);
        Assert.True(plan.Reached, plan.Reason);
        Assert.NotEmpty(plan.Cuts);
        Assert.All(plan.Cuts, c => Assert.InRange(c.z, 8f, 12f));
        Assert.All(plan.Cuts, c => Assert.True(mound(c.x, c.z) > c.y, "a cut only lowers ground"));
        Assert.InRange(plan.MaxCut, 0.6f, 0.6f + StairLayout.CutBelowSurface + 1e-3f);
    }

    /// <summary>Ground grazing one edge of a stair is fine; an edge buried
    /// deeper than <see cref="StairCover.ApproachSearch"/> is dug out across the
    /// row, even when most of the walkway is clear (it is fine if some of the stairs are covered, just not all of them;
    /// too much terrain overlap is not wanted).</summary>
    [Theory]
    [InlineData(0.2f, false)]
    [InlineData(0.6f, true)]
    public void AnEdgeInTheSlopeIsDugOutOnlyWhenDeeplyBuried(float bank, bool dug)
    {
        // The burial rule alone: the end guard (TreadsNearEitherEndAreKeptClearOfEarthwork) cuts every row near the ends.
        using var guard = new EndGuardOff();
        // A bank along one side (x > 0.4) of a straight flight up +z.
        Func<float, float, float> ground = (x, z) => 0.5f * z + (x > 0.4f ? bank : 0f);
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), ground);
        Assert.True(plan.Reached, plan.Reason);
        Assert.Equal(dug, plan.Cuts.Count > 0);
    }

    /// <summary>A gentle valley floor for 100 m, then a face steeper than 1:1:
    /// 230 m of climb over 300 m. Too long for one search; it follows a coarse
    /// route leg by leg (valley bottom to mountain top, with some flex).</summary>
    private static float ValleyThenFace(float x, float z) =>
        z < 100f ? 0.15f * z : 15f + (z - 100f) * 1.05f + 6f * Mathf.Sin(z * 0.05f) + 4f * Mathf.Sin(x * 0.07f);

    [Fact]
    public void ValleyBottomToMountainTopIsReachedAlongACoarseRoute()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(40, 300), ValleyThenFace);

        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.Waypoints > 1, "a run this long follows a route");
        Assert.InRange(plan.Rise, 228f, 232f);
        Assert.True(plan.Switchbacks > 0, "a face steeper than 1:1 is climbed on switchbacks");
        Assert.True(plan.MaxBuried <= StairCover.RoadStaircase + 1e-3f, $"buried {plan.MaxBuried:F2}");
    }

    /// <summary>The coarse route never climbs steeper than a stair flight: up a
    /// face steeper than 1:1 it zigzags, so it is about twice as long as the climb.</summary>
    [Fact]
    public void CoarseRouteIsNeverSteeperThanAFlight()
    {
        var waypoints = StairRoute.Waypoints(new Vector2(0, 0), new Vector2(40, 300), ValleyThenFace);

        Assert.NotNull(waypoints);
        float length = Vector2.Distance(new Vector2(0, 0), waypoints![0]);
        for (int i = 1; i < waypoints.Count; i++) length += Vector2.Distance(waypoints[i - 1], waypoints[i]);
        float climb = ValleyThenFace(40, 300) - ValleyThenFace(0, 0);
        // Waypoints are thinned to about 12 m, which cuts the zigzag corners a little.
        Assert.True(length >= climb / StairRoute.MaxClimb * 0.85f, $"route {length:F0} m for a {climb:F0} m climb");
    }

    /// <summary>Every tread and landing within EndGuardReach of either end has a cut under
    /// it, buried by the natural ground or not: the road meeting a staircase is held at its end
    /// height and its earthwork covered the last steps (a top
    /// step 0.8 m under, hard to walk up). Pieces further in on ground below
    /// them get none, as before.</summary>
    [Fact]
    public void TreadsNearEitherEndAreKeptClearOfEarthwork()
    {
        // A slope gentler than a flight: the treads stand over the ground all the way up.
        float Slope(float x, float z) => 40f + 0.4f * z;
        var plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 40), Slope, sunkFoot: true);
        Assert.True(plan.Reached, plan.Reason);
        Vector2 foot = new(0, 0), top = new(plan.End.x, plan.End.z);
        int guarded = 0, inner = 0;
        // The first walked piece is the sunk foot, buried on purpose: skipped.
        foreach (StairPiece p in Walked(plan).Skip(1))
        {
            var c = new Vector2(p.Position.x, p.Position.z);
            float tread = p.Kind == StairPieceKind.Stair ? p.Position.y + StairLayout.PieceRise / 2f : p.Position.y + StairKit.Stone.FloorTop;
            bool near = Vector2.Distance(c, foot) <= StairLayout.EndGuardReach || Vector2.Distance(c, top) <= StairLayout.EndGuardReach;
            bool cut = plan.Cuts.Exists(k => Vector2.Distance(new Vector2(k.x, k.z), c) < 1f && k.y <= tread);
            if (near) { guarded++; Assert.True(cut, $"no cut under the {p.Kind} at {p.Position}, {Vector2.Distance(c, foot):F1} m from the foot"); }
            else if (!cut) inner++;
        }
        Assert.True(guarded > 0);
        Assert.True(inner > 0, "pieces away from the ends are left as they were");
    }

    /// <summary>A stair flight climbs between two switchbacks: two with only
    /// floors between them made a pad that shuffled the stair 4 m sideways on
    /// the summit run.</summary>
    [Theory]
    [InlineData("gully")]
    [InlineData("face")]
    [InlineData("valley")]
    public void AFlightClimbsBetweenTwoSwitchbacks(string ground)
    {
        StairPlan plan = ground switch
        {
            "gully" => StairLayout.Build(new Vector2(0, 0), new Vector2(0, 16), Gully),
            "face" => StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), FixtureGround("mountain-face-2m.txt")),
            _ => StairLayout.Build(new Vector2(0, 0), new Vector2(40, 300), ValleyThenFace),
        };
        Assert.True(plan.Reached, plan.Reason);
        StairPiece[] walked = Walked(plan);
        // A switchback is two floors abreast at one height and heading; count stairs since the last one,
        // and from the foot: a switchback after the first stair stood its pad over the road it leaves
        // (the bottom of the stairs was blocked off by the lower switchback).
        int? stairsSinceSwitchback = 0;
        for (int k = 0; k + 1 < walked.Length; k++)
        {
            // Abreast: 2 m apart ACROSS their heading (a straight run of floors is 2 m apart along it).
            Vector2 a = new Vector2(walked[k].Position.x, walked[k].Position.z), b = new Vector2(walked[k + 1].Position.x, walked[k + 1].Position.z);
            bool switchback = walked[k].Kind == StairPieceKind.Landing && walked[k + 1].Kind == StairPieceKind.Landing &&
                walked[k].YawDegrees == walked[k + 1].YawDegrees && walked[k].Position.y == walked[k + 1].Position.y &&
                Math.Abs(Vector2.Distance(a, b) - 2f) < 0.01f &&
                Math.Abs(Vector2.Dot(b - a, StairLayout.Forward(walked[k].YawDegrees))) < 0.01f;
            if (switchback)
            {
                if (stairsSinceSwitchback.HasValue) Assert.True(stairsSinceSwitchback >= StairLayout.MinFlightBetweenSwitchbacks, $"a switchback {stairsSinceSwitchback} stair(s) after the last one or the foot, at {walked[k].Position}");
                stairsSinceSwitchback = 0;
                k++;
            }
            else if (walked[k].Kind == StairPieceKind.Stair && stairsSinceSwitchback.HasValue) stairsSinceSwitchback++;
        }
    }

    /// <summary>No walked piece lies over another, seen from above, except the one
    /// laid just before it (floors may overlap at a turn): an upper flight over a
    /// lower one has nowhere for its supports to stand.</summary>
    [Theory]
    [InlineData("face")]
    [InlineData("summit")]
    public void NoFlightLiesOverAnother(string ground)
    {
        StairPlan plan = ground == "face"
            ? StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), FixtureGround("mountain-face-2m.txt"))
            : StairLayout.Build(new Vector2(0, 0), new Vector2(40, 300), ValleyThenFace);
        Assert.True(plan.Reached, plan.Reason);
        StairPiece[] walked = Walked(plan);

        static bool Overlap(StairPiece p, StairPiece q)
        {
            Vector2 cp = new(p.Position.x, p.Position.z), cq = new(q.Position.x, q.Position.z);
            Vector2[] axes = { StairLayout.Forward(p.YawDegrees), StairLayout.Right(p.YawDegrees), StairLayout.Forward(q.YawDegrees), StairLayout.Right(q.YawDegrees) };
            foreach (Vector2 axis in axes)
            {
                float r = Math.Abs(Vector2.Dot(StairLayout.Forward(p.YawDegrees), axis)) + Math.Abs(Vector2.Dot(StairLayout.Right(p.YawDegrees), axis))
                        + Math.Abs(Vector2.Dot(StairLayout.Forward(q.YawDegrees), axis)) + Math.Abs(Vector2.Dot(StairLayout.Right(q.YawDegrees), axis));
                if (Math.Abs(Vector2.Dot(cq - cp, axis)) >= r - 0.2f) return false;
            }
            return true;
        }
        for (int i = 0; i < walked.Length; i++)
            for (int j = i + 2; j < walked.Length; j++)
                Assert.False(Overlap(walked[i], walked[j]), $"{walked[j].Kind} at {walked[j].Position} over {walked[i].Kind} at {walked[i].Position}");
    }

    /// <summary>Every column's base lies wholly under the finished ground beneath it
    /// (after the staircase's own cuts and their batters), not only at its centre
    /// (the whole bottom must clip into the ground; a support that did not fully clip its bottom into the ground was
    /// 1.6 m short beside a cut's batter).</summary>
    /// <summary>A cantilever's pillars are its anchor (Valheim grounds a piece that touches terrain): on
    /// the real face, each one's foot stands at least SupportBury inside the ground the staircase's own
    /// cuts and their batters leave, and each stone arch's tall end meets it.</summary>
    [Fact]
    public void EveryCantileverPillarIsClippedIntoTheFinishedGround()
    {
        Func<float, float, float> ground = FixtureGround("mountain-face-2m.txt");
        StairPlan plan = StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), ground);
        CantileverKit ck = StairKit.Stone.Cantilever!;
        var pillars = plan.Pieces.Where(p => p.Cantilevered && p.Prefab == ck.Pillar).ToList();
        Assert.NotEmpty(pillars);
        float Finished(float px, float pz)
        {
            float h = ground(px, pz);
            foreach (Vector3 c in plan.Cuts)
            {
                float d = Vector2.Distance(new Vector2(c.x, c.z), new Vector2(px, pz));
                if (d <= StairLayout.CutRadius + 3f) h = Math.Min(h, c.y + Math.Max(0f, d - StairLayout.CutRadius));
            }
            return h;
        }
        foreach (StairPiece pillar in pillars)
        {
            float foot = pillar.Position.y - ck.PillarHeight / 2f;
            Assert.True(Finished(pillar.Position.x, pillar.Position.z) >= foot + StairLayout.SupportBury - 1e-3f,
                $"pillar at {pillar.Position}: foot {foot:F2}, finished ground {Finished(pillar.Position.x, pillar.Position.z):F2}");
        }
    }

    [Fact]
    public void EveryColumnsBaseIsWhollyInTheGround()
    {
        Func<float, float, float> ground = FixtureGround("mountain-face-2m.txt");
        StairPlan plan = StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), ground);
        // The finished ground: natural, lowered by every cut within reach, each
        // flat to its radius and climbing back on a 1:1 batter for 3 m beyond.
        float Finished(float px, float pz)
        {
            float h = ground(px, pz);
            foreach (Vector3 c in plan.Cuts)
            {
                float d = Vector2.Distance(new Vector2(c.x, c.z), new Vector2(px, pz));
                if (d <= StairLayout.CutRadius + 3f) h = Math.Min(h, c.y + Math.Max(0f, d - StairLayout.CutRadius));
            }
            return h;
        }
        // Columns are carried to the finished ground by one owner, once the network's earthwork is known: the
        // layout sizes them on the ground it read, and BuriedPieces.CarryColumns carries them on below the cuts.
        StairRun run = StairRun.FromPlan(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), 2f, plan);
        BuriedPieces.CarryColumns(run, (i, k) => Finished(i, k));
        BuriedPieces.Drop(run, (i, k) => Finished(i, k));
        Assert.All(run.Pieces, p => Assert.False(BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, (i, k) => Finished(i, k)), $"{p.Prefab} at {p.Position} wholly under the ground"));
        var columns = run.Pieces.Where(p => p.Kind == StructurePieceKind.StairSupport && !p.Cantilevered)
            .GroupBy(p => (Math.Round(p.Position.x, 2), Math.Round(p.Position.z, 2)));
        Assert.NotEmpty(columns);
        foreach (var column in columns)
        {
            StructurePiece bottom = column.OrderBy(p => p.Position.y).First();
            float baseY = bottom.Position.y - StairKit.Stone.SupportHeight / 2f;
            float x = bottom.Position.x, z = bottom.Position.z;
            float lowest = new[] { Finished(x - 0.5f, z - 0.5f), Finished(x + 0.5f, z - 0.5f), Finished(x - 0.5f, z + 0.5f), Finished(x + 0.5f, z + 0.5f) }.Min();
            // SupportBury under the lowest corner, or already in the ground under all of the next pillar's grown footprint
            // (the one column rule, BuriedPieces.ColumnGoesOn: no pillar wholly under the ground).
            Assert.True(baseY <= lowest - StairLayout.SupportBury + 1e-3f
                || BuriedPieces.Buried("stone_pillar", new Vector3(x, baseY - 1f, z), bottom.YawDegrees, (i, k) => Finished(i, k)),
                $"column at {bottom.Position} ends at {baseY:F2}, finished ground as low as {lowest:F2}");
        }
    }

    /// <summary>A staircase ends level with the ground at its own end, not merely
    /// near the goal's height: on a steep slope one stopped 2.4 m under the ground
    /// there and the road leaving it was dug 5 m into the hill.</summary>
    [Fact]
    public void ItEndsLevelWithTheGroundAtItsEnd()
    {
        Func<float, float, float> ground = FixtureGround("mountain-face-2m.txt");
        StairPlan plan = StairLayout.Build(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), ground);

        Assert.True(plan.Reached, plan.Reason);
        Assert.InRange(plan.End.y - ground(plan.End.x, plan.End.z), -StairLayout.ReachHeight - 1e-3f, StairLayout.ReachHeight + 1e-3f);
    }

    /// <summary>A generated staircase ends on a floor carried on straight from its
    /// top step, level with it, and the road meets that floor's far edge (a flat 2x2 landing or two
    /// that clips into the road).</summary>
    [Fact]
    public void ItCanEndOnALandingForTheRoad()
    {
        Func<float, float, float> ground = PlaneAlongZ(0.5f);
        StairPlan bare = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), ground);
        StairPlan landed = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), ground, topLandings: 1);

        Assert.True(landed.Reached, landed.Reason);
        var walkedLanded = Walked(landed);
        StairPiece last = walkedLanded[walkedLanded.Length - 1];
        Assert.Equal(StairPieceKind.Landing, last.Kind);
        Assert.Equal(bare.End.y, landed.End.y, 3);
        Vector2 step = new Vector2(landed.End.x - bare.End.x, landed.End.z - bare.End.z);
        Assert.Equal(StairLayout.PieceRun, step.magnitude, 2);
        Assert.Equal(StairLayout.Wrap(landed.EndHeading), StairLayout.Bearing(new Vector2(0f, 0f), step), 1);
        // Its walking surface is the top step's.
        Assert.Equal(landed.End.y, last.Position.y + StairKit.Stone.FloorTop, 3);
    }

    /// <summary>A generated staircase starts with one more stair sunk a step into the
    /// ground, so the flight comes up out of the road rather than standing on it
    /// with a lip (one extra set of stairs clipped down into the
    /// earth makes it connect better); no earthwork uncovers it.</summary>
    [Fact]
    public void ItCanStartWithAStairSunkIntoTheGround()
    {
        Func<float, float, float> ground = PlaneAlongZ(0.5f);
        StairPlan bare = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), ground);
        StairPlan sunk = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 20), ground, sunkFoot: true);

        Assert.True(sunk.Reached, sunk.Reason);
        StairPiece first = Walked(bare)[0];
        StairPiece below = Walked(sunk)[0];
        Assert.Equal(StairPieceKind.Stair, below.Kind);
        Assert.Equal(first.YawDegrees, below.YawDegrees, 3);
        Assert.Equal(first.Position.y - StairLayout.PieceRise, below.Position.y, 3);
        // One run back down the flight, and nothing dug to show it.
        Vector2 back = new Vector2(first.Position.x - below.Position.x, first.Position.z - below.Position.z);
        Assert.Equal(StairLayout.PieceRun, back.magnitude, 2);
        Assert.Equal(bare.Cuts.Count, sunk.Cuts.Count);
        Assert.Equal(Walked(bare).Length + 1, Walked(sunk).Length);
    }

    /// <summary>A cliff the stair cannot climb within its piece budget is reported, not faked.</summary>
    [Fact]
    public void UnclimbableCliffIsReportedNotReached()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 4), (x, z) => z > 2f ? 400f : 0f);

        Assert.False(plan.Reached);
        Assert.NotEqual("", plan.Reason);
    }

    /// <summary>A flight that reaches the ledge it climbs to does not end on a switchback: finishing on
    /// the pad turned back the way it came, and the steps down off the top ran back down beside the
    /// flight, two rows wide (MountainCave02 -2560,1536: the double stairs looked like
    /// a bug).</summary>
    [Fact]
    public void AStaircaseDoesNotEndOnASwitchbackFacingBackDownItsFlight()
    {
        // Up a 0.5 face along z to 5 m at z = 10; past it, a ledge at 5 m to the left (x < -1) and a
        // drop to 2 m straight ahead: the floor straight on overhangs the drop, the switchback's
        // second floor stands on the ledge.
        float Ground(float x, float z) => z <= 0f ? 0f : x < -1f && z > 8f ? 5f : z >= 9.9f ? 2f : 0.5f * z;
        Vector2 from = new(0f, 0f), to = new(-3f, 14f);

        // Held to the edge cover an approach is held to, nothing turned off the flight fits onto the ledge.
        StairPlan plan = StairLayout.Build(from, to, Ground, maxBuried: StairCover.ApproachSearch, arriveWithin: 6f);

        Assert.True(plan.Reached, plan.Reason);
        float back = Mathf.Abs(StairLayout.Wrap(plan.EndHeading - StairLayout.Bearing(from, to) + 180f) - 180f);
        Assert.True(back < 90f, $"ends facing {plan.EndHeading} after {plan.Switchbacks} switchback(s): "
            + string.Join(" ", plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing)
                .Select(p => FormattableString.Invariant($"{p.Kind}({p.Position.x:F1},{p.Position.y:F1},{p.Position.z:F1})y{p.YawDegrees}"))));
    }
}
