using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A Mistlands valley viaduct's banks moved along its line where the road meets no end of the banks the search bought
/// (StructureEnds.MeetEnds over DvergrViaduct.MovedBanks): a captured valley whose far bank stands on its wall, the road as
/// generation searched it, met again on the production end rule and the viaduct's own predicate. The move is taken only
/// where the search's test of a viaduct's line allows it (RoadPathfinder.ViaductLineAllowed, asked through the generator's own
/// RoadPathfinder.MovedViaductAllowed), never takes a bank a bought staircase climbs to, and changes nothing where no move is
/// asked.
/// </summary>
public sealed class ViaductBankMoveTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public ViaductBankMoveTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>The captured call: its window world (ApproachCaptureFile's), the path, the bought stair steps and the viaduct
    /// as DvergrViaduct.AddJumps records it from the path's bank points.</summary>
    private sealed class Capture
    {
        public ApproachCaptureFile.World World = null!;
        public List<Vector2> Path = new();
        public List<(Vector2 from, Vector2 to)> Steps = new();
        public int From, To;
        public Vector2 FromBank, ToBank;

        public static Capture Load(string name)
        {
            var file = ApproachCaptureFile.Load(name);
            var c = new Capture { World = new ApproachCaptureFile.World(file) };
            static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            foreach (string line in File.ReadLines(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))
            {
                string[] f = line.Split(',');
                switch (f[0])
                {
                    case "path": c.Path.Add(new Vector2(F(f[1]), F(f[2]))); break;
                    case "step": c.Steps.Add((new Vector2(F(f[1]), F(f[2])), new Vector2(F(f[3]), F(f[4])))); break;
                    case "viaduct":
                        c.From = int.Parse(f[1]); c.To = int.Parse(f[2]);
                        c.FromBank = new Vector2(F(f[3]), F(f[4])); c.ToBank = new Vector2(F(f[5]), F(f[6]));
                        break;
                }
            }
            return c;
        }

        public bool IsStep(Vector2 a, Vector2 b) => Steps.Any(s =>
            (Vector2.Distance(a, s.from) <= 1f && Vector2.Distance(b, s.to) <= 1f) || (Vector2.Distance(a, s.to) <= 1f && Vector2.Distance(b, s.from) <= 1f));

        /// <summary>The viaduct as the generator records it from the searched path (DvergrViaduct.AddJumps).</summary>
        public List<RoadCrossing> Crossings()
        {
            var crossings = new List<RoadCrossing>();
            DvergrViaduct.AddJumps(Path, new[] { (FromBank, ToBank) }, World, crossings);
            return crossings;
        }
    }

    private const string Wall = "viaduct-ends-capture-far-wall.txt";

    private (RoadCrossing? unmet, List<Vector2> path, List<RoadCrossing> crossings) Meet(Capture c, Func<RoadCrossing, RoadCrossing, bool>? mayMove)
    {
        var path = new List<Vector2>(c.Path);
        var crossings = c.Crossings();
        RoadCrossing? unmet = StructureEnds.MeetEnds(path, crossings, c.World, RoadNetworkGenerator.EarthworkLimit, c.IsStep,
            RoadNetworkGenerator.RoadWidth, mayMove);
        return (unmet, path, crossings);
    }

    [Fact]
    public void AViaductEndNoWayMeetsIsMetOnBanksMovedAlongItsLine()
    {
        var c = Capture.Load(Wall);
        _scope.WithWorld(c.World);
        PlainRoads.WithModDefaults(() =>
        {
            // The banks the search bought: the far end is met by no way within the earthwork cap.
            var (bought, _, boughtCrossings) = Meet(c, null);
            Assert.Same(Assert.Single(boughtCrossings), bought);

            var (unmet, path, crossings) = Meet(c, (_, _) => true);
            Assert.Null(unmet);
            RoadCrossing viaduct = Assert.Single(crossings);
            // On the same line: its banks moved along it, by no more than a search cell, a deck module at a time.
            Vector2 dir = (c.ToBank - c.FromBank).normalized;
            float near = Vector2.Dot(c.FromBank - viaduct.FromBank, dir), far = Vector2.Dot(viaduct.ToBank - c.ToBank, dir);
            Assert.True(Mathf.Abs(near) > 1e-3f || Mathf.Abs(far) > 1e-3f, "no bank moved");
            Assert.True(Mathf.Abs(near) <= RoadConstants.PathfindingCellSize && Mathf.Abs(far) <= RoadConstants.PathfindingCellSize);
            Assert.Equal(0f, Mathf.Abs(near) % DvergrViaduct.Module, 3);
            Assert.Equal(0f, Mathf.Abs(far) % DvergrViaduct.Module, 3);
            Assert.True(Mathf.Abs(Vector2.Dot(viaduct.FromBank - c.FromBank, new Vector2(-dir.y, dir.x))) < 1e-3f, "off the line");
            // The viaduct's own predicate lays it there, and its ends are met and recorded as structures.
            Assert.NotNull(DvergrViaduct.ValleyCrossing(viaduct.FromBank, viaduct.ToBank, c.World, out string refused));
            Assert.True(refused.Length == 0, refused);
            Assert.True(viaduct.ValleyJump);
            Assert.True(viaduct.FromEnd is { IsStructure: true } && viaduct.ToEnd is { IsStructure: true });
            // The road runs to the deck's ends: the met road is continuous through the crossing's indices.
            Assert.InRange(viaduct.FromIndex, 0, path.Count - 1);
            Assert.InRange(viaduct.ToIndex, viaduct.FromIndex + 1, path.Count - 1);
        });
    }

    [Fact]
    public void AMoveTheSearchsLineTestRefusesIsNotTakenAndTheRoadIsAsTheBoughtBanksLeftIt()
    {
        var c = Capture.Load(Wall);
        _scope.WithWorld(c.World);
        PlainRoads.WithModDefaults(() =>
        {
            var (bought, boughtPath, boughtCrossings) = Meet(c, null);
            int asked = 0;
            var (unmet, path, crossings) = Meet(c, (from, moved) => { asked++; return false; });
            Assert.True(asked > 0, "no move asked");
            Assert.NotNull(unmet);
            Assert.Same(Assert.Single(crossings), unmet);
            Assert.Equal(boughtPath, path);
            RoadCrossing a = boughtCrossings[0], b = crossings[0];
            Assert.Equal((a.FromBank, a.ToBank, a.FromIndex, a.ToIndex, a.RaiseAtMost, a.FromEnd, a.ToEnd),
                (b.FromBank, b.ToBank, b.FromIndex, b.ToIndex, b.RaiseAtMost, b.FromEnd, b.ToEnd));
            Assert.Equal(c.FromBank, b.FromBank);
            Assert.Equal(c.ToBank, b.ToBank);
            _ = bought;
        });
    }

    [Fact]
    public void EveryMovedBankIsTheViaductsOwnPredicateOnTheSameLine()
    {
        var c = Capture.Load(Wall);
        _scope.WithWorld(c.World);
        PlainRoads.WithModDefaults(() =>
        {
            RoadCrossing bought = Assert.Single(c.Crossings());
            var moves = DvergrViaduct.MovedBanks(bought, c.World, nearFirst: false).ToList();
            Assert.NotEmpty(moves);
            Vector2 dir = bought.Direction, side = new(-dir.y, dir.x);
            foreach (RoadCrossing m in moves)
            {
                Assert.NotNull(DvergrViaduct.ValleyCrossing(m.FromBank, m.ToBank, c.World, out _));
                Assert.True(Mathf.Abs(Vector2.Dot(m.FromBank - bought.FromBank, side)) < 1e-3f && Mathf.Abs(Vector2.Dot(m.ToBank - bought.FromBank, side)) < 1e-3f);
                Assert.InRange(Mathf.Abs(Vector2.Dot(bought.FromBank - m.FromBank, dir)), 0f, RoadConstants.PathfindingCellSize + 1e-3f);
                Assert.InRange(Mathf.Abs(Vector2.Dot(m.ToBank - bought.ToBank, dir)), 0f, RoadConstants.PathfindingCellSize + 1e-3f);
            }
            // The far end's moves first (the end the road did not meet), then the near end's.
            Assert.True(Vector2.Distance(moves[0].FromBank, bought.FromBank) < 1e-3f, "the near bank moved first");
        });
    }

    /// <summary>The capture with a staircase the search bought up to one of the viaduct's banks: the road climbs it onto the rim
    /// and jumps from its top (<paramref name="near"/>: the near bank; else it lands on the far bank and climbs on from it).</summary>
    private static (Vector2 below, Vector2 top) ClimbTo(Capture c, bool near)
    {
        (Vector2 below, Vector2 top) step = near ? (c.Path[c.From - 1], c.Path[c.From]) : (c.Path[c.To + 1], c.Path[c.To]);
        c.Steps.Add(step);
        return step;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMoveNeverMovesTheTopOfABoughtStaircaseTheRoadJumpsFrom(bool near)
    {
        var c = Capture.Load(Wall);
        var (below, top) = ClimbTo(c, near);
        Assert.Equal(top, near ? c.FromBank : c.ToBank);
        _scope.WithWorld(c.World);
        PlainRoads.WithModDefaults(() =>
        {
            // Every move asked keeps the staircase whole: the bank it climbs to stands where the search bought it.
            var asked = new List<(Vector2 from, Vector2 to)>();
            Meet(c, (_, moved) => { asked.Add((moved.FromBank, moved.ToBank)); return false; });
            Assert.NotEmpty(asked);
            Assert.All(asked, m => Assert.True(c.IsStep(below, near ? m.from : m.to), $"a move took the staircase's top from {top} to {(near ? m.from : m.to)}"));
            // Taken where the road meets it, the staircase is still on the road as bought, its top the viaduct's bank.
            var (_, path, crossings) = Meet(c, (_, _) => true);
            RoadCrossing viaduct = Assert.Single(crossings);
            Assert.Equal(top, near ? viaduct.FromBank : viaduct.ToBank);
            Assert.Contains(Enumerable.Range(1, path.Count - 1), k => c.IsStep(path[k - 1], path[k]) && (path[k - 1] == top || path[k] == top));
        });
    }

    /// <summary>A pathfinder holding the capture's bought jump and stair steps as its search left them, the jump on
    /// <paramref name="ground"/>.</summary>
    private static RoadPathfinder Bought(Capture c, RoadPathfinder.SearchGround ground = default)
    {
        var finder = new RoadPathfinder(c.World) { Stairs = true, Viaducts = true };
        finder.LastStairSteps.AddRange(c.Steps);
        finder.LastViaductJumps.Add((c.FromBank, c.ToBank));
        finder.LastViaductGrounds.Add(ground);
        return finder;
    }

    [Fact]
    public void TheGeneratorsMoveTestRefusesAMovedLineTheRoadIsBannedFrom()
    {
        var c = Capture.Load(Wall);
        _scope.WithWorld(c.World);
        PlainRoads.WithModDefaults(() =>
        {
            // The generator's own closure (RoadNetworkGenerator.MeetEnds) moves the banks as the end rule meets them.
            var finder = Bought(c);
            var path = new List<Vector2>(c.Path);
            var crossings = c.Crossings();
            Assert.Null(RoadNetworkGenerator.MeetEnds(finder, path, crossings, RoadNetworkGenerator.RoadWidth));
            RoadCrossing chosen = Assert.Single(crossings);
            Assert.False(DvergrViaduct.SameJump(chosen.FromBank, chosen.ToBank, c.FromBank, c.ToBank), "no bank moved");
            // That line banned for the road (RoadPathfinder.BanViaduct, as a refused viaduct is): the move is not taken, as the
            // search would not buy it.
            var banned = Bought(c);
            banned.BanViaduct(chosen.FromBank, chosen.ToBank);
            path = new List<Vector2>(c.Path);
            crossings = c.Crossings();
            RoadNetworkGenerator.MeetEnds(banned, path, crossings, RoadNetworkGenerator.RoadWidth);
            RoadCrossing again = Assert.Single(crossings);
            Assert.False(DvergrViaduct.SameJump(again.FromBank, again.ToBank, chosen.FromBank, chosen.ToBank), "a banned line was taken");
            // A viaduct no jump of the road's searches bought is not moved.
            var none = new RoadPathfinder(c.World) { Stairs = true, Viaducts = true };
            none.LastStairSteps.AddRange(c.Steps);
            path = new List<Vector2>(c.Path);
            crossings = c.Crossings();
            Assert.NotNull(RoadNetworkGenerator.MeetEnds(none, path, crossings, RoadNetworkGenerator.RoadWidth));
            Assert.Equal(c.Path, path);
        });
    }

    [Fact]
    public void AMovedLineIsAskedOnTheGroundItsJumpWasBoughtOnAndOnItsHeading()
    {
        var world = new DvergrViaductTests.MistValley();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        Vector2 a = new(-32f, 8f), b = new(32f, 8f);
        var bought = new RoadCrossing { FromBank = a, ToBank = b };
        var moved = new RoadCrossing { FromBank = a - new Vector2(2f, 0f), ToBank = b };
        // A zone the road goes round under the deck. A manual road's own end zones are exempt (NetworkCutoff.AvoidsSegment):
        // a leg that ends in it may cross it; the leg that bought the jump ends elsewhere.
        using var planning = NetworkCutoff.AvoidWhilePlanning(new HashSet<Vector2s> { new(0, 0) }, 0f);
        var lastLeg = new RoadPathfinder.SearchGround(new Vector2(0f, 8f), new Vector2(0f, 20f), default);
        var buyingLeg = new RoadPathfinder.SearchGround(new Vector2(-200f, 8f), new Vector2(-40f, 8f), default);
        finder.LastViaductJumps.Add((a, b));
        finder.LastViaductGrounds.Add(lastLeg);
        Assert.True(finder.MovedViaductAllowed(bought, moved));
        finder.LastViaductGrounds[0] = buyingLeg;
        Assert.False(finder.MovedViaductAllowed(bought, moved));
        // On a heading no valley viaduct is offered on (RoadRules.ViaductHeadings: the grid's eight), no line is allowed.
        Vector2 knight = new(32f, 40f);
        finder.LastViaductJumps[0] = (a, knight);
        finder.LastViaductGrounds[0] = lastLeg;
        Assert.False(finder.MovedViaductAllowed(new RoadCrossing { FromBank = a, ToBank = knight }, new RoadCrossing { FromBank = a - new Vector2(2f, 1f), ToBank = knight }));
    }

    [Fact]
    public void AManualRoadAndGenerationPlanTheSameMovedViaductWhichIsStillTheBoughtJump()
    {
        var c = Capture.Load(Wall);
        _scope.WithWorld(c.World);
        PlainRoads.WithModDefaults(() =>
        {
            // The searched road planned through the generator's one entry (RoadNetworkGenerator.PlanSearchedPath), as generation
            // plans it and as a manual road plans its marked legs.
            RoadCrossing Planned(IReadOnlyList<Vector2>? marks)
            {
                RoadSpatialGrid.Clear();
                var plan = RoadNetworkGenerator.PlanSearchedPath(Bought(c), new List<Vector2>(c.Path), c.Path[0], 0f, c.Path[c.Path.Count - 1], 0f,
                    false, false, false, false, RoadNetworkGenerator.RoadWidth, default, marks);
                Assert.NotNull(plan.Crossings);
                return Assert.Single(plan.Crossings!, x => x.ValleyJump);
            }
            RoadCrossing generated = Planned(null), manual = Planned(new[] { c.Path[0], c.Path[c.Path.Count - 1] });
            Assert.Equal((generated.FromBank, generated.ToBank), (manual.FromBank, manual.ToBank));
            Assert.False(DvergrViaduct.SameJump(generated.FromBank, generated.ToBank, c.FromBank, c.ToBank), "no bank moved");
            foreach (RoadCrossing v in new[] { generated, manual })
            {
                // Still the jump the search bought where later code asks it: its searched moves are the bought jump (its bans
                // match it, RoadNetworkGenerator.BanForTheNextSearch), and the laid viaduct is the same structure as itself
                // (DvergrViaduct.IsTooClose), not another back to back with it.
                Assert.NotNull(v.Searched);
                Assert.Contains(v.Searched!, j => DvergrViaduct.SameJump(j.from, j.to, c.FromBank, c.ToBank));
                Assert.False(DvergrViaduct.IsTooClose(v.FromBank, v.ToBank, new[] { (v.FromBank, v.ToBank) }));
            }
        });
    }

    [Fact]
    public void TheSearchsViaductLineTestRefusesALineOverAnotherRoad()
    {
        var world = new DvergrViaductTests.MistValley();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        Vector2 a = new(-32f, 0f), b = new(32f, 0f);
        Assert.True(finder.ViaductLineAllowed(a, b, a, b, default));
        // A road along the valley floor, under the deck.
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(0f, -60f), new(0f, 60f) }, 4f, world));
        Assert.False(finder.ViaductLineAllowed(a, b, a, b, default));
    }
}
