using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The game's support rule run offline (<see cref="SupportGraph"/>), against what the game read on a viaduct that fell.
/// </summary>
public class ViaductSupportTests
{
    /// <summary>One piece the game read: cli_piece_support over a river viaduct read in game (from
    /// (4442, -4992) to (4356.5, -4992), 2 Nov 2026), whose deck between its piers at x 4368, 4384 and 4400 read 100-120
    /// against black marble's least of 100 and fell piece by piece.</summary>
    internal readonly struct Reading
    {
        public readonly string Prefab;
        public readonly Vector3 Position;
        public readonly float Support;
        public Reading(string prefab, Vector3 position, float support) { Prefab = prefab; Position = position; Support = support; }
    }

    internal static List<Reading> GameReadings()
    {
        var rows = new List<Reading>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "viaduct-support-river.csv")))
        {
            if (line.StartsWith("#") || line.StartsWith("prefab,")) continue;
            string[] f = line.Split(',');
            float F(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
            rows.Add(new Reading(f[0], new Vector3(F(1), F(2), F(3)), F(4)));
        }
        return rows;
    }

    /// <summary>The model over the pieces the game read, its boxes the layout's (<see cref="DvergrViaduct.Extents"/>, the deck
    /// along x), grounded where the game read the most support (on the terrain, or touching a rock).</summary>
    private static float[] Model(List<Reading> rows) =>
        SupportGraph.Settle(rows.Select(r =>
        {
            var (hu, hv, hy) = DvergrViaduct.Extents(r.Prefab);
            Vector3 c = r.Position;
            var lo = new Vector3(c.x - hu, c.y - hy, c.z - hv);
            var hi = new Vector3(c.x + hu, c.y + hy, c.z + hv);
            if (r.Prefab == DvergrViaduct.StepPrefab) { lo.y = c.y; hi.y = c.y + 1f; }
            return new SupportGraph.Piece(c, lo, hi, ViaductSupport.MaterialOf(r.Prefab)!.Value, r.Support >= SupportGraph.Marble.Max - 0.1f);
        }).ToList());

    /// <summary>
    /// The rule as transcribed gives the game's own numbers: half the pieces to 2 %, and the deck that fell (the floors,
    /// course and arches between x 4364 and 4398) to 6 % over and 12 % under (the arches: the model takes an arch's
    /// colliders for its 2 x 2 x 1 box), nowhere more than 20 % over, and with ViaductSupport.Overread taken off (corbels
    /// 1.2, arches 1.05) nowhere over the game at all. Where the two differ most the model reads LOW (the game keeps a
    /// piece's support cached while what it rests on does not change, so a piece can keep more than the rule now gives it):
    /// a check on the model is on the safe side.
    /// </summary>
    [Fact]
    public void TheRuleGivesTheGameReadings()
    {
        List<Reading> rows = GameReadings();
        float[] model = Model(rows);
        var errors = new List<float>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Support >= SupportGraph.Marble.Max - 0.1f) continue;
            float error = (model[i] - rows[i].Support) / rows[i].Support;
            errors.Add(error);
            // With its over-reading taken off (ViaductSupport.Overread), the model never reads a piece over the game.
            float taken = model[i] / ViaductSupport.Overread(rows[i].Prefab);
            Assert.True(taken <= rows[i].Support * 1.01f, FormattableString.Invariant(
                $"{rows[i].Prefab} at {rows[i].Position}: game {rows[i].Support:F1}, model {model[i]:F1}, {taken:F1} with its over-reading taken off"));
            bool span = rows[i].Position.x >= 4364f && rows[i].Position.x <= 4398f && rows[i].Position.y >= 45f;
            if (span)
                Assert.True(error >= -0.12f && error <= 0.06f, FormattableString.Invariant(
                    $"{rows[i].Prefab} at {rows[i].Position}: game {rows[i].Support:F1}, model {model[i]:F1}"));
            Assert.True(error <= 0.2f, FormattableString.Invariant($"{rows[i].Prefab} at {rows[i].Position}: game {rows[i].Support:F1}, model {model[i]:F1}"));
        }
        errors.Sort((a, b) => Mathf.Abs(a).CompareTo(Mathf.Abs(b)));
        Assert.True(Mathf.Abs(errors[errors.Count / 2]) <= 0.02f, $"median error {errors[errors.Count / 2]:P1}");
    }

    /// <summary>What fell, by the rule: the deck between the piers at x 4368, 4384 and 4400 (piers 22 m tall, 16 m apart)
    /// under 1.2 times black marble's least, the arches at about 100, while every pier's footing holds its 1500.</summary>
    [Fact]
    public void TheReadSpanIsUnderTheMargin()
    {
        List<Reading> rows = GameReadings();
        float[] model = Model(rows);
        var span = Enumerable.Range(0, rows.Count)
            .Where(i => rows[i].Prefab.StartsWith("blackmarble_") && rows[i].Position.x > 4373f && rows[i].Position.x < 4397f && rows[i].Position.y >= 45f)
            .ToList();
        Assert.NotEmpty(span);
        Assert.True(span.Min(i => model[i]) < 1.2f * SupportGraph.Marble.Min);
        Assert.True(span.Min(i => rows[i].Support) < 1.2f * SupportGraph.Marble.Min);
    }
}
