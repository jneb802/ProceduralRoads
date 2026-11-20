using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A late landing keeps the schedule its road was accepted on, on real roads: the end pieces of two roads as generation
/// planned and accepted them (pitches and the grade cap on, the mod's junction landing), captured offline at
/// RoadNetworkGenerator.PlanEntryFrom before and after RoadPitches.LandEnd landed their ends for a way onto a placed location
/// (Fixtures/landing-capture-*.txt): a 920 m road to a stone tower, its end at the top of a 50% pitch, and a 230 m road to a
/// frost cave whose landing moves the pitch beside it by its own length. Landed by the one acceptance the road is landed
/// as generation landed it, and landed again on what the road then carries nothing moves: the late landing that placed the
/// pitches again re-profiled a road by up to 2.8 m over 130 m before its foot, and every repeat moved it again.
/// </summary>
public sealed class LateLandingCaptureTests
{
    private sealed class Piece
    {
        public bool AtStart, Ok;
        public (bool start, bool end) Before, After;
        public float Cap;
        public readonly List<Vector2> Points = new();
        public readonly List<float> Accepted = new(), Landed = new();
        public readonly List<bool> Held = new();
        public List<float>? Grades, LandedGrades;

        private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        public static Piece Load(string name)
        {
            var p = new Piece();
            foreach (string raw in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                string[] f = raw.TrimEnd('\r').Split(',');
                switch (f[0])
                {
                    case "landing":
                        p.AtStart = f[3] == "True"; p.Ok = f[4] == "True";
                        p.Before = (f[5] == "True", f[6] == "True"); p.After = (f[7] == "True", f[8] == "True"); p.Cap = F(f[9]);
                        break;
                    case "point":
                        p.Points.Add(new Vector2(F(f[1]), F(f[2]))); p.Accepted.Add(F(f[3])); p.Landed.Add(F(f[4]));
                        if (f[5] != "") p.Held.Add(f[5] == "1");
                        break;
                    case "grades": p.Grades = f[1].Split(';').Select(F).ToList(); break;
                    case "landedgrades": p.LandedGrades = f[1].Split(';').Select(F).ToList(); break;
                }
            }
            return p;
        }
    }

    [Theory]
    [InlineData("landing-capture-tower-road.txt")]
    [InlineData("landing-capture-cave-road.txt")]
    public void ALateLandingOnARealRoadKeepsItAsAcceptedAndMovesNothingWhenRepeated(string fixture)
    {
        Piece c = Piece.Load(fixture);
        using var cap = GradeCap.At(c.Cap);
        using var landing = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        IReadOnlyList<bool>? held = c.Held.Count == c.Points.Count ? c.Held : null;

        var heights = new List<float>(c.Accepted);
        bool ok = RoadPitches.LandEnd(c.Points, heights, c.AtStart, RoadGrade.Configured, out List<float>? grades, out var ends, c.Grades, held, c.Before);
        Assert.Equal(c.Ok, ok);
        Assert.Equal(c.After, ends);
        // Landed as generation landed it: the landing, and only what it changes. These captures were accepted with edges
        // beside a rest allowed past the grade cap; such an edge is a last-resort pitch now (RoadPitches.LimitCap), so the
        // landing keeps the schedule it was accepted on: every edge of the landed road is within the cap, or within its
        // pitch's limit where the schedule gives it one.
        bool pastCap = c.Grades != null && c.Grades.Exists(g => g > RoadGrade.Configured + 1e-6f);
        float top = RoadPitches.LimitCap(RoadGrade.Configured, c.Grades);
        for (int i = 0; i < heights.Count; i++)
            if (pastCap)
                Assert.True(i == 0 || Mathf.Abs(heights[i] - heights[i - 1]) <= (c.Grades![i] > RoadGrade.Configured + 1e-6f ? Mathf.Min(c.Grades[i], top) : RoadGrade.Configured) * Vector2.Distance(c.Points[i - 1], c.Points[i]) + 1e-3f,
                    FormattableString.Invariant($"{fixture}: at ({c.Points[i].x:F1}, {c.Points[i].y:F1}) landed past the cap or its pitch's limit"));
            else
                Assert.True(Mathf.Abs(heights[i] - c.Landed[i]) < 1e-4f,
                    FormattableString.Invariant($"{fixture}: at ({c.Points[i].x:F1}, {c.Points[i].y:F1}) landed {heights[i]:F3}, generation {c.Landed[i]:F3}"));
        Assert.False(RoadPitches.LeavesItsLanding(c.Points, heights, c.AtStart, !c.AtStart, RoadGrade.Configured), $"{fixture}: a pitch runs into its landed end");
        // The far end of the road is as it was accepted.
        int far = c.AtStart ? heights.Count - 1 : 0;
        Assert.Equal(c.Accepted[far], heights[far]);

        // Landed again on what the road now carries (its schedule and its landed ends): nothing moves.
        var again = new List<float>(heights);
        Assert.True(RoadPitches.LandEnd(c.Points, again, c.AtStart, RoadGrade.Configured, out _, out _, grades, held, ends));
        for (int i = 0; i < again.Count; i++)
            Assert.True(Mathf.Abs(again[i] - heights[i]) < 1e-4f,
                FormattableString.Invariant($"{fixture}: landed again, ({c.Points[i].x:F1}, {c.Points[i].y:F1}) moved {heights[i]:F3} -> {again[i]:F3}"));
    }
}
