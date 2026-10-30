using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>
/// A refused approach's ground as the game once wrote it (BepInEx/config/ProceduralRoads.approach_X_Z.csv), read back to lay the approach again
/// offline as the game did (<see cref="Replay"/>): the request, the place's turn, root,
/// levelling and boulders, the staircase row the road ends on, and the ground on a 1 m grid.
/// Older files lack the root, the place's levelling (only its parts'
/// offsets), its rock radius and the row: pass them in (<see cref="Parse"/>).
/// </summary>
public sealed class ApproachGroundFile
{
    public SiteApproachRequest Request = new();
    public float Yaw;
    /// <summary>The place's root height (NaN: not written; then no landing is held to its entrance's level).</summary>
    public float Root = float.NaN;
    public float RockRadius;
    public bool KeepSmallRocks;
    public bool OnStairs;
    public float RoadWidth;
    public StairContinuation? Top;
    public IReadOnlyList<LevelOp> Ops = Array.Empty<LevelOp>();
    public List<SiteRock> Rocks = new();
    public int X0, Z0, W, H;
    public float[] Heights = Array.Empty<float>();
    /// <summary>The footprint the terrain writer leaves alone: the place's terrain reach round its centre.</summary>
    public float FootprintRadius;
    public Func<float, float, float> Ground => PlacedSiteGround.Sampler(Heights, X0, Z0, W, H);

    private static float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);

    /// <param name="prefabOps">The place's prefab levelling (road_sites_dump), for a file that names only
    /// its kept parts' offsets: each part is matched to the prefab's operation at that offset.</param>
    /// <param name="footprintRadius">The place's terrain reach (road_sites_dump terrainRadius), for a file without it.</param>
    public static ApproachGroundFile Parse(string text, IReadOnlyList<LevelOp>? prefabOps = null, float? root = null,
        float? rockRadius = null, bool? keepSmallRocks = null, float? footprintRadius = null)
    {
        string[] lines = text.Split('\n');
        var head = new Dictionary<string, string>();
        foreach (string kv in lines[0].Trim().Split(','))
        {
            int eq = kv.IndexOf('=');
            if (eq > 0) head[kv.Substring(0, eq)] = kv.Substring(eq + 1);
        }
        var file = new ApproachGroundFile
        {
            X0 = int.Parse(head["x0"], CultureInfo.InvariantCulture), Z0 = int.Parse(head["z0"], CultureInfo.InvariantCulture),
            W = int.Parse(head["w"], CultureInfo.InvariantCulture), H = int.Parse(head["h"], CultureInfo.InvariantCulture),
            Yaw = F(head["yaw"]),
            RoadWidth = head.TryGetValue("roadWidth", out string? width) ? F(width) : RoadConstants.DefaultRoadWidth,
            OnStairs = head.TryGetValue("onStairs", out string? on) && on == "True",
        };
        float[] end = head["roadEnd"].Split(';').Select(F).ToArray(), centre = head["centre"].Split(';').Select(F).ToArray();
        file.Request = new SiteApproachRequest
        {
            RoadEnd = new Vector2(end[0], end[1]), RoadHeight = F(head["roadHeight"]), Arriving = F(head["arriving"]),
            Centre = new Vector2(centre[0], centre[1]),
        };
        file.Request.RoadTail.Add(file.Request.RoadEnd);
        file.Root = root ?? (head.TryGetValue("root", out string? r) ? F(r) : float.NaN);
        file.RockRadius = rockRadius ?? (head.TryGetValue("rockRadius", out string? rr) ? F(rr) : 0f);
        file.KeepSmallRocks = keepSmallRocks ?? (head.TryGetValue("keepSmallRocks", out string? ks) && ks == "True");
        file.FootprintRadius = footprintRadius ?? (head.TryGetValue("footprint", out string? fp) ? F(fp) : 0f);
        if (head.TryGetValue("ops", out string? ops))
            file.Ops = Items(ops).Select(o => o.Split(';')).Select(c => new LevelOp(F(c[0]), F(c[1]), F(c[2]), F(c[3]), c[4] == "1", F(c[5]), F(c[6]))).ToList();
        else if (prefabOps != null)
        {
            // "(x;z)L7": a kept part's offset, its levelling radius or smoothing (L: levels) rounded.
            var kept = new List<LevelOp>();
            foreach (string part in Items(head["parts"]))
            {
                string[] xz = part.Substring(1, part.IndexOf(')') - 1).Split(';');
                float px = F(xz[0]), pz = F(xz[1]);
                LevelOp? match = prefabOps.Where(o => Mathf.Abs(o.OffsetX - px) <= 0.051f && Mathf.Abs(o.OffsetZ - pz) <= 0.051f)
                    .Select(o => (LevelOp?)o).FirstOrDefault();
                if (match == null) throw new FormatException($"part {part}: no operation of the place's levelling at that offset");
                kept.Add(match.Value);
            }
            file.Ops = kept;
        }
        if (head.TryGetValue("top", out string? top) && top != "none")
        {
            float[] t = top.Split(';').Take(4).Select(F).ToArray();
            file.Top = new StairContinuation(new Vector2(t[0], t[1]), t[2], t[3], top.EndsWith("Stair") ? StairPieceKind.Stair : StairPieceKind.Landing);
        }
        // name@x;z;halfX;halfZ;yaw;minY;maxY: the box only (a rock's real top is not written).
        foreach (string rock in Items(head["rocks"]))
        {
            int at = rock.IndexOf('@');
            float[] c = rock.Substring(at + 1).Split(';').Select(F).ToArray();
            file.Rocks.Add(new SiteRock(new Vector2(c[0], c[1]), c[2], c[3], c[4], c[5], c[6], rock.Substring(0, at)));
        }
        file.Heights = new float[file.W * file.H];
        for (int k = 0; k < file.H; k++)
        {
            string[] row = lines[k + 1].Trim().Split(',');
            for (int i = 0; i < file.W; i++) file.Heights[k * file.W + i] = F(row[i]);
        }
        return file;
    }

    private static IEnumerable<string> Items(string list) =>
        list.Trim('[', ']').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The approach laid again from a road arriving on a
    /// staircase (no road onto the landing is tried), with these allowances: per layout, stall and per leg.
    /// <see cref="Steps"/>: the steps it spent.</summary>
    public RoadStairSpan? Replay(int total, int stall, int leg, out string why, StairContinuation? top = null, bool useTop = true,
        bool joinedAims = true)
    {
        var request = new SiteApproachRequest
        {
            RoadEnd = Request.RoadEnd, RoadHeight = Request.RoadHeight, Arriving = Request.Arriving, Centre = Request.Centre,
            RoadTail = new List<Vector2>(Request.RoadTail), Reach = Request.Reach, TotalExpansions = total, Stall = stall, Leg = leg,
        };
        Vector2 c = Request.Centre;
        float reach = FootprintRadius;
        RoadStairSpan? span = RoadStairRuns.PlanPlacedSiteApproach(request, Yaw, Ops, Ground, (x, z) => Heightmap.Biome.Mountain, 0, out why, RockRadius,
            p => reach > 0f && Vector2.Distance(p, c) <= reach, Rocks, KeepSmallRocks, Root, useTop ? top ?? Top : null, joinedAims);
        Steps = RoadStairRuns.LastApproachSteps;
        return span;
    }

    /// <summary>Search steps the last <see cref="Replay"/> spent, every layout together.</summary>
    public int Steps { get; private set; }

    /// <summary>The layout the approach tries for one landing, for a look at where it stops: as
    /// <see cref="RoadStairRuns.PlanPlacedSiteApproach"/> lays it with no ridge or valley between
    /// and the road's end in front of the place (its <c>turn</c> null, <c>around</c> null), not reached or reached.</summary>
    public (StairPlan plan, Vector2 goal, (Vector2 at, bool crest)? turn) Layout(Vector2 target, int total, int stall, int leg)
    {
        LevelOp flat = RoadStairRuns.FlatOf(Ops) ?? throw new InvalidOperationException("no flat");
        PlacedSiteShape shape = PlacedSiteShape.Of(Request.Centre, Yaw, flat, RockRadius, Rocks, Ground, KeepSmallRocks, Root);
        bool Allowed(Vector2 p) => shape.Allowed(p, RoadStairRuns.FillerReach, RoadStairRuns.Width * 0.5f + RoadStairRuns.FillerReach);
        Vector2 goal = RoadStairRuns.FlatsFrom(Request.RoadEnd, target, Ground);
        bool flats = goal != target;
        StairPlan plan = StairLayout.Build(Request.RoadEnd, goal, Ground, RoadStairRuns.Width, StairKit.Stone, leg, null,
            flats ? null : StairLayout.Wrap(Yaw + 180f), null, total, stall, 0, sunkFoot: Top == null, maxBuried: StairCover.ApproachSearch,
            arriveWithin: RoadStairRuns.LedgeReach, allowed: Allowed, after: Top);
        return (plan, goal, RoadStairRuns.Turn(Request.RoadEnd, target, Ground));
    }

    /// <summary>Where the approach aims, best first (<see cref="PlacedSiteShape.Aims"/>).</summary>
    public List<(string name, Vector2 at)> Aims()
    {
        LevelOp flat = RoadStairRuns.FlatOf(Ops) ?? throw new InvalidOperationException("no flat");
        return PlacedSiteShape.Of(Request.Centre, Yaw, flat, RockRadius, Rocks, Ground, KeepSmallRocks, Root).Aims(Ground, Request.RoadEnd, 1f, RoadStairRuns.AimTries);
    }

    /// <summary>The landings the approach aims at (<see cref="PlacedSiteShape.Landings"/>), as the game picks them.</summary>
    public List<(Vector2 at, float height)> Landings()
    {
        LevelOp flat = RoadStairRuns.FlatOf(Ops) ?? throw new InvalidOperationException("no flat");
        return PlacedSiteShape.Of(Request.Centre, Yaw, flat, RockRadius, Rocks, Ground, KeepSmallRocks, Root).Landings(Ground, 1f);
    }
}
