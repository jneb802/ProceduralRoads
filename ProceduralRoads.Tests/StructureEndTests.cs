using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The one owner of how a structure's end meets the road (StructureEnds): one owner for how blocks/stairs and roads meet
/// each other, so they are consistent. Four viaduct ends a
/// player could not walk onto, reproduced as valleys whose far bank point stands off the road that comes to it; a
/// good end stays as it was; and a bank seam.
/// <para>Written against what any version has (DvergrViaduct.Ends: the deck's end edge and
/// the stairs' foot; the road as stored).</para>
/// </summary>
public sealed class StructureEndTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A Mistlands valley along z: the near (west) flat at <see cref="Top"/>, a floor <see cref="Depth"/> under
    /// it, faces of 1.2; past the far (east) rim the ground is <see cref="Far"/> of the metres beyond the rim. Sea past
    /// |x|, |z| = 200.</summary>
    internal sealed class RimValley : WorldGenerator
    {
        public float Top = 60f, Depth = 18f, FloorHalf = 8f, Face = 1.2f;
        public Func<float, float> Far = _ => 0f;
        public float Rim => FloorHalf + Depth / Face;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float floor = Top - Depth;
            float valley = Mathf.Min(Top, floor + Mathf.Max(0f, Mathf.Abs(wx) - FloorHalf) * Face);
            if (wx <= Rim) return valley;
            return Top + Far(wx - Rim);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 78;
    }

    public StructureEndTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.PieceCommitted = null;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>The road across <paramref name="world"/> from west to east (z from <paramref name="z0"/> to
    /// <paramref name="z1"/>), and its one viaduct.</summary>
    private RoadCrossing Build(WorldGenerator world, List<RoadSpatialGrid.PlannedPath> roads, float z0 = 0f, float z1 = 0f)
    {
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, z0), 0f, new Vector3(110f, 0f, z1), 0f, 4f, "across"), "no road");
        return Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
    }

    /// <summary>The road's own height where it meets each end of <paramref name="viaduct"/> (the stairs' foot, or the deck's
    /// edge), against the end's walking height there: a player walks on within a step.</summary>
    private static void AssertEveryEndReachable(RoadCrossing viaduct, WorldGenerator world, List<RoadSpatialGrid.PlannedPath> roads, string site)
    {
        float deck = DvergrViaduct.DeckHeight(viaduct, world);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        foreach (var (end, which) in new[] { (near, "near"), (far, "far") })
        {
            float walk = deck - end.Steps * BridgeLayout.StairRise;
            // The stored road nearest the end's meeting point.
            var points = roads.SelectMany(r => r.Points.Select((p, i) => (p, h: r.Heights[i]))).OrderBy(x => Vector2.Distance(x.p, end.Meet)).ToList();
            Assert.True(points.Count > 0 && Vector2.Distance(points[0].p, end.Meet) <= 1f, $"{site}, {which} end: no road at its foot {end.Meet}");
            float road = points[0].h;
            Assert.True(Mathf.Abs(road - walk) <= 0.5f,
                $"{site}, {which} end: the road meets it at {road:F2} m, its {(end.Steps == 0 ? "deck" : $"{end.Steps} stairs' foot")} at {walk:F2} m: a player cannot walk on");
        }
    }

    /// <summary>The 80 m span's east end at (6986.4, 50.8, -883.6) on a test world: a player can't walk up from the road to the top of
    /// the deck. The rim is a lip: 45.0 m at the span's recorded bank point but 51.4 m eight metres on, where the road comes.
    /// Here the far bank point stands 2 m under the near flat and the ground climbs 0.9 a metre past it to 4.6 m over it.</summary>
    [Fact]
    public void AStepRimRisingPastTheBankPoint()
    {
        var world = new RimValley { Far = d => -2f + Mathf.Min(d, 7.3f) * 0.9f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "east");
    }

    /// <summary>A 64 m N-S span's end that can't even be reached: a deck end block about 2 m
    /// over the road, no stairs. The far bank holds the deck's height for 3 m past the rim, then the road's ground lies 2 m
    /// lower.</summary>
    [Fact]
    public void ADeckEndTwoMetresOverTheRoad()
    {
        var world = new RimValley { Far = d => d < 3f ? 0f : -2f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "N-S");
    }

    /// <summary>A 57 m span's north-west end, with another large step up that can't be walked over: the bank point at the deck's height, the ground falling away past it, 0.25 a metre to 1.5 m under it,
    /// where the road comes.</summary>
    [Fact]
    public void TheBankFallingAwayPastItsPoint()
    {
        var world = new RimValley { Far = d => -0.25f * Mathf.Min(d, 6f) };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "far");
    }

    /// <summary>A 56 m span at (-5476.7, 82.1, 5717.3) with a similar deck height problem: the road climbs between
    /// rocks to a deck end about one marble block and the course, 2-2.5 m, over it. A 1.5 m lip at the deck's height at the
    /// rim, the road's ground 2.5 m under it.</summary>
    [Fact]
    public void ALipAtTheDecksHeightTheRoadTwoAndAHalfMetresUnder()
    {
        var world = new RimValley { Far = d => d < 1.5f ? 0f : -2.5f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "lip");
    }

    /// <summary>A 57 m span's south-east end, which was fine: level banks at the deck's height. It stays as it was: the deck at the
    /// flats' height, no stairs, the road onto it level.</summary>
    [Fact]
    public void AGoodEndStaysAsItWas()
    {
        var world = new RimValley();
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadCrossing viaduct = Build(world, roads);
        Assert.Equal(world.Top, DvergrViaduct.DeckHeight(viaduct, world), 3);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        Assert.Equal((0, 0), (near.Steps, far.Steps));
        Assert.DoesNotContain(BridgeStructures.Solve(viaduct, world, 1), p => p.Kind == StructurePieceKind.Stair);
        AssertEveryEndReachable(viaduct, world, roads, "good end");
    }

    /// <summary>A 66 m river viaduct's north-west bank (2187, -6265), where the road/bridge boundary needed a
    /// smoother transition, such as extending the earthwork slightly: the road came in high and to one side of the deck
    /// end. Its last 6-8 m is levelled to the deck and centred on its line. Here the road comes at the valley on a slant
    /// (z -40 to 40) over a gently rising near flat.</summary>
    [Fact]
    public void TheRoadsLastMetresAreLevelWithTheDeckAndOnItsLine()
    {
        var world = new RimValley { Far = _ => 0f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadCrossing viaduct = Build(world, roads, -40f, 40f);
        float deck = DvergrViaduct.DeckHeight(viaduct, world);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        foreach (var end in new[] { near, far })
        {
            float walk = deck - end.Steps * BridgeLayout.StairRise;
            Vector2 line = -end.Up, across = new(-line.y, line.x);
            int seen = 0;
            foreach (var r in roads)
                for (int i = 0; i < r.Points.Count; i++)
                {
                    float along = Vector2.Dot(r.Points[i] - end.Meet, line);
                    if (along < -0.25f || along > 6f || Vector2.Distance(r.Points[i], end.Meet) > 8f) continue;
                    seen++;
                    Assert.True(Mathf.Abs(r.Heights[i] - walk) <= 0.1f, $"{along:F1} m out from the end at {end.Meet} the road stands {r.Heights[i]:F2}, the end {walk:F2}");
                    Assert.True(Mathf.Abs(Vector2.Dot(r.Points[i] - end.Meet, across)) <= 0.5f, $"{along:F1} m out from the end the road is {Vector2.Dot(r.Points[i] - end.Meet, across):F2} m off its line");
                }
            Assert.True(seen >= 6, $"only {seen} road point(s) in the last 6 m before the end at {end.Meet}");
        }
    }

    // ------------------------------------------------- an end met level, the road from the side

    /// <summary>A Mistlands river along z (channel |x| under 30, its bed at 28) whose east bank stands at 38 m, walled but for
    /// a gap at z within 3 of 0, and whose west bank point stands 2 m under it, at 36: the ground falls away west of the bank
    /// along the crossing's line (0.6 a metre past 33 m out, to 5.4 m down) and rises to the south (0.32 a metre, to 5.1 m
    /// up), the fall fading out 8 m south of the line.</summary>
    internal sealed class BankRisingBeside : WorldGenerator
    {
        /// <summary>How far the ground south of the west bank rises over it: 5.1 m, past the deck's height.</summary>
        public float SouthRise = 0.32f * 16f;
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 240f || Mathf.Abs(z) > 200f) return 20f;
            if (x > -30f && x < 30f) return 28f;
            if (x >= 30f) return x < 36f ? 38f + Mathf.Min(Mathf.Max(0f, Mathf.Abs(z) - 3f) * 4f, 12f) : 38f;
            float rise = Mathf.Min(0.32f * Mathf.Clamp(-z, 0f, 16f), SouthRise);
            float fade = z >= 0f ? 1f : Mathf.Clamp01(1f + z / 8f);
            return 36f + rise - 0.6f * Mathf.Clamp(-33f - x, 0f, 9f) * fade;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = GetHeight(x, z) < 29f && Mathf.Abs(x) < 240f ? 1f : 0f;
            width = 60f;
        }
        public override int GetSeed() => 61;
    }

    /// <summary>
    /// The check for every kit: at each end of <paramref name="c"/>'s structure as solved (its pieces, not the owner's
    /// account of them), stairs lead to the road: road within a metre of their foot at its height, and none leaving from
    /// their top at the deck's height. A tour found a 114 m Dvergr wood bridge whose end stairs went down to rough ground
    /// while the road came from the side to the deck's end at its height.
    /// </summary>
    internal static void AssertNoStairsLeadingNowhere(RoadCrossing c, WorldGenerator world, IEnumerable<RoadSpatialGrid.PlannedPath> roads, string site)
    {
        var road = roads.SelectMany(r => r.Points.Select((p, i) => (p, h: r.Heights[i]))).ToList();
        var stairs = BridgeStructures.Solve(c, world, 1).FindAll(p => p.Kind == StructurePieceKind.Stair);
        foreach (bool near in new[] { true, false })
        {
            Vector2 bank = near ? c.FromBank : c.ToBank, outward = near ? -c.Direction : c.Direction;
            Vector2 other = near ? c.ToBank : c.FromBank;
            var flight = stairs.FindAll(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), bank) < Vector2.Distance(new Vector2(p.Position.x, p.Position.z), other));
            if (flight.Count == 0) continue;
            float lowest = flight.Min(p => p.Position.y), highest = flight.Max(p => p.Position.y), deck = highest + BridgeLayout.StairRise;
            Vector2 Mean(float y)
            {
                var at = flight.FindAll(p => Mathf.Abs(p.Position.y - y) < 0.01f);
                return new Vector2(at.Average(p => p.Position.x), at.Average(p => p.Position.z));
            }
            Vector2 foot = Mean(lowest) + outward * BridgeLayout.StairHalfRun, top = Mean(highest) - outward * BridgeLayout.StairHalfRun;
            string which = near ? "near" : "far";
            Assert.True(road.Exists(x => Vector2.Distance(x.p, foot) <= 1f && Mathf.Abs(x.h - lowest) <= StructureEnds.StepTolerance),
                $"{site}, {which} end: its {flight.Count / 2} stairs go down to {foot} at {lowest:F1} m, and no road meets their foot");
            Assert.False(road.Exists(x => Vector2.Distance(x.p, top) <= 1f && Mathf.Abs(x.h - deck) <= StructureEnds.StepTolerance),
                $"{site}, {which} end: the road leaves from the top of its stairs at {top}, at the deck's height {deck:F1} m");
        }
    }

    /// <summary>
    /// A 114 m Dvergr wood bridge's west end on a test world (-6224.5, 4904): its end stairs went down the crossing's line
    /// from the deck's end to rough ground with no road, while the road came from the south, where the bank rises to the
    /// deck's height, and left the deck's end sideways from the stairs' top. Their foot (16 m out, 6 m down) could not be met
    /// (the road, refused, was planned again with its ends as they were, which no check looked at). Here the road comes to
    /// the west bank from the south: the end is met level (no stairs), the road at the deck's height on its last apron,
    /// brought onto the end's line from its landing pad a road's width out, and no stairs lead nowhere at either end.
    /// </summary>
    [Fact]
    public void TheRoadFromTheSideMeetsAnEndLevelWhereTheBankThereIsAtTheDecksHeight()
    {
        var world = new BankRisingBeside();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, Bridges = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-45f, 0f, -40f), 0f, new Vector3(70f, 0f, 0f), 0f, 4f, "from the side"), RoadSpatialGrid.LastRefusal);
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, world));
        var (near, far) = BridgeStructures.Ends(c, world);
        float deck = BridgeStructures.DeckHeight(c, world);

        // The west end: met level from its pad, no stairs laid there.
        Assert.True(MetEnds.Level(c, world).near);
        Assert.Equal(0, near.Steps);
        Assert.True(c.FromPad, "the road from the south does not meet the west end from its pad");
        Assert.DoesNotContain(BridgeStructures.Solve(c, world, 1), p => p.Kind == StructurePieceKind.Stair && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), c.FromBank) < 20f);
        // The road on its last apron level with the deck, up to the deck's end.
        var onApron = roads.SelectMany(r => r.Points.Select((p, i) => (p, h: r.Heights[i])))
            .Where(x => Vector2.Dot(x.p - near.Meet, -near.Up) is float along && along >= -0.25f && along <= StructureEnds.ApronLength && Vector2.Distance(x.p, near.Meet) <= StructureEnds.ApronLength + 1f).ToList();
        Assert.True(onApron.Count >= 6, $"only {onApron.Count} road point(s) on the last apron to the west end");
        Assert.All(onApron, x => Assert.True(Mathf.Abs(x.h - deck) <= 0.1f, $"the road at {x.p} stands {x.h:F2}, the deck {deck:F2}"));
        Assert.Null(MetEnds.Unmet(roads, new List<RoadCrossing> { c }, world));
        // The east end, its bank at the deck's height, is low (StructureEnds.Low): met by its one step; neither end leads
        // nowhere.
        Assert.Equal(1, far.Steps);
        AssertNoStairsLeadingNowhere(c, world, roads, "bridge end from the side");

        // The end as the tour found it, which the check refuses: its stairs down the line, the road come from the south to
        // the deck's end at its height.
        var asFound = RoadCrossing.Between(c.FromBank, c.ToBank, c.RiverbedHeight, c.FairwayCenter, c.FairwayWidth, CrossingKind.Bridge);
        Assert.True(BridgeStructures.Ends(asFound, world).near.Steps > 3, "the end as found has its stairs down the line");
        var fromTheSouth = Piece(c.FromBank + new Vector2(-4f, -7f), c.FromBank, deck);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoStairsLeadingNowhere(asFound, world, new[] { fromTheSouth }, "as found"));
    }

    /// <summary>
    /// The west end of <see cref="TheRoadFromTheSideMeetsAnEndLevelWhereTheBankThereIsAtTheDecksHeight"/> where the ground
    /// south of the bank rises only half a metre: no level bank beside it (the bank is not at the deck's height), and the
    /// whole run's eight stairs land 6 m under where the road arrives, the deck 1.5 m over it. Priced
    /// (StructureEnds.Price), the flight down to its pad and the road climbing back up from it cost far more than the end
    /// met at the deck's height or by its one step (a low end, StructureEnds.Low), filled to it within the cap. Its road
    /// reaches it, and the crossing saves and reads back so.
    /// </summary>
    [Fact]
    public void AnEndWithNoLevelBankBesideItIsNotAFlightTheRoadClimbsBackFrom()
    {
        var world = new BankRisingBeside { SouthRise = 0.5f };
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, Bridges = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-45f, 0f, -40f), 0f, new Vector3(70f, 0f, 0f), 0f, 4f, "from the side"), RoadSpatialGrid.LastRefusal);
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
        var (near, _) = BridgeStructures.Ends(c, world);

        // The end as the kit lays it: eight stairs down the line, and no level end from the road's side.
        var asFound = RoadCrossing.Between(c.FromBank, c.ToBank, c.RiverbedHeight, c.FairwayCenter, c.FairwayWidth, CrossingKind.Bridge);
        StructureEnds.End whole = BridgeStructures.Ends(asFound, world).near;
        Assert.Equal(8, whole.Steps);
        float G(Vector2 p) => world.GetHeight(p.x, p.y);

        // Met by one step at most: not the flight to its pad.
        Assert.True(near.Steps <= 1, $"{near.Steps} of the whole run's {whole.Steps} stairs laid");
        Assert.Equal(2 * near.Steps, BridgeStructures.Solve(c, world, 1).Count(p => p.Kind == StructurePieceKind.Stair && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), c.FromBank) < 20f));
        var atFoot = roads.SelectMany(r => r.Points.Select((p, i) => (p, h: r.Heights[i])))
            .Where(x => Vector2.Dot(x.p - near.Meet, -near.Up) >= -0.25f && Vector2.Distance(x.p, near.Meet) <= 3f).ToList();
        Assert.True(atFoot.Count >= 2, $"only {atFoot.Count} road point(s) within 3 m of the end");
        Assert.All(atFoot, x => Assert.True(StructureEnds.Reachable(x.h, near), $"the road at {x.p} stands {x.h:F2}, the end {near.MeetHeight:F2}"));
        Assert.True(atFoot.Max(x => x.h - G(x.p)) <= RoadConstants.TerrainDeltaMax);
        Assert.Null(MetEnds.Unmet(roads, new List<RoadCrossing> { c }, world));
        AssertNoStairsLeadingNowhere(c, world, roads, "filled end");

        // Saved and read back so.
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { c }), loaded));
        RoadCrossing back = Assert.Single(loaded);
        Assert.Equal((c.FromEnd, c.ToEnd), (back.FromEnd, back.ToEnd));
        Assert.Equal(near.Steps, BridgeStructures.Ends(back, world).near.Steps);
    }

    /// <summary>The road on <paramref name="path"/> brought to <paramref name="c"/>'s ends as the one owner decides
    /// (StructureEnds.MeetEnds), from the path as given (its indices 2 and 3) and nothing decided yet; the crossing returned
    /// where an end is not met.</summary>
    private static RoadCrossing? Meet(List<Vector2> path, RoadCrossing c, WorldGenerator world, float cap)
    {
        c.FromEnd = c.ToEnd = null;
        c.FromIndex = 2; c.ToIndex = 3;
        return StructureEnds.MeetEnds(new List<Vector2>(path), new List<RoadCrossing> { c }, world, cap, null);
    }

    /// <summary>
    /// The choice on its own (StructureEnds.MeetEnds) at the west end where the ground south of the bank stays 2 m under
    /// the deck, the road come straight from the south: no level end at a high bank, and no row it can be brought to the foot
    /// of down the line, but the landing at the last row fits, the road filled 2 m up to the deck's end, met from its pad a
    /// road's width out on the line. Under a 1 m cap, or with a place on the bank west and south of the end, no way fits: the end is not met, and the road is searched
    /// round the crossing.
    /// </summary>
    [Fact]
    public void AnEndIsLandedOnWhereItsFillFitsAndElseTheRoadIsRefused()
    {
        var world = new BankRisingBeside { SouthRise = 0f };
        _scope.WithWorld(world);
        var c = RoadCrossing.Between(new Vector2(-30f, 0f), new Vector2(30f, 0f), 28f, new Vector2(0f, 0f), 60f, CrossingKind.Bridge);
        var path = new List<Vector2> { new(-31f, -40f), new(-30.5f, -20f), new(-30f, 0f), new(30f, 0f), new(70f, 0f) };
        var (whole, wholeFar) = BridgeStructures.Ends(c, world);
        Assert.True(whole.Steps > 3, "the end as the kit lays it has its stairs down the line");

        Assert.Null(Meet(path, c, world, RoadConstants.TerrainDeltaMax));
        Assert.Equal(new StructureEnds.End(whole.Edge, whole.Up, whole.Height, 0), c.FromEnd);
        Assert.True(c.FromPad, "the road from the south meets the deck's edge from its pad");
        Assert.Equal(0, BridgeStructures.Ends(c, world).near.Steps);
        Assert.Equal(wholeFar, c.ToEnd);

        Assert.Same(c, Meet(path, c, world, 1f));
        try
        {
            RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(-38f, -4f), 9f) });
            Assert.Same(c, Meet(path, c, world, RoadConstants.TerrainDeltaMax));
        }
        finally { RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>()); }
    }

    /// <summary>
    /// The west end of <see cref="AnEndIsLandedOnWhereItsFillFitsAndElseTheRoadIsRefused"/> with an older road along the
    /// crossing's line from the west, at the ground's height, where the end's stairs would stand: the landing raises it as
    /// the staircase landing raises the road at an old foot (RoadStairRuns.Land, RaiseOthers), level with the deck on the
    /// landing's ground and eased down west at the road grade, within the cap. Laid as the generator lays it, both roads
    /// stand level on the landing, and the network and the crossing save and read back so.
    /// </summary>
    [Fact]
    public void AnEndsLandingRaisesTheRoadOnItsGroundToo()
    {
        var world = new BankRisingBeside { SouthRise = 0f };
        _scope.WithWorld(world);
        var older = new List<Vector2>();
        for (float x = -80f; x <= -34f + 0.01f; x += 2f) older.Add(new Vector2(x, 0f));
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(older, older.Select(p => world.GetHeight(p.x, p.y)).ToList(),
            older.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 46f, false));
        var c = RoadCrossing.Between(new Vector2(-30f, 0f), new Vector2(30f, 0f), 28f, new Vector2(0f, 0f), 60f, CrossingKind.Bridge);
        var path = new List<Vector2> { new(-31f, -40f), new(-30.5f, -20f), new(-30f, 0f), new(30f, 0f), new(70f, 0f) };
        float deck = BridgeStructures.DeckHeight(c, world);
        StructureEnds.End whole = BridgeStructures.Ends(c, world).near;
        Assert.Null(Meet(path, c, world, RoadConstants.TerrainDeltaMax));
        Assert.Equal(new StructureEnds.End(whole.Edge, whole.Up, whole.Height, 0), c.FromEnd);
        Assert.NotNull(c.FromRaised);
        foreach (var (at, to) in c.FromRaised!)
        {
            Assert.True(to > at.h && to <= deck + 1e-3f, FormattableString.Invariant($"the older road at {at.p} goes from {at.h:F2} to {to:F2}"));
            Assert.True(to - world.GetHeight(at.p.x, at.p.y) <= RoadConstants.TerrainDeltaMax + 1e-3f);
        }

        // Laid: the road from the south levelled on its apron at the deck, the older road raised with it.
        StructureEnds.End near = BridgeStructures.Ends(c, world).near;
        RoadSpatialGrid.Commit(Piece(near.Apron, near.Joint, deck));
        RoadSpatialGrid.SetRoadHeights(c.FromRaised);
        void AssertLevel(string when)
        {
            var onLanding = new[] { near.Apron, near.Joint, new Vector2(-34f, 0f), new Vector2(-36f, 0f) }
                .SelectMany(q => RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(q.x, 0f, q.y), 0.1f)).Where(rp => rp.IsRoad).ToList();
            Assert.True(onLanding.Count >= 4, $"{when}: only {onLanding.Count} road point(s) on the landing");
            Assert.All(onLanding, rp => Assert.True(Mathf.Abs(rp.h - deck) < 0.01f, FormattableString.Invariant($"{when}: road at {rp.p} stands {rp.h:F2}, the deck {deck:F2}")));
            var west = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-50f, 0f, 0f), 0.1f).Where(rp => rp.IsRoad).ToList();
            Assert.NotEmpty(west);
            Assert.All(west, rp => Assert.True(rp.h > world.GetHeight(-50f, 0f) + 0.5f && rp.h < deck, FormattableString.Invariant($"{when}: the older road at {rp.p} stands {rp.h:F2}")));
        }
        AssertLevel("laid");
        byte[]? roads = RoadSpatialGrid.SerializeAllRoadPoints();
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { c }), loaded));
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(roads!));
        AssertLevel("read back");
        Assert.Equal(c.FromEnd, Assert.Single(loaded).FromEnd);
        Assert.Equal(0, BridgeStructures.Ends(loaded[0], world).near.Steps);
    }

    /// <summary>
    /// The west end of <see cref="AnEndWithNoLevelBankBesideItIsFilledLevelRatherThanAFlightTheRoadClimbsBackFrom"/> with
    /// the ground south of the bank a quarter metre over it, or level with it: of every way the road reaches (the grade is
    /// part of each way, StructureEnds.MeetEnds) the cheapest is taken, at the deck's edge or the row under it with its
    /// fill, not the whole run's eight stairs. With a place on the ground west and south-west of the bank, where
    /// every landing and the stairs' foot would be, no way there is: the road crosses elsewhere, on a bridge whose ends it
    /// meets.
    /// </summary>
    [Fact]
    public void TheCheapestWayTheRoadReachesIsTakenAndWithNoneLeftTheRoadCrossesElsewhere()
    {
        foreach (float rise in new[] { 0.25f, 0f })
        {
            RoadNetworkGenerator.Reset();
            RoadSpatialGrid.Clear();
            var world = new BankRisingBeside { SouthRise = rise };
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, Bridges = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            var roads = new List<RoadSpatialGrid.PlannedPath>();
            RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-45f, 0f, -40f), 0f, new Vector3(70f, 0f, 0f), 0f, 4f, "from the side"), $"rise {rise}: {RoadSpatialGrid.LastRefusal}");
            RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
            // At the deck's edge or the row under it, filled to the road's height: not the whole run.
            Assert.True(c.FromEnd!.Value.Steps <= 1, $"rise {rise}: {c.FromEnd!.Value.Steps} of the whole run's 8 stairs laid");
            Assert.Equal(c.FromEnd!.Value.Steps, BridgeStructures.Ends(c, world).near.Steps);
            Assert.Null(MetEnds.Unmet(roads, new List<RoadCrossing> { c }, world));
            AssertNoStairsLeadingNowhere(c, world, roads, $"rise {rise}");
        }

        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        var blocked = new BankRisingBeside { SouthRise = 0f };
        _scope.WithWorld(blocked);
        try
        {
            RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(-36f, -1f), 4f) });
            SetPathfinder(new RoadPathfinder(blocked) { Stairs = true, Viaducts = true, Bridges = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            var roads = new List<RoadSpatialGrid.PlannedPath>();
            RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector3(-45f, 0f, -40f), 0f, new Vector3(70f, 0f, 0f), 0f, 4f, "from the side");
            Assert.True(built, RoadSpatialGrid.LastRefusal);
            RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
            Assert.True(Vector2.Distance(c.FromBank, new Vector2(-30f, 0f)) > 8f, $"the bridge {c} still springs from the blocked bank");
            Assert.Null(MetEnds.Unmet(roads, new List<RoadCrossing> { c }, blocked));
            AssertNoStairsLeadingNowhere(c, blocked, roads, "round the place");
        }
        finally { RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>()); }
    }

    /// <summary>A swamp channel across x from -30 to 30, its bed at 26 m; beyond either bank the ground stands at
    /// <see cref="Shore"/>, under the floor a road on land is built up to (RoadConstants.DryRoadFloor) by default.</summary>
    internal sealed class SwampBanks : WorldGenerator
    {
        public float Shore = 29.6f, Half = 30f;
        public override float GetHeight(float x, float z) => Mathf.Abs(x) < Half ? 26f : Shore;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < Half ? 1f : 0f;
            width = 60f;
        }
        public override int GetSeed() => 62;
    }

    /// <summary>
    /// The earthwork meet (StructureEnds.MeetEnds): a bridge over a swamp channel whose end stairs march down the bank to a
    /// foot under the floor every road on land is built up to. The road, standing on its floor there, never meets that foot,
    /// and the shore under the floor is wet for an ordinary landing; so the landing is filled over it to a row at or over the
    /// floor, as an ordinary road's floor is, and the end is met there (not refused): its stairs laid down to that row, a
    /// road at the floor reaching it. Where the ground stands further under the floor than the 8 m earthwork cap, no
    /// landing fits, nothing is recorded and the road is refused for an end it does not meet.
    /// </summary>
    [Fact]
    public void AnEndWhoseFootStandsUnderTheRoadsFloorIsMetByEarthworkWithinTheCap()
    {
        var world = new SwampBanks();
        _scope.WithWorld(world);
        var c = RoadCrossing.Between(new Vector2(-30f, 0f), new Vector2(30f, 0f), 26f, new Vector2(0f, 0f), 60f, CrossingKind.Bridge);
        var path = new List<Vector2> { new(-80f, 0f), new(-55f, 0f), new(-30f, 0f), new(30f, 0f), new(55f, 0f), new(80f, 0f) };
        var (wholeNear, wholeFar) = BridgeStructures.Ends(c, world);
        Assert.True(wholeNear.Steps > 0 && !StructureEnds.OverTheFloor(wholeNear),
            FormattableString.Invariant($"the end as the kit lays it: {wholeNear.Steps} stairs to a foot at {wholeNear.MeetHeight:F2} m, the floor {RoadConstants.DryRoadFloor:F2} m"));
        float floor = RoadConstants.DryRoadFloor;
        // The road at its floor where it comes to the stairs' foot does not meet it.
        var atTheFoot = new List<RoadSpatialGrid.PlannedPath> { Piece(wholeNear.Apron, wholeNear.Joint, floor), Piece(wholeFar.Apron, wholeFar.Joint, floor) };
        Assert.Same(c, MetEnds.Unmet(atTheFoot, new List<RoadCrossing> { c }, world));

        Assert.Null(Meet(path, c, world, RoadConstants.TerrainDeltaMax));
        Assert.True(c.FromEarthwork && c.ToEarthwork, "both ends met by earthwork");
        var (near, far) = BridgeStructures.Ends(c, world);
        foreach (var (end, whole) in new[] { (near, wholeNear), (far, wholeFar) })
        {
            Assert.True(StructureEnds.OverTheFloor(end), FormattableString.Invariant($"the landed end's foot at {end.MeetHeight:F2} m is under the floor"));
            Assert.True(end.Steps < whole.Steps, $"the end keeps {end.Steps} of its {whole.Steps} stairs");
        }
        // Laid: the road at its floor, or at the landed foot's height over it, meets both ends.
        var landed = new List<RoadSpatialGrid.PlannedPath>
        {
            Piece(near.Apron, near.Joint, Mathf.Max(near.MeetHeight, floor)), Piece(far.Apron, far.Joint, Mathf.Max(far.MeetHeight, floor)),
        };
        Assert.Null(MetEnds.Unmet(landed, new List<RoadCrossing> { c }, world));

        // The ground beyond the banks 9.5 m under the floor: past the cap, no landing fits; the end is not met.
        world.Shore = floor - RoadConstants.TerrainDeltaMax - 1.5f;
        Assert.Same(c, Meet(path, c, world, RoadConstants.TerrainDeltaMax));
        Assert.False(c.FromEarthwork || c.ToEarthwork);
        Assert.Null(c.FromEnd ?? c.ToEnd);
    }

    /// <summary>
    /// The deck's edge met from the road's side (StructureEnds.Approach from its landing pad, no stairs): the road comes
    /// square to the line, as at the tour's bridge whose end reached into the road; it is brought onto the line a road's
    /// width out from the edge (StructureEnds.PadTurn), its centreline crossing the line there and its edge a half-width
    /// beyond the edge, so a landing lies between the end and the road. Along its line, the road runs straight to the edge.
    /// </summary>
    [Fact]
    public void TheDecksEdgeMetFromTheSideLeavesALandingBeforeTheRoad()
    {
        var edge = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 38f, 0);   // the deck runs east
        // The road along the bank square to the line, five metres west of the edge, on south.
        var path = new List<Vector2> { new(0f, 0f), new(-5f, -5f), new(-5f, -12f), new(-5f, -20f) };
        var (way, k, stair, clear) = StructureEnds.Approach(path, edge, 0, +1, path.Count - 1, false, true, null)!.Value;
        Assert.False(stair);
        Assert.True(clear);
        Assert.Equal(edge.Meet, way[0]);
        Assert.Equal(edge.Meet - edge.Up * StructureEnds.PadTurn, way[1]);
        Assert.True(StructureEnds.PadTurn >= RoadSpatialGrid.DefaultRoadWidth - 1e-3f, "the road turns nearer the edge than a road's width");
        // Every point of the road past the turn stands at least the turn's distance out from the edge, on its line.
        for (int i = 1; i < way.Count; i++)
            Assert.True(Vector2.Dot(way[i] - edge.Meet, -edge.Up) >= StructureEnds.PadTurn - 1e-3f, $"the road at {way[i]} is nearer the edge than the turn");
        Assert.Equal(path[k], way[way.Count - 1]);
        // The ways an end is met by: the deck's edge from its pad among them, never turned off its line.
        var whole = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 38f, 4);
        var ways = StructureEnds.Ways(whole, _ => 34f).ToList();
        Assert.Contains((edge, true), ways);
        Assert.All(ways, w => Assert.Equal(whole.Up, w.end.Up));
    }

    /// <summary>A crossing's ends are saved with it (RoadNetworkPersistence) and read back as they were: one met level,
    /// one landed on a row, a road's end, none; the bridge respawned from the record lays the stairs they say. A record
    /// from before the ends were saved reads back with none: each end as the bank takes it.</summary>
    [Fact]
    public void ACrossingsEndsAreSavedWithIt()
    {
        var c = RoadCrossing.Between(new Vector2(-30f, 0f), new Vector2(30f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        var near = new StructureEnds.End(c.FromBank, c.Direction, 34f, 4);
        c.FromEnd = new StructureEnds.End(near.Edge, near.Up, near.Height, 0);
        c.ToEnd = new StructureEnds.End(c.ToBank, -c.Direction, 34f, 3);
        var ford = RoadCrossing.Between(new Vector2(100f, 0f), new Vector2(110f, 0f), 28f, new Vector2(105f, 0f), 0f, CrossingKind.Ford, FordStyle.Wade);
        ford.FromEnd = new StructureEnds.End(ford.FromBank, ford.Direction, 30.5f, 0, road: true);
        var none = RoadCrossing.Between(new Vector2(200f, 0f), new Vector2(240f, 0f), 28f, new Vector2(220f, 0f), 0f, CrossingKind.Bridge);
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { c, ford, none }), loaded));
        Assert.Equal(3, loaded.Count);
        Assert.Equal((c.FromEnd, c.ToEnd), (loaded[0].FromEnd, loaded[0].ToEnd));
        Assert.Equal((ford.FromEnd, (StructureEnds.End?)null), (loaded[1].FromEnd, loaded[1].ToEnd));
        Assert.True(loaded[1].FromEnd!.Value.Road);
        Assert.Null(loaded[2].FromEnd ?? loaded[2].ToEnd);

        // Format 3: no ends.
        byte[] v4 = RoadNetworkPersistence.SerializeRoadCrossings(new[] { none });
        var v3 = new List<byte>(BitConverter.GetBytes(3));
        v3.AddRange(v4.Skip(4).Take(4 + 12 * 4));
        loaded.Clear();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(v3.ToArray(), loaded));
        Assert.Null(Assert.Single(loaded).FromEnd);
    }

    // ------------------------------------------------- the price of a way

    /// <summary>The price (StructureEnds.Price): meeting level with no earthwork costs nothing; a metre of fill over an
    /// apron's length costs a quarter step, so a landing filled to a row is cheaper than the stair it takes away; the road
    /// climbing back what the stairs came down is paid for too; a way off the line costs a little more than the same on
    /// it; and at a low end (StructureEnds.Low) one step or two cost nothing and none more than a step with its
    /// earthwork.</summary>
    [Fact]
    public void LevelIsCheaperThanAnEarthworkLandingWhichIsCheaperThanStairs()
    {
        float level = StructureEnds.Price(0, 0f, 0f, false, 0f);
        float landing = StructureEnds.Price(0, StructureEnds.ApronLength * 1f, 0f, false, 0f);
        float stair = StructureEnds.Price(1, 0f, 0f, false, 0f);
        Assert.Equal(0f, level);
        Assert.True(level < landing && landing < stair, FormattableString.Invariant($"level {level:F2}, a 1 m landing {landing:F2}, a step {stair:F2}"));
        Assert.True(StructureEnds.Price(4, 0f, 3f, false, 0f) > StructureEnds.Price(4, 0f, 0f, false, 0f));
        Assert.True(StructureEnds.Price(4, 0f, 0f, true, 0f) > StructureEnds.Price(4, 0f, 0f, false, 0f));
        Assert.Equal(0f, StructureEnds.Price(1, 0f, 0f, false, 0f, low: true));
        Assert.True(StructureEnds.Price(0, 0f, 0f, false, 0f, low: true) > StructureEnds.Price(1, StructureEnds.ApronLength * 1.5f, 1f, false, 0f, low: true));
        Assert.Equal(0f, StructureEnds.Price(2, 0f, 0f, false, 0f, low: true));
        Assert.True(StructureEnds.Price(3, 0f, 0f, false, 0f, low: true) > StructureEnds.Price(2, 0f, 0f, false, 0f, low: true));
    }

    /// <summary>A channel across x from -30 to 30, its bed at 26 m; east of it the ground stands at 38 m (the deck's
    /// height), west of it at <see cref="West"/> of x.</summary>
    internal sealed class WestBank : WorldGenerator
    {
        public Func<float, float> West = _ => 34f;
        public override float GetHeight(float x, float z) => x <= -30f ? West(x) : x < 30f ? 26f : 38f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 30f ? 1f : 0f;
            width = 60f;
        }
        public override int GetSeed() => 64;
    }

    /// <summary>The west end of a bridge over <see cref="WestBank"/>, the road come along its line from the west, as the
    /// one owner meets it (StructureEnds.MeetEnds) within <paramref name="cap"/>; and the end as the bank takes it.</summary>
    private (StructureEnds.End met, StructureEnds.End whole, RoadCrossing c) WestEnd(WestBank world, float cap = RoadConstants.TerrainDeltaMax)
    {
        _scope.WithWorld(world);
        var c = RoadCrossing.Between(new Vector2(-30f, 0f), new Vector2(30f, 0f), 26f, new Vector2(0f, 0f), 60f, CrossingKind.Bridge);
        c.Kit = BridgeStructures.Kit.WoodRail;
        StructureEnds.End whole = StructureEnds.Natural(c, world).near;
        var path = new List<Vector2> { new(-90f, 0f), new(-60f, 0f), new(-30f, 0f), new(30f, 0f), new(60f, 0f), new(90f, 0f) };
        Assert.Null(Meet(path, c, world, cap));
        // The road come along the line: the end as its site alone meets it (StructureEnds.AtSite) is the one met.
        var site = StructureEnds.AtSite(whole, c.FromBank, p => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world), cap);
        Assert.True(site != null, "the site alone meets no way of the end the road met");
        Assert.Equal((c.FromEnd!.Value.Steps, c.FromEnd!.Value.Up, c.FromPad, c.FromEarthwork), (site!.Value.End.Steps, site.Value.End.Up, site.Value.Pad, site.Value.Earthwork));
        return (c.FromEnd!.Value, whole, c);
    }

    /// <summary>A deck 4 m over the road arriving along its line (a higher bridge): the road approaches it flat, filled up
    /// to the deck's height within the caps, no stairs laid; it is cheaper than the whole run's four steps.</summary>
    [Fact]
    public void AHigherDeckIsApproachedFlat()
    {
        var (met, whole, c) = WestEnd(new WestBank());
        Assert.True(whole.Steps >= 3, $"the end as the bank takes it has {whole.Steps} stairs");
        Assert.Equal(0, met.Steps);
        Assert.Equal(whole.Up, met.Up);
        Assert.False(c.FromPad || c.FromStands);
    }

    /// <summary>A deck a metre over the road arriving along its line (a low bridge, as a ford's span is): met by one step
    /// up onto it (StructureEnds.Low), not the road filled flush with it.</summary>
    [Fact]
    public void ALowDeckIsMetByOneStep()
    {
        var world = new WestBank { West = _ => 37f };
        var (met, whole, c) = WestEnd(world);
        Assert.True(StructureEnds.Low(whole, 37f));
        Assert.Equal(1, met.Steps);
        Assert.Equal(whole.Up, met.Up);
        Assert.True(StructureEnds.Reachable(37f, met), FormattableString.Invariant($"the step's foot at {met.MeetHeight:F1} m, the road at 37 m"));
        Assert.False(c.FromPad || c.FromStands);
    }

    /// <summary>A deck two metres over the road arriving along its line (a low bridge): met by two steps up onto it, their
    /// foot at the road's height, not one step and a metre of fill, nor the road filled flush.</summary>
    [Fact]
    public void ALowDeckTwoMetresOverTheRoadIsMetByTwoSteps()
    {
        var world = new WestBank { West = _ => 36f };
        var (met, whole, c) = WestEnd(world);
        Assert.True(StructureEnds.Low(whole, 36f));
        Assert.Equal(2, met.Steps);
        Assert.Equal(whole.Up, met.Up);
        Assert.Equal(36f, met.MeetHeight, 3);
        Assert.False(c.FromPad || c.FromStands);
    }

    /// <summary>A deck lying a metre over its bank, the road coming down to it from ground 1.5 m over the deck (the road
    /// arriving higher than the deck it meets): the structure is low over the bank it stands on, so it is met by a step up
    /// onto it, the road cut down to the step's foot, not the road run onto the deck flush.</summary>
    [Fact]
    public void ARoadComingDownToALowDeckMeetsItByAStep()
    {
        var world = new WestBank { West = x => x > -40f ? 37f : 39.5f };
        var (met, whole, c) = WestEnd(world);
        Assert.True(whole.Steps >= 1, $"the end as the bank takes it has {whole.Steps} stairs");
        Assert.True(met.Steps >= 1, FormattableString.Invariant($"met flush, the deck at {met.Height:F1} m, the road arriving at 39.5 m"));
        Assert.Equal(whole.Up, met.Up);
        Assert.True(StructureEnds.OverTheFloor(met));
    }

    /// <summary>A sea-level river (the water at 30 m, its bed at 26 m) between banks 0.95 m over the water, at
    /// 30.95 m, the road arriving along the line at the banks' height.</summary>
    private sealed class LowSeaBanks : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) < 30f ? 26f : 30.95f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 30f ? 1f : 0f;
            width = 60f;
        }
        public override int GetSeed() => 66;
    }

    /// <summary>
    /// A bridge or a ford's span over sea-level water between low banks (0.95 m over the water, the road's floor 0.45 m
    /// under them): its deck stands a step over the road's floor (BridgeLayout.MinDeck), not on the bank, so its foot
    /// stands at the floor and each end is met by its one step up onto the deck, in every kit. A deck on the bank's
    /// height would put a step's foot under the floor, where no road is laid, and the end was met flush.
    /// </summary>
    [Fact]
    public void ALowDeckOverSeaLevelWaterIsMetByAStepInEveryKit()
    {
        var world = new LowSeaBanks();
        _scope.WithWorld(world);
        float floor = RoadConstants.DryRoadFloor;
        Assert.Equal(floor + BridgeLayout.StairRise, BridgeLayout.MinDeck(RoadConstants.SeaLevel), 3);
        var path = new List<Vector2> { new(-90f, 0f), new(-60f, 0f), new(-30f, 0f), new(30f, 0f), new(60f, 0f), new(90f, 0f) };
        foreach (var (kind, style) in new[] { (CrossingKind.Bridge, FordStyle.None), (CrossingKind.Ford, FordStyle.Span) })
            foreach (var kit in new[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced, BridgeStructures.Kit.Stone, BridgeStructures.Kit.DvergrWood })
            {
                string what = $"{kind} {style} {kit}";
                var c = RoadCrossing.Between(new Vector2(-30f, 0f), new Vector2(30f, 0f), 26f, new Vector2(0f, 0f), 60f, kind, style);
                c.WaterLevel = RoadConstants.SeaLevel;
                c.Kit = kit;
                float deck = BridgeStructures.DeckHeight(c, world);
                Assert.True(deck >= floor + BridgeLayout.StairRise - 1e-3f, FormattableString.Invariant($"{what}: the deck at {deck:F2} m"));
                Assert.Null(Meet(path, c, world, RoadConstants.TerrainDeltaMax));
                var (near, far) = BridgeStructures.Ends(c, world);
                foreach (var end in new[] { near, far })
                {
                    Assert.True(end.Steps >= 1, FormattableString.Invariant($"{what}: met flush, the deck at {end.Height:F2} m"));
                    Assert.True(end.Steps <= 2, $"{what}: {end.Steps} steps");
                    Assert.True(StructureEnds.OverTheFloor(end), FormattableString.Invariant($"{what}: the foot at {end.MeetHeight:F2} m"));
                }
                Assert.Equal(2 * (near.Steps + far.Steps), BridgeStructures.Solve(c, world, 1).Count(p => p.Kind == StructurePieceKind.Stair));
            }
    }

    /// <summary>A road arriving at the height the whole run lands on, where no flat approach fits (here, under a 1 m
    /// earthwork cap), takes the stairs on their line down to the row nearest its own height: the whole run, or the row
    /// over it with a metre of fill.</summary>
    [Fact]
    public void WhereNoFlatApproachFitsTheRoadTakesTheStairsOnTheirLine()
    {
        var (met, whole, c) = WestEnd(new WestBank(), cap: 1f);
        Assert.True(whole.Steps >= 3, $"the end as the bank takes it has {whole.Steps} stairs");
        Assert.True(met.Steps >= whole.Steps - 1, $"{met.Steps} of the whole run's {whole.Steps} stairs laid");
        Assert.Equal(whole.Up, met.Up);
        Assert.True(Mathf.Abs(met.MeetHeight - 34f) <= 1f + 1e-3f, FormattableString.Invariant($"the foot at {met.MeetHeight:F1} m, the road at 34 m"));
        Assert.False(c.FromPad || c.FromEarthwork || c.FromStands);
    }

    /// <summary>A road arriving high over a low shelf the whole run lands on (the shelf 4 m under the deck for 16 m past
    /// the bank, the road's ground beyond it 1 m under the deck) lands on the row nearest its own height, its landing
    /// filled over the shelf, on the line: not the whole run down onto the shelf with the road climbing back up.</summary>
    [Fact]
    public void ARoadArrivingHighLandsOnTheNearestRowWithEarthworkNotTheWholeRun()
    {
        var world = new WestBank { West = x => x > -46f ? 34f : 37f };
        var (met, whole, c) = WestEnd(world);
        Assert.True(whole.Steps >= 3, $"the end as the bank takes it has {whole.Steps} stairs");
        Assert.True(met.Steps < whole.Steps, $"the end lays all {whole.Steps} stairs");
        Assert.True(Mathf.Abs(met.MeetHeight - 37f) <= 1f + 1e-3f, FormattableString.Invariant($"the end lands at {met.MeetHeight:F1} m, the road arrives at 37 m"));
        Assert.Equal(whole.Up, met.Up);
        Assert.False(c.FromPad, "the end opens onto its pad");
    }

    /// <summary>Two channels a short stretch of land apart (x -20 to -12, at 32 m): the west at 35 m and the east at 33 m
    /// beyond, so the first bridge's deck stands 3 m over the land between and the second's 1 m.</summary>
    internal sealed class TwoChannels : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            x <= -60f ? 35f : x < -20f ? 26f : x <= -12f ? 32f : x < 28f ? 26f : 33f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = (x > -60f && x < -20f) || (x > -12f && x < 28f) ? 1f : 0f;
            width = 40f;
        }
        public override int GetSeed() => 65;
    }

    /// <summary>
    /// Two structures whose facing ends have between them too little ground for each end's own run-up or pad (a bridge
    /// then the next, 8 m of land apart) are one approach (StructureEnds.Share): both ends are met on one landing between
    /// their feet, at a common height (within a step), each laying its stairs down to it; the road crosses the landing
    /// from one foot to the other.
    /// </summary>
    [Fact]
    public void TwoFacingEndsWithAShortGapShareOneLanding()
    {
        var world = new TwoChannels();
        _scope.WithWorld(world);
        var a = RoadCrossing.Between(new Vector2(-60f, 0f), new Vector2(-20f, 0f), 26f, new Vector2(-40f, 0f), 40f, CrossingKind.Bridge);
        var b = RoadCrossing.Between(new Vector2(-12f, 0f), new Vector2(28f, 0f), 26f, new Vector2(8f, 0f), 40f, CrossingKind.Bridge);
        a.Kit = b.Kit = BridgeStructures.Kit.WoodRail;
        StructureEnds.End aFar = StructureEnds.Natural(a, world).far, bNear = StructureEnds.Natural(b, world).near;
        Assert.True(aFar.Steps > 0, "the first bridge's far end has stairs");
        var path = new List<Vector2> { new(-100f, 0f), new(-60f, 0f), new(-20f, 0f), new(-16f, 0f), new(-12f, 0f), new(28f, 0f), new(80f, 0f) };
        (a.FromIndex, a.ToIndex, b.FromIndex, b.ToIndex) = (1, 2, 4, 5);
        Assert.Null(StructureEnds.MeetEnds(path, new List<RoadCrossing> { a, b }, world, RoadConstants.TerrainDeltaMax, null));

        Assert.True(a.ToShared && b.FromShared, "the two facing ends do not share a landing");
        StructureEnds.End left = a.ToEnd!.Value, right = b.FromEnd!.Value;
        Assert.True(Mathf.Abs(left.MeetHeight - right.MeetHeight) <= StructureEnds.StepTolerance + 1e-3f,
            FormattableString.Invariant($"the feet stand at {left.MeetHeight:F2} and {right.MeetHeight:F2} m"));
        Assert.True(Vector2.Distance(left.Meet, right.Meet) >= StructureEnds.SharedLandingLength - 1e-3f, "the feet leave no landing between them");
        Assert.True(left.Steps + right.Steps < aFar.Steps + bNear.Steps, "the shared landing takes away no stairs");
        // The road between them is the landing, from one foot to the other.
        Assert.Equal(left.Meet, path[a.ToIndex]);
        Assert.Equal(right.Meet, path[b.FromIndex]);
        Assert.Equal(a.ToIndex + 1, b.FromIndex);
        // Each structure lays its stairs down to the landing, and the road meets both.
        Assert.Equal(left.Steps, BridgeStructures.Ends(a, world).far.Steps);
        Assert.Equal(right.Steps, BridgeStructures.Ends(b, world).near.Steps);
        var landing = new List<RoadSpatialGrid.PlannedPath> { Piece(left.Meet, right.Meet, 0.5f * (left.MeetHeight + right.MeetHeight)) };
        Assert.True(StructureEnds.Reachable(landing[0].Heights[0], left) && StructureEnds.Reachable(landing[0].Heights[1], right));
    }

    // ------------------------------------------------- the final check

    private static RoadSpatialGrid.PlannedPath Piece(Vector2 a, Vector2 b, float h) =>
        new(new List<Vector2> { a, b }, new List<float> { h, h }, new List<RoadPointDebugInfo>(), 4f, Vector2.Distance(a, b), false);

    /// <summary>A jump the road was refused with (a crossing whose end no way of meeting reaches, a valley viaduct the
    /// built road does not allow) is not bought again by its search: the road is searched again without it.</summary>
    [Fact]
    public void AViaductTheRoadWasRefusedWithIsNotBoughtAgain()
    {
        var world = new RimValley();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        Assert.NotNull(finder.FindPath(new Vector2(-110f, 0f), new Vector2(110f, 0f)));
        var (from, to) = Assert.Single(finder.LastViaductJumps);
        finder.BanJump(from, to);
        Assert.NotNull(finder.FindPath(new Vector2(-110f, 0f), new Vector2(110f, 0f)));
        Assert.DoesNotContain(finder.LastViaductJumps, j => (Vector2.Distance(j.from, from) < 1f && Vector2.Distance(j.to, to) < 1f) || (Vector2.Distance(j.from, to) < 1f && Vector2.Distance(j.to, from) < 1f));
        finder.ClearNoStairs();
        Assert.NotNull(finder.FindPath(new Vector2(-110f, 0f), new Vector2(110f, 0f)));
        Assert.Single(finder.LastViaductJumps);
    }
}

/// <summary>Crossings' ends as tests set and check them.</summary>
internal static class MetEnds
{
    /// <summary>Records <paramref name="c"/>'s ends as the bank takes them (StructureEnds.Natural), each landed on a row (its
    /// stairs laid that many steps down) or met level at the deck's edge (no stairs) where asked.</summary>
    public static void Set(RoadCrossing c, WorldGenerator world, int? fromRow = null, int? toRow = null, bool fromLevel = false, bool toLevel = false)
    {
        var (near, far) = StructureEnds.Natural(c, world);
        static StructureEnds.End Way(StructureEnds.End e, int? row, bool level) =>
            level ? new StructureEnds.End(e.Edge, e.Up, e.Height, 0) : row is int r ? new StructureEnds.End(e.Edge, e.Up, e.Height, r) : e;
        c.FromEnd = Way(near, fromRow, fromLevel);
        c.ToEnd = Way(far, toRow, toLevel);
    }

    /// <summary>Whether each end of <paramref name="c"/> is met level at the deck's edge: no stairs, where the bank would
    /// take some.</summary>
    public static (bool near, bool far) Level(RoadCrossing c, WorldGenerator world)
    {
        var (near, far) = BridgeStructures.Ends(c, world);
        var (wholeNear, wholeFar) = StructureEnds.Natural(c, world);
        return (near.Steps == 0 && wholeNear.Steps > 0, far.Steps == 0 && wholeFar.Steps > 0);
    }

    /// <summary>The road on <paramref name="path"/> planned as the generator plans it: each crossing's ends met
    /// (StructureEnds.MeetEnds, no stair step of <paramref name="stairs"/> cut), the staircases found again on the path so
    /// changed, and its pieces planned (PlanRoadPathWithCrossings). Null where an end is not met or the road is refused.</summary>
    public static List<RoadSpatialGrid.PlannedPath>? Plan(List<Vector2> path, List<RoadCrossing> crossings, WorldGenerator world, List<RoadStairSpan>? stairs = null)
    {
        var steps = (stairs ?? new List<RoadStairSpan>()).Select(st => (from: path[st.FromIndex], to: path[st.ToIndex])).ToList();
        bool IsStep(Vector2 a, Vector2 b) => steps.Any(st => (a == st.from && b == st.to) || (a == st.to && b == st.from));
        if (StructureEnds.MeetEnds(path, crossings, world, float.PositiveInfinity, IsStep) != null) return null;
        if (stairs != null)
            for (int k = 0; k < stairs.Count; k++)
            {
                stairs[k].FromIndex = path.IndexOf(steps[k].from);
                stairs[k].ToIndex = path.IndexOf(steps[k].to);
            }
        return RoadNetworkGenerator.PlanRoadPathWithCrossings(path, crossings, 4f, null, null, stairs);
    }

    /// <summary>The first of <paramref name="crossings"/> with a structure's end no road of <paramref name="plans"/> (or the
    /// network laid) meets: no road point within 0.75 m of its foot within a step of its height. Null where a player walks
    /// onto every end from the road.</summary>
    public static RoadCrossing? Unmet(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, WorldGenerator world)
    {
        foreach (RoadCrossing c in crossings)
        {
            var (near, far) = BridgeStructures.Ends(c, world);
            if (!near.IsSet) continue;
            foreach (StructureEnds.End end in new[] { near, far })
                if (!StructureEnds.BuiltRoadNear(end.Meet, 0.75f, plans).Any(x => StructureEnds.Reachable(x.h, end))) return c;
        }
        return null;
    }
}
