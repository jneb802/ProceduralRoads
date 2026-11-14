using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The railing a staircase's kit lays over its walking pieces at first build (one owner), from a hand-built railing
/// prototype of 31 Oct 2026: the wooden bridges' rail pieces (<see cref="BridgeLayout.RailPostPrefab"/>,
/// <see cref="BridgeLayout.StairRailPrefab"/>), on both sides always, whatever holds the stair up.
/// <list type="bullet">
/// <item>a flight: on each side edge a post at every joint, a 1 m post standing on the step's low corner, and two sloped
/// rails over each stair, its handrail <see cref="BridgeLayout.RailHeight"/> over the walking line and a lower rail a metre
/// under it, framing the stair as a landing's two rails do (in game: added by hand at -1931.2,261.8,6709.0);</item>
/// <item>a floor: on each free edge two level rails, at the walking surface and a metre over it, posts at its corners;</item>
/// <item>which edges are railed is <see cref="Railed"/>'s to say, by one principle: every edge of every walking piece, a
/// flight's two sides and a floor's four, but where the walk goes on across it. It goes on across an edge
/// <list type="number">
/// <item>onto another walking piece (<see cref="Open"/>: a switchback's two floors, where a flight meets a floor, a turn's
/// fillers, another staircase met end to end); never across a flight's side, beside another flight (a switchback's legs,
/// each railed along the line they share; in game: railed by hand at -1904,6608) or a switchback's join filler alike;</item>
/// <item>where what the staircase ends on takes the walk over (<see cref="TakenOver"/>): the road, on every edge at walking
/// height (within <see cref="LedgeDrop"/>) of a floor the road's end meets, within its ring's reach, and a location's landing
/// alike, on every edge at walking height of a floor its end meets (<see cref="Ways.Road"/>, one rule for both ends; not the
/// floors laid on past it, railed as any floor; none where another staircase meets the landing end to end, which no road
/// comes onto); and the ground straight on off a floor the walk ends on (<see cref="WalkOut"/>), where it stands at walking
/// height;</item>
/// <item>and an end step in the ground has no railing at all (<see cref="InTheGround"/>).</item>
/// </list>
/// A flight is railed right down to the road: its rails run along its sides, and the road comes onto its end.</item>
/// </list>
/// A rail or post wholly in the ground is left out, and a post wholly in the staircase's supports. A turn's wedge is
/// closed by the flights' rails run on over it (<see cref="CloseWedges"/>), from the corners of the pieces either side of
/// it whether their own sides are railed or not (a floor the road comes onto); where a switchback's two legs' rails cross on
/// the line they share, one <see cref="CrossedRailPrefab"/> stands for both (<see cref="Cross"/>).
/// <para>A black marble viaduct's end stairs are railed by the same rules in a stepped black marble parapet
/// (<see cref="LayParapet"/>): only the pieces differ.</para>
/// </summary>
public static class StairRailing
{
    /// <summary>A level rail along a floor's edge, 2 m.</summary>
    public const string LevelRailPrefab = BridgeLayout.BeamPrefab;

    /// <summary>Two 26 degree rails crossed (they read as two 26 degree pieces crossed).</summary>
    public const string CrossedRailPrefab = "wood_wall_roof_top";

    /// <summary>The one owner of railings and parapets (docs/OWNERS.md).</summary>
    /// <param name="beside">The walking pieces of the staircases meeting this one end to end
    /// (<see cref="StairStructures.MetAtEnds"/>): walked on across, never railed here.</param>
    /// <param name="steps">The blocks filling the staircase's sideways steps (<see cref="StepBlocks"/>), laid with its walking
    /// pieces: read with the staircase for burial, not walked by its railing (its steps are railed as the floors leave them).</param>
    public static List<StructurePiece> Lay(StairKit kit, IReadOnlyList<StructurePiece> walking, IReadOnlyList<StairEnd> ends,
        Func<int, int, float> finished, IReadOnlyList<StructurePiece>? supports = null, IReadOnlyList<StructurePiece>? beside = null,
        IReadOnlyCollection<StructurePiece>? steps = null)
    {
        var (walks, railed, own, flanks) = WalksOf(kit, walking.Where(p => steps == null || !steps.Contains(p)), beside);
        // The staircase its railing stands with, for the one burial omission (BuriedPieces.Omit): its walking pieces and supports.
        var structure = new List<StructurePiece>(walking);
        if (supports != null) structure.AddRange(supports);
        return Lay(kit, walks, railed, own, ends, finished, supports, null, structure, flanks: flanks);
    }

    /// <summary>
    /// The blocks a staircase's sideways steps are filled with (<see cref="Step"/>, <see cref="FillStep"/>), from its walking
    /// pieces as its layout laid them and those met end to end <paramref name="beside"/> it: walking pieces of its own, laid
    /// with the rest before its kit's burial and support pass (<see cref="StairKit.Dress"/>), so each is left out where buried
    /// and held as any filler is. The step's rails are laid with the railing (<see cref="Lay"/>, given these as its steps).
    /// </summary>
    public static List<StructurePiece> StepBlocks(StairKit kit, IReadOnlyList<StructurePiece> walking, IReadOnlyList<StairEnd> ends,
        Func<int, int, float> finished, IReadOnlyList<StructurePiece>? beside = null)
    {
        var (walks, railed, own, _) = WalksOf(kit, walking, beside);
        return Lay(kit, walks, railed, own, ends, finished, null, null, null, blocks: true);
    }

    /// <summary>The walks of a staircase's walking pieces (railed but its fillers) and, after them (from own on), those of
    /// the staircases met end to end with it, walked but railed by their own.</summary>
    private static (List<StairSupport.Walk> walks, List<bool> railed, int own, HashSet<int> flanks) WalksOf(StairKit kit,
        IEnumerable<StructurePiece> walking, IReadOnlyList<StructurePiece>? beside)
    {
        // Every walking piece closes the edges it touches, a turn's wedge filler too; fillers get no rail of their own.
        var walks = new List<StairSupport.Walk>();
        var railed = new List<bool>();
        foreach (StructurePiece piece in walking)
            if (StairSupport.WalkOf(piece, kit) is StairSupport.Walk w) { walks.Add(w); railed.Add(piece.Kind != StructurePieceKind.StairFiller); }
        // Another staircase's pieces after its own (from own on): walked, railed by their own staircase.
        int own = walks.Count;
        // Of those, the stairs and floors (not a turn's fillers): a wedge between the two staircases is closed from their corners.
        var flanks = new HashSet<int>();
        foreach (StructurePiece piece in beside ?? Array.Empty<StructurePiece>())
            if (StairSupport.WalkOf(piece, StairKit.Named(piece.Prefab.Split('_')[0]) ?? kit) is StairSupport.Walk w)
            {
                if (piece.Kind != StructurePieceKind.StairFiller) flanks.Add(walks.Count);
                walks.Add(w);
                railed.Add(false);
            }
        return (walks, railed, own, flanks);
    }

    /// <summary>
    /// The railing down a structure's end stairs in black marble (after a hand-built example on
    /// a river viaduct's west end at (4356.5, -4992)): a Dvergr viaduct's (DvergrViaduct, its steps
    /// laid by <see cref="BridgeLayout.LayEndStairs"/>). A stepped parapet (<see cref="Parapet"/>) in place of the
    /// staircases' wooden posts and rails, railed by the staircases' own rules, which this one owner keeps for both
    /// (<see cref="Railed"/>, <see cref="InTheGround"/>): apart from the material it behaves like the other railings
    /// where it meets the road. The flight is railed on both sides right down to the road, which comes onto its
    /// foot; an end step in the ground has none, and on up from it while the next is in the ground too; a piece wholly in
    /// the ground is left out. None where the road meets the end level (no steps).
    /// The one owner of railings and parapets (docs/OWNERS.md).
    /// </summary>
    /// <param name="end">The end as the road meets it (StructureEnds.Of), its <see cref="StructureEnds.End.Steps"/> the
    /// steps laid down from its edge.</param>
    /// <param name="across">The steps' width across the flight: two 2 m lanes on a viaduct.</param>
    /// <param name="finished">The finished ground at a whole-metre vertex.</param>
    public static List<StructurePiece> LayParapet(StructureEnds.End end, float across, Func<int, int, float> finished)
    {
        if (!end.IsSet || end.Steps <= 0) return new List<StructurePiece>();
        // One walk a step, the lanes as one flight: step k from the top, its foot k + 1 rises under the edge.
        float heading = StairLayout.Wrap(BridgeLayout.YawDegrees(end.Up));
        var walks = new List<StairSupport.Walk>();
        var railed = new List<bool>();
        for (int k = 0; k < end.Steps; k++)
        {
            walks.Add(new StairSupport.Walk(StructureEnds.StepJoint(end.Edge, end.Up, k + 1), heading, StairLayout.PieceRun, across,
                StructureEnds.StepBase(end.Height, k), true, 0f));
            railed.Add(true);
        }
        // The road meets the flight at its foot.
        var ends = new[] { new StairEnd(end.Meet, end.Meet, false) };
        return Lay(StairKit.BlackMarble, walks, railed, walks.Count, ends, finished, null, new ParapetCourses(end.Height));
    }

    /// <summary>A stepped parapet's courses: its supports are laid in the 2 m courses of the walking surface at
    /// <see cref="Top"/> (a viaduct's deck).</summary>
    private sealed class ParapetCourses
    {
        public readonly float Top;
        public ParapetCourses(float top) { Top = top; }
    }

    /// <summary>The railing of <paramref name="walks"/>: the staircases' wooden posts and rails, or with
    /// <paramref name="parapet"/> a structure's end stairs' black marble parapet (<see cref="LayParapet"/>), which has
    /// stairs only. Those from <paramref name="own"/> on are another staircase's met end to end, walked on across, never
    /// railed here. With <paramref name="blocks"/>, only the blocks its sideways steps are filled with (<see cref="StepBlocks"/>);
    /// else its railing, its steps railed but not filled.</summary>
    private static List<StructurePiece> Lay(StairKit kit, List<StairSupport.Walk> walks, List<bool> railed, int own, IReadOnlyList<StairEnd> ends,
        Func<int, int, float> finished, IReadOnlyList<StructurePiece>? supports, ParapetCourses? parapet, IReadOnlyList<StructurePiece>? structure = null,
        bool blocks = false, IReadOnlyCollection<int>? flanks = null)
    {
        var laid = new List<StructurePiece>();
        var posts = new Dictionary<(int, int, int), Spot>();
        var sloped = new List<(StructurePiece rail, float rank)>();
        Ways ways = WaysOn(walks, railed, own, ends, kit, finished);
        for (int i = 0; i < walks.Count; i++)
        {
            if (!railed[i]) continue;
            StairSupport.Walk w = walks[i];
            float half = w.Across / 2f;
            if (w.Stair)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    float middle = w.Underside + StairLayout.StepMiddle;
                    if (!Railed(walks, ways, i, w.At(0f, side * half), w.At(w.Depth, side * half), w.Right * side, middle)) continue;
                    if (parapet != null) { Parapet(laid, w, side, parapet.Top, finished); continue; }
                    foreach (bool front in new[] { false, true })
                    {
                        var (at, bottom) = Corner(w, side, front, kit);
                        Post(posts, i, at, bottom, w.Heading, middle);
                    }
                    // Its origin over the middle of the step at its low end's height, its local x up the flight (the bridges' end
                    // stairs, BridgeLayout.EmitRailedEnd): the handrail a metre over the walking line, the lower rail on it.
                    for (int tier = 0; tier < 2; tier++)
                    {
                        float low = w.Underside + tier * BridgeLayout.RailHeight;
                        if (Rail(laid, BridgeLayout.StairRailPrefab, w.At(w.Depth / 2f, side * half), low, StairLayout.Wrap(w.Heading - 90f),
                            finished) is StructurePiece rail)
                            sloped.Add((rail, w.Underside));
                    }
                }
                continue;
            }
            if (parapet != null) continue;   // a parapet's flights have no floor (LayParapet)
            float surface = w.Surface(0f, kit);
            for (int e = 0; e < 4; e++)
            {
                // Its four edges: right, front, left, back; each by its middle, the way out across it, and its run.
                var (edge, along, run, outward, edgeHeading) = EdgeFrame(w, e);
                if (!Railed(walks, ways, i, edge - along * (run / 2f), edge + along * (run / 2f), outward, surface))
                {
                    // An edge running on into a floor it overlaps (the inside of a jog) is railed where it stands clear of it.
                    if (Overlapped(walks, railed, i, edge - along * (run / 2f), edge + along * (run / 2f), outward, surface) is var (from, to)
                        && Railed(walks, ways, i, from, to, outward, surface))
                    {
                        Post(posts, i, from, surface, w.Heading, surface);
                        Post(posts, i, to, surface, w.Heading, surface);
                        Level(laid, ShortRailPrefab, from, to, surface, finished);
                    }
                    // The walk goes on across the edge's middle, but past a stretch at either end nothing is walked onto: two
                    // floors met out of line (set aside or turned), whose outer sides stand apart. That stretch is railed post to
                    // post, so the outer rails close round the join.
                    else
                        foreach (var (end, bare) in Exposed(walks, i, edge - along * (run / 2f), edge + along * (run / 2f), outward, surface))
                        {
                            if (!Railed(walks, ways, i, end, bare, outward, surface)) continue;
                            Post(posts, i, end, surface, w.Heading, surface);
                            // A sideways step between two floors in line (a clipped join set aside more than a joint): railed
                            // across it from corner to corner, its wedge filled, no post at its inner corner.
                            if (Step(walks, kit, i, end, bare, surface) is var (partner, corner, owns))
                            {
                                if (!owns) continue;
                                if (partner < own) Post(posts, partner, corner, surface, walks[partner].Heading, surface);
                                FillStep(laid, kit, end, corner, bare, surface, finished, blocks);
                                continue;
                            }
                            // Where the other floor's edge is railed to the same crossing (RunsInto), its post stands for both.
                            Post(posts, i, NearPost(posts, bare, surface) ?? bare, surface, w.Heading, surface);
                            float span = Vector2.Distance(end, bare);
                            if (span > Joint) Level(laid, ClosingRail(span), end, bare, surface, finished);
                        }
                    continue;
                }
                // An edge whose far end stands on another floor (a jog too shallow for the walk to carry on across it) is railed
                // from its clear corner to where it meets that floor's edge, never on into its walk.
                if (RunsInto(walks, railed, own, i, edge - along * (run / 2f), edge + along * (run / 2f), surface) is var (clear, meets))
                {
                    if (Vector2.Distance(clear, meets) < StairLayout.EndReach) continue;
                    Post(posts, i, clear, surface, w.Heading, surface);
                    Post(posts, i, meets, surface, w.Heading, surface);
                    Level(laid, ClosingRail(Vector2.Distance(clear, meets)), clear, meets, surface, finished);
                    continue;
                }
                Post(posts, i, edge - along * (run / 2f), surface, w.Heading, surface);
                Post(posts, i, edge + along * (run / 2f), surface, w.Heading, surface);
                for (int tier = 0; tier < 2; tier++)
                    Rail(laid, LevelRailPrefab, edge, surface + tier * BridgeLayout.RailHeight, StairLayout.Wrap(edgeHeading - 90f), finished);
            }
        }
        if (blocks) return laid.FindAll(p => p.Kind == StructurePieceKind.StairFiller);
        CloseWedges(laid, walks, railed, own, posts, kit, finished, flanks);
        foreach (Spot post in posts.Values)
        {
            var position = new Vector3(post.At.x, post.Bottom + BridgeLayout.RailHeight * 0.5f, post.At.y);
            if (supports != null && InASupport(post, supports)) continue;
            laid.Add(new StructurePiece { Kind = StructurePieceKind.Post, Prefab = BridgeLayout.RailPostPrefab,
                Position = position, YawDegrees = post.Yaw });
        }
        // A rail or post wholly in the finished ground left out where the staircase stands without it (BuriedPieces.Omit, the
        // one owner, as the staircase's own walking pieces and supports are): read with the staircase by the support rule,
        // a buried post a rail rests on is kept. Before the crossed rails are made: a rail left out crosses none.
        if (structure != null)
        {
            var all = new List<StructurePiece>(structure);
            all.AddRange(laid);
            var railing = new HashSet<StructurePiece>(laid);
            var left = new HashSet<StructurePiece>(BuriedPieces.Omit(all, finished, pieces => StairSupport.Holding(pieces, finished), railing.Contains));
            laid.RemoveAll(left.Contains);
            sloped.RemoveAll(s => left.Contains(s.rail));
        }
        Cross(laid, sloped);
        return laid;
    }

    /// <summary>A stepped parapet's piece over each step: 2 x 2 x 1, its top 1 m deep at its high end, its face sloping 2 m
    /// down over the 1 m to its low end (the rip's collider), origin at its middle.</summary>
    public const string ParapetPrefab = "blackmarble_base_1";

    /// <summary>The cap on a stepped parapet's piece: a 1 m cube.</summary>
    public const string ParapetCapPrefab = "blackmarble_1x1";

    /// <summary>A course under a parapet: a 2 x 2 x 2 block, and a 2 x 1 x 1 to bring it to the courses.</summary>
    public const string ParapetCoursePrefab = DvergrViaduct.Block, ParapetHalfCoursePrefab = "blackmarble_2x1x1";

    /// <summary>
    /// One side of a step's parapet, as built by hand (a river viaduct's west end): a
    /// <see cref="ParapetPrefab"/> beside the step, 1 m thick just outside its edge, its foot at the step's, its origin over
    /// the step's middle half a metre over the tread there, its slope down the flight; a <see cref="ParapetCapPrefab"/>
    /// on from its high end, half a metre up the flight past the step's top, under the next step's piece's slope (on the
    /// top step, on the deck), so the parapet climbs a metre a step with them. Where the step's edge has nothing to stand
    /// on (its foot not sunk <see cref="StairSupport.Bury"/> into the lowest finished ground under the walk's edge beside it,
    /// as <see cref="Courses"/> reads it), black marble under it down to the ground in the 2 m courses of <paramref name="top"/>: a
    /// <see cref="ParapetHalfCoursePrefab"/> under the parapet where its foot stands between two, then
    /// <see cref="ParapetCoursePrefab"/>s under the step's edge and the parapet both, until one stands on the ground (as
    /// the stairs' columns, BridgeLayout.LayEndStairs). A piece wholly in the ground is left out.
    /// </summary>
    private static void Parapet(List<StructurePiece> laid, StairSupport.Walk w, int side, float top, Func<int, int, float> finished,
        bool onBank = false)
    {
        float half = w.Across / 2f, yaw = StairLayout.Wrap(w.Heading + 90f);   // the piece's local +x down the flight
        Vector2 strip = w.At(w.Depth / 2f, side * (half + ParapetHalf));
        Place(laid, ParapetPrefab, strip, w.Underside + 1f, yaw, finished);
        Place(laid, ParapetCapPrefab, w.At(w.Depth + ParapetHalf, side * (half + ParapetHalf)), w.Underside + StairLayout.PieceRise + 0.5f, yaw, finished);
        Courses(laid, strip, w.At(w.Depth / 2f, side * half), w.Underside, top, yaw, finished, onBank);
    }

    /// <summary>The black marble under a parapet's piece whose foot is at <paramref name="bottom"/>, down to the finished ground
    /// in the 2 m courses of <paramref name="top"/>: a <see cref="ParapetHalfCoursePrefab"/> under <paramref name="strip"/>
    /// (the parapet's middle) where the foot stands between two, then <see cref="ParapetCoursePrefab"/>s under
    /// <paramref name="edge"/> (the walk's edge beside it), by the staircases' walls' rule for a foundation (StairSupport's
    /// walls, one owner): on until the lowest is sunk <see cref="StairSupport.Bury"/> into the lowest finished ground under the
    /// column (<see cref="BuriedPieces.RangeOver"/>, a 2 x 2 course's footprint at <paramref name="edge"/>), so none stands
    /// over a gap on a bank falling away across it (in game: two blocks added by hand under a viaduct's bank parapet at
    /// (9082, 126)); none where that ground is not known. On a bank (<paramref name="onBank"/>, a viaduct's parapet run on past its edge) the course is
    /// instead the <see cref="ParapetHalfCoursePrefab"/> alone, a metre at a time, under <paramref name="strip"/> only, so none reaches over the road. A course wholly in the ground is left out (<see cref="Place"/>).</summary>
    private static void Courses(List<StructurePiece> laid, Vector2 strip, Vector2 edge, float bottom, float top, float yaw,
        Func<int, int, float> finished, bool onBank = false)
    {
        Vector2 along = StairLayout.Right(yaw), across = StairLayout.Forward(yaw);   // the piece's local x and z
        if (onBank)
        {
            // On the bank the parapet stands beside the road, so its black marble stays off the road's surface (the owner, 5 Nov
            // 2026: a 2 x 2 x 2 under the walk's edge left a gap in the road): ParapetHalfCoursePrefab, 2 m long and 1 m
            // thick, laid along the parapet's line under its middle, a course a metre on down to the ground by the same rule.
            var (su, sv, sy) = DvergrViaduct.Extents(ParapetHalfCoursePrefab);
            float low = BuriedPieces.RangeOver(new[]
            {
                strip - along * su - across * sv, strip + along * su - across * sv,
                strip + along * su + across * sv, strip - along * su + across * sv,
            }, finished).lowest;
            for (int n = 0; bottom > low - StairSupport.Bury && n < MaxParapetCourses; n++)
            {
                Place(laid, ParapetHalfCoursePrefab, strip, bottom - sy, yaw, finished);
                bottom -= 2f * sy;
            }
            return;
        }
        var (hu, hv, _) = DvergrViaduct.Extents(ParapetCoursePrefab);
        float ground = BuriedPieces.RangeOver(new[]
        {
            edge - along * hu - across * hv, edge + along * hu - across * hv,
            edge + along * hu + across * hv, edge - along * hu + across * hv,
        }, finished).lowest;
        for (int n = 0; bottom > ground - StairSupport.Bury && n < MaxParapetCourses; n++)
        {
            if (Mathf.RoundToInt(top - bottom) % 2 != 0)
            {
                Place(laid, ParapetHalfCoursePrefab, strip, bottom - 0.5f, yaw, finished);
                bottom -= 1f;
                continue;
            }
            Place(laid, ParapetCoursePrefab, edge, bottom - 1f, yaw, finished);
            bottom -= 2f;
        }
    }

    /// <summary>
    /// The parapet a structure's end without stairs runs on past its edge onto the bank, on both sides, whatever the bank
    /// beside the road does (after a hand-built example at a river viaduct's east end, (4443.2, -4992), railed over the
    /// drop on both sides; and the owner's word on it, 5 Nov 2026: one side alone "could look uneven", so both, unless it
    /// gets in the way): the parapet the end's first step down would have (<see cref="Parapet"/>), a module
    /// (<see cref="StairLayout.PieceRun"/>) further out, its courses from its own foot, as built by hand (a
    /// <see cref="ParapetCapPrefab"/> half a metre past the first module, a <see cref="ParapetPrefab"/> over the next sloping
    /// down onto the bank, black marble under it to the ground); and under the first module's edge, the deck's course carried
    /// on (<see cref="ParapetCoursePrefab"/>, its top at the edge's height) and on down to the ground. Over a bank rising
    /// beside the road the courses stop where the ground is (<see cref="Courses"/>) and a piece wholly in it is left out
    /// (<see cref="Place"/>). What gets in the way (a road over it, the support rule) and the structure's own railing run on
    /// over its edge (a viaduct's arch) are the structure's to say and lay.
    /// </summary>
    /// <param name="end">The end as the road meets it (StructureEnds.Of); nothing unless it has no steps.</param>
    /// <param name="across">The walk's width: two 2 m lanes on a viaduct.</param>
    /// <param name="finished">The finished ground at a whole-metre vertex.</param>
    /// <returns>Each side: the way out across it and its pieces (none wholly in the ground).</returns>
    public static List<(Vector2 outward, List<StructurePiece> pieces)> LayBankParapet(StructureEnds.End end, float across,
        Func<int, int, float> finished)
    {
        var sides = new List<(Vector2, List<StructurePiece>)>();
        if (!end.IsSet || end.Steps != 0) return sides;
        float heading = StairLayout.Wrap(BridgeLayout.YawDegrees(end.Up));
        // The bank's first module past the edge, and the first step's walk a module further out.
        var bank = new StairSupport.Walk(StructureEnds.StepJoint(end.Edge, end.Up, 1), heading, StairLayout.PieceRun, across,
            end.Height - StairKit.BlackMarble.FloorThickness, false, StairKit.BlackMarble.FloorThickness);
        var step = new StairSupport.Walk(StructureEnds.StepJoint(end.Edge, end.Up, 2), heading, StairLayout.PieceRun, across,
            StructureEnds.StepBase(end.Height, 0), true, 0f);
        float yaw = StairLayout.Wrap(heading + 90f), half = across / 2f;
        foreach (int side in new[] { -1, 1 })
        {
            var laid = new List<StructurePiece>();
            Courses(laid, bank.At(bank.Depth / 2f, side * (half + ParapetHalf)), bank.At(bank.Depth / 2f, side * half), end.Height,
                end.Height, yaw, finished, onBank: true);
            Parapet(laid, step, side, step.Underside, finished, onBank: true);
            sides.Add((bank.Right * side, laid));
        }
        return sides;
    }

    /// <summary>Half a parapet's thickness: its middle this far out past the step's edge.</summary>
    private const float ParapetHalf = 0.5f;

    /// <summary>The most pieces under one parapet piece: 32, at least 32 m, deeper than any end stairs' bank falls.</summary>
    private const int MaxParapetCourses = 32;

    /// <summary>A piece of the parapet laid; one wholly in the ground is left out with the rest of the viaduct's where it
    /// stands without it (DvergrViaduct.LeaveOutBuried, BuriedPieces.Omit, the one owner).</summary>
    private static void Place(List<StructurePiece> laid, string prefab, Vector2 at, float y, float yaw, Func<int, int, float> finished)
    {
        var position = new Vector3(at.x, y, at.y);
        laid.Add(new StructurePiece { Kind = StructurePieceKind.Beam, Prefab = prefab, Position = position, YawDegrees = yaw });
    }

    /// <summary>What the staircase's ends leave unrailed: pieces with no rail at all (<see cref="Bare"/>), and the floors what
    /// it ends on takes the walk over from (<see cref="Road"/>).</summary>
    private sealed class Ways
    {
        public readonly HashSet<int> Bare = new();
        /// <summary>Floors at an end the road or the location's landing takes the walk over from, the floors that end meets: open on
        /// every edge at walking height (within <see cref="LedgeDrop"/>), railed only over the ledge, one rule for a road's end
        /// and a location's (in game: a black marble staircase's top at -5380.9,5364.4 where the road carries on, bare; a cave's
        /// approach's last floor at 5046.8,-5892.6, on its landing 0.4-0.7 m over the ground beside it, railed on both sides,
        /// though it stands on the flat landing). Not a landing another staircase
        /// meets end to end, where the walk carries on across it onto that one: no road comes onto it from any side, however
        /// level the ground beside it stands, so it is railed on every edge not walked on across as any floor is (in game: the
        /// landing where two staircases meet at 3913.5,-6144.4, bare where the ground stood 0.4 m over it; a ridge where two
        /// staircases meet at 1601.0,-6187.5, bare where it stood 0.16 m under it).</summary>
        public readonly HashSet<int> Road = new();
        /// <summary>Floors the walk ends on (<see cref="WalkOut"/>), each with the way out across the edges it leaves by.</summary>
        public readonly Dictionary<int, List<Vector2>> Out = new();
        /// <summary>The exit of each floor at an end (<see cref="Road"/>, <see cref="Out"/>) that has one, the way out across
        /// it (<see cref="Exit"/>). Left open, always (<see cref="TakenOver"/>).</summary>
        public readonly Dictionary<int, Vector2> Exits = new();
        public Func<int, int, float> Finished = (x, z) => float.NaN;
        public StairKit? Kit;
    }

    /// <summary>The <see cref="Ways"/> of a staircase with these <paramref name="ends"/>: at each end, a road's or a location's, the
    /// floors it meets taken over by it (<see cref="AtEnd"/>); and the end steps in the ground bare (<see cref="InTheGround"/>).</summary>
    private static Ways WaysOn(List<StairSupport.Walk> walks, List<bool> railed, int own, IReadOnlyList<StairEnd> ends, StairKit kit,
        Func<int, int, float> finished)
    {
        var ways = new Ways { Finished = finished, Kit = kit };
        var joined = new HashSet<int>();
        foreach (StairEnd end in ends)
        {
            HashSet<int> at = AtEnd(walks, railed, own, end, kit, out HashSet<int> met);
            // The landing a staircase met there end to end carries the walk on from (the 2x2 connector with a flight on each
            // side; two floors at the top of two staircases, the second one's top stair onto the first; a ridge's row of floors
            // between them): the road comes onto none of its floors from any side, and takes nothing over there. Else the road
            // or the location's landing takes the walk over on the floors its end meets, not on those laid on past them level with
            // them, out of its reach: railed as any floor (in game: six flat floors from a black marble staircase's
            // foot at 1807.9,-6613.5 to its first stair railed on one side only, four at its top at 1776.4,-6554.4 on neither).
            if (at.Any(i => MeetsAnother(walks, own, i, kit))) joined.UnionWith(at);
            else ways.Road.UnionWith(met);
        }
        ways.Bare.UnionWith(InTheGround(walks, railed, finished));
        ways.Road.ExceptWith(ways.Bare);
        ways.Road.ExceptWith(joined);
        for (int i = 0; i < own; i++)
            if (railed[i] && !walks[i].Stair && WalkOut(walks, railed, own, i, kit) is List<Vector2> leaves) ways.Out[i] = leaves;
        foreach (int i in ways.Road.Concat(ways.Out.Keys).Distinct())
            if (Exit(walks, i, kit, finished) is Vector2 exit) ways.Exits[i] = exit;
        return ways;
    }

    /// <summary>The exit of floor <paramref name="i"/> on the finished ground, the way out across it: the one rule's choice
    /// (StairEnds.Exit over StairEnds.FloorDrops: the first free edge, straight on, its right, its left, with both corners within
    /// a step of the ground), its edges walked on across (<see cref="Open"/>) joined, and its ground built dry at both corners
    /// (RoadWater.BuiltDry: a walker never steps off into the water). Null where it has none.</summary>
    private static Vector2? Exit(List<StairSupport.Walk> walks, int i, StairKit kit, Func<int, int, float> finished)
    {
        StairSupport.Walk w = walks[i];
        float surface = w.Surface(0f, kit);
        float Ground(float x, float z) => BuriedPieces.MeshHeight(x, z, finished);
        var joined = new HashSet<StairEnds.FloorEdge>();
        for (int e = 0; e < 4; e++)
        {
            (Vector2 a, Vector2 b, Vector2 outward) = EdgeOf(w, e);
            if (!Open(walks, i, a + outward * StairLayout.EndReach, b + outward * StairLayout.EndReach, surface, outward)) continue;
            if (Vector2.Dot(outward, w.Dir) > 0.99f) joined.Add(StairEnds.FloorEdge.Front);
            else if (Vector2.Dot(outward, w.Right) > 0.99f) joined.Add(StairEnds.FloorEdge.Right);
            else if (Vector2.Dot(outward, w.Right) < -0.99f) joined.Add(StairEnds.FloorEdge.Left);
        }
        bool Dry(float a, float b) => (float.IsNaN(a) || RoadWater.BuiltDry(surface - a)) && (float.IsNaN(b) || RoadWater.BuiltDry(surface - b));
        StairEnds.FloorEdge exit = StairEnds.Exit(StairEnds.FloorDrops(w.Back, w.Dir, surface, w.Across, Ground), joined.Contains, also: Dry);
        return exit switch
        {
            StairEnds.FloorEdge.Front => w.Dir,
            StairEnds.FloorEdge.Right => w.Right,
            StairEnds.FloorEdge.Left => -w.Right,
            _ => null,
        };
    }

    /// <summary>
    /// Whether the edge of walking piece <paramref name="i"/> from <paramref name="a"/> to <paramref name="b"/> (the way out
    /// across it <paramref name="outward"/>, standing near <paramref name="height"/>) is railed: the one owner of that choice.
    /// Every edge is, but where the walk goes on across it: not on an end step in the ground (<see cref="Ways.Bare"/>); not
    /// onto another walking piece (<see cref="Open"/>); not where what the staircase ends on takes the walk over
    /// (<see cref="TakenOver"/>).
    /// The one owner of railings and parapets (docs/OWNERS.md).
    /// </summary>
    private static bool Railed(List<StairSupport.Walk> walks, Ways ways, int i, Vector2 a, Vector2 b, Vector2 outward, float height)
    {
        if (ways.Bare.Contains(i)) return false;
        if (Open(walks, i, a + outward * StairLayout.EndReach, b + outward * StairLayout.EndReach, height, outward)) return false;
        return !TakenOver(walks, ways, i, (a + b) * 0.5f, outward);
    }

    /// <summary>
    /// Whether the road, a location's landing or the ground takes the walk over across the edge of floor <paramref name="i"/> whose
    /// middle is <paramref name="middle"/>, the way out across it <paramref name="outward"/>: where the staircase ends.
    /// <list type="bullet">
    /// <item>The floor's exit (<see cref="Ways.Exits"/>: a free edge with both corners within a step of the finished ground,
    /// StairEnds.EdgeLands, the one rule of whether a floor lands), always (in game: an approach's top floor at 5704.6,3590.9,
    /// level on its right side, the ground falling away on its left and past its far edge, is walked off its right side).</item>
    /// <item>The road or the location's landing, on a floor its end meets (<see cref="Ways.Road"/>): on every edge but one over
    /// the ledge, the ground half a metre out more than <see cref="LedgeDrop"/> under it (in game: the top floor at
    /// -9341.6,-792.2, laid on past the road's end over a drop, railed by hand on both its sides).</item>
    /// <item>The ground straight on off a floor the walk ends on (<see cref="WalkOut"/>) with no exit (its ground not read),
    /// judged from the walk itself, not from where the end was recorded, where the ground there stands at walking height (in game: the top floor of an approach to
    /// a stone tower at -1930.0,6720.0, laid on past the end recorded at the last stair's top, was railed across the way off
    /// it onto the landing as well as on its sides).</item>
    /// </list>
    /// </summary>
    private static bool TakenOver(List<StairSupport.Walk> walks, Ways ways, int i, Vector2 middle, Vector2 outward)
    {
        if (walks[i].Stair) return false;
        // The floor's exit (StairEnds.EdgeLands, the one rule of whether a floor lands): open, whatever else is read.
        bool hasExit = ways.Exits.TryGetValue(i, out Vector2 exit);
        if (hasExit && Vector2.Dot(exit, outward) > 0.99f) return true;
        float drop = Drop(walks[i].Surface(0f, ways.Kit!), middle + outward * 0.5f, ways.Finished);
        // One rule both ways: over the ledge only where the ground is known to drop past it; ground not known (NaN: a location
        // placed after the staircase, a run with no expected location) leaves the walk open, as at a road's end.
        if (drop > LedgeDrop) return false;
        // Where the road or the location's landing takes the walk over, every edge at walking height; where the walk ends on the
        // floor, its exit alone, and straight on only where it has none (the ground there not read, or a floor not landing).
        return ways.Road.Contains(i) || !hasExit && ways.Out.TryGetValue(i, out List<Vector2>? leaves) && leaves.Exists(o => Vector2.Dot(o, outward) > 0.99f);
    }

    /// <summary>How far the finished ground just outside a floor's edge may stand under its walking surface and the walk still
    /// carry on onto it, at walking height: a metre, a step down a player takes (1.5 m before: a cave's approach's last floor
    /// stood on its landing 0.4-0.7 m over the ground beside it; maybe if the drop is over 1 m it is railed). Deeper, the edge
    /// is over the ledge. The one owner of step and stair geometry (docs/OWNERS.md).</summary>
    public const float LedgeDrop = 1f;

    /// <summary>How far the finished ground at <paramref name="outside"/> (half a metre out from an edge's middle) stands under
    /// <paramref name="surface"/>; NaN where that ground is not known (neither over the ledge nor at walking height).</summary>
    private static float Drop(float surface, Vector2 outside, Func<int, int, float> finished) =>
        surface - BuriedPieces.MeshHeight(outside.x, outside.y, finished);

    /// <summary>Whether the walk carries on across an edge of floor <paramref name="i"/> onto a piece of another staircase
    /// (those from <paramref name="own"/> on) near its walking height.</summary>
    private static bool MeetsAnother(List<StairSupport.Walk> walks, int own, int i, StairKit kit)
    {
        for (int e = 0; e < 4; e++)
        {
            (Vector2 a, Vector2 b, Vector2 outward) = EdgeOf(walks[i], e);
            for (int j = own; j < walks.Count; j++)
                foreach (float t in new[] { 0.25f, 0.5f, 0.75f })
                    if (WalkedOn(walks[j], Vector2.Lerp(a, b, t) + outward * StairLayout.EndReach, walks[i].Surface(0f, kit))) return true;
        }
        return false;
    }

    /// <summary>
    /// Where the walk leaves floor <paramref name="i"/>, if it ends on it: the way out across each edge it leaves by, read from
    /// the walk itself. The walk comes on across the edges walked on across (<see cref="Open"/>); it ends on a floor whose
    /// edges walked on across lead, directly or through a turn's wedge fillers, to a single other stair or floor: a floor
    /// between two (a switchback's, a landing's within a flight) is passed through. It leaves straight on across the floor:
    /// by the edge facing away from those it came on by, or, come on across a corner, by both edges facing away from it.
    /// Null where it does not end on it.
    /// </summary>
    private static List<Vector2>? WalkOut(List<StairSupport.Walk> walks, List<bool> railed, int own, int i, StairKit kit)
    {
        StairSupport.Walk w = walks[i];
        var outs = new Vector2[4];
        var on = new bool[4];
        Vector2 came = new Vector2(0f, 0f);
        var reached = new HashSet<int> { i };
        var fillers = new Queue<int>();
        for (int e = 0; e < 4; e++)
        {
            (Vector2 a, Vector2 b, Vector2 outward) = EdgeOf(w, e);
            outs[e] = outward;
            on[e] = Open(walks, i, a + outward * StairLayout.EndReach, b + outward * StairLayout.EndReach, w.Surface(0f, kit), outward);
            if (!on[e]) continue;
            came += outward;
            Across(i, e);
        }
        // Through the wedge fillers it came on across, to the walked piece it came from.
        while (fillers.Count > 0)
        {
            int f = fillers.Dequeue();
            for (int e = 0; e < 4; e++) Across(f, e);
        }
        if (reached.Count(j => j != i && (railed[j] || j >= own)) != 1 || came.sqrMagnitude < 0.25f) return null;
        Vector2 way = -came.normalized;
        float best = float.NegativeInfinity;
        for (int e = 0; e < 4; e++) if (!on[e]) best = Mathf.Max(best, Vector2.Dot(outs[e], way));
        if (best < 0.5f) return null;
        var leaves = new List<Vector2>();
        for (int e = 0; e < 4; e++)
            if (!on[e] && Vector2.Dot(outs[e], way) >= best - 0.05f) leaves.Add(outs[e]);
        return leaves;

        // The walked pieces just past edge e of piece k at its walking height, added to those reached; a filler among them to
        // be walked on through.
        void Across(int k, int e)
        {
            (Vector2 a, Vector2 b, Vector2 outward) = EdgeOf(walks[k], e);
            float height = walks[k].Surface(walks[k].Depth, kit);
            foreach (float t in new[] { 0.25f, 0.5f, 0.75f })
            {
                Vector2 p = Vector2.Lerp(a, b, t) + outward * StairLayout.EndReach;
                for (int j = 0; j < walks.Count; j++)
                    if (!reached.Contains(j) && WalkedOn(walks[j], p, height))
                    {
                        reached.Add(j);
                        if (!railed[j] && j < own) fillers.Enqueue(j);
                    }
            }
        }
    }

    /// <summary>Edge <paramref name="e"/> of a walking piece (right, back, left, front), from end to end, and the way out across it.</summary>
    private static (Vector2 a, Vector2 b, Vector2 outward) EdgeOf(StairSupport.Walk w, int e)
    {
        var (edge, along, run, outward, _) = EdgeFrame(w, e);
        return (edge - along * (run / 2f), edge + along * (run / 2f), outward);
    }

    /// <summary>Edge <paramref name="e"/> of a walking piece by its middle, its direction along, its run, the way out across it and
    /// that way's heading.</summary>
    private static (Vector2 edge, Vector2 along, float run, Vector2 outward, float heading) EdgeFrame(StairSupport.Walk w, int e)
    {
        float edgeHeading = StairLayout.Wrap(w.Heading + 90f * e);
        Vector2 outward = StairLayout.Right(edgeHeading), along = StairLayout.Forward(edgeHeading);
        float reach = e % 2 == 0 ? w.Across / 2f : w.Depth / 2f, run = e % 2 == 0 ? w.Depth : w.Across;
        return (w.At(w.Depth / 2f, 0f) + outward * reach, along, run, outward, edgeHeading);
    }

    /// <summary>
    /// The steps at either end of the staircase whose bottom is in the finished ground, and on from each the next while its
    /// is too: no railing at all, neither rail nor post, which would lie on the ground (the foot of the
    /// MountainCave02 approach: the first stair at -3005.2,110.5,-1748.6, the ground 111.44 over it, had its rails in the
    /// snow). The railing starts at the first step standing clear. An end step is one nothing walked carries on from, at
    /// its foot or its top; it is in the ground where the finished ground at its middle stands at or over its walking
    /// surface there. A step resting on the ground, its underside at the ground, stands clear: a flight laid down a slope
    /// to the road is railed right down (in game: a last flight, at -9224.4,62.3,-792.9, stood bare on
    /// both sides from its foot to its turn, railed by hand).
    /// </summary>
    private static HashSet<int> InTheGround(List<StairSupport.Walk> walks, List<bool> railed, Func<int, int, float> finished)
    {
        var bare = new HashSet<int>();
        var ends = new Queue<int>();
        Vector2 Past(StairSupport.Walk w, bool top) => w.At(top ? w.Depth + StairLayout.EndReach : -StairLayout.EndReach, 0f);
        bool Carried(int i, bool top) => Beside(walks, i, Past(walks[i], top), walks[i].Underside + (top ? StairLayout.PieceRise : 0f));
        for (int i = 0; i < walks.Count; i++)
            if (railed[i] && walks[i].Stair && (!Carried(i, false) || !Carried(i, true))) ends.Enqueue(i);
        while (ends.Count > 0)
        {
            int i = ends.Dequeue();
            StairSupport.Walk w = walks[i];
            Vector2 middle = w.At(w.Depth / 2f, 0f);
            if (bare.Contains(i) || !(BuriedPieces.MeshHeight(middle.x, middle.y, finished) >= w.Underside + StairLayout.StepMiddle - 0.01f)) continue;
            bare.Add(i);
            // The next step on, across either end.
            foreach (bool top in new[] { false, true })
                for (int j = 0; j < walks.Count; j++)
                    if (j != i && railed[j] && walks[j].Stair && !bare.Contains(j) && WalkedOn(walks[j], Past(w, top), w.Underside + (top ? StairLayout.PieceRise : 0f)))
                        ends.Enqueue(j);
        }
        return bare;
    }

    /// <summary>A post's spot: where, the height it stands on, its yaw and the rank of the piece that gave it, and every
    /// railed piece with a post there.</summary>
    private sealed class Spot
    {
        public Vector2 At;
        public float Bottom, Yaw, Rank;
        public readonly List<int> Owners = new();
    }

    /// <summary>A 1 m rail (wood_beam_1, from the railing prototype).</summary>
    public const string ShortRailPrefab = "wood_beam_1";

    /// <summary>A turn's wedge filler leaves the railing open at the outer corner, between the outer front corner of the flight
    /// below it and the outer back corner of the one above (0.8 m at 22.5°, 1.5 m at 45°, 2.2 m at 67.5°). Closed as in
    /// The hand-built railing prototype (its 22.5° turns): the lower flight's outer rail runs straight
    /// on 1 m over the wedge (a <see cref="ShortRailPrefab"/> at the joint's height and one a metre over, as a landing's two)
    /// to a post, which the upper flight's rail starts from in place of its own corner post. Where that would run onto
    /// another walked piece (a switchback's other leg), the upper flight's rail runs 1 m back instead. Wider turns (no
    /// example yet): both run on 1 m, a 1 m rail between their ends where they still stand apart. A square turn (90°, its
    /// wedge a 2 x 2 filler, 2.8 m across): both run on to the clean corner, where the lower flight's outer side carried
    /// straight on meets the upper's carried back, a level rail each and a post there (in game: railed by hand round the
    /// corner at -9253.1,-803.7, which stood open on its outside). Whatever still stands apart, a single run's end and the
    /// other flight's corner (one run stopped by a walked piece at its heights), is closed post to post. A switchback's join
    /// (a turn wider than square) is closed post to post where its two corners stand a joint or more apart.
    /// <para>The wedge is closed from the corners of the pieces either side of it whether or not those pieces' own sides are
    /// railed there: a floor the road takes the walk over from (<see cref="TakenOver"/>), its sides bare at walking height,
    /// still gives the wedge's run its corner, and gets its post where a run starts or ends on it (in game: a black marble
    /// staircase's foot at 5769.1,5651.4, its last stair turning square onto the floor the road comes onto, the 2 x 2 filler
    /// between them bare round its outer corner over a 6 m drop; railed by hand round that corner, posts at the corner and
    /// the floor's, the path between the two left open).</para>
    /// <para>A wedge between two staircases met end to end is closed by the one whose layout holds its filler, from the other's
    /// corners too (<paramref name="flanks"/>: its stairs and floors; their posts are its own railing's), and the other's
    /// pieces stop a run as this one's do. Two met at a pit's bottom (RoadStairRuns.JoinHigh: the first's last stair walked
    /// down to the join, the second's floor turned off its foot) are both walked down to the wedge: the other's stair's foot
    /// corner stands for the lower flight's front (in game: the outside of such a square turn at -9387.3,-1427.7 stood open
    /// over the pit).</para></summary>
    private static void CloseWedges(List<StructurePiece> laid, List<StairSupport.Walk> walks, List<bool> railed, int own,
        Dictionary<(int, int, int), Spot> posts, StairKit kit, Func<int, int, float> finished, IReadOnlyCollection<int>? flanks = null)
    {
        // The corners the wedges are closed from: the posts laid, and every other corner of a railed piece of this staircase
        // where a post would stand had its side been railed, a post only once a run uses it; and the corners of the stairs and
        // floors of a staircase met end to end (flanks), whose own railing posts them.
        // What stops a run: this staircase's walked pieces and those of one met end to end alike.
        List<bool> solid = flanks == null ? railed : railed.Select((r, i) => r || flanks.Contains(i)).ToList();
        var spots = new Dictionary<(int, int, int), Spot>(posts);
        var spare = new HashSet<(int, int, int)>();
        var theirs = new HashSet<(int, int, int)>();
        for (int i = 0; i < walks.Count; i++)
        {
            if (i < own ? !railed[i] : flanks == null || !flanks.Contains(i)) continue;
            StairSupport.Walk w = walks[i];
            float middle = w.Underside + StairLayout.StepMiddle, surface = w.Surface(0f, kit);
            foreach (var (at, bottom) in Corners(w, kit))
            {
                var key = Key(at, bottom);
                if (posts.ContainsKey(key)) continue;
                if (i >= own && !spare.Contains(key)) theirs.Add(key);
                else if (i < own) theirs.Remove(key);
                if (spare.Add(key)) spots[key] = new Spot { At = at, Bottom = bottom, Yaw = w.Heading, Rank = w.Stair ? middle : surface };
                if (!spots[key].Owners.Contains(i)) spots[key].Owners.Add(i);
            }
        }
        var used = new HashSet<(int, int, int)>();
        var keys = spots.Keys.ToList();
        var dropped = new HashSet<(int, int, int)>();
        var added = new List<Spot>();
        var done = new HashSet<(int, int)>();
        for (int f = 0; f < own; f++)
        {
            if (railed[f]) continue;
            StairSupport.Walk filler = walks[f];
            Vector2 centre = filler.At(filler.Depth / 2f, 0f);
            var near = new List<int>();
            for (int k = 0; k < keys.Count; k++)
                if (Vector2.Distance(spots[keys[k]].At, centre) < 3.5f) near.Add(k);
            foreach (int a in near)
            foreach (int b in near)
            {
                Spot p = spots[keys[a]], q = spots[keys[b]];
                if (a == b || Mathf.Abs(p.Bottom - q.Bottom) > 0.05f || p.Owners.Intersect(q.Owners).Any()) continue;
                // p the lower flight's outer front corner, q the upper's outer back corner; on (the walk out of the lower
                // across its edge at p) and back (out of the upper across its edge at q). Two staircases met at a pit's bottom
                // (RoadStairRuns.JoinHigh) are both walked down to the wedge: the other staircase's last stair stands with its
                // foot's corner at p, walked out of across its foot.
                int below = Owner(p, o => Mathf.Abs(Along(walks[o], p.At) - walks[o].Depth) < 0.05f);
                int above = Owner(q, o => Mathf.Abs(Along(walks[o], q.At)) < 0.05f);
                bool down = below < 0;
                if (down) below = Owner(p, o => o >= own && Mathf.Abs(Along(walks[o], p.At)) < 0.05f);
                float gap = Vector2.Distance(p.At, q.At);
                Vector2 mid = (p.At + q.At) * 0.5f;
                Vector2 on = below < 0 ? default : down ? walks[below].Dir * -1f : walks[below].Dir;
                Vector2 back = above < 0 ? default : walks[above].Dir * -1f;
                float turn = below < 0 || above < 0 ? 0f
                    : down ? Apart(StairLayout.Wrap(walks[below].Heading + 180f), walks[above].Heading) : Apart(walks[below].Heading, walks[above].Heading);
                bool square = Mathf.Abs(turn - 90f) < SameHeading;
                // Two pieces in line have no turn, so no wedge between them: a jog's floor two back and the next flight stand in line
                // across the jog's own floors (their edges are railed by the edge pass, not closed across the walk).
                if (below >= 0 && above >= 0 && turn < SameHeading) continue;
                // A walked piece stops a rail only where it stands at the rail's heights: not a switchback's other leg
                // passing over or under the turn (in game: run on under the upper leg at -1858.5,261.7,6889.9 and back over
                // the lower leg at -1858.0,240.7,6911.4; the wedge at -6894.6,-3680.4 over a lower leg).
                float lo = p.Bottom - 0.3f, hi = p.Bottom + BridgeLayout.RailHeight + 0.3f;
                if (below < 0 || above < 0 || gap < 0.3f || gap > (square ? 2.9f : 2.4f) || !Covers(filler, mid, 0.25f)
                    || Walked(walks, solid, mid, 0.05f, kit, lo, hi) || !done.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                if (turn > 91f)
                {
                    // A switchback's join (its legs turned more than square): the lower leg's top corner and the upper leg's
                    // back corner a joint or more apart beside the join's filler, closed post to post by a handrail, and a
                    // lower rail where it has air under it (in game: at -1868.8,249.7,6898.8, its lower rail
                    // left out, which would have lain on the join's filler).
                    if (gap > Joint && gap <= 1.25f)
                    {
                        Level(laid, ShortRailPrefab, p.At, q.At, p.Bottom, finished, !Floored(walks, kit, mid, p.Bottom));
                        used.Add(keys[a]); used.Add(keys[b]);
                    }
                    continue;
                }
                if (gap > 2.4f)
                {
                    Vector2 corner = p.At + on * Vector2.Dot(q.At - p.At, on);
                    if (Walked(walks, solid, (p.At + corner) * 0.5f, -0.05f, kit, lo, hi) || Walked(walks, solid, (q.At + corner) * 0.5f, -0.05f, kit, lo, hi)) continue;
                    Level(laid, LevelRailPrefab, p.At, corner, p.Bottom, finished);
                    Level(laid, LevelRailPrefab, q.At, corner, p.Bottom, finished);
                    added.Add(new Spot { At = corner, Bottom = p.Bottom, Yaw = walks[above].Heading, Rank = Mathf.Max(p.Rank, q.Rank) });
                    used.Add(keys[a]); used.Add(keys[b]);
                    continue;
                }
                bool lowerRuns = !Walked(walks, solid, p.At + on, -0.05f, kit, lo, hi);
                bool upperRuns = !lowerRuns || gap > 1.25f;
                Spot? lowerEnd = null, upperEnd = null;
                if (lowerRuns) lowerEnd = RunOn(laid, added, p, on, walks[below].Heading, finished);
                if (upperRuns && !Walked(walks, solid, q.At + back, -0.05f, kit, lo, hi)) upperEnd = RunOn(laid, added, q, back, walks[above].Heading, finished);
                // A short turn's single run ends by the other flight's corner: its post stands in for that corner's.
                // What still stands apart, the two runs' ends or a run's end and the other flight's corner, is closed by a rail
                // from post to post; within a joint's reach the single run's post stands in for that corner's.
                Vector2 from = lowerEnd?.At ?? p.At, to = upperEnd?.At ?? q.At;
                float apart = Vector2.Distance(from, to);
                if (lowerEnd != null) used.Add(keys[a]);
                if (upperEnd != null) used.Add(keys[b]);
                if (apart <= Joint)
                {
                    if (lowerEnd != null && upperEnd == null) dropped.Add(keys[b]);
                    if (upperEnd != null && lowerEnd == null) dropped.Add(keys[a]);
                }
                else if ((lowerEnd != null || upperEnd != null) && apart <= 2.25f && !Walked(walks, solid, (from + to) * 0.5f, -0.05f, kit, lo, hi))
                {
                    Level(laid, ClosingRail(apart), from, to, p.Bottom, finished, !Floored(walks, kit, (from + to) * 0.5f, p.Bottom));
                    used.Add(keys[a]); used.Add(keys[b]);
                }
            }
        }
        // A spare corner a run starts or ends on gets its post; the other staircase's corners are its own railing's.
        foreach (var key in used) if (spare.Contains(key) && !theirs.Contains(key)) posts[key] = spots[key];
        foreach (var key in dropped) posts.Remove(key);
        foreach (Spot spot in added) posts[Key(spot.At, spot.Bottom)] = spot;
    }

    /// <summary>A flight's outer rail run on 1 m from its corner <paramref name="from"/> along <paramref name="dir"/>, at both a
    /// landing's heights, to a post square to the flight; that post.</summary>
    private static Spot RunOn(List<StructurePiece> laid, List<Spot> added, Spot from, Vector2 dir, float heading, Func<int, int, float> finished)
    {
        var end = new Spot { At = from.At + dir, Bottom = from.Bottom, Yaw = heading, Rank = from.Rank };
        Level(laid, ShortRailPrefab, from.At, end.At, from.Bottom, finished);
        added.Add(end);
        return end;
    }

    /// <summary>A level rail from <paramref name="a"/> to <paramref name="b"/> at <paramref name="bottom"/> and a metre over it.</summary>
    private static void Level(List<StructurePiece> laid, string prefab, Vector2 a, Vector2 b, float bottom, Func<int, int, float> finished,
        bool lower = true)
    {
        Vector2 d = b - a;
        float yaw = StairLayout.Wrap(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg - 90f);
        for (int tier = lower ? 0 : 1; tier < 2; tier++)
        {
            float y = bottom + tier * BridgeLayout.RailHeight;
            Rail(laid, prefab, (a + b) * 0.5f, y, yaw, finished);
        }
    }

    /// <summary>How far apart two headings are, 0 to 180 degrees.</summary>
    private static float Apart(float a, float b) => Mathf.Abs(StairLayout.Wrap(a - b + 180f) - 180f);

    private static int Owner(Spot spot, Func<int, bool> match)
    {
        foreach (int o in spot.Owners) if (match(o)) return o;
        return -1;
    }

    private static float Along(StairSupport.Walk w, Vector2 p) => Vector2.Dot(p - w.Back, w.Dir);

    /// <summary>Whether a railed walking piece covers <paramref name="p"/> (within <paramref name="margin"/>; negative: well inside);
    /// with a <paramref name="kit"/>, only one whose body (underside to walking surface) reaches into <paramref name="lo"/> to
    /// <paramref name="hi"/>.</summary>
    private static bool Walked(List<StairSupport.Walk> walks, List<bool> railed, Vector2 p, float margin, StairKit? kit = null,
        float lo = float.NegativeInfinity, float hi = float.PositiveInfinity)
    {
        for (int j = 0; j < walks.Count; j++)
        {
            StairSupport.Walk w = walks[j];
            if (!railed[j] || !Covers(w, p, margin)) continue;
            if (kit != null && (w.Underside >= hi || w.Surface(w.Depth, kit) <= lo)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Whether a lower rail at <paramref name="bottom"/> over <paramref name="p"/> would lie on a walked piece (a
    /// filler too) rather than over air: one covers it, its walking surface within a few centimetres of the rail.</summary>
    private static bool Floored(List<StairSupport.Walk> walks, StairKit kit, Vector2 p, float bottom)
    {
        foreach (StairSupport.Walk w in walks)
            if (Covers(w, p, -0.05f) && Mathf.Abs(w.Surface(Mathf.Clamp(Along(w, p), 0f, w.Depth), kit) - bottom) < 0.1f) return true;
        return false;
    }

    /// <summary>Whether a post stands wholly in the staircase's supports (a wall's courses): its four corners at its foot and
    /// its top each in one, the post's measured box (<see cref="BuriedPieces.DataOf"/>) turned as it stands. A post partly
    /// clear stands, the corner post a rail ends on (in game: none at the end of the
    /// rail run on by hand into the upper leg's wall at -1858.9,6889.5; one clear of the wall at -1869.2,6898.6).</summary>
    private static bool InASupport(Spot post, IReadOnlyList<StructurePiece> supports)
    {
        BuriedPieces.Measured pole = BridgeLayout.Measured(BridgeLayout.RailPostPrefab);
        Vector2 right = StairLayout.Right(post.Yaw), forward = StairLayout.Forward(post.Yaw);
        foreach (float y in new[] { post.Bottom + 0.05f, post.Bottom + BridgeLayout.RailHeight - 0.05f })
        foreach (Vector2 c in new[] { new Vector2(pole.Min.x, pole.Min.z), new Vector2(pole.Max.x, pole.Min.z), new Vector2(pole.Max.x, pole.Max.z), new Vector2(pole.Min.x, pole.Max.z) })
        {
            Vector2 at = post.At + right * c.x + forward * c.y;
            bool inside = false;
            foreach (StructurePiece s in supports)
            {
                if (!BuriedPieces.BoundsOf(s.Prefab, out Vector3 min, out Vector3 max)) continue;
                Vector2 d = at - new Vector2(s.Position.x, s.Position.z);
                float x = Vector2.Dot(d, StairLayout.Right(s.YawDegrees)), z = Vector2.Dot(d, StairLayout.Forward(s.YawDegrees)), h = y - s.Position.y;
                if (x > min.x && x < max.x && z > min.z && z < max.z && h > min.y && h < max.y) { inside = true; break; }
            }
            if (!inside) return false;
        }
        return true;
    }

    /// <summary>The floors at a staircase's <paramref name="end"/>: each one its recorded point or where the road (or the location's
    /// ground) meets it touches, then the floors level with one of those beside it, and so on (a landing's whole row: at a
    /// location, every floor of its landing; at a road's end, the row read for another staircase meeting it end to end, while
    /// the road takes the walk over only on the floors it meets, <paramref name="met"/>). Where the road meets it, not only its recorded point: a road's
    /// own staircase is recorded at a path point, and its top landing laid on past it (staircase -1880,6632: the landing
    /// railed across the road coming onto it at -1882.8,6634.0). At a location, where the location's ground meets it on past the
    /// landing's lip, the floor nearest that ground. A stair is never among them: railed along its sides, it leaves its end
    /// open to the road. A road's end is met a ring's radius off the staircase (RoadEndRing): within the ring's outer edge.</summary>
    /// A floor the walk passes through from one of its flights to another (<see cref="PassedThrough"/>: a flight's rest) is never
    /// among them, whatever the point its end was recorded at: the walk carries on across it, and it is railed as any floor.
    /// <param name="met">The floors the end itself meets, before those laid on past them are added.</param>
    private static HashSet<int> AtEnd(List<StairSupport.Walk> walks, List<bool> railed, int own, StairEnd end, StairKit kit, out HashSet<int> met)
    {
        var bare = new HashSet<int>();
        // A road's end is the centre of the ring the staircase meets it on (RoadEndRing): the road comes onto the floors within
        // the ring's outer edge of it.
        float meets = end.AtLocation ? StairLayout.EndReach : new RoadEndRing(end.Meet, 0f, 0f).Radius + RoadEndRing.Delta + 0.3f;
        for (int i = 0; i < walks.Count; i++)
            if (railed[i] && !walks[i].Stair && !PassedThrough(walks, own, i, kit)
                && (Covers(walks[i], end.At, StairLayout.EndReach) || Covers(walks[i], end.Meet, meets))) bare.Add(i);
        if (end.AtLocation && bare.Count == 0 && walks.Count > 0)
        {
            int nearest = Enumerable.Range(0, walks.Count).OrderBy(i => Off(walks[i], end.Meet)).First();
            if (railed[nearest] && !walks[nearest].Stair) bare.Add(nearest);
        }
        met = new HashSet<int>(bare);
        var floors = new Queue<int>(bare);
        while (floors.Count > 0)
        {
            StairSupport.Walk w = walks[floors.Dequeue()];
            for (int j = 0; j < walks.Count; j++)
            {
                StairSupport.Walk o = walks[j];
                if (!railed[j] || o.Stair || bare.Contains(j) || Mathf.Abs(o.Surface(0f, kit) - w.Surface(0f, kit)) > 0.3f
                    || PassedThrough(walks, own, j, kit)) continue;
                // Beside it: past the middle of one of its edges.
                for (int e = 0; e < 4; e++)
                {
                    float edgeHeading = StairLayout.Wrap(w.Heading + 90f * e);
                    float reach = e % 2 == 0 ? w.Across / 2f : w.Depth / 2f;
                    if (!Covers(o, w.At(w.Depth / 2f, 0f) + StairLayout.Right(edgeHeading) * (reach + StairLayout.EndReach), 0f)) continue;
                    bare.Add(j); floors.Enqueue(j);
                    break;
                }
            }
        }
        return bare;
    }

    /// <summary>Whether floor <paramref name="i"/> is a rest the walk passes through between two of its own flights: across two
    /// of its edges it steps onto a stair of this staircase (those before <paramref name="own"/>). Recorded at a road's path
    /// point, a road staircase's end can fall on such a rest when its top was carried on past it (in game: the rest between two
    /// flights at 1536.2,-6072.0, its top at 1539.0,-6073.2, stood open along both its sides).</summary>
    private static bool PassedThrough(List<StairSupport.Walk> walks, int own, int i, StairKit kit)
    {
        StairSupport.Walk w = walks[i];
        if (w.Stair) return false;
        float height = w.Surface(0f, kit);
        int edges = 0;
        for (int e = 0; e < 4; e++)
        {
            (Vector2 a, Vector2 b, Vector2 outward) = EdgeOf(w, e);
            bool onto = false;
            foreach (float t in new[] { 0.25f, 0.5f, 0.75f })
            {
                Vector2 p = Vector2.Lerp(a, b, t) + outward * StairLayout.EndReach;
                for (int j = 0; j < own && !onto; j++)
                    onto = j != i && walks[j].Stair && WalkedOn(walks[j], p, height);
                if (onto) break;
            }
            if (onto && ++edges >= 2) return true;
        }
        return false;
    }

    /// <summary>How far <paramref name="p"/> lies off <paramref name="w"/>'s footprint, 0 on it.</summary>
    private static float Off(StairSupport.Walk w, Vector2 p)
    {
        Vector2 d = p - w.Back;
        float along = Vector2.Dot(d, w.Dir), across = Mathf.Abs(Vector2.Dot(d, w.Right));
        float outAlong = Mathf.Max(0f, Mathf.Max(-along, along - w.Depth)), outAcross = Mathf.Max(0f, across - w.Across / 2f);
        return Mathf.Sqrt(outAlong * outAlong + outAcross * outAcross);
    }

    /// <summary>Whether <paramref name="p"/> is on <paramref name="w"/>'s footprint, or within <paramref name="margin"/> of it.</summary>
    private static bool Covers(StairSupport.Walk w, Vector2 p, float margin) =>
        StairLayout.OnFootprint(w.Back, w.Dir, w.Right, w.Depth, w.Across, p, margin);

    /// <summary>Whether an edge is walked on across: another walking piece covers a quarter, the middle or three quarters of
    /// the line just past it (<paramref name="a"/> to <paramref name="b"/>), near <paramref name="height"/>. A flight turned
    /// onto a floor covers only part of the floor's edge, its wedge filler some more; a rail there would block the way. A
    /// flight's side is never walked across: not onto another flight (a switchback's two legs side by side are railed each
    /// along the line they share; in game: railed by hand at -1905.5,6609.1 and along -3012.8,-1770.8, which stood open),
    /// nor onto a floor or filler beside it, level with its top at most, a step or more over its foot (in game: a
    /// switchback's join filler beside the stair under it left that stair's side bare, railed by hand at -1868.3,6899.0
    /// and -1858.1,6911.6).
    /// Nor onto a flight running along the edge (its side on it): that flight's sloped rails stand on its side and the floor's own
    /// flat pair on the floor's (in game: a black marble landing's edge beside a flight, which had the flight's rails only).</summary>
    private static bool Open(List<StairSupport.Walk> walks, int self, Vector2 a, Vector2 b, float height, Vector2 outward)
    {
        if (walks[self].Stair) return false;
        return Beside(walks, self, Vector2.Lerp(a, b, 0.25f), height, outward) || Beside(walks, self, Vector2.Lerp(a, b, 0.5f), height, outward)
            || Beside(walks, self, Vector2.Lerp(a, b, 0.75f), height, outward);
    }

    /// <summary>
    /// The part of floor <paramref name="self"/>'s edge from <paramref name="a"/> to <paramref name="b"/> that stands clear of
    /// the floor it runs on into, where <see cref="Open"/> found it walked on across only because it does: a row of floors
    /// jogged 45 degrees, each pivoting on the outer corner it shares with the last, overlaps the last on the inside of the
    /// jog, and the inner edges of the two run on into each other, their far quarter just past the other's edge. The walk
    /// goes on across the overlap, not across those edges: each is railed from its clear corner a metre on, a
    /// <see cref="ShortRailPrefab"/> to a post, as a flight's rail is run on over a turn's wedge (<see cref="RunOn"/>) (in
    /// game: railed by hand on the inside of two such jogs at 1592.7,6930.4 and 1597.0,6925.3, which stood open over the
    /// bank and over a 3 m drop, and of a black marble turn floor jogged off a flight's top at 8871.5,-92.5). Null for any other edge: one another piece covers past it without overlapping this one
    /// (a flight or a floor met edge to edge, a turn's filler), or whose clear corner is covered too.
    /// </summary>
    private static (Vector2 from, Vector2 to)? Overlapped(List<StairSupport.Walk> walks, List<bool> railed, int self, Vector2 a, Vector2 b,
        Vector2 outward, float height)
    {
        var covered = new bool[3];
        float[] at = { 0.25f, 0.5f, 0.75f };
        for (int s = 0; s < 3; s++)
        {
            Vector2 p = Vector2.Lerp(a, b, at[s]), past = p + outward * StairLayout.EndReach;
            for (int j = 0; j < walks.Count; j++)
            {
                StairSupport.Walk o = walks[j];
                if (j == self || !WalkedOn(o, past, height)) continue;
                // Only a floor the edge itself runs on into, well inside it.
                if (!railed[j] || o.Stair || !Covers(o, p, -0.05f)) return null;
                covered[s] = true;
            }
        }
        if (covered[0] == covered[2] || covered[1]) return null;
        Vector2 from = covered[0] ? b : a, dir = ((covered[0] ? a : b) - from).normalized;
        Vector2 to = from + dir;
        for (int j = 0; j < walks.Count; j++)
            if (j != self && WalkedOn(walks[j], to, height, -0.05f)) return null;
        return (from, to);
    }

    /// <summary>
    /// The clear part of floor <paramref name="self"/>'s railed edge from <paramref name="a"/> to <paramref name="b"/> where one
    /// of its ends stands well inside another floor at its walking height, this staircase's or one met end to end: a jog too
    /// shallow for the walk to carry on across the edge (<see cref="Open"/> reads nothing past it), whose inner edges cross
    /// inside each other's walk (in game: two black marble staircases met end to end, the first floor of the upper one turned
    /// 22.5 degrees onto the next, both their inner rails and corner posts standing up to 0.4 m into the other's walk at
    /// -6592.6,-3488.3). From the end standing clear to where the edge meets that floor's edge, the inner edges' crossing,
    /// where both are railed to; null for any other edge: both ends clear, or both on other floors.
    /// </summary>
    private static (Vector2 clear, Vector2 meets)? RunsInto(List<StairSupport.Walk> walks, List<bool> railed, int own, int self,
        Vector2 a, Vector2 b, float height)
    {
        int Under(Vector2 p)
        {
            for (int j = 0; j < walks.Count; j++)
                if (j != self && !walks[j].Stair && (railed[j] || j >= own) && WalkedOn(walks[j], p, height, -0.05f)) return j;
            return -1;
        }
        int atA = Under(a), atB = Under(b);
        if (atA >= 0 == atB >= 0) return null;
        Vector2 clear = atA >= 0 ? b : a, inside = atA >= 0 ? a : b;
        StairSupport.Walk other = walks[Math.Max(atA, atB)];
        // Its footprint is convex: the edge goes in across its edge once.
        float lo = 0f, hi = 1f;
        for (int k = 0; k < 24; k++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Covers(other, Vector2.Lerp(clear, inside, mid), 0f)) hi = mid; else lo = mid;
        }
        return (clear, Vector2.Lerp(clear, inside, hi));
    }

    /// <summary>
    /// The stretches at either end of floor <paramref name="self"/>'s edge from <paramref name="a"/> to <paramref name="b"/> past
    /// which nothing is walked onto (<see cref="Beside"/> just past the edge, <see cref="Margin"/>) and that stand on no other
    /// walking piece: where <see cref="Open"/> found the edge walked on across by its middle,
    /// the part of it left on the outline. Two floors met in line but set aside from each other (in a clipped join, up to half
    /// the width aside), or turned off each other across two staircases, overlap; each one's edge runs on past the other's
    /// side, and the walk goes on across it only where the other floor stands past it. From the end corner to where the
    /// other floor's edge crosses it, each longer than half a post (<see cref="PostHalf"/>), which the corner's post closes.
    /// </summary>
    private static List<(Vector2 from, Vector2 to)> Exposed(List<StairSupport.Walk> walks, int self, Vector2 a, Vector2 b, Vector2 outward,
        float height)
    {
        bool Bare(Vector2 p)
        {
            if (Beside(walks, self, p + outward * (2f * Margin), height, outward)) return false;
            for (int j = 0; j < walks.Count; j++)
                if (j != self && WalkedOn(walks[j], p, height, -0.05f)) return false;
            return true;
        }
        var stretches = new List<(Vector2, Vector2)>();
        float length = Vector2.Distance(a, b), half = PostHalf;
        foreach (var (corner, other) in new[] { (a, b), (b, a) })
        {
            // Within half a post of the corner the corner's own post closes it (two floors turned off each other on the corner
            // they share leave a sliver past it, as wide as the line is read past the edge, a floor abutting there its margin).
            float lo = half / length, hi = 1f;
            if (!Bare(Vector2.Lerp(corner, other, lo)) || Bare(Vector2.Lerp(corner, other, 0.99f))) continue;   // 0.99: walked on across somewhere
            // From there on to where the edge is first walked past: one crossing, found by halving.
            for (int k = 0; k < 24; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Bare(Vector2.Lerp(corner, other, mid))) lo = mid; else hi = mid;
            }
            stretches.Add((corner, Vector2.Lerp(corner, other, lo)));
        }
        return stretches;
    }

    /// <summary>
    /// Where the bare stretch of floor <paramref name="self"/>'s edge from its corner <paramref name="end"/> to
    /// <paramref name="bare"/> (<see cref="Exposed"/>) is one leg of a sideways step: two floors in line, level, the other one
    /// set aside and run back over this one (a clipped join), whose bare stretch on the other side of the step meets this one's
    /// at the step's inner corner (their inner ends within half a post), each leg longer than a joint (<see cref="Joint"/>:
    /// within it two posts close a step) and no longer than the kit's block (<see cref="StepBlock"/>), the line from corner to
    /// corner no longer than the longest closing rail (<see cref="ClosingRail"/>): one block and one rail pair close it. Railed by hand in game across a black marble join set half a metre aside: no post at
    /// the inner corner, a rail pair across from this leg's corner to the other's on the build grid, and the wedge between
    /// filled with the kit's 1 m block (<see cref="FillStep"/>). One of the two floors lays them (<paramref name="owns"/>): the longer
    /// leg's, a tie the corner first by x then z, whichever staircase is dressed, so two staircases met there lay it once.
    /// Null for any other stretch (railed post to post as before): floors turned off each other, a step a joint or less aside,
    /// or one wider than a block and a rail close, or a kit with no such block.
    /// </summary>
    /// <returns>The other floor, its leg's corner, and whether this floor lays the step.</returns>
    private static (int partner, Vector2 corner, bool owns)? Step(List<StairSupport.Walk> walks, StairKit kit, int self, Vector2 end,
        Vector2 bare, float surface)
    {
        if (StepBlock(kit) is not FillerKind block) return null;
        for (int j = 0; j < walks.Count; j++)
        {
            StairSupport.Walk o = walks[j];
            // In line: turned a whole number of quarter turns off this one (a floor is square), level, walked within half a post of the inner end.
            float turn = Apart(walks[self].Heading, o.Heading) % 90f;
            if (j == self || o.Stair || Mathf.Min(turn, 90f - turn) >= SameHeading || !WalkedOn(o, bare, surface, PostHalf)) continue;
            for (int e = 0; e < 4; e++)
            {
                var (a, b, outward) = EdgeOf(o, e);
                foreach (var (corner, inner) in Exposed(walks, j, a, b, outward, surface))
                {
                    if (Vector2.Distance(inner, bare) > PostHalf) continue;
                    // The inner corner: where the two legs' lines cross (square to each other).
                    Vector2 along = (bare - end).normalized;
                    Vector2 at = end + along * Vector2.Dot(corner - end, along);
                    float mine = Vector2.Distance(end, at), theirs = Vector2.Distance(corner, at);
                    // Within a joint two posts close it; past one block or past the longest closing rail, one block and
                    // one rail pair would leave it open: posts as at any bare stretch.
                    if (mine <= Joint || theirs <= Joint || mine > block.Depth + Tie || theirs > block.Depth + Tie
                        || Vector2.Distance(end, corner) > RailLength(LevelRailPrefab) + Tie) return null;
                    bool owns = mine - theirs > Tie || Mathf.Abs(mine - theirs) <= Tie && First(end, corner);
                    return (j, corner, owns);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// A sideways step (<see cref="Step"/>) railed and filled as built by hand in game (a black marble join set half a metre
    /// aside: its rails and blocks turned 292.5 degrees), between the longer leg's corner <paramref name="end"/> and the
    /// shorter leg's <paramref name="corner"/>, its inner corner by <paramref name="inner"/>. The heading across it is the
    /// game's build grid's nearest the line from corner to corner (<see cref="StairLayout.HeadingStep"/>, the hammer's
    /// turn). The rail pair (<see cref="ClosingRail"/>, at the floors' walking surface and a metre over it) starts at the
    /// longer leg's corner and runs on at that heading toward the other; a rail longer than the line (the 2 m one over a
    /// line of 1.25 to 2 m) stands on its middle instead, as a closing rail does, so it overhangs neither corner alone. The wedge holds the kit's 1 m block (its flat piece one by one,
    /// <see cref="StairKit.Fillers"/>; none where it has none), level with the floors as a turn's fillers are, at that
    /// heading, its outer face's corner on the shorter leg's corner and the face running on toward the other.
    /// </summary>
    private static void FillStep(List<StructurePiece> laid, StairKit kit, Vector2 end, Vector2 corner, Vector2 inner, float surface,
        Func<int, int, float> finished, bool block)
    {
        Vector2 d = end - corner, middle = (end + corner) * 0.5f;
        float heading = Mathf.Round(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg / StairLayout.HeadingStep) * StairLayout.HeadingStep;
        float yaw = StairLayout.Wrap(heading - 90f);
        Vector2 along = StairLayout.Forward(heading), outward = StairLayout.Right(heading);
        string rail = ClosingRail(d.magnitude);
        float railHalf = RailLength(rail) / 2f;
        Vector2 railAt = 2f * railHalf <= d.magnitude ? end - along * railHalf : middle;
        if (!block)
        {
            for (int tier = 0; tier < 2; tier++)
                Rail(laid, rail, railAt, surface + tier * BridgeLayout.RailHeight, yaw, finished);
            return;
        }
        if (StepBlock(kit) is not FillerKind flat) return;
        if (Vector2.Dot(middle - inner, outward) < 0f) outward = -outward;
        Vector2 at = corner + along * (flat.Depth / 2f) - outward * (flat.Across / 2f);
        laid.Add(new StructurePiece
        {
            Kind = StructurePieceKind.StairFiller, Prefab = flat.Prefab, Position = new Vector3(at.x, surface - kit.FloorTop, at.y),
            YawDegrees = yaw,
        });
    }

    /// <summary>The kit's 1 m block a sideways step's wedge is filled with (its flat piece one by one, <see cref="StairKit.Fillers"/>);
    /// null for a kit with none.</summary>
    private static FillerKind? StepBlock(StairKit kit)
    {
        foreach (FillerKind f in kit.Fillers) if (f.Across == 1f && f.Depth == 1f) return f;
        return null;
    }

    /// <summary>A level rail's length along its local x, its measured box (<see cref="BridgeLayout.Measured"/>).</summary>
    private static float RailLength(string prefab)
    {
        BuriedPieces.Measured m = BridgeLayout.Measured(prefab);
        return m.Max.x - m.Min.x;
    }

    /// <summary>How far apart two pieces' headings may be, in degrees, and be read as the same heading (a square turn's,
    /// <see cref="CloseWedges"/>; two floors a whole number of quarter turns apart, <see cref="Step"/>): float noise, the layouts
    /// turning in <see cref="StairLayout.HeadingStep"/>s.</summary>
    private const float SameHeading = 1f;

    /// <summary>How near two lengths may be, in metres, and be read as equal (a sideways step's legs, against each other and
    /// against its block and rail, <see cref="Step"/>): float noise.</summary>
    private const float Tie = 0.01f;

    /// <summary>A joint's reach (<see cref="CloseWedges"/>'s): two posts within it close what stands between them, no rail.</summary>
    private const float Joint = 0.45f;

    /// <summary><see cref="WalkedOn"/>'s margin: <see cref="Exposed"/> reads whether the walk goes on just past an edge, twice
    /// it out (clear of the margin another piece is read with), not <see cref="StairLayout.EndReach"/> out (which reads a
    /// turn's outer corner as open).</summary>
    private const float Margin = 0.05f;

    /// <summary>Half a railing post's width (its measured box, <see cref="BridgeLayout.Measured"/>).</summary>
    private static float PostHalf
    {
        get
        {
            BuriedPieces.Measured pole = BridgeLayout.Measured(BridgeLayout.RailPostPrefab);
            return (pole.Max.x - pole.Min.x) / 2f;
        }
    }

    /// <summary>A post already standing within half a post of <paramref name="at"/> on <paramref name="bottom"/>, where it is.</summary>
    private static Vector2? NearPost(Dictionary<(int, int, int), Spot> posts, Vector2 at, float bottom)
    {
        foreach (Spot spot in posts.Values)
            if (OneSpot(spot.At, spot.Bottom, at, bottom)) return spot.At;
        return null;
    }

    /// <summary>Whether two posts stand at one spot, one post standing for both: within half a post of each other, on the
    /// same height.</summary>
    private static bool OneSpot(Vector2 a, float aBottom, Vector2 b, float bBottom) =>
        Mathf.Abs(aBottom - bBottom) < 0.05f && Vector2.Distance(a, b) <= PostHalf;

    /// <summary>The tie-break of which of two lays what they share, where nothing else decides: the one whose point comes first,
    /// by x then z. A sideways step's legs of one length (<see cref="Step"/>) and two staircases met end to end
    /// (<see cref="LaysShared"/>) alike.</summary>
    private static bool First(Vector2 a, Vector2 b) => a.x < b.x || a.x == b.x && a.y < b.y;

    /// <summary>Whether staircase <paramref name="run"/> lays the posts it shares with <paramref name="other"/>, met end to end
    /// with it (<see cref="LeaveShared"/>): the one whose start comes first (<see cref="First"/>, the tie-break by which one of two
    /// floors lays a sideways step), then its end; so of two staircases met there one lays them, whichever is dressed.</summary>
    internal static bool LaysShared(StairRun run, StairRun other) =>
        run.From != other.From ? First(run.From, other.From) : First(run.To, other.To);

    /// <summary>The posts of a staircase's pieces <paramref name="mine"/> standing where a staircase met end to end with it lays
    /// one too (<paramref name="theirs"/>, its pieces as dressed), left out: at a corner the two share each railing lays a post,
    /// and one stands for both (<see cref="OneSpot"/>), the one of the staircase that lays shared posts
    /// (<see cref="LaysShared"/>). Where that one lays none (buried, in a support), this one's stands.</summary>
    internal static void LeaveShared(List<StructurePiece> mine, IReadOnlyList<StructurePiece> theirs)
    {
        static (Vector2 at, float bottom) SpotOf(StructurePiece post) =>
            (new Vector2(post.Position.x, post.Position.z), post.Position.y - BridgeLayout.RailHeight * 0.5f);
        var posts = theirs.Where(p => p.Kind == StructurePieceKind.Post).Select(SpotOf).ToList();
        mine.RemoveAll(p => p.Kind == StructurePieceKind.Post && SpotOf(p) is var (at, bottom)
            && posts.Exists(q => OneSpot(q.at, q.bottom, at, bottom)));
    }

    /// <summary>The level rail closing a span post to post: the 1 m rail up to a joint and a quarter, the 2 m one past it.</summary>
    private static string ClosingRail(float span) => span <= 1.25f ? ShortRailPrefab : LevelRailPrefab;

    /// <summary>Whether another walking piece of the staircase covers <paramref name="p"/> near <paramref name="height"/>.</summary>
    private static bool Beside(List<StairSupport.Walk> walks, int self, Vector2 p, float height, Vector2? outward = null)
    {
        for (int j = 0; j < walks.Count; j++)
        {
            // A flight running along the edge (rising beside it, outward across its side) is not walked onto from this edge: its
            // own sloped rails stand on its side, the floor's flat pair on the floor's.
            if (j == self || outward is Vector2 across && walks[j].Stair && Mathf.Abs(Vector2.Dot(walks[j].Dir, across)) < 0.5f) continue;
            if (WalkedOn(walks[j], p, height)) return true;
        }
        return false;
    }

    /// <summary>Whether a walk at <paramref name="height"/> carries on onto <paramref name="w"/> at <paramref name="p"/> (the
    /// one test, for every edge read): <see cref="StairEnds.Walks"/>, the stair-end rule, its walking surface within
    /// <see cref="StairLayout.TopFloorBand"/>. A floor beside a higher or lower piece is railed, as against a wall. The one owner of is this edge walked across (rails, stair ends) (docs/OWNERS.md).</summary>
    private static bool WalkedOn(StairSupport.Walk w, Vector2 p, float height, float margin = 0.05f) =>
        StairEnds.Walks(w.Back, w.Dir, w.Right, w.Depth, w.Across, w.Walking(0f), w.Stair, p, height, margin);

    /// <summary>Corner <paramref name="side"/> (-1 left, +1 right) at the back or <paramref name="front"/> of walking piece
    /// <paramref name="w"/>, where a post stands, and the height it stands on: a stair's at its foot behind and a rise up in
    /// front, a floor's at its walking surface.</summary>
    private static (Vector2 at, float bottom) Corner(StairSupport.Walk w, int side, bool front, StairKit kit) =>
        (w.At(front ? w.Depth : 0f, side * w.Across / 2f),
         !w.Stair ? w.Surface(0f, kit) : front ? w.Underside + StairLayout.PieceRise : w.Underside);

    /// <summary>The four corners of walking piece <paramref name="w"/> (<see cref="Corner"/>).</summary>
    private static IEnumerable<(Vector2 at, float bottom)> Corners(StairSupport.Walk w, StairKit kit)
    {
        foreach (int side in new[] { -1, 1 })
        foreach (bool front in new[] { false, true })
            yield return Corner(w, side, front, kit);
    }

    /// <summary>The one key of a post's spot: where it stands and the height it stands on, to a tenth of a metre.</summary>
    private static (int, int, int) Key(Vector2 at, float bottom) =>
        (Mathf.RoundToInt(at.x * 10f), Mathf.RoundToInt(at.y * 10f), Mathf.RoundToInt(bottom * 10f));

    /// <summary>A 1 m post standing on <paramref name="bottom"/> at <paramref name="at"/>, square to its piece
    /// (<paramref name="yaw"/>), once per spot: where two pieces share one (a joint between two stairs, a flight's foot
    /// on a floor) it is square to the upper, the piece walked on higher (<paramref name="rank"/>, its middle).</summary>
    private static void Post(Dictionary<(int, int, int), Spot> posts, int owner, Vector2 at, float bottom, float yaw, float rank)
    {
        var key = Key(at, bottom);
        if (!posts.TryGetValue(key, out Spot? spot)) posts[key] = spot = new Spot { At = at, Bottom = bottom, Yaw = yaw, Rank = rank };
        else if (rank > spot.Rank) { spot.Yaw = yaw; spot.Rank = rank; }
        spot.Owners.Add(owner);
    }

    /// <summary>A rail at <paramref name="at"/> (its origin's height <paramref name="y"/>: a sloped one's low end), laid; one
    /// wholly in the ground is left out with the staircase's where it stands without it (<see cref="Lay"/>, BuriedPieces.Omit,
    /// the one owner: a lower rail along a step resting on a slope lies half out of it, and is laid, as laid by hand down a
    /// last flight).</summary>
    private static StructurePiece Rail(List<StructurePiece> laid, string prefab, Vector2 at, float y, float yaw, Func<int, int, float> finished)
    {
        var rail = new StructurePiece { Kind = StructurePieceKind.Beam, Prefab = prefab, Position = new Vector3(at.x, y, at.y), YawDegrees = yaw };
        laid.Add(rail);
        return rail;
    }

    /// <summary>Where a switchback's two legs share a line, the lower leg's handrail and the upper leg's lower rail can stand
    /// at one spot and height sloping opposite ways, crossed: one <see cref="CrossedRailPrefab"/> stands in for the two, turned
    /// as the upper leg's (in game: at -1905.5,186.9,6609.1, yaw 202.5, between the lower leg's rail under it and the upper
    /// leg's over it). Each sloped rail with its piece's underside.</summary>
    private static void Cross(List<StructurePiece> laid, List<(StructurePiece rail, float rank)> sloped)
    {
        var gone = new HashSet<StructurePiece>();
        for (int a = 0; a < sloped.Count; a++)
        for (int b = a + 1; b < sloped.Count; b++)
        {
            var (x, xRank) = sloped[a];
            var (y, yRank) = sloped[b];
            if (gone.Contains(x) || gone.Contains(y) || Vector3.Distance(x.Position, y.Position) > 0.05f
                || Apart(x.YawDegrees, y.YawDegrees) < 179f) continue;
            gone.Add(x);
            gone.Add(y);
            StructurePiece upper = yRank > xRank ? y : x;
            laid.Add(new StructurePiece { Kind = StructurePieceKind.Beam, Prefab = CrossedRailPrefab, Position = upper.Position, YawDegrees = upper.YawDegrees });
        }
        laid.RemoveAll(gone.Contains);
    }
}
