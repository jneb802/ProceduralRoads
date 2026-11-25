using System;
using System.Collections.Generic;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Xunit;

/// <summary>The railing catalogue (road_railcat): its bays are dressed by the kits as generation dresses a network's
/// staircases, and a hand's corrections come back as a diff against what the kit laid.</summary>
public class RailingCatalogueTests
{
    private const float Ground = 35f;

    private static (List<StairRun> runs, List<List<StructurePiece>> dressed) Laid(RailingCatalogue.Bay bay)
    {
        List<StairRun> runs = RailingCatalogue.Place(bay, new Vector2(100f, 200f), RailingCatalogue.SurfaceOver(bay, Ground));
        var dressed = RailingCatalogue.Dress(runs, _ => (x, z) => Ground, (x, z) => Ground, new StairSupport.Shortfall(), null);
        return (runs, dressed);
    }

    [Fact]
    public void EveryKitHasEveryCaseAndBlackMarbleTheNetworksBend()
    {
        List<string> names = RailingCatalogue.Bays(StairKit.Stone).ConvertAll(b => b.Name);
        Assert.Equal(new[]
        {
            "turn-left-11.25", "turn-right-11.25", "turn-left-22.5", "turn-right-22.5", "turn-left-33.75", "turn-right-33.75",
            "turn-left-45.0", "turn-right-45.0", "offset-0.25", "offset-0.5", "on-0.1", "floor-to-stair-22.5",
        }, names);
        Assert.Equal(names.Append("network-bend"), RailingCatalogue.Bays(StairKit.BlackMarble).ConvertAll(b => b.Name));
    }

    /// <summary>Each bay's two staircases meet end to end (each walks on across the other's pieces), and each is laid by its
    /// kit with its walking pieces, a railing, and nothing refused.</summary>
    [Fact]
    public void EveryBayIsDressedWithARailingAndItsTwoStaircasesMeet()
    {
        foreach (StairKit kit in StairKit.All)
            foreach (RailingCatalogue.Bay bay in RailingCatalogue.Bays(kit))
            {
                var (runs, dressed) = Laid(bay);
                Assert.Equal(2, runs.Count);
                for (int i = 0; i < 2; i++)
                {
                    Assert.True(StairStructures.MetAtEnds(runs[i], runs).Count > 0, $"{kit.Name} {bay.Name}: staircase {i} meets no other");
                    Assert.True(dressed[i].Count > 0, $"{kit.Name} {bay.Name}: staircase {i} refused");
                    Assert.Contains(dressed[i], p => p.Kind == StructurePieceKind.Post);
                    Assert.Contains(dressed[i], p => p.Kind == StructurePieceKind.Beam);
                    foreach (StructurePiece p in runs[i].Pieces)
                        Assert.Contains(dressed[i], d => d.Prefab == p.Prefab && Vector3.Distance(d.Position, p.Position) < 0.001f);
                }
            }
    }

    /// <summary>No post laid twice: where a bay's two staircases share a corner each railing would lay a post there, and one
    /// stands (StairRailing.LeaveShared, the staircase that lays shared posts keeping its own); every kit, every bay.</summary>
    [Fact]
    public void NoPostIsLaidTwice()
    {
        var twice = new List<string>();
        foreach (StairKit kit in StairKit.All)
            foreach (RailingCatalogue.Bay bay in RailingCatalogue.Bays(kit))
            {
                var (_, dressed) = Laid(bay);
                List<Vector3> posts = dressed.SelectMany(d => d).Where(p => p.Kind == StructurePieceKind.Post).Select(p => p.Position).ToList();
                for (int i = 0; i < posts.Count; i++)
                    for (int j = i + 1; j < posts.Count; j++)
                        if (Vector3.Distance(posts[i], posts[j]) < 0.05f)
                            twice.Add(FormattableString.Invariant($"{kit.Name} {bay.Name}: two posts at ({posts[i].x:F2}, {posts[i].y:F2}, {posts[i].z:F2})"));
            }
        Assert.True(twice.Count == 0, string.Join("; ", twice));
    }

    /// <summary>The railing a bay gets is the kit's own: the same as StairRailing.Lay over the same walking pieces with the
    /// other staircase's beside them, but the posts at the corners the two share, where the other lays them
    /// (StairRailing.LaysShared, StairRailing.LeaveShared).</summary>
    [Fact]
    public void ABaysRailingIsTheKitsRailing()
    {
        RailingCatalogue.Bay bay = RailingCatalogue.Bays(StairKit.BlackMarble).Single(b => b.Name == "turn-right-22.5");
        var (runs, dressed) = Laid(bay);
        List<StructurePiece> railing = StairRailing.Lay(StairKit.BlackMarble, runs[0].Pieces, runs[0].Ends, (x, z) => Ground,
            dressed[0].Where(p => p.Kind == StructurePieceKind.StairSupport).ToList(), runs[1].Pieces);
        if (StairRailing.LaysShared(runs[1], runs[0])) StairRailing.LeaveShared(railing, dressed[1]);
        var laid = dressed[0].Where(p => p.Kind is StructurePieceKind.Post or StructurePieceKind.Beam).ToList();
        Assert.Equal(railing.Count, laid.Count);
        Assert.All(railing, r => Assert.Contains(laid, l => l.Prefab == r.Prefab && Vector3.Distance(l.Position, r.Position) < 0.001f));
    }

    /// <summary>The bend as the network laid it keeps its pieces where they were, moved into the bay's frame.</summary>
    [Fact]
    public void TheNetworksBendIsWhereTheNetworkLaidIt()
    {
        RailingCatalogue.Bay bay = RailingCatalogue.Bays(StairKit.BlackMarble).Single(b => b.Name == "network-bend");
        List<StairRun> runs = RailingCatalogue.Place(bay, RailingCatalogue.NetworkBendJoin, RailingCatalogue.NetworkBendSurface);
        Assert.Contains(runs[1].Pieces, p => p.Prefab == "blackmarble_floor" && Vector3.Distance(p.Position, new Vector3(-6592.799f, 61.935f, -3488.402f)) < 0.002f);
        Assert.Contains(runs[0].Pieces, p => p.Prefab == "blackmarble_floor" && Vector3.Distance(p.Position, new Vector3(-6595.665f, 61.935f, -3489.258f)) < 0.002f);
        Assert.Equal(new Vector2(-6594.741f, -3488.875f), runs[0].ToMeet!.Value);
    }

    [Fact]
    public void AHandsCorrectionsComeBackAsKeptRemovedAddedAndMoved()
    {
        var join = new Vector3(10f, 40f, 20f);
        RailingCatalogue.Placed P(string prefab, float x, float z, float yaw, bool laid = true) => new(prefab, new Vector3(x, 40f, z), yaw, 0f, 0f, laid);
        var laid = new List<RailingCatalogue.Placed>
        {
            P("wood_pole", 9f, 19f, 0f), P("wood_pole", 11f, 19f, 0f), P("wood_beam", 10f, 19f, 90f), P("wood_beam_1", 12f, 21f, 0f),
        };
        var now = new List<RailingCatalogue.Placed>
        {
            P("wood_pole", 9.01f, 19f, 90f),                 // a post a quarter turn round is the same post
            P("wood_beam", 10f, 19f, 270f),                  // a level rail a half turn round too
            P("wood_pole", 11.5f, 19.2f, 0f, laid: false),   // moved
            P("wood_beam_26", 14f, 22f, 45f, laid: false),   // added
        };
        List<string> diff = RailingCatalogue.Diff(laid, now, join, out var counts);
        Assert.Equal((2, 1, 1, 1), counts);
        Assert.Contains(diff, l => l.StartsWith("removed\twood_beam_1", StringComparison.Ordinal));
        Assert.Contains(diff, l => l.StartsWith("added\twood_beam_26", StringComparison.Ordinal) && l.Contains("local (4.000, 0.000, 2.000)"));
        Assert.Contains(diff, l => l.StartsWith("moved\twood_pole at (11.000", StringComparison.Ordinal));
        // The tables read back what they wrote.
        RailingCatalogue.Placed back = RailingCatalogue.Parse(RailingCatalogue.Row(now[3], join))!.Value;
        Assert.Equal(("wood_beam_26", false), (back.Prefab, back.Laid));
        Assert.True(Vector3.Distance(back.At, now[3].At) < 0.001f);
        Assert.Null(RailingCatalogue.Parse(RailingCatalogue.Header));
    }

    /// <summary>A bay is laid <see cref="RailingCatalogue.Clearance"/> over the highest ground of its box: on a slope whose air
    /// under its highest piece is past the stone wall's height (<see cref="RailingCatalogue.Air"/>), its stone pieces may be
    /// held out from the slope (arches and corbels, their low ends over the air) or refused, so the catalogue moves a bay on to
    /// gentler ground. Within it, every stone bay stands on walls alone and nothing of its supports is short.</summary>
    [Theory]
    [InlineData(0.15f, false)]
    [InlineData(0.9f, true)]
    public void AStoneBayStandsOnWallsWhereTheAirUnderItIsWithinTheWallsHeight(float slope, bool steep)
    {
        var join = new Vector2(100f, 200f);
        float Natural(float x, float z) => Ground + slope * (x - join.x);
        Func<int, int, float> finished = (x, z) => Natural(x, z);
        foreach (RailingCatalogue.Bay bay in RailingCatalogue.Bays(StairKit.Stone))
        {
            var (lowest, highest) = RailingCatalogue.GroundUnder(bay, join, finished);
            float air = RailingCatalogue.Air(bay, lowest, highest);
            Assert.True(steep == air > StairSupport.WallHeight, $"{bay.Name}: air {air:F1} m on a slope of {slope}");
            if (steep) continue;
            List<StairRun> runs = RailingCatalogue.Place(bay, join, RailingCatalogue.SurfaceOver(bay, highest));
            var shortfall = new StairSupport.Shortfall();
            var dressed = RailingCatalogue.Dress(runs, _ => Natural, finished, shortfall, null);
            Assert.All(dressed, d => Assert.NotEmpty(d));
            Assert.False(shortfall.Any, $"{bay.Name}: supports short on gentle ground (air {air:F1} m)");
            Assert.DoesNotContain(dressed.SelectMany(d => d), p => p.Prefab == "stone_arch");
        }
    }

    /// <summary>A bay's capture looks over every piece its kits laid: its supports and railing too, not its walking pieces'
    /// box alone (a corbel 2 m out from a floor's side stood outside that, read as removed).</summary>
    [Fact]
    public void ABaysCaptureReachesEveryPieceItsKitsLaid()
    {
        var piece = new StructurePiece { Kind = StructurePieceKind.StairSupport, Prefab = "stone_wall_2x1", Position = new Vector3(-2.644f, -2.5f, 3.043f) };
        var floor = new StructurePiece { Kind = StructurePieceKind.StairFloor, Prefab = "stone_floor_2x2", Position = new Vector3(-0.585f, -0.5f, 2.942f) };
        var (min, max) = RailingCatalogue.Reach(new[] { piece, floor });
        Assert.True(min.x < -2.644f && max.x > -0.585f && min.y < 2.942f && max.y > 3.043f);
    }
}
