using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Every staircase end has a way off it (StairEnds, the one end reader; StairLayout.FinishTop, the one finisher): four ends
/// as the game laid them, on the ground read in game (Fixtures/stair-ends-a..d.txt), and one walkable staircase on the
/// finished ground predicted offline (stair-ends-e.txt). Thresholds are the layout's own (StairLayout.StepUp, TopFloorBand,
/// PieceRun, StairRailing.LedgeDrop): the rule is read from them, never from numbers written here.
/// </summary>
[Collection("StairLayout")]
public class StairEndsTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public StairEndsTests() { RoadSpatialGrid.Clear(); StairStructures.Reset(); }
    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    private sealed class Case
    {
        public readonly StairRun Run = new();
        public readonly Dictionary<(int, int), float> Vertices = new();
        /// <summary>The place the staircase comes onto, as the game placed it (PlacedSites.Write's rows), where the case has one.</summary>
        public readonly List<string> Site = new();
        /// <summary>The place's ground where the game was not read (its preload rebuilt offline), where the case has it.</summary>
        public readonly Dictionary<(int, int), float> Placed = new();
        /// <summary>The network's road points stored round the staircase, where the case has them.</summary>
        public readonly List<(Vector2 at, float width, float height)> Roads = new();

        /// <summary>Its road points stored in the network (RoadSpatialGrid), each at <paramref name="height"/>'s height.</summary>
        public void StoreRoads(Func<Vector2, float, float> height)
        {
            var points = Roads.ConvertAll(r => r.at);
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, Roads.ConvertAll(r => height(r.at, r.height)),
                Roads.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 0f, false) { Widths = Roads.ConvertAll(r => r.width) });
        }

        /// <summary>The ground read in game, else the place's (<see cref="Placed"/>); NaN off both.</summary>
        public float PlaceGround(float x, float z)
        {
            float V(int i, int k) => Vertices.TryGetValue((i, k), out float h) ? h : Placed.TryGetValue((i, k), out h) ? h : float.NaN;
            return BuriedPieces.MeshHeight(x, z, V);
        }

        /// <summary>The read ground between its vertices as the game's mesh spreads it; NaN off what was read.</summary>
        public float Ground(float x, float z)
        {
            float V(int i, int k) => Vertices.TryGetValue((i, k), out float h) ? h : float.NaN;
            return BuriedPieces.MeshHeight(x, z, V);
        }
    }

    private static Case Load(string name)
    {
        var c = new Case();
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Trim() == "") continue;
            string[] f = line.Split(',');
            switch (f[0])
            {
                case "run":
                    c.Run.From = new Vector2(F(f[1]), F(f[2])); c.Run.To = new Vector2(F(f[3]), F(f[4]));
                    c.Run.Width = F(f[5]); c.Run.KeepsSiteRocks = f[6] == "True";
                    break;
                case "meets": c.Run.FromMeet = new Vector2(F(f[1]), F(f[2])); c.Run.ToMeet = new Vector2(F(f[3]), F(f[4])); break;
                case "piece":
                    c.Run.Pieces.Add(new StructurePiece
                    {
                        Kind = (StructurePieceKind)Enum.Parse(typeof(StructurePieceKind), f[1]), Prefab = f[2],
                        Position = new Vector3(F(f[3]), F(f[4]), F(f[5])), YawDegrees = F(f[6]),
                    });
                    break;
                case "ground": c.Vertices[((int)F(f[1]), (int)F(f[2]))] = F(f[3]); break;
                case "placed": c.Placed[((int)F(f[1]), (int)F(f[2]))] = F(f[3]); break;
                case "road": c.Roads.Add((new Vector2(F(f[1]), F(f[2])), F(f[3]), F(f[4]))); break;
                case "site": case "op": case "rock": case "top": c.Site.Add(line); break;
            }
        }
        return c;
    }

    private static StairEnds.End TopOf(Case c) => StairEnds.Read(c.Run, c.Ground).Single(e => e.Top);
    private static StairEnds.End FootOf(Case c) => StairEnds.Read(c.Run, c.Ground).Single(e => !e.Top);

    /// <summary>The staircase as a plan climbing to its top (its pieces in the order laid, foot first), for the finisher.</summary>
    private static StairPlan PlanOf(Case c)
    {
        var plan = new StairPlan { Kit = c.Run.KitName, Reached = true };
        foreach (StructurePiece p in c.Run.Pieces)
            plan.Pieces.Add(new StairPiece
            {
                Kind = p.Kind switch { StructurePieceKind.StairStep => StairPieceKind.Stair, StructurePieceKind.StairFloor => StairPieceKind.Landing, _ => StairPieceKind.Filler },
                Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees,
            });
        StairLayout.TerminalExit exit = StairLayout.Terminal(plan)!;
        plan.EndHeading = StairLayout.Wrap(exit.Pieces[0].YawDegrees + 180f);
        exit = StairLayout.Terminal(plan)!;
        plan.End = exit.Edge;
        return plan;
    }

    /// <summary>A straight flight walking +z from a foot at z = 0 (stairs <paramref name="first"/> rises from -1 m, each a
    /// run long), as laid: foot first.</summary>
    private static StairRun Flight(int first, int stairs)
    {
        var run = new StairRun { From = new Vector2(0f, first * StairLayout.PieceRun), To = new Vector2(0f, (first + stairs) * StairLayout.PieceRun), Width = 2f };
        for (int i = first; i < first + stairs; i++)
            run.Pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", YawDegrees = 180f,
                Position = new Vector3(0f, i * StairLayout.PieceRise, (i + 0.5f) * StairLayout.PieceRun),
            });
        return run;
    }

    /// <summary>A foot's bottom stair level with the ground in front of it, the ground under the flight cut to just under its
    /// walk (StairStructures.CutFloor): that stair is walked, not sunk, and the foot steps off it onto the ground (read as
    /// sunk, the foot was read off the stair above it, a rise over the ground). And a foot over a stair sunk on purpose a
    /// rise under the ground, the ground falling away past it: the walker steps off at the sunk stair's high edge, onto the
    /// ground there (read a floor's size out, over the sunk stair's own low end, it was a gap).</summary>
    [Fact]
    public void AFootIsReadOffItsLowestWalkedStairAndOverItsSunkOnesAtTheirHighEdge()
    {
        float Cut(float z) => z * StairLayout.PieceRise / StairLayout.PieceRun - StairLayout.StepUp / 2f;
        StairEnds.End foot = StairEnds.Read(Flight(0, 3), (x, z) => z <= 0f ? 0f : Cut(z)).Single(e => !e.Top);
        Assert.True(foot.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(foot));
        Assert.Equal(0f, foot.Edge!.Value.y, 2);

        // A sunk stair from z = -2 to 0 (its low edge a rise under the ground at the flight's foot), the ground falling past it
        // a half rise over its length: more than a step, less than a rise, so the sunk stair's own low edge is in the ground.
        float fall = StairLayout.PieceRise / 2f;
        Assert.True(fall > StairLayout.StepUp && fall < StairLayout.PieceRise);
        float Ground(float x, float z) => z <= 0f ? fall * z / StairLayout.PieceRun : Cut(z);
        StairEnds.End over = StairEnds.Read(Flight(-1, 4), Ground).Single(e => !e.Top);
        Assert.True(over.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(over));
        Assert.Equal(0f, over.Edge!.Value.y, 2);
    }

    [Fact]
    public void TheFourEndsLeftWithNoWayOffAreReadSo()
    {
        foreach (string name in new[] { "stair-ends-a.txt", "stair-ends-b.txt", "stair-ends-c.txt" })
        {
            StairEnds.End top = TopOf(Load(name));
            Assert.True(top.Verdict == StairEnds.Verdict.Miss, $"{name}: {StairEnds.Line(top)}");
            Assert.Equal(0, top.FloorsPast + top.StairsPast);
        }
        StairEnds.End foot = FootOf(Load("stair-ends-d.txt"));
        Assert.True(foot.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(foot));
        // Over its road by more than a ledge: the rule's step back, at both sides of the floor it would take.
        Assert.Equal(StairEnds.Case.StepBack, foot.Rule);
        Assert.True(foot.Left > StairRailing.LedgeDrop && foot.Right > StairRailing.LedgeDrop, StairEnds.Line(foot));
    }

    [Fact]
    public void AWalkableStaircaseIsReadOkAtBothEnds()
    {
        Case c = Load("stair-ends-e.txt");
        foreach (StairEnds.End e in StairEnds.Read(c.Run, c.Ground))
            Assert.True(e.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(e));
        StairEnds.End top = TopOf(c);
        Assert.Equal(1, top.FloorsPast);
        // A floor laid off the top where its far corners are within a step: the rule's floor.
        Assert.Equal(StairEnds.Case.Floor, top.Rule);
    }

    /// <summary>The ground 1-2.5 m past the top stood over the top floor's band, so no floor, and 4.5 m on within it, so no
    /// crest, two reads of the ground for one decision: the one reader reads the top short, it climbs on and the rule there
    /// lays a stair down off the stair climbed, the crest laid by hand there.</summary>
    [Fact]
    public void ATopUnderAShoulderTakesTheFloorOrTheCrestNeverNeither()
    {
        Case c = Load("stair-ends-a.txt");
        StairPlan plan = PlanOf(c);
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(plan, c.Ground, RoadStairRuns.Width));
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, c.Ground, RoadStairRuns.Width, StairCover.ApproachRefusal, _ => true, out _, down: true);
        Assert.Equal(StairLayout.TopFinish.Crest, finish);
        var run = StairRun.FromPlan(c.Run.From, c.Run.To, RoadStairRuns.Width, plan);
        StairEnds.End end = StairEnds.Read(run, c.Ground).Single(e => e.Top);
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(end));
    }

    /// <summary>An approach's top over ground level with it, whose floor the approach's own rules refused: the end reader
    /// reads it a miss, so the layout is refused and the next aim tried (RoadStairRuns.TopEnd); with a floor that fits, the
    /// rule's floor, it walks off.</summary>
    [Theory]
    [InlineData("stair-ends-b.txt")]
    [InlineData("stair-ends-c.txt")]
    public void AnApproachTopWithNoFinishFittingIsRefusedAndAFloorThatFitsWalksOff(string name)
    {
        Case c = Load(name);
        StairPlan refused = PlanOf(c);
        Assert.Equal(StairLayout.TopFinish.None, RoadStairRuns.FinishOverLedge(refused, c.Ground, RoadStairRuns.Width, _ => false));
        Assert.Equal(StairEnds.Verdict.Miss, RoadStairRuns.TopEnd(refused, c.Ground)!.Verdict);
        StairPlan finished = PlanOf(c);
        StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(finished, c.Ground, RoadStairRuns.Width, _ => true);
        Assert.NotEqual(StairLayout.TopFinish.None, finish);
        StairEnds.End end = RoadStairRuns.TopEnd(finished, c.Ground)!;
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, $"{finish}: {StairEnds.Line(end)}");
    }

    /// <summary>A road staircase's foot whose first walked piece stands over its road's end (the road cut under it after it
    /// was laid out): the commit refuses the road, never keeps the staircase hanging over it. Its road's end read where the
    /// road stored it: at the ring it was laid against, or (in game, <paramref name="asStored"/>) where its road met it, 4.0 m
    /// off that ring's centre and so outside the ring's own disc, where the refusal never fired.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFootOverItsRoadRefusesTheRoad(bool asStored)
    {
        Case c = Load("stair-ends-d.txt");
        StairEnds.End foot = FootOf(c);
        Vector3 low = foot.Edge!.Value;
        // The road where it ends, as read in game: more than a ledge under the foot's low edge.
        Vector2 ringAt = c.Run.To;
        Vector2 end = asStored ? c.Run.ToMeet!.Value : ringAt;
        float road = c.Ground(ringAt.x, ringAt.y);
        Assert.True(low.y - road > StairRailing.LedgeDrop);
        var ring = new RoadEndRing(ringAt, 0f, road);
        Assert.True(Vector2.Distance(end, ringAt) <= ring.StartReach, "its road ends where the staircase still starts");
        Assert.Equal(asStored, Vector2.Distance(end, ringAt) > ring.RoadsEnd);
        var plan = new StairPlan { Kit = c.Run.KitName, Reached = true, Ring = ring, RingStart = new Vector2(low.x, low.z),
            Foot = new Vector3(low.x, road, low.z) };
        foreach (StructurePiece p in c.Run.Pieces.AsEnumerable().Reverse())
            plan.Pieces.Add(new StairPiece
            {
                Kind = p.Kind switch { StructurePieceKind.StairStep => StairPieceKind.Stair, StructurePieceKind.StairFloor => StairPieceKind.Landing, _ => StairPieceKind.Filler },
                Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees,
            });
        var span = new RoadStairSpan { Plan = plan };
        // The road arriving at its end there.
        var roads = new List<RoadSpatialGrid.PlannedPath> { new(new List<Vector2> { end + new Vector2(10f, 0f), end }, new List<float> { road, road },
            new List<RoadPointDebugInfo> { default, default }, RoadNetworkGenerator.RoadWidth, 0f, false) };
        Assert.Equal(RoadStairRuns.FootOverItsRoad, RoadStairRuns.FootLeftOver(new[] { span }, roads, out Vector2 at));
        Assert.Equal(ringAt, at);
        // The road at its foot's level: no refusal.
        roads[0].Heights[0] = roads[0].Heights[1] = low.y;
        Assert.Null(RoadStairRuns.FootLeftOver(new[] { span }, roads, out _));
    }

    /// <summary>A road staircase whose foot has no road (laid offline on RoadSeedD where the road from the island's start
    /// set off with a staircase at its first point, on a shore falling faster than a flight): the walker steps off it onto
    /// the ground or nowhere. The one reader finds no edge stepping off, more than a ledge over the ground off its last
    /// stair, and the commit refuses the road (FootLeftOver), as it does a foot over its road. A foot with a road there is
    /// judged by its road, not by this rule; and on ground up to just under its low edge the walker steps off: no refusal.</summary>
    [Fact]
    public void AFootWithNoRoadOverTheGroundRefusesTheRoad()
    {
        Case c = Load("stair-foot-no-road.txt");
        StairEnds.End foot = FootOf(c);
        Assert.True(foot.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(foot));
        Assert.True(foot.Left > StairRailing.LedgeDrop && foot.Right > StairRailing.LedgeDrop, StairEnds.Line(foot));
        Vector3 low = foot.Edge!.Value;
        var span = new RoadStairSpan { Plan = PlanOf(c), FromPoint = c.Run.From, ToPoint = c.Run.To, NoRoadAtFoot = true };
        var none = new List<RoadSpatialGrid.PlannedPath>();
        Assert.Equal(RoadStairRuns.FootOverTheGround, RoadStairRuns.FootLeftOver(new[] { span }, none, out Vector2 at, c.Ground));
        Assert.True(Vector2.Distance(at, new Vector2(low.x, low.z)) < 0.01f, $"refused at {at}, its foot's edge at {low}");
        // Its road at its foot: judged by its road (a ring), not here.
        span.NoRoadAtFoot = false;
        Assert.Null(RoadStairRuns.FootLeftOver(new[] { span }, none, out _, c.Ground));
        // The ground off it raised to just under its low edge (within a step): stepped off, no refusal.
        span.NoRoadAtFoot = true;
        Func<float, float, float> raised = (x, z) => Mathf.Max(c.Ground(x, z), low.y - StairLayout.StepUp / 2f);
        Assert.True(StairEnds.Read(c.Run, raised).Single(e => !e.Top).Verdict == StairEnds.Verdict.Ok);
        Assert.Null(RoadStairRuns.FootLeftOver(new[] { span }, none, out _, raised));
        // Against a bank instead (the ground off its low edge two rises over it): short, refused; with the level apron cut for
        // it there (StairLayout.CutFootApron, as Lay cuts one), read on the ground as its own cuts leave it: kept (read
        // without them, a foot onto its apron at an island's start on RoadSeedA was refused).
        StairPiece bottom = span.Plan.Pieces.First(p => p.Kind == StairPieceKind.Stair);
        float heading = StairLayout.Footprint(bottom).heading;
        Vector2 edge = new(low.x, low.z), outward = -StairLayout.Forward(heading);
        Func<float, float, float> bank = (x, z) => Vector2.Dot(new Vector2(x, z) - edge, outward) > 0f ? low.y + 2f * StairLayout.PieceRise : c.Ground(x, z);
        Assert.Equal(RoadStairRuns.FootOverTheGround, RoadStairRuns.FootLeftOver(new[] { span }, none, out _, bank));
        span.Plan.Foot = low;
        span.Plan.StartHeading = heading;
        Assert.True(StairLayout.CutFootApron(span.Plan, bank, RoadStairRuns.Width) > 0, "no apron cut against the bank");
        Assert.Null(RoadStairRuns.FootLeftOver(new[] { span }, none, out _, bank));
    }

    /// <summary>A road staircase's foot lowered toward the ground back along its road, its road then built higher: its lowest
    /// stair wholly under the road (more than a rise), a pit with the road's bank standing over it a floor's size out. The one
    /// reader reads it so (read half a metre out, the ground there in the pit, it was read a walkable foot), and the commit
    /// refuses the road, as it does a foot over its road.</summary>
    [Fact]
    public void AFootUnderItsRoadIsAGapAndRefusesTheRoad()
    {
        Case c = Load("stair-ends-f.txt");
        StairEnds.End foot = FootOf(c);
        Assert.True(foot.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(foot));
        Assert.Equal(StairEnds.Case.Short, foot.Rule);
        Assert.True(foot.Left < -StairLayout.PieceRise && foot.Right < -StairLayout.PieceRise, StairEnds.Line(foot));
        // Its road where it ends, as built in game: its height the ground's there (levelled to it).
        Vector2 end = c.Run.FromMeet!.Value;
        float road = c.Ground(end.x, end.y);
        Vector3 low = foot.Edge!.Value;
        Assert.True(road - low.y > StairLayout.PieceRise);
        var span = new RoadStairSpan { Plan = FootPlan(c, end, road, low) };
        var roads = new List<RoadSpatialGrid.PlannedPath> { new(new List<Vector2> { end + new Vector2(0f, 10f), end }, new List<float> { road, road },
            new List<RoadPointDebugInfo> { default, default }, RoadNetworkGenerator.RoadWidth, 0f, false) };
        Assert.Equal(RoadStairRuns.FootUnderItsRoad, RoadStairRuns.FootLeftOver(new[] { span }, roads, out _));
        // The road at its foot's low edge: no refusal; within a rise over it (its slope meets the road): none either.
        roads[0].Heights[0] = roads[0].Heights[1] = low.y;
        Assert.Null(RoadStairRuns.FootLeftOver(new[] { span }, roads, out _));
        roads[0].Heights[0] = roads[0].Heights[1] = low.y + StairLayout.PieceRise;
        Assert.Null(RoadStairRuns.FootLeftOver(new[] { span }, roads, out _));
    }

    /// <summary>
    /// Read in game: an approach's foot onto its road's end, the road falling away from it at a pitch's grade (50 % for its
    /// last 8 m, steeper live), was read "onto its road": only the step off it onto the level start pad was judged. Judged
    /// against the road's grade past the pad, over a level landing's length (RoadPitches.JunctionLanding), it is a gap past the
    /// grade cap. The same road levelled there for that length, as a road's end where a staircase starts is, is onto its road.
    /// </summary>
    [Fact]
    public void AFootOntoItsRoadFallingAwayPastTheCapIsAGap()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        Case c = Load("stair-start-road-grade.txt");
        c.StoreRoads((_, h) => h);
        StairEnds.End foot = FootOf(c);
        Assert.True(foot.OntoRoad, StairEnds.Line(foot));
        Assert.True(foot.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(foot) + $" grade {foot.OntoGrade}");
        Assert.Contains("onto its road at", foot.Why);

        // Levelled at the foot's road end for a landing's length and a pad's reach (RoadEndRing.StartReach) past it.
        RoadSpatialGrid.Clear();
        Vector2 end = c.Run.From;
        float level = c.Roads.OrderBy(r => Vector2.Distance(r.at, end)).First().height;
        float reach = RoadPitches.JunctionLanding + new RoadEndRing(end, 0f, level).StartReach;
        c.StoreRoads((at, h) => Vector2.Distance(at, end) <= reach ? level : h);
        foot = FootOf(c);
        Assert.True(foot.Verdict == StairEnds.Verdict.Ok && foot.OntoRoad, StairEnds.Line(foot));
    }

    /// <summary>A plan for a road staircase's foot set off from a ring at its road's end (<paramref name="end"/>, the road at
    /// <paramref name="road"/>), its pieces the case's.</summary>
    private static StairPlan FootPlan(Case c, Vector2 end, float road, Vector3 low)
    {
        var ring = new RoadEndRing(end, 0f, road);
        var plan = new StairPlan { Kit = c.Run.KitName, Reached = true, Ring = ring, RingStart = new Vector2(low.x, low.z),
            Foot = new Vector3(low.x, road, low.z) };
        foreach (StructurePiece p in c.Run.Pieces)
            plan.Pieces.Add(new StairPiece
            {
                Kind = p.Kind switch { StructurePieceKind.StairStep => StairPieceKind.Stair, StructurePieceKind.StairFloor => StairPieceKind.Landing, _ => StairPieceKind.Filler },
                Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees,
            });
        return plan;
    }

    /// <summary>A switchback whose upper flights pass back by its lower ones: its top is where its walk ends (the highest
    /// stairs, at the point the end was recorded), not a stair mid-flight that lies past that point.</summary>
    [Fact]
    public void ASwitchbacksTopIsWhereItsWalkEnds()
    {
        Case c = Load("stair-ends-g.txt");
        StairEnds.End top = TopOf(c);
        Vector3 edge = top.Edge!.Value;
        float highest = c.Run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).Max(p => p.Position.y);
        Assert.True(edge.y >= highest, StairEnds.Line(top));
        Assert.True(Vector2.Distance(new Vector2(edge.x, edge.z), c.Run.ToMeet!.Value) <= 2f * StairLayout.PieceRun, StairEnds.Line(top));
        Assert.True(FootOf(c).Edge!.Value.y < edge.y - StairLayout.PieceRise);
    }

    /// <summary>A top that turns on a turn's fillers onto a stair down: the fillers are the turn, not floors laid past it.</summary>
    [Fact]
    public void AStairDownOffATurnIsReadAsOneStairDown()
    {
        StairEnds.End top = TopOf(Load("stair-ends-h.txt"));
        Assert.Equal((0, 1), (top.FloorsPast, top.StairsPast));
        Assert.Equal("1 stair down", top.Laid);
        Assert.True(top.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(top));
    }

    /// <summary>A top under the ground past it by more than a step but less than a rise was crested, two stairs, its stair
    /// down buried in the ground level with the stair climbed: by the rule, short, it climbs one stair and the ground off
    /// that one is within a step, so a floor.</summary>
    [Fact]
    public void AShortTopClimbsOnUntilTheRuleLaysItsFinish()
    {
        Case c = Load("stair-ends-i.txt");
        // As laid before its finish: the crest's two stairs, the last two pieces laid, taken off.
        Assert.All(c.Run.Pieces.Skip(c.Run.Pieces.Count - 2), p => Assert.Equal(StructurePieceKind.StairStep, p.Kind));
        c.Run.Pieces.RemoveRange(c.Run.Pieces.Count - 2, 2);
        StairPlan plan = PlanOf(c);
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(plan, c.Ground, RoadStairRuns.Width));
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, c.Ground, RoadStairRuns.Width, StairCover.ApproachRefusal, _ => true, out bool climbed, down: true);
        Assert.Equal(StairLayout.TopFinish.TopFloor, finish);
        Assert.True(climbed);
        var run = StairRun.FromPlan(c.Run.From, c.Run.To, RoadStairRuns.Width, plan);
        StairEnds.End end = StairEnds.Read(run, c.Ground).Single(e => e.Top);
        Assert.True(end.Verdict == StairEnds.Verdict.Ok && end.Rule == StairEnds.Case.Floor && end.FloorsPast == 1, StairEnds.Line(end));
    }

    /// <summary>An approach top onto a cave's shelf (Hildir_cave -2880,-5440), short by more than a step: it climbs one stair,
    /// over the ground by more than a step there, and the crest's stair down off it stands where a floor off that stair would
    /// (StairEnds.Corners, a piece's size out). The shelf's lip lets the floor a top finishes on stand on past it by its own
    /// depth (RoadStairRuns.PastTheLip); the stair down had nothing, so the crest was refused "too far onto the flat" and the
    /// top was laid a bare stair over the shelf, read in game. The lip read as the approach's search reads it
    /// (PlacedSiteShape.ConnectedLanding), on the ground read in game, ground not read never level with the shelf.</summary>
    [Fact]
    public void AShortTopOntoAShelfIsCrestedWithinItsLipAsAFloorWouldBe()
    {
        Case c = Load("stair-ends-j.txt");
        StairEnds.End laid = TopOf(c);
        Assert.True(laid.Verdict == StairEnds.Verdict.Miss && laid.Rule == StairEnds.Case.Reverse, StairEnds.Line(laid));
        // Before its finish: the stair it climbed on, the last piece laid, taken off; short there.
        Assert.Equal(StructurePieceKind.StairStep, c.Run.Pieces[c.Run.Pieces.Count - 1].Kind);
        c.Run.Pieces.RemoveAt(c.Run.Pieces.Count - 1);
        StairPlan plan = PlanOf(c);
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(plan, c.Ground, RoadStairRuns.Width));
        PlacedSiteRecord r = PlacedSites.Read(string.Join("\n", c.Site)).Single();
        float Read(float x, float z) => c.PlaceGround(x, z) is var h && !float.IsNaN(h) ? h : float.NegativeInfinity;
        PlacedSiteShape shape = PlacedSiteShape.Of(r.Centre, r.Yaw, RoadStairRuns.FlatOf(r.Ops)!.Value, r.RockRadius, r.Rocks, Read,
            r.KeepSmallRocks, r.Root, r.Prefab);
        PlacedSiteShape.LandingRegion landing = shape.ConnectedLanding(Read, 1f);
        StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(plan, c.Ground, RoadStairRuns.Width, p => !RoadStairRuns.PastTheLip(p, landing.Depth));
        StairEnds.End end = RoadStairRuns.TopEnd(plan, c.Ground)!;
        Assert.True(finish == StairLayout.TopFinish.Crest && end.Verdict == StairEnds.Verdict.Ok, $"{finish}: {StairEnds.Line(end)}");
    }

    /// <summary>A road's staircase down onto a stone tower's landing (StoneTowerRuins05, -1916.8,6724.3), its top on its road's
    /// end's ring (-1867.8,6733.9, the road arriving 226.8): its last stair turned square onto a floor whose edge toward the road
    /// meets it level, and the search laid its top landing row on past that floor, a second 2x2, redundant (read in game). The
    /// walk ending on a floor met past its edge by the one reader (StairEnds.Lands, its own far corners, the road's end on the
    /// ground) is finished: no further floor row is laid (StairLayout.FloorMet, the top landings' stop). The staircase as the
    /// game saved it and its finished ground (stair-ends-l.txt).</summary>
    [Fact]
    public void ATopEndingOnAFloorMetByItsRoadLaysNoSecondFloor()
    {
        Case c = Load("stair-ends-l.txt");
        StairEnds.End laid = TopOf(c);
        Assert.True(laid.Verdict == StairEnds.Verdict.Ok && laid.FloorsPast == 2, StairEnds.Line(laid));
        // The two floors past the last stair, in walking order up to the road: the turned floor, then the row laid on past it.
        var floors = c.Run.Pieces.Where(p => p.Kind == StructurePieceKind.StairFloor && p.Position.y > 291f).ToList();
        Assert.Equal(2, floors.Count);
        StructurePiece first = floors.OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), c.Run.From)).Last();
        float heading = first.YawDegrees, height = first.Position.y + StairKit.Stone.FloorTop;
        Vector2 edge = new Vector2(first.Position.x, first.Position.z) + StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f);
        // Met by the road's end as the search reads it (the ring's ground, nothing else known) and on the finished ground.
        var ring = new RoadEndRing(new Vector2(-1867.8f, 6733.9f), 226.8f, height);
        Assert.True(StairLayout.FloorMet(StairPieceKind.Landing, edge, heading, height, RoadStairRuns.Width, ring.RoadsGround((x, z) => float.NaN)));
        Assert.True(StairLayout.FloorMet(StairPieceKind.Landing, edge, heading, height, RoadStairRuns.Width, c.Ground));
        // A stair's edge is never a floor met: the top landing row is still laid off a climbing stair.
        Assert.False(StairLayout.FloorMet(StairPieceKind.Stair, edge, heading, height, RoadStairRuns.Width, c.Ground));
        // Laid without the second floor, the top is walked off its floor onto the road's level ground.
        c.Run.Pieces.Remove(floors.Single(p => p != first));
        StairEnds.End one = TopOf(c);
        Assert.True(one.Verdict == StairEnds.Verdict.Ok && one.FloorsPast == 1, StairEnds.Line(one));
    }

    /// <summary>The rule lays exactly one finish per case, read off the top at the floor's far corners, whatever the step
    /// (<see cref="StairLayout.StepUp"/>) and the ledge (<see cref="StairRailing.LedgeDrop"/>): a floor within a step under
    /// it; a stair down onto ground up to a ledge under it (an approach's top only); short, a stair climbed on and then the
    /// rule there: within a step of the stair climbed a floor, else its stair down (the crest).</summary>
    [Theory]
    [InlineData("floor")]
    [InlineData("reverse")]
    [InlineData("crest")]
    [InlineData("climbed floor")]
    public void TheRuleLaysOneFinishPerCase(string which)
    {
        const float top = 50f;
        float ground = which switch
        {
            "floor" => top - StairLayout.StepUp / 2f,
            "reverse" => top - (StairLayout.StepUp + StairRailing.LedgeDrop) / 2f,
            "crest" => top + StairLayout.PieceRise - (StairLayout.StepUp + StairRailing.LedgeDrop) / 2f,
            _ => top + StairLayout.PieceRise - StairLayout.StepUp / 2f,
        };
        var expected = which switch
        {
            "floor" => StairLayout.TopFinish.TopFloor,
            "reverse" => StairLayout.TopFinish.LandingThenDown,
            "crest" => StairLayout.TopFinish.Crest,
            _ => StairLayout.TopFinish.TopFloor,
        };
        StairPlan Plan() => new() { Kit = StairKit.Stone.Name, Reached = true, EndHeading = 0f, End = new Vector3(0f, top, 0f) };
        StairPlan plan = Plan();
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, (_, _) => ground, RoadStairRuns.Width, StairCover.RoadStaircase, _ => true, out bool climbed, down: true);
        Assert.Equal(expected, finish);
        Assert.Equal(which is "crest" or "climbed floor", climbed);
        // A road's top (no stair down off it as laid) takes the floor and the crest, never a stair down off its own top.
        StairLayout.TopFinish road = StairLayout.FinishTop(Plan(), (_, _) => ground, RoadStairRuns.Width, StairCover.RoadStaircase, _ => true, out _);
        Assert.Equal(which == "reverse" ? StairLayout.TopFinish.None : expected, road);
    }

    /// <summary>A straight flight of <paramref name="stairs"/> climbing north from (0, 0, 0), as the layout lays it, its top at its
    /// last stair's exit edge.</summary>
    private static StairPlan Flight(int stairs)
    {
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, EndHeading = 0f };
        for (int k = 0; k < stairs; k++)
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair",
                Position = new Vector3(0f, k * StairLayout.PieceRise, (k + 0.5f) * StairLayout.PieceRun), YawDegrees = 180f });
        plan.End = new Vector3(0f, stairs * StairLayout.PieceRise, stairs * StairLayout.PieceRun);
        return plan;
    }

    /// <summary>Over the ledge (StairEnds.Case.StepBack) a top steps back stair by stair until the floor off the stair then last
    /// lands, and lays it ("keep stepping back until the floor lands"): a flight overshooting a ledge by 2.5 m, the ground
    /// falling away gently past the lip, the floor off the stair one back still over the ledge, off the stair two back level
    /// with the ledge.</summary>
    [Fact]
    public void OverTheLedgeATopStepsBackUntilItsFloorLands()
    {
        StairPlan plan = Flight(6);
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        // The ledge level with the third stair's top (4 rises up) from 4 runs to 5.25 runs on, falling 0.5 m over the 1.75 runs past it.
        float Ground(float x, float z) => z < 4f * run ? z / run * rise : z <= 5.25f * run ? 4f * rise : 4f * rise - (z - 5.25f * run) / (1.75f * run) * 0.5f;
        Assert.Equal(StairEnds.Case.StepBack, StairLayout.TopCase(plan, Ground, RoadStairRuns.Width));
        // One stair back, its floor still over the ledge: today's single step back laid nothing there.
        Assert.False(StairEnds.Lands(new Vector2(0f, 5f * run), new Vector2(0f, 1f), 5f * rise, RoadStairRuns.Width, Ground));
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, Ground, RoadStairRuns.Width, StairCover.ApproachRefusal, _ => true, out _, down: true);
        Assert.Equal(StairLayout.TopFinish.StepBack, finish);
        Assert.Equal(4, plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair));
        StairPiece floor = plan.Pieces.Single(p => p.Kind == StairPieceKind.Landing);
        Assert.Equal(4f * rise, StairLayout.Footprint(floor).height, 3);
        Assert.Equal(new Vector3(0f, 4f * rise, 4f * run), plan.End);
    }

    /// <summary>Stepping back never goes past the staircase's own walk: no floor landing off any stair back to its first, the
    /// top stays as laid (today's finish) and says why.</summary>
    [Fact]
    public void ATopThatSteppedBackToItsFirstStairKeepsItsFinish()
    {
        StairPlan plan = Flight(3);
        int pieces = plan.Pieces.Count;
        float Ground(float x, float z) => -10f;
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, Ground, RoadStairRuns.Width, StairCover.ApproachRefusal, _ => true, out _, down: true);
        Assert.Equal(StairLayout.TopFinish.None, finish);
        Assert.Equal(pieces, plan.Pieces.Count);
        Assert.Contains("first stair", StairLayout.LastStepBack);
    }

    [Fact]
    public void TheRuleIsReadFromTheLayoutsOwnThresholds()
    {
        Assert.Equal(StairEnds.Case.Floor, StairEnds.CaseOf(StairLayout.StepUp));
        Assert.Equal(StairEnds.Case.Floor, StairEnds.CaseOf(-StairLayout.StepUp));
        Assert.Equal(StairEnds.Case.Short, StairEnds.CaseOf(-StairLayout.StepUp - 0.01f));
        Assert.Equal(StairEnds.Case.Reverse, StairEnds.CaseOf(StairLayout.StepUp + 0.01f));
        Assert.Equal(StairEnds.Case.Reverse, StairEnds.CaseOf(StairRailing.LedgeDrop));
        Assert.Equal(StairEnds.Case.StepBack, StairEnds.CaseOf(StairRailing.LedgeDrop + 0.01f));
        // Sides that disagree: a floor where either is within a step, else the gentler.
        Assert.Equal(StairEnds.Case.Floor, StairEnds.Choose(StairLayout.StepUp / 2f, StairRailing.LedgeDrop + 1f, out bool disagree));
        Assert.True(disagree);
        Assert.Equal(StairEnds.Case.Reverse, StairEnds.Choose(StairLayout.StepUp + 0.05f, StairRailing.LedgeDrop + 1f, out _));
        // Read at the floor's far corners, a floor's size out from the edge.
        float Ground(float x, float z) => z >= StairLayout.PieceRun - 0.01f ? (x < 0f ? -StairLayout.StepUp / 2f : -(StairLayout.StepUp + StairRailing.LedgeDrop) / 2f) : 0f;
        var (l, r) = StairEnds.Drops(new Vector2(0f, 0f), new Vector2(0f, 1f), 0f, StairLayout.PieceRun, Ground);
        Assert.Equal(StairLayout.StepUp / 2f, l, 3);
        Assert.Equal((StairLayout.StepUp + StairRailing.LedgeDrop) / 2f, r, 3);
    }
}
