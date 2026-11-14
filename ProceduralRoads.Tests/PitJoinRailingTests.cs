using System;
using System.Collections.Generic;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Two staircases met at a pit's bottom and joined where their flights pass (RoadStairRuns.JoinHigh): the first's last stair
/// walked down to the join, the second's floor turned square off its foot with the wedge filled, in the second's layout.
/// Each staircase is railed by its own kit with the other beside it (StairStructures.DressAmong, StairRailing.Lay); the
/// wedge's outside is railed round its clean corner as a square turn within one staircase is.
/// </summary>
public class PitJoinRailingTests
{
    private readonly ITestOutputHelper m_out;
    public PitJoinRailingTests(ITestOutputHelper output) { m_out = output; }

    /// <summary>The pit of the doubling-back test (BackTrackTests.APitThatDoublesBackIsJoinedHighWhereItsFlightsPass),
    /// joined high as the road joins it, laid as two staircases met at the join.</summary>
    private static (List<StairRun> runs, Vector2 at) Joined()
    {
        StairKit kit = StairKit.BlackMarble;
        const float m = 37.494f;
        StairPiece Over(StairPiece p) => new() { Kind = p.Kind, Prefab = p.Prefab, Position = new Vector3(p.Position.x, 2f * m - p.Position.y - 1f, p.Position.z),
            YawDegrees = StairLayout.Wrap(p.YawDegrees + 180f) };
        var (first, second) = ProceduralRoads.Tests.BackTrackTests.KnollPieces();
        StairPlan below = ProceduralRoads.Tests.BackTrackTests.Plan(first.Select(Over).Reverse(), true);
        StairPlan up = ProceduralRoads.Tests.BackTrackTests.Plan(second.Select(Over).Reverse(), false);
        Assert.True(RoadStairRuns.JoinHigh(below, up, kit, (x, z) => 33f, 2f, (_, _) => true, out Vector2 at));
        var a = new StairRun { From = new Vector2(below.Pieces[0].Position.x, below.Pieces[0].Position.z), To = at, Width = 2f, Kit = kit };
        a.Pieces.AddRange(below.Pieces.Select(StairRun.LaidPiece));
        var b = new StairRun { From = at, To = new Vector2(up.End.x, up.End.z), Width = 2f, Kit = kit };
        b.Pieces.AddRange(up.Pieces.Select(StairRun.LaidPiece));
        return (new List<StairRun> { a, b }, at);
    }

    /// <summary>The square turn's wedge between the two (a 2 x 2 filler in the second's layout, the first's last stair on one
    /// side, the second's first floor on the other): its outside railed round the clean corner, a level rail pair from each
    /// piece's outer corner to a post there, and no rail across the walk; the outline round the join closed.</summary>
    [Fact]
    public void TheOutsideOfASquareTurnBetweenTwoStaircasesMetInAPitIsRailed()
    {
        StairKit kit = StairKit.BlackMarble;
        var (runs, at) = Joined();
        // The wedge: a 2 x 2 filler of the second's, square to the first's last stair beside it.
        StructurePiece wedge = Assert.Single(runs[1].Pieces, p => p.Kind == StructurePieceKind.StairFiller && p.Prefab == kit.Floor);
        var dressed = runs.ConvertAll(r => StairStructures.DressAmong(r, runs, _ => (x, z) => 34f, (x, z) => 34f, new StairSupport.Shortfall(), false, null));
        Assert.All(dressed, d => Assert.NotEmpty(d));
        List<StructurePiece> railing = dressed.SelectMany(d => d).Where(p => p.Kind is StructurePieceKind.Post or StructurePieceKind.Beam).ToList();
        foreach (StructurePiece p in railing.Where(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) < 4f))
            m_out.WriteLine(FormattableString.Invariant($"{p.Kind} {p.Prefab} ({p.Position.x:F3}, {p.Position.y:F3}, {p.Position.z:F3}) yaw {p.YawDegrees:F1}"));

        var walks = runs.SelectMany(r => r.Pieces).Select(p => StairSupport.WalkOf(p, kit)).OfType<StairSupport.Walk>().ToList();
        var open = JoinGapTests.OpenOutline(walks, railing, at, 4f);
        Assert.True(open.All(o => o.length < 0.1f), "railing open round the pit's join: "
            + string.Join(", ", open.Where(o => o.length >= 0.1f).Select(o => FormattableString.Invariant($"{o.length:F2} m at ({o.at.x:F2}, {o.at.y:F2})"))));

        // The clean corner: the first's last stair's outer side carried on past its foot meets the floor's carried back.
        var corner = new Vector2(3.172f, 5.828f);
        Assert.True(Vector2.Distance(new Vector2(wedge.Position.x, wedge.Position.z), corner) > 1f);
        Assert.Contains(railing, p => p.Kind == StructurePieceKind.Post && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), corner) < 0.01f);
        Assert.Equal(4, railing.Count(p => p.Prefab == StairRailing.LevelRailPrefab
            && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), corner) is > 0.99f and < 1.01f));
        // No rail across the walk: none stands well inside a walked piece at its walking height.
        foreach (StructurePiece rail in railing.Where(p => p.Kind == StructurePieceKind.Beam))
        {
            var r = new Vector2(rail.Position.x, rail.Position.z);
            Assert.DoesNotContain(walks, w => StairLayout.OnFootprint(w.Back, w.Dir, w.Right, w.Depth, w.Across, r, -0.3f)
                && Mathf.Abs(w.Surface(Mathf.Clamp(Vector2.Dot(r - w.Back, w.Dir), 0f, w.Depth), kit) - rail.Position.y) < 0.6f);
        }
    }

    /// <summary>Every level rail round the pit's join ends on a post of one of the two staircases' railings (the wedge's rail
    /// run on from the other staircase's corner included).</summary>
    [Fact]
    public void EveryRailRoundTheJoinEndsOnAPost()
    {
        var (runs, at) = Joined();
        var dressed = runs.ConvertAll(r => StairStructures.DressAmong(r, runs, _ => (x, z) => 34f, (x, z) => 34f, new StairSupport.Shortfall(), false, null));
        var all = dressed.SelectMany(d => d).ToList();
        var posts = all.Where(p => p.Kind == StructurePieceKind.Post).Select(p => new Vector3(p.Position.x, p.Position.y, p.Position.z)).ToList();
        var lines = new List<string>();
        foreach (StructurePiece r in all.Where(p => p.Kind == StructurePieceKind.Beam && p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab))
        {
            var mid = new Vector2(r.Position.x, r.Position.z);
            if (Vector2.Distance(mid, at) > 4f) continue;
            Vector2 axis = StairLayout.Forward(r.YawDegrees + 90f) * (r.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f);
            foreach (Vector2 end in new[] { mid - axis, mid + axis })
                if (!posts.Any(q => Vector2.Distance(new Vector2(q.x, q.z), end) < 0.35f))
                    lines.Add(FormattableString.Invariant($"{r.Prefab} at ({mid.x:F2}, {r.Position.y:F2}, {mid.y:F2}) yaw {r.YawDegrees:F0}: end ({end.x:F2}, {end.y:F2}) on no post"));
        }
        Assert.True(lines.Count == 0, string.Join("; ", lines));
    }

    /// <summary>The verdict's dress (StairStructures.Refusal, sharedPosts off) dresses only its own staircase: the one it meets
    /// is not dressed for the posts they share (its ground never asked), and its shortfall is the full dress's, read the same
    /// whether or not the one met is dressed; with them on, the one met that lays the shared posts is dressed (its ground asked).</summary>
    [Fact]
    public void TheVerdictsDressLeavesTheStaircaseItMeetsUndressed()
    {
        var (runs, _) = Joined();
        int metDressed = 0;
        foreach (StairRun run in runs)
        {
            var asked = new List<StairRun>();
            Func<StairRun, Func<float, float, float>> natural = r => { asked.Add(r); return (x, z) => 34f; };
            var own = new StairSupport.Shortfall();
            List<StructurePiece> alone = StairStructures.DressAmong(run, runs, natural, (x, z) => 34f, own, false, null, sharedPosts: false);
            Assert.Equal(new[] { run }, asked);
            asked.Clear();
            var full = new StairSupport.Shortfall();
            List<StructurePiece> dressed = StairStructures.DressAmong(run, runs, natural, (x, z) => 34f, full, false, null);
            if (asked.Exists(r => !ReferenceEquals(r, run))) metDressed++;
            Assert.Equal(full.Falling, own.Falling);
            Assert.Equal(full.Unheld, own.Unheld);
            Assert.Equal(full.Fell, own.Fell);
            Assert.Equal(full.ShortWalls, own.ShortWalls);
            Assert.Equal(full.Weak, own.Weak);
            Assert.NotEmpty(alone);
            Assert.True(alone.Count >= dressed.Count);
        }
        // Of two staircases met end to end one lays the posts they share (StairRailing.LaysShared): the other's full dress
        // dresses it.
        Assert.Equal(1, metDressed);
    }

    /// <summary>No post laid twice round the join: the two staircases' railings share a corner there, and one post stands
    /// at it, not one from each (StairRailing.LeaveShared).</summary>
    [Fact]
    public void NoPostIsLaidTwiceRoundTheJoin()
    {
        var (runs, _) = Joined();
        var dressed = runs.ConvertAll(r => StairStructures.DressAmong(r, runs, _ => (x, z) => 34f, (x, z) => 34f, new StairSupport.Shortfall(), false, null));
        var posts = dressed.SelectMany(d => d).Where(p => p.Kind == StructurePieceKind.Post).Select(p => p.Position).ToList();
        var twice = new List<string>();
        for (int i = 0; i < posts.Count; i++)
            for (int j = i + 1; j < posts.Count; j++)
                if (Vector3.Distance(posts[i], posts[j]) < 0.05f)
                    twice.Add(FormattableString.Invariant($"two posts at ({posts[i].x:F2}, {posts[i].y:F2}, {posts[i].z:F2})"));
        Assert.True(twice.Count == 0, string.Join("; ", twice));
    }
    /// <summary>Where the staircase that lays the posts two met end to end share lays nothing at its build
    /// (StairStructures.Dresser's verdicts), the other keeps its posts there (StairStructures.DressAmong): one not in the world
    /// whole (its held ground past the world's edge), and one on ground not known under its walking pieces.</summary>
    [Fact]
    public void ASharedPostStandsWhereTheStaircaseThatLaysItLaysNothing()
    {
        var (runs, _) = Joined();
        StairRun owner = runs.Single(r => runs.Exists(o => !ReferenceEquals(o, r) && StairRailing.LaysShared(r, o)));
        StairRun other = runs.Single(r => !ReferenceEquals(r, owner));
        int Posts(Func<int, int, float> finished, bool shared) => StairStructures.DressAmong(other, runs, _ => (x, z) => 34f, finished,
            new StairSupport.Shortfall(), false, null, shared).Count(p => p.Kind == StructurePieceKind.Post);
        Func<int, int, float> known = (x, z) => 34f;
        int alone = Posts(known, false);
        Assert.True(Posts(known, true) < alone, "the shared posts are not left out where the staircase that lays them lays them");

        // Its own walking pieces' ground known, the other's not everywhere: the other lays nothing (Dresser, StructureGround.NotLaid).
        var mine = new HashSet<(int, int)>(StairStructures.WalkingPieces(other).SelectMany(p => StructureGround.VerticesUnder(p.Position)));
        Func<int, int, float> partly = (x, z) => mine.Contains((x, z)) ? 34f : float.NaN;
        Assert.Null(StairStructures.UnknownGround(other, partly));
        Assert.NotNull(StairStructures.UnknownGround(owner, partly));
        Assert.Equal(Posts(partly, false), Posts(partly, true));

        // Its held ground past the world's edge: not in the world whole (StructureSites.SpatialFault), so it lays nothing.
        owner.Ground.Add(new GroundPin(30000, 0, 34f));
        Assert.NotNull(StructureSites.SpatialFault(owner.From, owner.To, owner.Pieces, owner.Ground));
        Assert.Equal(alone, Posts(known, true));
    }
}
