using System;
using System.Collections.Generic;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

/// <summary>Two staircases met end to end at a join that does not snap: no gap between their walking surfaces, and the
/// railing closed round the join's outline (every bay of the railing catalogue, each kit).</summary>
public class JoinGapTests
{
    private readonly ITestOutputHelper m_out;
    public JoinGapTests(ITestOutputHelper output) { m_out = output; }

    private const float Ground = 35f;
    private static readonly Vector2 Join = new(100f, 200f);

    internal static (List<StairRun> runs, List<List<StructurePiece>> dressed) Laid(RailingCatalogue.Bay bay)
    {
        List<StairRun> runs = RailingCatalogue.Place(bay, Join, RailingCatalogue.SurfaceOver(bay, Ground));
        var dressed = RailingCatalogue.Dress(runs, _ => (x, z) => Ground, (x, z) => Ground, new StairSupport.Shortfall(), null);
        return (runs, dressed);
    }

    private static List<StairSupport.Walk> Walks(RailingCatalogue.Bay bay, List<StairRun> runs) =>
        runs.SelectMany(r => r.Pieces).Select(p => StairSupport.WalkOf(p, bay.Kit)).OfType<StairSupport.Walk>().ToList();

    private static bool Covers(StairSupport.Walk w, Vector2 p, float margin)
    {
        Vector2 d = p - w.Back;
        float along = Vector2.Dot(d, w.Dir), across = Vector2.Dot(d, w.Right);
        return along >= -margin && along <= w.Depth + margin && Mathf.Abs(across) <= w.Across / 2f + margin;
    }

    private static Vector2 Nearest(StairSupport.Walk w, Vector2 p)
    {
        Vector2 d = p - w.Back;
        float along = Mathf.Clamp(Vector2.Dot(d, w.Dir), 0f, w.Depth), across = Mathf.Clamp(Vector2.Dot(d, w.Right), -w.Across / 2f, w.Across / 2f);
        return w.At(along, across);
    }

    /// <summary>The widest gap between two walking pieces round the join: an uncovered point standing between two pieces (their
    /// nearest points on opposite sides of it, more than 120 degrees apart), the longest uncovered stretch between those
    /// nearest points.</summary>
    internal static float WidestGap(List<StairSupport.Walk> walks, Vector2 join, float reach = 2.5f) => WidestGap(walks, join, out _, reach);

    internal static float WidestGap(List<StairSupport.Walk> walks, Vector2 join, out Vector2 where, float reach = 2.5f)
    {
        float widest = 0f;
        where = join;
        for (float x = -reach; x <= reach; x += 0.025f)
        for (float z = -reach; z <= reach; z += 0.025f)
        {
            Vector2 p = join + new Vector2(x, z);
            if (walks.Any(w => Covers(w, p, 0.001f))) continue;
            var near = walks.Select(w => Nearest(w, p)).Where(n => Vector2.Distance(n, p) < 0.5f).ToList();
            for (int a = 0; a < near.Count; a++)
            for (int b = a + 1; b < near.Count; b++)
            {
                Vector2 da = near[a] - p, db = near[b] - p;
                if (da.sqrMagnitude < 1e-8f || db.sqrMagnitude < 1e-8f) continue;
                if (Vector2.Dot(da.normalized, db.normalized) > -0.5f) continue;   // under 120 degrees apart
                float d = Uncovered(walks, near[a], near[b]);
                if (d > widest) { widest = d; where = p; }
            }
        }
        return widest;
    }

    /// <summary>The longest stretch of the segment from <paramref name="a"/> to <paramref name="b"/> no walking piece covers.</summary>
    private static float Uncovered(List<StairSupport.Walk> walks, Vector2 a, Vector2 b)
    {
        float len = Vector2.Distance(a, b), run = 0f, longest = 0f, step = 0.01f;
        for (float t = step / 2f; t < len; t += step)
        {
            Vector2 p = Vector2.Lerp(a, b, t / len);
            if (walks.Any(w => Covers(w, p, 0.001f))) run = 0f;
            else longest = Mathf.Max(longest, run += step);
        }
        return longest;
    }

    private static float HalfLength(string prefab) => prefab switch
    {
        StairRailing.ShortRailPrefab => 0.5f,
        _ => 1f,
    };

    private static float ToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>The points round the join's outline (the walking pieces' edges with nothing walked past them) with no rail
    /// or post along them: the stretches the railing leaves open, as (where, how long).</summary>
    internal static List<(Vector2 at, float length)> OpenOutline(List<StairSupport.Walk> walks, IEnumerable<StructurePiece> railing,
        Vector2 join, float reach = 2.5f)
    {
        var rails = new List<(Vector2 a, Vector2 b)>();
        var posts = new List<Vector2>();
        foreach (StructurePiece r in railing)
        {
            var at = new Vector2(r.Position.x, r.Position.z);
            if (r.Kind == StructurePieceKind.Post) { posts.Add(at); continue; }
            if (r.Kind != StructurePieceKind.Beam) continue;
            Vector2 axis = StairLayout.Forward(r.YawDegrees + 90f) * HalfLength(r.Prefab);
            rails.Add((at - axis, at + axis));
        }
        var open = new List<(Vector2, float)>();
        foreach (StairSupport.Walk w in walks)
        for (int e = 0; e < 4; e++)
        {
            // Right, front, left, back: its two ends and the way out.
            Vector2 a, b, outward;
            float h = w.Across / 2f;
            switch (e)
            {
                case 0: a = w.At(0f, h); b = w.At(w.Depth, h); outward = w.Right; break;
                case 1: a = w.At(w.Depth, h); b = w.At(w.Depth, -h); outward = w.Dir; break;
                case 2: a = w.At(w.Depth, -h); b = w.At(0f, -h); outward = -w.Right; break;
                default: a = w.At(0f, -h); b = w.At(0f, h); outward = -w.Dir; break;
            }
            float len = Vector2.Distance(a, b);
            Vector2? start = null;
            Vector2 last = a;
            int n = Mathf.CeilToInt(len / 0.05f);
            for (int s = 1; s < n; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, s / (float)n);
                bool outline = Vector2.Distance(p, join) <= reach && !walks.Any(o => Covers(o, p + outward * 0.15f, 0f))
                    && !walks.Any(o => !o.Equals(w) && Covers(o, p, -0.03f));
                bool held = rails.Any(r => ToSegment(p, r.a, r.b) < 0.12f) || posts.Any(q => Vector2.Distance(q, p) < 0.2f);
                if (outline && !held) { start ??= p; last = p; }
                else if (start is Vector2 s0) { open.Add((s0, Vector2.Distance(s0, last) + 0.05f)); start = null; }
            }
            if (start is Vector2 s1) open.Add((s1, Vector2.Distance(s1, last) + 0.05f));
        }
        return open;
    }

    public static IEnumerable<object[]> EveryBay() =>
        StairKit.All.SelectMany(k => RailingCatalogue.Bays(k).Select(b => new object[] { k.Name, b.Name }));

    private static RailingCatalogue.Bay BayOf(string kit, string name) =>
        RailingCatalogue.Bays(StairKit.Named(kit)!).Single(b => b.Name == name);

    /// <summary>No gap wider than a few centimetres between the two staircases' walking surfaces at the join.</summary>
    [Theory]
    [MemberData(nameof(EveryBay))]
    public void TheWalkingSurfacesMeetWithNoGap(string kit, string name)
    {
        RailingCatalogue.Bay bay = BayOf(kit, name);
        var (runs, _) = Laid(bay);
        float gap = WidestGap(Walks(bay, runs), Join, out Vector2 at);
        m_out.WriteLine($"{kit} {name}: widest gap {gap:F3} m at ({at.x - Join.x:F2}, {at.y - Join.y:F2})");
        Assert.True(gap <= 0.05f, $"{kit} {name}: a {gap:F2} m gap between the walking surfaces at the join");
    }

    /// <summary>The railing is closed round the join: every edge with nothing walked past it is railed, the outer rails meet.</summary>
    [Theory]
    [MemberData(nameof(EveryBay))]
    public void TheRailingIsClosedRoundTheJoin(string kit, string name)
    {
        RailingCatalogue.Bay bay = BayOf(kit, name);
        var (runs, dressed) = Laid(bay);
        // The outline is the walk's as laid: the layouts' pieces and the blocks the railing fills a sideways step with.
        var walks = Walks(bay, runs);
        walks.AddRange(dressed.SelectMany(d => d).Where(p => p.Kind == StructurePieceKind.StairFiller && !runs.Any(r => r.Pieces.Exists(l => Vector3.Distance(l.Position, p.Position) < 0.001f)))
            .Select(p => StairSupport.WalkOf(p, bay.Kit)).OfType<StairSupport.Walk>());
        var open = OpenOutline(walks, dressed.SelectMany(d => d), Join);
        foreach (var (at, length) in open) m_out.WriteLine($"{kit} {name}: open {length:F2} m at ({at.x - Join.x:F2}, {at.y - Join.y:F2})");
        Assert.True(open.All(o => o.length < 0.1f), $"{kit} {name}: railing open at the join: "
            + string.Join(", ", open.Where(o => o.length >= 0.1f).Select(o => $"{o.length:F2} m at ({o.at.x - Join.x:F2}, {o.at.y - Join.y:F2})")));
    }
}
