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
        /// <summary>The location the staircase comes onto, as the game placed it (PlacedLocations.Write's rows), where the case has one.</summary>
        public readonly List<string> Location = new();
        /// <summary>The location's ground where the game was not read (its preload rebuilt offline), where the case has it.</summary>
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

        /// <summary>The ground read in game, else the location's (<see cref="Placed"/>); NaN off both.</summary>
        public float LocationGround(float x, float z)
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
                    c.Run.Width = F(f[5]); c.Run.KeepsLocationRocks = f[6] == "True";
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
                case "site": case "op": case "rock": case "top": c.Location.Add(line); break;
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

    /// <summary>Where the synthetic flights stand: over the water (a foot's landing stands above it, RoadWater.Wet).</summary>
    private const float Dry = 40f;

    /// <summary>A straight flight walking +z from a foot at z = 0, <paramref name="at"/> high (stairs <paramref name="first"/>
    /// rises from a rise under it, each a run long), as laid: foot first.</summary>
    private static StairRun Flight(int first, int stairs, float at = Dry)
    {
        var run = new StairRun { From = new Vector2(0f, first * StairLayout.PieceRun), To = new Vector2(0f, (first + stairs) * StairLayout.PieceRun), Width = 2f };
        for (int i = first; i < first + stairs; i++)
            run.Pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", YawDegrees = 180f,
                Position = new Vector3(0f, at + i * StairLayout.PieceRise, (i + 0.5f) * StairLayout.PieceRun),
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
        StairEnds.End foot = StairEnds.Read(Flight(0, 3), (x, z) => Dry + (z <= 0f ? 0f : Cut(z))).Single(e => !e.Top);
        Assert.True(foot.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(foot));
        Assert.Equal(Dry, foot.Edge!.Value.y, 2);

        // A sunk stair from z = -2 to 0 (its low edge a rise under the ground at the flight's foot), the ground falling past it
        // a half rise over its length: more than a step, less than a rise, so the sunk stair's own low edge is in the ground.
        float fall = StairLayout.PieceRise / 2f;
        Assert.True(fall > StairLayout.StepUp && fall < StairLayout.PieceRise);
        float Ground(float x, float z) => z <= 0f ? fall * z / StairLayout.PieceRun : Cut(z);
        StairEnds.End over = StairEnds.Read(Flight(-1, 4), (x, z) => Dry + Ground(x, z)).Single(e => !e.Top);
        Assert.True(over.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(over));
        Assert.Equal(Dry, over.Edge!.Value.y, 2);
    }

    /// <summary>A foot's stair down cut into a side slope (offline, Fixtures/stair-foot-down-side-slope.txt: a staircase whose foot
    /// has no road past it, finished with a stair down, on the finished ground the end audit reads): its low edge's middle stands
    /// 0.22 m under the ground, by 2 cm more than a step, its two sides level with it and under it. Read sunk off its middle
    /// alone, the foot was read off the stair above it, its corners over the new stair's cut (+0.96/+1.20) and no edge stepping
    /// off: a gap, and its road was refused. It is the foot's last stair, finished: under the ground across its low edge, its
    /// middle and both sides, is sunk; free at a side is walked.</summary>
    [Fact]
    public void AFootsStairCutIntoASideSlopeIsItsLastStairNotSunk()
    {
        Case c = Load("stair-foot-down-side-slope.txt");
        StairEnds.End foot = FootOf(c);
        float lowest = c.Run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).Min(p => p.Position.y);
        Assert.True(foot.Edge != null && Mathf.Abs(foot.Edge.Value.y - lowest) < 0.01f, $"not read off its lowest stair ({lowest:F2}): {StairEnds.Line(foot)}");
        Assert.True(foot.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(foot));
        Assert.Null(StairEnds.Unfinished(foot));
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

    /// <summary>A road staircase's top whose road on was held 2.4 m down to the road beside it (RoadSpatialGrid.HoldToNetwork,
    /// captured offline at 6518.7,-1452.7, horizontally translated, on the finished ground its road's cut leaves): read by the
    /// one end reader on the finished ground as the road will stand (its own points' surface on it, RoadTerrainModifier.AsStored,
    /// StairEnds.WithRoads, as StairEnds.GroundOf reads it), no edge of its floor steps off, a gap, and the commit refuses the
    /// road (ConnectionRefusal: an end with no way off it), the verdict the staircase-end audit gives it once stored. Laid out
    /// on its planning ground the top was kept with a floor. Its road on starting level with the top's exit: the floor steps
    /// onto it, kept.</summary>
    [Fact]
    public void ATopWithNoWayOffOnTheFinishedGroundRefusesItsRoad()
    {
        Case c = Load("stair-finished-top-gap.txt");
        StairEnds.End top = TopOf(c);
        Assert.Equal(StairEnds.Verdict.Gap, top.Verdict);
        Assert.Equal(134.5f, top.Edge!.Value.y);
        Assert.True(top.WalkOff < -StairRailing.LedgeDrop, StairEnds.Line(top));
        // Its road on from its top as stored: its first point the ring's centre, 2.1 m off the top floor.
        var road = new RoadSpatialGrid.PlannedPath(new List<Vector2> { new(-2.11f, 0.10f), new(-2.25f, -0.48f), new(-2.44f, -1.25f), new(-2.67f, -2.17f) },
            new List<float> { 132.08f, 132.17f, 132.53f, 132.87f }, new List<RoadPointDebugInfo> { default, default, default, default },
            RoadNetworkGenerator.RoadWidth, 0f, false);
        string? Verdict(out Vector2 at)
        {
            using (RoadTerrainModifier.AsStored(RoadSpatialGrid.PointsOf(road, intoLocation: false), new List<(RoadSpatialGrid.RoadPoint at, float to)>()))
                return RoadStairRuns.ConnectionRefusal(Array.Empty<RoadStairSpan>(), new[] { road }, out at, new[] { c.Run }, _ => StairEnds.WithRoads(c.Ground));
        }
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, Verdict(out Vector2 refusedAt));
        Assert.True(Vector2.Distance(refusedAt, new Vector2(top.Edge.Value.x, top.Edge.Value.z)) < 0.01f, $"refused at {refusedAt}");
        // Its road starting level with the top's exit: the top floor steps onto it.
        for (int i = 0; i < road.Heights.Count; i++) road.Heights[i] = top.Edge.Value.y;
        Assert.Null(Verdict(out _));
    }

    /// <summary>The ground 1-2.5 m past the top stood over the top floor's band, so no floor, and 4.5 m on within it, so no
    /// crest, two reads of the ground for one decision: the one reader reads the top short, it climbs on and the rule there
    /// lays its stair down, landing first: a floor off the stair climbed, its exit the stair down off it (the crest, laid by
    /// hand there, only where the floor does not fit).</summary>
    [Fact]
    public void ATopUnderAShoulderTakesTheFloorOrTheCrestNeverNeither()
    {
        Case c = Load("stair-ends-a.txt");
        StairPlan plan = PlanOf(c);
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(plan, c.Ground, RoadStairRuns.Width));
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, c.Ground, RoadStairRuns.Width, StairCover.ApproachRefusal, _ => true, out _, down: true);
        Assert.Equal(StairLayout.TopFinish.FloorThenDown, finish);
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
        Assert.Equal(RoadStairRuns.FootOverItsRoad, RoadStairRuns.ConnectionRefusal(new[] { span }, roads, out Vector2 at));
        Assert.Equal(ringAt, at);
        // The road at its foot's level: no refusal.
        roads[0].Heights[0] = roads[0].Heights[1] = low.y;
        Assert.Null(RoadStairRuns.ConnectionRefusal(new[] { span }, roads, out _));
    }

    /// <summary>A road staircase whose foot has no road (laid offline on RoadSeedD where the road from the island's start
    /// set off with a staircase at its first point, on a shore falling faster than a flight): the walker steps off it onto
    /// the ground or nowhere. The one reader finds no edge stepping off, more than a ledge over the ground off its last
    /// stair, and the commit refuses the road (ConnectionRefusal: an end with no way off it); on ground up to just under its
    /// low edge the walker steps off: no refusal. Against a bank, short, refused; with the level apron cut for it there
    /// (StairLayout.CutFootApron, as Lay cuts one), on the ground as its cuts leave it (they are written with its road, so
    /// the finished ground the verdict reads has them): kept (read without them, a foot onto its apron at an island's start
    /// on RoadSeedA was refused).</summary>
    [Fact]
    public void AFootWithNoRoadOverTheGroundRefusesTheRoad()
    {
        Case c = Load("stair-foot-no-road.txt");
        StairEnds.End foot = FootOf(c);
        Assert.True(foot.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(foot));
        Assert.True(foot.Left > StairRailing.LedgeDrop && foot.Right > StairRailing.LedgeDrop, StairEnds.Line(foot));
        Vector3 low = foot.Edge!.Value;
        var none = new List<RoadSpatialGrid.PlannedPath>();
        // Its top as laid in game: onto its road, level with its exit (the case's ground is read round its foot only).
        Vector3 exit = TopOf(c).Edge!.Value;
        Func<float, float, float> Topped(Func<float, float, float> ground) => (x, z) =>
            Vector2.Distance(new Vector2(x, z), new Vector2(exit.x, exit.z)) < 2f * StairLayout.PieceRun ? exit.y : ground(x, z);
        string? Ends(StairRun run, Func<float, float, float> ground, out Vector2 at) =>
            RoadStairRuns.ConnectionRefusal(Array.Empty<RoadStairSpan>(), none, out at, new[] { run }, _ => Topped(ground));
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, Ends(c.Run, c.Ground, out Vector2 at));
        Assert.True(Vector2.Distance(at, new Vector2(low.x, low.z)) < 0.01f, $"refused at {at}, its foot's edge at {low}");
        // The ground off it raised to its low edge (on the shore, built dry): stepped off, no refusal.
        Assert.False(RoadWater.Wet(low.y));
        Func<float, float, float> raised = (x, z) => Mathf.Max(c.Ground(x, z), low.y);
        Assert.True(StairEnds.Read(c.Run, raised).Single(e => !e.Top).Verdict == StairEnds.Verdict.Ok);
        Assert.Null(Ends(c.Run, raised, out _));
        StairPlan plan = PlanOf(c);
        StairPiece bottom = plan.Pieces.First(p => p.Kind == StairPieceKind.Stair);
        float heading = StairLayout.Footprint(bottom).heading;
        Vector2 edge = new(low.x, low.z), outward = -StairLayout.Forward(heading);
        Func<float, float, float> bank = (x, z) => Vector2.Dot(new Vector2(x, z) - edge, outward) > 0f ? low.y + 2f * StairLayout.PieceRise : c.Ground(x, z);
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, Ends(c.Run, bank, out _));
        plan.Foot = low;
        plan.StartHeading = heading;
        Assert.True(StairLayout.CutFootApron(plan, bank, RoadStairRuns.Width, RoadGrade.Configured) > 0, "no apron cut against the bank");
        Func<float, float, float> cut = (x, z) => RoadTerrainModifier.GroundAfterCuts(bank(x, z), plan.Cuts, x, z);
        StairRun aproned = StairRun.FromPlan(c.Run.From, c.Run.To, RoadStairRuns.Width, plan);
        // Its apron, cut a little under the foot on this shore (30.35 m), stands under the built-dry floor (RoadWater.BuiltDry,
        // the owner's line of 12 Nov for a foot's landing): refused, the landing in the shallows.
        Vector2 apron = edge + outward * StairLayout.PieceRun;
        Assert.False(RoadWater.BuiltDry(cut(apron.x, apron.y)));
        Assert.True(Ends(aproned, cut, out _) == RoadStairRuns.NoWayOffItsEnd, string.Join("; ", StairEnds.Read(aproned, Topped(cut)).Select(StairEnds.Line)));
        // The same apron on built-dry ground: kept.
        Func<float, float, float> dry = (x, z) => cut(x, z) + RoadConstants.ShallowWaterHeight - RoadConstants.SeaLevel;
        Assert.True(Ends(aproned, dry, out _) == null, string.Join("; ", StairEnds.Read(aproned, Topped(dry)).Select(StairEnds.Line)));
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
        Assert.Equal(RoadStairRuns.FootUnderItsRoad, RoadStairRuns.ConnectionRefusal(new[] { span }, roads, out _));
        // The road at its foot's low edge: no refusal; within a rise over it (its slope meets the road): none either.
        roads[0].Heights[0] = roads[0].Heights[1] = low.y;
        Assert.Null(RoadStairRuns.ConnectionRefusal(new[] { span }, roads, out _));
        roads[0].Heights[0] = roads[0].Heights[1] = low.y + StairLayout.PieceRise;
        Assert.Null(RoadStairRuns.ConnectionRefusal(new[] { span }, roads, out _));
    }

    /// <summary>
    /// Read in game: an approach's foot onto its road's end, the road falling away from it at a pitch's grade (50 % for its
    /// last 8 m, steeper live), was read "onto its road". A saved staircase does not keep which road meets its foot, and the
    /// dumped road points round it are the network's, not a road in its order: the foot's step onto the road is read (onto
    /// its road), its road's slope into it is not verified, steep as dumped or levelled for a landing's length, never a way
    /// off for want of a slope (read so, the levelled road was "onto its road" by a heading average over the grid).
    /// </summary>
    [Fact]
    public void AFootOntoARoadNotRecordedWithItIsNotVerified()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        Case c = Load("stair-start-road-grade.txt");
        c.StoreRoads((_, h) => h);
        StairEnds.End foot = FootOf(c);
        Assert.True(foot.OntoRoad, StairEnds.Line(foot));
        Assert.True(foot.Verdict == StairEnds.Verdict.NotVerified, StairEnds.Line(foot));
        Assert.Contains("slope not verified", foot.Why);

        // Levelled at the foot's road end for a landing's length and a pad's reach (RoadEndRing.StartReach) past it.
        RoadSpatialGrid.Clear();
        Vector2 end = c.Run.From;
        float level = c.Roads.OrderBy(r => Vector2.Distance(r.at, end)).First().height;
        float reach = RoadPitches.JunctionLanding + new RoadEndRing(end, 0f, level).StartReach;
        c.StoreRoads((at, h) => Vector2.Distance(at, end) <= reach ? level : h);
        foot = FootOf(c);
        Assert.True(foot.OntoRoad, StairEnds.Line(foot));
        Assert.True(foot.Verdict == StairEnds.Verdict.NotVerified, StairEnds.Line(foot));
    }

    /// <summary>
    /// A foot onto its road, the road's first metre from the foot rising <paramref name="rise"/> and then level for 11 m: at
    /// 0.75 m that metre is past the cap, averaged over a landing's length (12.5 %) it was hidden and the foot read "onto its
    /// road". The foot's road (StairRun.FromRoad, ToRoad) is judged by the rule its planning keeps it to
    /// (RoadPitches.LeavesItsLanding): the same verdict for the run either way round and the road's points either way along; the
    /// level road a way off. The same staircase with no road recorded at its foot is not verified, never a way off. (Six
    /// stairs: the end reader tells a flight's top from its foot by its last three pieces' heights.)
    /// </summary>
    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.75f, true)]
    public void AFootsRoadIsJudgedAsItsPlanningJudgesIt(float rise, bool steep)
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding).AndKeep(() => RoadGrade.Configured);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade;
        RoadSpatialGrid.PlannedPath Road(bool fromFoot)
        {
            var points = Enumerable.Range(0, 13).Select(k => new Vector2(0f, fromFoot ? -k : k - 12)).ToList();
            return new RoadSpatialGrid.PlannedPath(points, points.Select(q => Dry + (q.y == 0f ? 0f : rise)).ToList(),
                points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 12f, false);
        }
        RoadSpatialGrid.Commit(Road(fromFoot: true));
        foreach (bool fromFoot in new[] { true, false })
        {
            RoadSpatialGrid.PlannedPath road = Road(fromFoot);
            bool pitched = RoadPitches.LeavesItsLanding(road.Points, road.Heights, fromFoot, !fromFoot, RoadGrade.Configured);
            Assert.Equal(steep, pitched);
            StairRun run = Flight(0, 6);
            var reversed = new StairRun { From = run.To, To = run.From, Width = run.Width };
            reversed.Pieces.AddRange(run.Pieces);
            run.FromRoad = StairRoad.Of(road, fromFoot);
            reversed.ToRoad = StairRoad.Of(road, fromFoot);
            foreach (StairRun r in new[] { run, reversed })
            {
                StairEnds.End foot = StairEnds.Read(r, (x, z) => Dry).Single(e => !e.Top);
                Assert.True(foot.OntoRoad, StairEnds.Line(foot));
                Assert.True(foot.Verdict == (pitched ? StairEnds.Verdict.Gap : StairEnds.Verdict.Ok), StairEnds.Line(foot));
            }
        }
        StairEnds.End unknown = StairEnds.Read(Flight(0, 6), (x, z) => Dry).Single(e => !e.Top);
        Assert.True(unknown.OntoRoad && unknown.Verdict == StairEnds.Verdict.NotVerified, StairEnds.Line(unknown));
    }

    /// <summary>
    /// The road a foot steps off onto is the network's road as it stands, through a save and after a raise: a staircase
    /// whose foot meets a level road (its road recorded at that end, StairRun.FromRoad) reads "onto its road" as laid, still
    /// does after the staircase record is saved and read back (StairStructures.Serialize, Parse: before, a run read from a
    /// record had no road and every road foot read NOT VERIFIED), and reads a gap once the road's first metre is raised past
    /// the cap (RoadSpatialGrid.SetRoadHeights, as a landing raises another road: before, the audit read the profile as
    /// planned and still said "onto its road"). Raised back, it reads a way off again.
    /// </summary>
    [Fact]
    public void AFootsRoadIsSavedWithItAndReadAsTheNetworkStoresIt()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding).AndKeep(() => RoadGrade.Configured);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade;
        var points = Enumerable.Range(0, 13).Select(k => new Vector2(0f, -k)).ToList();
        var road = new RoadSpatialGrid.PlannedPath(points, points.Select(_ => Dry).ToList(),
            points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 12f, false);
        RoadSpatialGrid.Commit(road);
        StairRun run = Flight(0, 6);
        run.FromRoad = StairRoad.Of(road, atStart: true);
        StairEnds.End Foot(StairRun r) => StairEnds.Read(r, (x, z) => Dry).Single(e => !e.Top);
        Assert.True(Foot(run).Verdict == StairEnds.Verdict.Ok, StairEnds.Line(Foot(run)));

        StairStructures.Add(run);
        StairRun read = Assert.Single(StairStructures.Parse(StairStructures.Serialize()));
        Assert.Equal(points, read.FromRoad!.Points);
        Assert.Null(read.ToRoad);
        Assert.True(Foot(read).Verdict == StairEnds.Verdict.Ok, StairEnds.Line(Foot(read)));

        // The road's point a metre out raised 0.75 m (past the cap within a landing's length of the foot).
        RoadSpatialGrid.RoadPoint second = Assert.Single(RoadSpatialGrid.StoredAt(new[] { points[1] })!);
        Assert.True(RoadSpatialGrid.SetRoadHeights(new[] { (second, Dry + 0.75f) }) > 0);
        StairEnds.End raised = Foot(read);
        Assert.True(raised.OntoRoad && raised.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(raised));
        RoadSpatialGrid.RoadPoint up = Assert.Single(RoadSpatialGrid.StoredAt(new[] { points[1] })!);
        RoadSpatialGrid.SetRoadHeights(new[] { (up, Dry) });
        Assert.True(Foot(read).Verdict == StairEnds.Verdict.Ok, StairEnds.Line(Foot(read)));
    }

    /// <summary>
    /// A foot onto its own road before that road is stored, as the one list of a road's refusals reads it
    /// (RoadNetworkGenerator.RoadRefusal, inside RoadTerrainModifier.AsStored over what the road writes): the end reader
    /// reads the road as it will stand, its surface under the foot (StairEnds.WithRoads) and its points for the slope into it
    /// (StairRoad.Stored), so the foot steps off onto it, as the audit reads it once stored. Read on the network as it
    /// stands, the road not there yet, the ground 2 m under the foot: a gap.
    /// </summary>
    [Fact]
    public void AFootOntoItsRoadNotStoredYetIsReadOnTheRoadAsItWillStand()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding).AndKeep(() => RoadGrade.Configured);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade;
        var points = Enumerable.Range(0, 13).Select(k => new Vector2(0f, -k)).ToList();
        var road = new RoadSpatialGrid.PlannedPath(points, points.Select(_ => Dry).ToList(),
            points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 12f, false);
        StairRun run = Flight(0, 6);
        run.FromRoad = StairRoad.Of(road, atStart: true);
        Func<float, float, float> ground = StairEnds.WithRoads((x, z) => Dry - 2f);
        StairEnds.End before = StairEnds.Read(run, ground).Single(e => !e.Top);
        Assert.True(before.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(before));
        using (RoadTerrainModifier.AsStored(RoadSpatialGrid.PointsOf(road, intoLocation: false), new List<(RoadSpatialGrid.RoadPoint at, float to)>()))
        {
            StairEnds.End foot = StairEnds.Read(run, ground).Single(e => !e.Top);
            Assert.True(foot.OntoRoad && foot.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(foot));
        }
    }

    /// <summary>
    /// A foot by a wade (as on a test world at -4265.1,31.20,926.9: the standing-water wade about 3 m off, its floor under the water,
    /// lowered the finished ground under the foot's way off to 0.70/0.67 m under it, read +0.07/+0.37 on its planning ground;
    /// its start pad not laid, its road ending lower). Its landing stands dry or it has none (the owner's, 11 Nov: a staircase landing
    /// above water, even if it means rejecting a route; RoadWater.Wet): the foot over ground 0.7 m under it is a gap, a
    /// foot level with ground in the water is a gap, a foot onto its road awash there (the road not stored yet, read as it
    /// will stand) is a gap, and each refuses its road (ConnectionRefusal); level with dry ground or onto its road above the
    /// water, kept. (Its top a floor onto level ground, so only its foot is judged.)
    /// </summary>
    [Fact]
    public void AFootsLandingByAWadeStandsDryOrRefusesItsRoad()
    {
        StairRun FootAt(float height)
        {
            StairRun run = Flight(0, 6, height);
            run.Pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.StairFloor, Prefab = "stone_floor_2x2", YawDegrees = 180f,
                Position = new Vector3(0f, height + 6f * StairLayout.PieceRise - StairKit.FloorTopOf("stone_floor_2x2"), 6.5f * StairLayout.PieceRun),
            });
            return run;
        }
        var none = new List<RoadSpatialGrid.PlannedPath>();
        string? Ends(StairRun run, Func<float, float, float> ground) =>
            RoadStairRuns.ConnectionRefusal(Array.Empty<RoadStairSpan>(), none, out _, new[] { run }, _ => StairEnds.WithRoads(ground));
        StairEnds.End Foot(StairRun run, Func<float, float, float> ground) => StairEnds.Read(run, StairEnds.WithRoads(ground)).Single(e => !e.Top);
        // The flight's own ground under it cut to its walk (StairStructures.CutFloor), past its foot (z < 0) the landing's.
        Func<float, float, float> Landing(float foot, float landing) =>
            (x, z) => z <= 0f ? landing : Mathf.Min(foot + 6f * StairLayout.PieceRise, foot + z * StairLayout.PieceRise / StairLayout.PieceRun - StairLayout.StepUp / 2f);

        StairRun high = FootAt(31.2f);
        Assert.Null(Ends(high, Landing(31.2f, 31.2f)));
        StairEnds.End lowered = Foot(high, Landing(31.2f, 31.2f - 0.7f));
        Assert.True(lowered.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(lowered));
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, Ends(high, Landing(31.2f, 31.2f - 0.7f)));

        // Level with its landing within a step, the landing under the water surface: in the water.
        float awash = RoadConstants.SeaLevel - StairLayout.StepUp / 2f;
        Assert.True(RoadWater.Wet(awash));
        StairRun low = FootAt(awash);
        StairEnds.End wet = Foot(low, Landing(awash, awash));
        Assert.True(wet.Verdict == StairEnds.Verdict.Gap && wet.Why.Contains("water"), StairEnds.Line(wet));
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, Ends(low, Landing(awash, awash)));

        // Onto its road, not stored yet, past the foot over the sea floor: awash there, refused; built dry, kept. The road is
        // the one recorded at its foot (StairRun.FromRoad): a foot onto a road not its own is refused whatever its height.
        string? OntoRoad(StairRun run, float road)
        {
            var points = Enumerable.Range(0, 13).Select(k => new Vector2(0f, -k)).ToList();
            var path = new RoadSpatialGrid.PlannedPath(points, points.Select(_ => road).ToList(),
                points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 12f, false);
            run.FromRoad = StairRoad.Of(path, atStart: true);
            using (RoadTerrainModifier.AsStored(RoadSpatialGrid.PointsOf(path, intoLocation: false), new List<(RoadSpatialGrid.RoadPoint at, float to)>()))
                return Ends(run, Landing(road, RoadConstants.SeaLevel - 1f));
        }
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, OntoRoad(low, awash));
        Assert.Null(OntoRoad(high, 31.2f));
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
    /// (PlacedLocationShape.ConnectedLanding), on the ground read in game, ground not read never level with the shelf.</summary>
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
        PlacedLocationRecord r = PlacedLocations.Read(string.Join("\n", c.Location)).Single();
        float Read(float x, float z) => c.LocationGround(x, z) is var h && !float.IsNaN(h) ? h : float.NegativeInfinity;
        PlacedLocationShape shape = PlacedLocationShape.Of(r.Centre, r.Yaw, RoadStairRuns.FlatOf(r.Ops)!.Value, r.RockRadius, r.Rocks, Read,
            r.KeepSmallRocks, r.Root, r.Prefab);
        PlacedLocationShape.LandingRegion landing = shape.ConnectedLanding(Read, 1f);
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
            // Landing first: the floor, then the stair down off it, its exit (a stair down in the floor's place, or the crest
            // off a stair climbed, only where that does not fit).
            "reverse" => StairLayout.TopFinish.FloorThenDown,
            "crest" => StairLayout.TopFinish.FloorThenDown,
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

    /// <summary>The same step back through an approach's own finisher (RoadStairRuns.FinishOverLedge, with the end reader's
    /// way off it), as a placed approach's top is finished.</summary>
    [Fact]
    public void AnApproachTopOverTheLedgeStepsBackThroughItsOwnFinisher()
    {
        StairPlan plan = Flight(6);
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        float Ground(float x, float z) => z < 4f * run ? z / run * rise : z <= 5.25f * run ? 4f * rise : 4f * rise - (z - 5.25f * run) / (1.75f * run) * 0.5f;
        Assert.Equal(StairLayout.TopFinish.StepBack, RoadStairRuns.FinishOverLedge(plan, Ground, RoadStairRuns.Width, _ => true));
        Assert.Equal(4, plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair));
        Assert.True(RoadStairRuns.WayOffItsTop(plan, Ground));
    }

    /// <summary>"1-2 fewer stairs up and 1-2 more level 2x2 floors": a flight overshooting a ledge by 2 m steps back until the
    /// floor off the stair then last lands by the one rule (StairEnds.Exit: a free edge with both corners within a step). Two
    /// stairs back, the floor stands within a step at one far corner only (on the ledge, the other 0.5 m over it: the slope
    /// under it falls away across and back down the flight): it does not land, and is not laid (it was, at that corner, and had
    /// no way off by the end reader, so more level floors were laid on past it). Three stairs back, the floor on the slope has
    /// its right side within a step at both corners: it lands there, its exit, and is kept with a way off.</summary>
    [Fact]
    public void AStepBackGoesOnPastAFloorWithinAStepAtOneCornerOnly()
    {
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        float Ground(float x, float z) => z < 4f * run ? z / run * rise : z <= 5f * run ? 4f * rise - (x + 1f) * 0.25f - (5f * run - z) * 0.25f : 4f * rise;
        Assert.Equal(StairEnds.Case.StepBack, StairLayout.TopCase(Flight(6), Ground, RoadStairRuns.Width));
        // Two stairs back: within a step at one far corner only.
        Assert.False(StairEnds.Lands(new Vector2(0f, 4f * run), new Vector2(0f, 1f), 4f * rise, RoadStairRuns.Width, Ground));
        StairPlan plan = Flight(6);
        StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(plan, Ground, RoadStairRuns.Width, _ => true);
        StairEnds.End top = RoadStairRuns.TopEnd(plan, Ground)!;
        Assert.True(finish == StairLayout.TopFinish.StepBack, $"{finish}: {StairEnds.Line(top)}");
        Assert.Equal(3, plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        var (back, heading, across, _, height) = StairLayout.Footprint(floor);
        Assert.Equal(3f * rise, height, 3);
        Assert.Equal(StairEnds.FloorEdge.Right, StairEnds.Exit(StairEnds.FloorDrops(back, StairLayout.Forward(heading), height, across, Ground)));
        Assert.True(top.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(top));
    }

    /// <summary>Landing first (the owner's choice): a top ending on its search's floor standing 0.6 m over the ground round it
    /// (a stair down's case), where both the floor kept with a stair down off its far edge and the floor made a stair down in
    /// its place (back to back) come down onto the ground, is finished with the landing then the stair down; back to back is
    /// only for where the landing does not fit.</summary>
    [Fact]
    public void ATopThatStepsDownKeepsItsLandingBeforeTheStairDown()
    {
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        StairPlan Top()
        {
            StairPlan plan = Flight(3);
            plan.LastRowPieces = plan.Pieces.Count; plan.LastRowPieceCount = 1; plan.LastRowClearances = 0; plan.LastRowClearanceCount = 0;
            plan.LastRowKind = StairPieceKind.Landing; plan.LastRowStart = new Vector2(0f, 3f * run); plan.LastRowHeading = 0f; plan.LastRowHeight = 3f * rise;
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(0f, 3f * rise - StairKit.Stone.FloorTop, 3.5f * run) });
            plan.Landings = 1;
            plan.End = new Vector3(0f, 3f * rise, 4f * run);
            plan.EndHeading = 0f;
            return plan;
        }
        float Ground(float x, float z) => z < 3f * run ? z / run * rise : 3f * rise - 0.6f;
        Assert.Equal(new[] { StairEnds.Case.Reverse }, StairLayout.TopCases(Top(), Ground, RoadStairRuns.Width));
        // Back to back fits there too.
        StairPlan backToBack = Top();
        Assert.True(StairLayout.StepDownTop(backToBack, Ground, RoadStairRuns.Width));
        Assert.True(StairLayout.Terminal(backToBack)!.StepsDownOnto(Ground));

        StairPlan plan = Top();
        StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(plan, Ground, RoadStairRuns.Width, _ => true);
        StairEnds.End top = RoadStairRuns.TopEnd(plan, Ground)!;
        Assert.True(finish == StairLayout.TopFinish.FloorThenDown, $"{finish}: {StairEnds.Line(top)}");
        Assert.Equal(1, plan.Pieces.Count(p => p.Kind == StairPieceKind.Landing));
        Assert.True(StairLayout.Terminal(plan)!.Descending);
        Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.FloorsPast == 1 && top.StairsPast == 1, StairEnds.Line(top));
    }

    /// <summary>
    /// A top's stair down comes off straight on, in its flight's own direction, never off a side or a corner of a floor turned
    /// off the flight (the owner, 12 Nov 2026: "stair should come off straight instead of sideways"). An approach's top to a
    /// mountain cave at -771.7,7478.3 ended on its search's floor turned off the flight (heading 315), 0.45-0.64 m over the
    /// snow past it (a stair down's case); the floor made a stair down in its place (back to back) came off the turn's corner
    /// along that heading, at an angle onto the slope, and was read "walk-off +0.66 OK". Here a flight climbing north ends on
    /// a floor turned west, the ground 0.55 m under it round it: no stair down is laid sideways off it (the floor and a stair
    /// down off its far edge, or one in its place), and the same floor straight on still takes the landing then the stair down.
    /// </summary>
    [Fact]
    public void ATopsStairDownComesOffStraightOnNeverOffATurnedFloor()
    {
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        StairPlan Top(float heading)
        {
            StairPlan plan = Flight(3);
            Vector2 centre = new Vector2(0f, 3.5f * run), dir = StairLayout.Forward(heading);
            plan.LastRowPieces = plan.Pieces.Count; plan.LastRowPieceCount = 1; plan.LastRowClearances = 0; plan.LastRowClearanceCount = 0;
            plan.LastRowKind = StairPieceKind.Landing; plan.LastRowStart = centre - dir * (run / 2f); plan.LastRowHeading = heading; plan.LastRowHeight = 3f * rise;
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(centre.x, 3f * rise - StairKit.Stone.FloorTop, centre.y), YawDegrees = heading });
            plan.Landings = 1;
            Vector2 far = centre + dir * (run / 2f);
            plan.End = new Vector3(far.x, 3f * rise, far.y);
            plan.EndHeading = heading;
            return plan;
        }
        float Ground(float x, float z) => z < 3f * run ? z / run * rise : 3f * rise - 0.55f;

        // Turned west off the flight: the stair down off it, in its place or off its far edge, would run west, sideways.
        StairPlan turned = Top(270f);
        Assert.Equal(StairEnds.Case.Reverse, StairLayout.TopCase(turned, Ground, RoadStairRuns.Width));
        StairPlan inPlace = Top(270f);
        Assert.True(StairLayout.StepDownTop(inPlace, Ground, RoadStairRuns.Width));
        Assert.True(StairLayout.Terminal(inPlace)!.StepsDownOnto(Ground), "the sideways stair down lands: the case is lost");
        Assert.False(StairLayout.StepsDownStraightOnto(inPlace, Ground));
        StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(turned, Ground, RoadStairRuns.Width, _ => true);
        StairEnds.End top = RoadStairRuns.TopEnd(turned, Ground)!;
        Assert.True(StairLayout.Terminal(turned) is not { Descending: true } exit || StairLayout.StraightOn(turned, exit),
            $"{finish}: a stair down off the turned floor sideways; {StairEnds.Line(top)}");
        Assert.True(finish is not (StairLayout.TopFinish.FloorThenDown or StairLayout.TopFinish.BackToBack), $"{finish}: {StairEnds.Line(top)}");

        // Straight on: the landing then the stair down, its stair on north.
        StairPlan straight = Top(0f);
        Assert.Equal(StairLayout.TopFinish.FloorThenDown, RoadStairRuns.FinishOverLedge(straight, Ground, RoadStairRuns.Width, _ => true));
        Assert.True(StairLayout.StraightOn(straight, StairLayout.Terminal(straight)!));
    }

    /// <summary>
    /// Parity for the straight-on rule: a top's stair down onto a placed Location's raised landing
    /// (RoadStairRuns.RaisedLandingWalk) is accepted by the same owner as every other top finish
    /// (StairLayout.StepsDownStraightOnto). The turned floor's stair down in its place above, come off sideways, lands on the
    /// landing's heights but does not arrive; the same floor straight on, finished with the landing then the stair down, does.
    /// </summary>
    [Fact]
    public void ARaisedLandingTakesATopsStairDownOnlyStraightOnAsEveryTopFinishDoes()
    {
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        StairPlan Top(float heading)
        {
            StairPlan plan = Flight(3);
            Vector2 centre = new Vector2(0f, 3.5f * run), dir = StairLayout.Forward(heading);
            plan.LastRowPieces = plan.Pieces.Count; plan.LastRowPieceCount = 1; plan.LastRowClearances = 0; plan.LastRowClearanceCount = 0;
            plan.LastRowKind = StairPieceKind.Landing; plan.LastRowStart = centre - dir * (run / 2f); plan.LastRowHeading = heading; plan.LastRowHeight = 3f * rise;
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(centre.x, 3f * rise - StairKit.Stone.FloorTop, centre.y), YawDegrees = heading });
            plan.Landings = 1;
            Vector2 far = centre + dir * (run / 2f);
            plan.End = new Vector3(far.x, 3f * rise, far.y);
            plan.EndHeading = heading;
            return plan;
        }
        float Ground(float x, float z) => z < 3f * run ? z / run * rise : 3f * rise - 0.55f;
        // The landing: the level ground past the flight's top, half-metre cells from the top floor's back edge on.
        var cells = new HashSet<(int, int)>();
        for (int x = -16; x <= 16; x++)
            for (int z = 7; z <= 24; z++) cells.Add((x, z));
        var landing = new PlacedLocationShape.LandingRegion(new List<(Vector2, float)> { (new Vector2(0f, 12f), Ground(0f, 12f)) },
            cells, new List<(Vector2, Vector2)>(), Ground);
        Func<Vector3, bool> clear = s => RoadStairRuns.RaisedLandingSurfaceAllowed(s, landing);

        // Turned west: the stair down in its place lands on the landing's heights but comes off the turn sideways.
        StairPlan inPlace = Top(270f);
        Assert.True(StairLayout.StepDownTop(inPlace, Ground, RoadStairRuns.Width));
        Assert.True(StairLayout.Terminal(inPlace)!.StepsDownOnto(Ground, clear), "the sideways stair down lands: the case is lost");
        Assert.False(StairLayout.StepsDownStraightOnto(inPlace, Ground, clear));
        Assert.False(RoadStairRuns.RaisedLandingWalk(inPlace, landing).arrival);

        // Straight on: the landing then the stair down, accepted by both.
        StairPlan straight = Top(0f);
        Assert.Equal(StairLayout.TopFinish.FloorThenDown, RoadStairRuns.FinishOverLedge(straight, Ground, RoadStairRuns.Width, _ => true));
        Assert.True(StairLayout.StepsDownStraightOnto(straight, Ground, clear));
        Assert.True(RoadStairRuns.RaisedLandingWalk(straight, landing).arrival);
    }

    /// <summary>"More floors first" (the owner, 12 Nov 2026): in a stair down's case (Reverse) the finisher tries more level
    /// floors on before a step back, after the floor then a stair down and the stair down in place. A top ending on its search's
    /// floor 0.6 m over the ground at its corners, a pit under it (the stair down in its place does not stand on the ground),
    /// the ground level with it again a floor on (no stair down off its far edge): one more floor on is laid there, with a way off, through the
    /// approach's finisher (RoadStairRuns.FinishOverLedge). With a drop past its far edge instead (no floor on lands, no stair
    /// down off it), nothing fits and the finisher's trace reads the floors tried before the step back. (A step back never fits a stair down's case: it steps back only from a stair over
    /// the ledge, StairLayout.StepBack; so the two never compete, and the order is pinned by the trace.)</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStairDownsCaseTriesMoreFloorsBeforeAStepBack(bool levelOn)
    {
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        StairPlan Top()
        {
            StairPlan plan = Flight(3);
            plan.LastRowPieces = plan.Pieces.Count; plan.LastRowPieceCount = 1; plan.LastRowClearances = 0; plan.LastRowClearanceCount = 0;
            plan.LastRowKind = StairPieceKind.Landing; plan.LastRowStart = new Vector2(0f, 3f * run); plan.LastRowHeading = 0f; plan.LastRowHeight = 3f * rise;
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(0f, 3f * rise - StairKit.Stone.FloorTop, 3.5f * run) });
            plan.Landings = 1;
            plan.End = new Vector3(0f, 3f * rise, 4f * run);
            plan.EndHeading = 0f;
            return plan;
        }
        float proud = 3f * rise - 0.6f;
        // The floor's corners (z = 6 and 8) 0.6 m under it, a pit between them (z = 7), so the stair down in its place does not
        // stand on the ground at its low end (TerminalExit.StepsDownOnto); past its far edge no corner reads a stair down's case.
        float V(int x, int z) => z < 6 ? z / run * rise : z == 7 ? proud - 20f : z <= 8 ? proud : levelOn ? 3f * rise : proud - 20f;
        float Ground(float x, float z) => BuriedPieces.MeshHeight(x, z, V);
        Assert.Equal(new[] { StairEnds.Case.Reverse }, StairLayout.TopCases(Top(), Ground, RoadStairRuns.Width));

        StairPlan plan = Top();
        StairLayout.Trace = new List<string>();
        StairLayout.TopFinish finish;
        List<string> trace;
        try
        {
            finish = StairLayout.FinishTop(plan, Ground, RoadStairRuns.Width, StairCover.ApproachRefusal, _ => true, out _, down: true,
                wayOff: p => RoadStairRuns.WayOffItsTop(p, Ground));
        }
        finally { trace = StairLayout.Trace!; StairLayout.Trace = null; }
        string said = string.Join(" | ", trace);
        if (levelOn)
        {
            StairEnds.End top = RoadStairRuns.TopEnd(plan, Ground)!;
            Assert.True(finish == StairLayout.TopFinish.TopFloor, $"{finish}: {said}");
            Assert.Equal(2, plan.Pieces.Count(p => p.Kind == StairPieceKind.Landing));
            Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.StairsPast == 0, StairEnds.Line(top));
            Assert.DoesNotContain(trace, l => l.StartsWith("step back", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(StairLayout.TopFinish.None, finish);
            int floors = trace.FindIndex(l => l.StartsWith("floors on", StringComparison.Ordinal));
            int back = trace.FindIndex(l => l.StartsWith("step back", StringComparison.Ordinal));
            Assert.True(floors >= 0 && back > floors, said);
        }
    }

    /// <summary>A floor laid on in a stair down's case is kept only where the end it leaves is finished as the end rule chose
    /// (StairEnds.Unfinished, the audit's own test), not only where a walker steps off it. An approach's top ends on its search's
    /// floor whose far corners stand 0.3 m and 0.6 m over the ground (a stair down's case), a pit under that floor so the stair
    /// down in its place stands on no ground, and the ground level with the top a floor on, so the floor's stair down off its
    /// far edge would be sunk in it. One floor on lands there and has a way off, but the end is still a stair down's case with no
    /// stair down: kept, the audit called the top UNFINISHED ("a reverse's case (a stair down), laid 2 floors"). The finisher
    /// keeps no such floor (RoadStairRuns.TopFinished) and the top is left unfinished by it (None), for the approach's
    /// own refusal of a top with no finish to take the next aim.</summary>
    [Fact]
    public void AFloorOnIsKeptOnlyWhereTheEndItLeavesIsFinished()
    {
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise, top = 3f * rise;
        StairPlan plan = Flight(3);
        plan.LastRowPieces = plan.Pieces.Count; plan.LastRowPieceCount = 1; plan.LastRowClearances = 0; plan.LastRowClearanceCount = 0;
        plan.LastRowKind = StairPieceKind.Landing; plan.LastRowStart = new Vector2(0f, 3f * run); plan.LastRowHeading = 0f; plan.LastRowHeight = top;
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(0f, top - StairKit.Stone.FloorTop, 3.5f * run) });
        plan.Landings = 1;
        plan.End = new Vector3(0f, top, 4f * run);
        plan.EndHeading = 0f;
        int pieces = plan.Pieces.Count;
        // The flight's ramp to z 6; a pit across z 7 (between the corners the end reader reads); the search floor's far corners
        // (z 8) 0.3 m and 0.6 m under the top; level with the top from z 9 on.
        float V(int x, int z) => z <= 6 ? z / run * rise : z == 7 ? top - 20f : z == 8 ? (x <= -1 ? top - 0.3f : x >= 1 ? top - 0.6f : top - 0.45f) : top;
        float Ground(float x, float z) => BuriedPieces.MeshHeight(x, z, V);
        Assert.Equal(new[] { StairEnds.Case.Reverse }, StairLayout.TopCases(plan, Ground, RoadStairRuns.Width));

        // The floor on alone: a way off by the end reader, but not finished.
        StairPlan on = plan.Copy(deepPieces: true);
        on.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(0f, top - StairKit.Stone.FloorTop, 4.5f * run) });
        on.Landings = 2;
        on.End = new Vector3(0f, top, 5f * run);
        StairEnds.End onTop = RoadStairRuns.TopEnd(on, Ground)!;
        Assert.True(RoadStairRuns.WayOffItsTop(on, Ground), StairEnds.Line(onTop));
        Assert.Equal(StairEnds.Case.Reverse, onTop.Rule);
        Assert.NotNull(StairEnds.Unfinished(onTop));
        Assert.False(RoadStairRuns.TopFinished(on, Ground));
        // Left so (the top stays as laid), the road's gate refuses it as the audit reads it, as it refuses an unfinished foot:
        // the same staircase and ground raised clear of the sea, the ground level with the foot before it (a way off there).
        const float up = 40f;
        StairPlan raised = on.Copy(deepPieces: true);
        foreach (StairPiece p in raised.Pieces) p.Position += new Vector3(0f, up, 0f);
        raised.End += new Vector3(0f, up, 0f);
        float Raised(float x, float z) => up + BuriedPieces.MeshHeight(x, z, (vx, vz) => vz <= 0 ? 0f : V(vx, vz));
        StairPiece first = raised.Pieces.Find(p => p.Kind != StairPieceKind.Support)!;
        StairLayout.TerminalExit? exit = StairLayout.Terminal(raised);
        StairRun stored = StairRun.FromPlan(new Vector2(first.Position.x, first.Position.z),
            exit != null ? new Vector2(exit.Edge.x, exit.Edge.z) : new Vector2(raised.End.x, raised.End.z), RoadStairRuns.Width, raised);
        Assert.All(StairEnds.Read(stored, Raised), e => Assert.True(e.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(e)));
        Assert.NotNull(StairEnds.Unfinished(StairEnds.Read(stored, Raised).Single(e => e.Top)));
        Assert.Equal(RoadStairRuns.TopUnfinished, RoadStairRuns.ConnectionRefusal(new RoadStairSpan[0], new List<RoadSpatialGrid.PlannedPath>(), out _,
            new List<StairRun> { stored }, _ => Raised));

        StairLayout.Trace = new List<string>();
        StairLayout.TopFinish finish;
        List<string> trace;
        try { finish = RoadStairRuns.FinishOverLedge(plan, Ground, RoadStairRuns.Width, _ => true); }
        finally { trace = StairLayout.Trace ?? new List<string>(); StairLayout.Trace = null; }
        string said = string.Join(" | ", trace);
        Assert.True(finish == StairLayout.TopFinish.None, $"{finish}: {StairEnds.Line(RoadStairRuns.TopEnd(plan, Ground)!)}; {said}");
        Assert.Equal(pieces, plan.Pieces.Count);
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
        // Sides that disagree: a floor where one free edge has both its corners within a step (here its left side, level at
        // its back corner), else the far corner not within a step's case, else the gentler.
        float level = 0f, step = StairLayout.StepUp, ledge = StairRailing.LedgeDrop;
        Assert.Equal(StairEnds.Case.Floor, StairEnds.Choose((level, level, step / 2f, ledge + 1f), out bool disagree));
        Assert.True(disagree);
        Assert.Equal(StairEnds.Case.StepBack, StairEnds.Choose((step + 0.05f, level, step / 2f, ledge + 1f), out _));
        Assert.Equal(StairEnds.Case.Reverse, StairEnds.Choose((level, level, step + 0.05f, ledge + 1f), out _));
        // Read at the floor's far corners, a floor's size out from the edge.
        float Ground(float x, float z) => z >= StairLayout.PieceRun - 0.01f ? (x < 0f ? -StairLayout.StepUp / 2f : -(StairLayout.StepUp + StairRailing.LedgeDrop) / 2f) : 0f;
        var (l, r) = StairEnds.Drops(new Vector2(0f, 0f), new Vector2(0f, 1f), 0f, StairLayout.PieceRun, Ground);
        Assert.Equal(StairLayout.StepUp / 2f, l, 3);
        Assert.Equal((StairLayout.StepUp + StairRailing.LedgeDrop) / 2f, r, 3);
    }

    /// <summary>The ground ahead of a top is read once, at the end reader's two far corners (StairEnds.OverAhead, the gentler of
    /// the two, as Choose combines them), by both gates: the search's arrival (StairLayout.TopReachesGround, a rise and a step-up)
    /// and a road top's finish (RoadStairRuns.TopOntoSurface, TopFloorBand). A wall of ground 2.25 m on stands beyond the corners
    /// (a floor out, 2 m) though a point 2.5 m on read it; and the corner nearer level stands for the two.</summary>
    [Fact]
    public void TheGroundAheadOfATopIsReadAtTheEndReadersCorners()
    {
        Vector2 edge = new(0f, 0f), dir = new(1f, 0f);
        float Wall(float x, float z) => 10f + (x >= 2.25f ? 3f : 0f);
        Assert.Equal(0f, StairEnds.OverAhead(edge, dir, 10f, 2f, Wall), 3);
        // Heading 90 degrees is +x in StairLayout.Forward: the wall is beyond the corners (read from 2.5 m on it was 3 m over,
        // more than a rise and a step-up), so the top reaches the ground.
        Assert.True(StairLayout.TopReachesGround(edge, 90f, 10f, 2f, Wall));
        // One side standing a rise and more over, the other level: the level corner stands for the two.
        float Side(float x, float z) => 10f + (z > 0f ? 4f : 0f);
        Assert.Equal(0f, StairEnds.OverAhead(edge, dir, 10f, 2f, Side), 3);
        Assert.Equal(0.5f, StairEnds.Gentler(-3f, 0.5f));
        Assert.Equal(0.5f, StairEnds.Gentler(float.NaN, 0.5f));
        Assert.True(float.IsNaN(StairEnds.Gentler(float.NaN, float.NaN)));
    }

    /// <summary>A road top whose ground stands over it only past the end reader's corners is not finished out over a lip
    /// (RoadStairRuns.TopOntoSurface): the road's earthwork meets it, as for ground within TopFloorBand.</summary>
    [Fact]
    public void ARoadTopWhoseGroundRisesOnlyPastTheCornersIsLeftToTheRoad()
    {
        float Ramp(float x, float z) => z < 0f ? 0f : z < 8f ? z : 8f;
        StairPlan plan = StairLayout.Build(new Vector2(0f, -4f), new Vector2(0f, 12f), Ramp, sunkFoot: true);
        Assert.True(plan.Reached, plan.Reason);
        float topZ = plan.End.z, pieces = plan.Pieces.Count;
        float Wall(float x, float z) => Ramp(x, z) + (z >= topZ + 2.25f ? 3f : 0f);
        var path = new List<Vector2> { new(0f, topZ), new(0f, topZ + 20f) };
        Assert.False(RoadStairRuns.TopOntoSurface(plan, path, 1, Wall, out _, out _));
        Assert.Equal(pieces, plan.Pieces.Count);
        Assert.Null(plan.TopCrested);
    }

    /// <summary>Two approaches' tops as laid in game, each a stone stair climbing onto a 2x2 floor (four stairs up to it here),
    /// and the finished ground read in game at the floor's centre and four corners, spread between them as planes from the
    /// centre: a cave's (the floor at 5046.798,255.259,-5892.605, yaw 135; its walk 255.759) and a cabin's (738.887,240.024,
    /// 2068.257, yaw 90; its walk 240.524).</summary>
    private static IEnumerable<(string name, StairRun run, Func<float, float, float> ground)> ProudTopFloors()
    {
        static StructurePiece Piece(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw) =>
            new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };
        (string, StairRun, Func<float, float, float>) Top(string name, Vector2 floor, float floorY, float yaw, float centre, (Vector2 at, float h)[] corners)
        {
            // The stair before the floor: a run back along the walk, a rise under it; four of them.
            Vector2 back = -StairLayout.Forward(yaw) * StairLayout.PieceRun;
            var run = new StairRun { From = floor + back * 4.5f, To = floor - back * 0.5f, Width = 2f, KeepsLocationRocks = true };
            for (int i = 4; i >= 1; i--)
            {
                Vector2 at = floor + back * i;
                run.Pieces.Add(Piece(StructurePieceKind.StairStep, "stone_stair", at.x, floorY + StairKit.Stone.FloorTop - StairLayout.PieceRise * i, at.y, StairLayout.Wrap(yaw + 180f)));
            }
            run.Pieces.Add(Piece(StructurePieceKind.StairFloor, "stone_floor_2x2", floor.x, floorY, floor.y, yaw));
            var ring = corners.OrderBy(c => Mathf.Atan2(c.at.y - floor.y, c.at.x - floor.x)).ToArray();
            float Ground(float x, float z)
            {
                Vector2 p = new Vector2(x, z) - floor;
                for (int i = 0; i < ring.Length; i++)
                {
                    Vector2 u = ring[i].at - floor, v = ring[(i + 1) % ring.Length].at - floor;
                    float det = u.x * v.y - u.y * v.x, s = (p.x * v.y - p.y * v.x) / det, t = (u.x * p.y - u.y * p.x) / det;
                    if (s >= -1e-4f && t >= -1e-4f) return centre + s * (ring[i].h - centre) + t * (ring[(i + 1) % ring.Length].h - centre);
                }
                return centre;
            }
            return (name, run, Ground);
        }
        yield return Top("cave", new Vector2(5046.798f, -5892.605f), 255.259f, 135f, 255.141f, new[] {
            (new Vector2(5048.212f, -5892.605f), 255.099f), (new Vector2(5045.384f, -5892.605f), 255.069f),
            (new Vector2(5046.798f, -5891.191f), 254.972f), (new Vector2(5046.798f, -5894.019f), 255.599f) });
        yield return Top("cabin", new Vector2(738.887f, 2068.257f), 240.024f, 90f, 240.103f, new[] {
            (new Vector2(739.887f, 2069.257f), 239.937f), (new Vector2(739.887f, 2067.257f), 240.401f),
            (new Vector2(737.887f, 2069.257f), 239.696f), (new Vector2(737.887f, 2067.257f), 240.024f) });
    }

    /// <summary>A top floor within a step of the ground at one far corner only, more than a step over it at the other (the
    /// sides disagree) and round the rest of it, stands proud of the ground but at that corner's tip: no edge of it has both
    /// corners within a step, so it does not land (StairEnds.Exit, the one rule: not the rule's floor, which it was when
    /// either corner sufficed) and no edge of it is stepped off (StairEnds.StepsOff). Read at either corner alone, both were OK.</summary>
    [Fact]
    public void ATopFloorWithinAStepAtOneCornerOnlyIsNotSteppedOff()
    {
        foreach (var (name, run, ground) in ProudTopFloors())
        {
            StairEnds.End top = StairEnds.Read(run, ground).Single(e => e.Top);
            Assert.True(top.SidesDisagree && top.Rule != StairEnds.Case.Floor && top.FloorsPast == 1, $"{name}: {StairEnds.Line(top)}");
            Assert.True(StairLayout.Walkable(Mathf.Min(top.Left, top.Right), 0f) && !StairLayout.Walkable(Mathf.Max(top.Left, top.Right), 0f), $"{name}: {StairEnds.Line(top)}");
            Assert.True(top.Verdict == StairEnds.Verdict.Gap, $"{name}: {StairEnds.Line(top)}");
            Assert.True(-top.WalkOff > StairLayout.StepUp, $"{name}: {StairEnds.Line(top)}");
        }
    }

    /// <summary>An approach's top climbing onto those floors (its stairs alone, as the search leaves it): the rule's floor
    /// (the sides disagree, one within a step) has no way off, so it is not kept alone: the floor and a stair down off it, or a
    /// stair down in place of it, onto the ground, which a walker steps off (RoadStairRuns.FinishOverLedge, StairLayout.FinishTop).
    /// The floor was kept before, proud of the ground by 0.6-0.8 m but at one corner.</summary>
    [Fact]
    public void AnApproachTopWhoseFloorWouldStandProudIsFinishedWithAStairDown()
    {
        foreach (var (name, run, ground) in ProudTopFloors())
        {
            var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true };
            foreach (StructurePiece p in run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
                plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees });
            plan.EndHeading = StairLayout.Wrap(plan.Pieces[plan.Pieces.Count - 1].YawDegrees + 180f);
            plan.End = StairLayout.Terminal(plan)!.Edge;
            StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(plan, ground, RoadStairRuns.Width, _ => true);
            StairEnds.End top = RoadStairRuns.TopEnd(plan, ground)!;
            // Landing first: the floor and a stair down off it where that comes down onto the ground (the cabin's), else the
            // stair down in place of the floor (the cave's).
            Assert.True(finish is StairLayout.TopFinish.FloorThenDown or StairLayout.TopFinish.LandingThenDown, $"{name}: finished {finish}; {StairEnds.Line(top)}");
            Assert.True(StairLayout.Terminal(plan)!.Descending, $"{name}: {StairEnds.Line(top)}");
            int floors = finish == StairLayout.TopFinish.FloorThenDown ? 1 : 0;
            Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.StairsPast == 1 && top.FloorsPast == floors, $"{name}: {StairEnds.Line(top)}");
        }
    }

    /// <summary>The same tops on an approach's second pass (RoadStairRuns.PlanPlacedLocationApproach lays its approach again,
    /// keeping a bare top, only where the first refused every layout for one), where no stair down fits: the second pass
    /// finishes a top by the same step as the first (RoadStairRuns.FinishApproachTop) and keeps no floor without a way off
    /// either. The floor, 0.6-0.8 m proud of the ground but at one corner, is not laid; the top is laid with a floor stepped
    /// off, or bare where no finish has one. The first pass refuses it. The second pass finished with the way-off check off,
    /// and laid the proud floor that the first refuses. No layout of an approach on two generated worlds has such a top, so
    /// the pass's own step is driven here.</summary>
    [Fact]
    public void AnApproachsSecondPassKeepsNoFloorWithoutAWayOff()
    {
        static bool NoStairDown(StairPlan p) => StairLayout.Terminal(p)?.Descending != true;
        foreach (var (name, run, ground) in ProudTopFloors())
        {
            StairPlan Laid()
            {
                var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true };
                foreach (StructurePiece p in run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
                    plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees });
                plan.EndHeading = StairLayout.Wrap(plan.Pieces[plan.Pieces.Count - 1].YawDegrees + 180f);
                plan.End = StairLayout.Terminal(plan)!.Edge;
                return plan;
            }
            StairPlan first = Laid();
            var (_, refused) = RoadStairRuns.FinishApproachTop(first, ground, NoStairDown, refuseBare: true);
            Assert.True(refused || RoadStairRuns.WayOffItsTop(first, ground), $"{name}, first pass: {StairEnds.Line(RoadStairRuns.TopEnd(first, ground)!)}");

            StairPlan second = Laid();
            var (finish, bare) = RoadStairRuns.FinishApproachTop(second, ground, NoStairDown, refuseBare: false);
            StairEnds.End top = RoadStairRuns.TopEnd(second, ground)!;
            Assert.False(bare, name);
            Assert.True(top.Verdict == StairEnds.Verdict.Ok || finish == StairLayout.TopFinish.None && top.FloorsPast == 0 && top.StairsPast == 0,
                $"{name}, second pass: finished {finish}; {StairEnds.Line(top)}");
        }
    }

    /// <summary>A road staircase's top as generation laid it (black marble, its last floor walked at 59.0 facing 22.5, the
    /// road carrying on that way, at -9425.4,-1414.9), on the planning ground read round it every metre (rows west to east,
    /// each south to north, from 6 m south west of -9425,-1415).</summary>
    private static readonly (StairPieceKind kind, string prefab, float x, float y, float z, float yaw)[] ShoulderLaid =
    {
        (StairPieceKind.Landing, "blackmarble_floor", -9414.484f, 51.500f, -1422.093f, 22.5f),
        (StairPieceKind.Filler, "blackmarble_floor", -9413.719f, 51.500f, -1420.246f, 22.5f),
        (StairPieceKind.Stair, "blackmarble_stair", -9415.566f, 52.000f, -1419.480f, 112.5f),
        (StairPieceKind.Filler, "blackmarble_2x1x1", -9416.952f, 52.500f, -1418.906f, 112.5f),
        (StairPieceKind.Filler, "blackmarble_1x1", -9416.985f, 52.500f, -1418.444f, 67.5f),
        (StairPieceKind.Stair, "blackmarble_stair", -9418.180f, 53.000f, -1419.480f, 67.5f),
        (StairPieceKind.Filler, "blackmarble_2x1x1", -9419.565f, 53.500f, -1420.054f, 67.5f),
        (StairPieceKind.Stair, "blackmarble_stair", -9420.135f, 54.000f, -1420.787f, 45.0f),
        (StairPieceKind.Filler, "blackmarble_2x1x1", -9421.195f, 54.500f, -1421.847f, 45.0f),
        (StairPieceKind.Stair, "blackmarble_stair", -9422.090f, 55.000f, -1422.093f, 67.5f),
        (StairPieceKind.Filler, "blackmarble_2x1x1", -9423.476f, 55.500f, -1422.667f, 67.5f),
        (StairPieceKind.Filler, "blackmarble_1x1", -9423.509f, 55.500f, -1423.129f, 112.5f),
        (StairPieceKind.Stair, "blackmarble_stair", -9424.703f, 56.000f, -1422.093f, 112.5f),
        (StairPieceKind.Filler, "blackmarble_2x1x1", -9426.089f, 56.500f, -1421.519f, 112.5f),
        (StairPieceKind.Filler, "blackmarble_1x1", -9426.438f, 56.500f, -1421.823f, 157.5f),
        (StairPieceKind.Stair, "blackmarble_stair", -9426.551f, 57.000f, -1420.245f, 157.5f),
        (StairPieceKind.Filler, "blackmarble_2x1x1", -9427.125f, 57.500f, -1418.860f, 157.5f),
        (StairPieceKind.Filler, "blackmarble_1x1", -9427.587f, 57.500f, -1418.827f, 202.5f),
        (StairPieceKind.Stair, "blackmarble_stair", -9426.551f, 58.000f, -1417.632f, 202.5f),
        (StairPieceKind.Landing, "blackmarble_floor", -9425.785f, 58.500f, -1415.785f, 22.5f),
    };
    private static readonly float[] ShoulderLattice =
    {
        57.219f, 57.500f, 57.781f, 58.062f, 58.344f, 58.625f, 58.867f, 59.109f, 59.352f, 59.594f, 59.836f, 60.078f, 60.320f,
        57.188f, 57.500f, 57.812f, 58.125f, 58.438f, 58.750f, 58.984f, 59.219f, 59.453f, 59.688f, 59.922f, 60.156f, 60.391f,
        57.156f, 57.500f, 57.844f, 58.188f, 58.531f, 58.875f, 59.102f, 59.328f, 59.555f, 59.781f, 60.008f, 60.234f, 60.461f,
        57.125f, 57.500f, 57.875f, 58.250f, 58.625f, 59.000f, 59.219f, 59.438f, 59.656f, 59.875f, 60.094f, 60.312f, 60.531f,
        57.094f, 57.500f, 57.906f, 58.312f, 58.719f, 59.125f, 59.336f, 59.547f, 59.758f, 59.969f, 60.180f, 60.391f, 60.602f,
        57.062f, 57.500f, 57.938f, 58.375f, 58.812f, 59.250f, 59.453f, 59.656f, 59.859f, 60.062f, 60.266f, 60.469f, 60.672f,
        57.031f, 57.500f, 57.969f, 58.438f, 58.906f, 59.375f, 59.570f, 59.766f, 59.961f, 60.156f, 60.352f, 60.547f, 60.742f,
        57.000f, 57.500f, 58.000f, 58.500f, 59.000f, 59.500f, 59.688f, 59.875f, 60.062f, 60.250f, 60.438f, 60.625f, 60.812f,
        56.352f, 56.781f, 57.211f, 57.641f, 58.070f, 58.500f, 58.711f, 58.922f, 59.133f, 59.344f, 59.555f, 59.766f, 59.977f,
        55.703f, 56.062f, 56.422f, 56.781f, 57.141f, 57.500f, 57.734f, 57.969f, 58.203f, 58.438f, 58.672f, 58.906f, 59.141f,
        55.055f, 55.344f, 55.633f, 55.922f, 56.211f, 56.500f, 56.758f, 57.016f, 57.273f, 57.531f, 57.789f, 58.047f, 58.305f,
        54.406f, 54.625f, 54.844f, 55.062f, 55.281f, 55.500f, 55.781f, 56.062f, 56.344f, 56.625f, 56.906f, 57.188f, 57.469f,
        53.758f, 53.906f, 54.055f, 54.203f, 54.352f, 54.500f, 54.805f, 55.109f, 55.414f, 55.719f, 56.023f, 56.328f, 56.633f,
    };
    private static float ShoulderGround(float x, float z)
    {
        float u = Mathf.Clamp(x - (-9425f - 6f), 0f, 12f), v = Mathf.Clamp(z - (-1415f - 6f), 0f, 12f);
        int i = Mathf.Min((int)u, 11), j = Mathf.Min((int)v, 11);
        float fu = u - i, fv = v - j;
        float H(int a, int b) => ShoulderLattice[a * 13 + b];
        return Mathf.Lerp(Mathf.Lerp(H(i, j), H(i, j + 1), fv), Mathf.Lerp(H(i + 1, j), H(i + 1, j + 1), fv), fu);
    }
    private static StairPlan ShoulderTop()
    {
        var plan = new StairPlan { Kit = StairKit.BlackMarble.Name, Reached = true, End = new Vector3(-9425.402f, 59f, -1414.861f), EndHeading = 22.5f };
        foreach (var p in ShoulderLaid) plan.Pieces.Add(new StairPiece { Kind = p.kind, Prefab = p.prefab, Position = new Vector3(p.x, p.y, p.z), YawDegrees = p.yaw });
        return plan;
    }

    /// <summary>The ground past the shoulder top stands over it, so the top is climbed on (TopOntoSurface): one stair, its
    /// floor's far corners 0.41 m under and 0.61 m over the ground; two, 0.19 m and nearly 1.9 m over it, proud round the rest
    /// of it. Neither floor lands by the one rule (StairEnds.Exit: no free edge with both corners within a step), so the
    /// finisher lays neither, and with no other finish the top stays as laid and the road's earthwork meets it. Before, the
    /// layout took the second at its one corner and the end reader refused it; a reader at its far edge's middle (the mean of
    /// the corners) took the first.</summary>
    [Fact]
    public void ARoadTopWhoseFloorWouldStandProudIsNotFloored()
    {
        Vector2 top = new(-9425.402f, -1414.861f), dir = StairLayout.Forward(22.5f);
        var path = new List<Vector2> { top - dir * 10f, top, top + dir * 12f };

        // The finisher with no way off asked lays no floor: the rule's floor does not land.
        StairPlan floored = ShoulderTop();
        floored.LastRowPieces = -1;
        int laid = floored.Pieces.Count;
        Assert.Equal(StairLayout.TopFinish.None, StairLayout.FinishTop(floored, ShoulderGround, RoadStairRuns.Width, StairCover.RoadStaircase, _ => true, out bool climbed));
        Assert.True(climbed);
        Assert.DoesNotContain(floored.Pieces.Skip(laid), p => p.Kind == StairPieceKind.Landing);

        // The road's top: no floor, nor the stairs climbed to it.
        StairPlan plan = ShoulderTop();
        int pieces = plan.Pieces.Count;
        Assert.False(RoadStairRuns.TopOntoSurface(plan, path, 2, ShoulderGround, out _, out _));
        Assert.Equal(pieces, plan.Pieces.Count);
        Assert.Null(plan.TopCrested);
        Assert.Equal(59f, plan.End.y, 3);
    }

    /// <summary>The shoulder top one stair on (its edge at -9424.6,-1413.0, walked at 60.0): the floor off it has its far
    /// corners 0.41 m under and 0.61 m over the ground, so no edge of it lands (StairEnds.Exit, both corners within a step):
    /// not the layout's floor (StairEnds.Lands, the rule's case a climb on, Short), and laid there by hand, no way off it by
    /// the end reader (RoadStairRuns.WayOffItsTop), which read its far edge by the middle, the mean of the corners, 0.10 m
    /// over the ground: a way off.</summary>
    [Fact]
    public void AFloorWithItsFarCornersAStepUnderAndOverTheGroundDoesNotLand()
    {
        StairPlan plan = ShoulderTop();
        Vector2 dir = StairLayout.Forward(22.5f), edge = new Vector2(plan.End.x, plan.End.z);
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "blackmarble_stair", Position = new Vector3(edge.x + dir.x, 59f, edge.y + dir.y), YawDegrees = 202.5f });
        Vector2 on = edge + dir * StairLayout.PieceRun;
        Assert.True(Vector2.Distance(on, new Vector2(-9424.6f, -1413.0f)) < 0.1f, $"{on}");
        var drops = StairEnds.FloorDrops(on, dir, 60f, RoadStairRuns.Width, ShoulderGround);
        Assert.Equal(-0.41f, Mathf.Min(drops.farLeft, drops.farRight), 1);
        Assert.Equal(0.61f, Mathf.Max(drops.farLeft, drops.farRight), 1);
        Assert.Equal(StairEnds.FloorEdge.None, StairEnds.Exit(drops));
        Assert.False(StairEnds.Lands(on, dir, 60f, RoadStairRuns.Width, ShoulderGround));
        Assert.Equal(StairEnds.Case.Short, StairEnds.At(on, dir, 60f, RoadStairRuns.Width, ShoulderGround));

        // Laid by hand: no way off it.
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "blackmarble_floor", Position = new Vector3(on.x + dir.x, 60f - StairKit.BlackMarble.FloorTop, on.y + dir.y), YawDegrees = 22.5f });
        plan.End = new Vector3(on.x, 60f, on.y);
        StairEnds.End top = RoadStairRuns.TopEnd(plan, ShoulderGround)!;
        Assert.True(top.FloorsPast == 1 && top.Verdict == StairEnds.Verdict.Gap, StairEnds.Line(top));
        Assert.False(RoadStairRuns.WayOffItsTop(plan, ShoulderGround));
    }

    /// <summary>
    /// "Does it land", one rule (StairEnds.Exit: a free edge with both corners within a step): the floors every top finish
    /// keeps (the rule's floor, more floors on, a step back's) are read by the end reader's way off (RoadStairRuns.WayOffItsTop,
    /// StairEnds.Read, as the audit reads them) and by the layout's own test where it laid them (StairEnds.Lands), and the two
    /// agree. The cave's and the cabin's tops, whose rule's floor stands within a step at one far corner only, do not land by
    /// the layout's test either now (it took them at that corner): the finisher keeps no floor there.
    /// </summary>
    [Fact]
    public void EveryFloorAFinishKeepsLandsByTheLayoutsTestAndHasAWayOffByTheEndReader()
    {
        float run = StairLayout.PieceRun, rise = StairLayout.PieceRise;
        var cases = new List<(string name, StairPlan plan, Func<float, float, float> ground)>();
        foreach (var (name, laid, ground) in ProudTopFloors())
        {
            var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true };
            foreach (StructurePiece p in laid.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
                plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees });
            plan.EndHeading = StairLayout.Wrap(plan.Pieces[plan.Pieces.Count - 1].YawDegrees + 180f);
            plan.End = StairLayout.Terminal(plan)!.Edge;
            // The layout's own test no longer takes the rule's floor off this top at its one far corner, as the way off does not.
            Assert.False(StairEnds.Lands(new Vector2(plan.End.x, plan.End.z), StairLayout.Forward(plan.EndHeading), plan.End.y, RoadStairRuns.Width, ground), name);
            cases.Add((name, plan, ground));
        }
        cases.Add(("overshoot", Flight(6), (x, z) => z < 4f * run ? z / run * rise : z <= 5.25f * run ? 4f * rise : 4f * rise - (z - 5.25f * run) / (1.75f * run) * 0.5f));
        cases.Add(("floors on", Flight(6), (x, z) => z < 4f * run ? z / run * rise : z <= 5f * run ? 4f * rise - (x + 1f) * 0.25f - (5f * run - z) * 0.25f : 4f * rise));
        int floorsKept = 0;
        foreach (var (name, plan, ground) in cases)
        {
            StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(plan, ground, RoadStairRuns.Width, _ => true);
            StairLayout.TerminalExit? exit = StairLayout.Terminal(plan);
            if (exit == null || exit.Descending || exit.Pieces[0].Kind != StairPieceKind.Landing) continue;
            floorsKept++;
            StairEnds.End top = RoadStairRuns.TopEnd(plan, ground)!;
            Assert.True(RoadStairRuns.WayOffItsTop(plan, ground) && top.Verdict == StairEnds.Verdict.Ok, $"{name}: kept {finish}; {StairEnds.Line(top)}");
            var (back, heading, _, _, height) = StairLayout.Footprint(exit.Pieces[0]);
            Assert.True(StairEnds.Lands(back, StairLayout.Forward(heading), height, RoadStairRuns.Width, ground), $"{name}: its floor does not land by the layout's test");
        }
        Assert.Equal(2, floorsKept);
    }
}
