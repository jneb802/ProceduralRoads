using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The Dvergr viaduct's pier ceiling (DvergrViaduct.PierCeiling): 26 m (the owner's number), over a valley and over a ship's
/// way alike; the game's support rule (DvergrViaduct.Solve, SupportGraph) judges every pier under it,
/// and a viaduct with a piece carrying its deck under the margin (DvergrViaduct.HoldMargin) is refused, never laid.
/// </summary>
public sealed class ViaductPierCeilingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public ViaductPierCeilingTests()
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

    /// <summary>A dry valley whose floor stands <paramref name="depth"/> under its 80 m flats, 16 m wide, its faces of 2.</summary>
    private static DvergrViaductTests.MistValley Valley(float depth) => new() { Top = 80f, Depth = depth, FloorHalf = 8f, Face = 2f };

    /// <summary>The weakest share of its least (ViaductSupport.Held.Share) of the pieces of <paramref name="laid"/> the
    /// support rule may not leave out (every one but an ornament, DvergrViaduct.IsOrnament).</summary>
    private static (float share, string prefab) WeakestCarrying(RoadCrossing c, WorldGenerator world, List<StructurePiece> laid)
    {
        var weakest = (share: float.PositiveInfinity, prefab: "");
        foreach (ViaductSupport.Held h in ViaductSupport.Of(c, world, laid))
            if (!float.IsNaN(h.Support) && !DvergrViaduct.IsOrnament(h.Piece) && h.Share < weakest.share) weakest = (h.Share, h.Piece.Prefab);
        return weakest;
    }

    /// <summary>A valley jump on piers of 25 to 26 m (the search's own probe, DvergrViaduct.ValleyCrossing) is a viaduct, and
    /// the support rule holds every piece of it, whole and ruined, at the margin: the corbel band's top course rests on
    /// the pier's iron plates (DvergrViaduct.CorbelBand). Before, its outer blocks fell under black marble's least at 26 m
    /// (1.15 of it at 24 m), and the ceiling was 22 m.</summary>
    [Fact]
    public void AValleyViaductOnTwentySixMetrePiersHoldsEveryCarryingPieceAtTheMargin()
    {
        Assert.Equal(26f, DvergrViaduct.PierCeiling);
        var world = Valley(26f);
        _scope.WithWorld(world);
        RoadCrossing? c = DvergrViaduct.ValleyCrossing(new Vector2(-32f, 0f), new Vector2(32f, 0f), world, out string why);
        Assert.True(c != null, why);
        float tallest = DvergrViaduct.TallestPier(c!, world);
        Assert.InRange(tallest, 25f, DvergrViaduct.PierCeiling);
        Assert.Empty(DvergrViaduct.Mounds(c!, world));
        foreach (bool ruin in new[] { false, true })
        {
            List<StructurePiece> laid = DvergrViaduct.Solve(c!, world, world.GetSeed(), ruin, out DvergrViaduct.HoldReport report, say: false);
            Assert.True(laid.Count > 0, $"ruin {ruin}: refused, {report.Unheld.Count} carrying piece(s) under the margin");
            Assert.Empty(report.Unheld);
            var (share, prefab) = WeakestCarrying(c!, world, laid);
            Assert.True(share >= DvergrViaduct.HoldMargin, $"ruin {ruin}, piers {tallest:F1} m: {prefab} at {share:F2} of its least");
        }
    }

    /// <summary>A ship's way (DvergrViaduct.ShipWayUnder: its 32 m navigation bay, the pier on the way's line left out) has
    /// the one ceiling, 26 m, as a valley: a river viaduct on piers of 24 and 26 m needs no mound, is offered
    /// (DvergrViaduct.Refusal) and is laid, whole and ruined, with every carrying piece at the margin or more (the shares
    /// of owners-20261109/cand13/shipway-shares.tsv: 18 to 27 m laid), and the check before its road is stored
    /// (DvergrViaduct.Refuses) agrees. Before, the ceiling over a ship's way was 22 m and a 24 m river viaduct stood
    /// on mounds.</summary>
    [Theory]
    [InlineData(24f)]
    [InlineData(26f)]
    public void AShipWayViaductOnPiersToTheCeilingIsLaid(float pier)
    {
        var river = new DvergrViaductTests.MistRiver { Bank = 26f + pier, Bed = 26f };
        _scope.WithWorld(river);
        RoadCrossing c = DvergrViaductTests.River();
        Assert.NotNull(DvergrViaduct.ShipWayUnder(c, river));
        BridgeLayout.ShipWay way = DvergrViaduct.ShipWayUnder(c, river)!.Value;
        // The navigation bay: the pier on the way's line left out, its piers half a bay either side of it.
        Assert.Contains(DvergrViaduct.PierAlongs(c, river), a => Mathf.Abs(a - (way.Centre - DvergrViaduct.NavigationBay * 0.5f)) < 1e-3f);
        Assert.Contains(DvergrViaduct.PierAlongs(c, river), a => Mathf.Abs(a - (way.Centre + DvergrViaduct.NavigationBay * 0.5f)) < 1e-3f);
        float tallest = DvergrViaduct.TallestPier(c, river);
        Assert.InRange(tallest, pier - 1.5f, DvergrViaduct.PierCeiling);
        Assert.Null(DvergrViaduct.Refusal(c, river));
        // Under the one ceiling a ship's way needs no mound (before, its 22 m ceiling raised a 24 m river viaduct's piers on mounds).
        Assert.Empty(DvergrViaduct.Mounds(c, river));
        foreach (bool ruin in new[] { false, true })
        {
            List<StructurePiece> laid = DvergrViaduct.Solve(c, river, river.GetSeed(), ruin, out DvergrViaduct.HoldReport report, say: false);
            Assert.True(laid.Count > 0, $"ruin {ruin}, piers {tallest:F1} m: refused, {report.Unheld.Count} carrying piece(s) under the margin");
            Assert.Empty(report.Unheld);
            var (share, prefab) = WeakestCarrying(c, river, laid);
            Assert.True(share >= DvergrViaduct.HoldMargin, $"ruin {ruin}, piers {tallest:F1} m: {prefab} at {share:F2} of its least");
        }
        // The check before a road is stored asks the solve as the bridges are worn (StructureWear.Bridges).
        Assert.False(DvergrViaduct.Refuses(c, river, river.GetSeed()), $"a river viaduct on {tallest:F1} m piers is refused");
    }

    /// <summary>Viaducts over valleys from 20 m to 34 m deep, laid as they stand (DvergrViaduct.Solve asks no ceiling, as for
    /// a deck stored taller than the rules build): each is laid with every carrying piece at the margin or more, or not at
    /// all. A piece held under the margin but not broken refuses the viaduct as one broken does: the sweep reaches piers just
    /// over the 26-27 m that the plates hold, where a carrying piece stands under the margin unbroken, and nothing is laid.</summary>
    [Fact]
    public void AViaductWithACarryingPieceUnderTheMarginIsRefusedNotLaid()
    {
        int laidCount = 0, underNotBroken = 0;
        for (float depth = 20f; depth <= 34f; depth += 1f)
        {
            var world = Valley(depth);
            _scope.WithWorld(world);
            RoadCrossing c = RoadCrossing.Between(new Vector2(-32f, 0f), new Vector2(32f, 0f), 80f - depth, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
            c.ValleyJump = true;
            List<StructurePiece> laid = DvergrViaduct.Solve(c, world, world.GetSeed(), false, out DvergrViaduct.HoldReport report, say: false);
            string at = FormattableString.Invariant($"{depth} m deep, piers {DvergrViaduct.TallestPier(c, world):F1} m");
            if (laid.Count == 0)
            {
                Assert.True(report.Unheld.Count > 0, $"{at}: nothing laid and nothing under the margin");
                if (report.Falling.Count == 0) underNotBroken++;
                continue;
            }
            laidCount++;
            Assert.Empty(report.Unheld);
            var (share, prefab) = WeakestCarrying(c, world, laid);
            Assert.True(share >= DvergrViaduct.HoldMargin, $"{at}: laid with {prefab} at {share:F2} of its least");
        }
        Assert.True(laidCount > 0, "the sweep should lay some");
        Assert.True(underNotBroken > 0, "the sweep should refuse one whose carrying pieces are under the margin but not broken");
    }

    /// <summary>The check made before a road is stored (BridgeStructures.BuiltInNoKit, through the viaduct's own verdict,
    /// DvergrViaduct.Refuses) refuses a viaduct whose carrying piece stands under the margin unbroken, as its solve does: over
    /// the same sweep, each depth's verdict is its solve's (whole or ruined as StructureWear.Bridges says), and at least one
    /// depth is refused with no piece broken. Else a road would be stored up to a viaduct that lays nothing.</summary>
    [Fact]
    public void TheCheckBeforeARoadIsStoredRefusesAViaductUnderTheMarginAsItsSolveDoes()
    {
        int refusedUnbroken = 0;
        for (float depth = 20f; depth <= 34f; depth += 1f)
        {
            var world = Valley(depth);
            _scope.WithWorld(world);
            RoadCrossing c = RoadCrossing.Between(new Vector2(-32f, 0f), new Vector2(32f, 0f), 80f - depth, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
            c.ValleyJump = true;
            c.Kit = BridgeStructures.Kit.DvergrViaduct;
            bool solveRefuses = DvergrViaduct.Solve(c, world, world.GetSeed(), StructureWear.Bridges, out DvergrViaduct.HoldReport report, say: false).Count == 0
                && report.Unheld.Count > 0;
            string at = FormattableString.Invariant($"{depth} m deep, piers {DvergrViaduct.TallestPier(c, world):F1} m");
            Assert.True(solveRefuses == DvergrViaduct.Refuses(c, world, world.GetSeed()), $"{at}: Refuses disagrees with the solve ({solveRefuses})");
            Assert.True(solveRefuses == BridgeStructures.BuiltInNoKit(c, world), $"{at}: BuiltInNoKit disagrees with the solve ({solveRefuses})");
            if (solveRefuses && report.Falling.Count == 0) refusedUnbroken++;
        }
        Assert.True(refusedUnbroken > 0, "the sweep should refuse, before its road is stored, a viaduct under the margin with nothing broken");
    }

    /// <summary>A support not read (NaN, ViaductSupport.Held) is no reading: never a piece under the margin
    /// (DvergrViaduct.UnderMargin), so it neither refuses the viaduct (HoldReport.Unheld) nor is left out; a read one under
    /// the margin is.</summary>
    [Fact]
    public void ASupportNotReadIsNeverUnderTheMargin()
    {
        var floor = new StructurePiece { Kind = StructurePieceKind.ViaductDeck, Prefab = DvergrViaduct.Floor };
        Assert.False(DvergrViaduct.UnderMargin(new ViaductSupport.Held(floor, float.NaN, 20f)));
        Assert.True(DvergrViaduct.UnderMargin(new ViaductSupport.Held(floor, 20f * (DvergrViaduct.HoldMargin - 0.01f), 20f)));
        Assert.False(DvergrViaduct.UnderMargin(new ViaductSupport.Held(floor, 20f * (DvergrViaduct.HoldMargin + 0.01f), 20f)));
    }

    /// <summary>The corbel band's iron plates (DvergrViaduct.CorbelBand) stay hidden on a whole viaduct, its ornaments left
    /// out at the margin (DvergrViaduct.Hold) or not, on piers of 14 to 27 m: every point of every plate's top face has black
    /// marble laid on it, the two plates under the top course (4 m under the deck) the core's blocks, a shaft's head plate
    /// (8 m under the deck) the core's blocks and the corbels beside them; no piece the rule left out stood on a plate (those
    /// left out are the top course's outermost corbels and, on 26 m piers and over, its corner pieces, none over a plate).
    /// Only a ruin, taking a corbel, shows the iron under it, as vanilla's ruins do.</summary>
    [Fact]
    public void EveryIronPlateIsCoveredByMarbleOnAWholeViaduct()
    {
        foreach (float depth in new[] { 14f, 22f, 24f, 26f, 27f })
        {
            var world = Valley(depth);
            _scope.WithWorld(world);
            RoadCrossing? c = DvergrViaduct.ValleyCrossing(new Vector2(-32f, 0f), new Vector2(32f, 0f), world, out string why);
            Assert.True(c != null, why);
            float deck = DvergrViaduct.DeckHeight(c!, world);
            List<StructurePiece> laid = DvergrViaduct.Solve(c!, world, world.GetSeed(), false, out DvergrViaduct.HoldReport report, say: false);
            Assert.NotEmpty(laid);
            var plates = laid.Where(p => p.Kind == StructurePieceKind.ViaductPier && p.Prefab == DvergrViaduct.Floor).ToList();
            Assert.NotEmpty(plates);
            var marble = laid.Where(p => p.Prefab.StartsWith("blackmarble_", StringComparison.Ordinal)).ToList();
            foreach (StructurePiece plate in plates)
            {
                bool top = plate.Position.y > deck - 6f;
                Assert.True(BuriedPieces.BoundsOf(plate.Prefab, out Vector3 pMin, out Vector3 pMax), plate.Prefab);
                float above = plate.Position.y + pMax.y + 0.05f;
                for (float du = pMin.x + 0.1f; du < pMax.x; du += 0.2f)
                    for (float dv = pMin.z + 0.1f; dv < pMax.z; dv += 0.2f)
                    {
                        var point = new Vector3(plate.Position.x + du, above, plate.Position.z + dv);
                        string at = FormattableString.Invariant($"{depth} m, plate ({plate.Position.x:F1}, {plate.Position.y - deck:F1} under the deck, {plate.Position.z:F1}) at ({point.x:F2}, {point.z:F2})");
                        List<StructurePiece> on = marble.Where(m => Contains(m, point)).ToList();
                        Assert.True(on.Count > 0, $"{at}: iron shows, no marble on it");
                        if (top) Assert.True(on.Any(m => !DvergrViaduct.IsOrnament(m)), $"{at}: only an ornament on a top-course plate");
                        Assert.False(report.DroppedPieces.Any(m => Contains(m, point)), $"{at}: a piece left out stood on it");
                    }
            }
        }
    }

    /// <summary>Whether <paramref name="point"/> is inside <paramref name="p"/>'s measured bounds (BuriedPieces.BoundsOf),
    /// turned about the vertical by its yaw (a quarter turn's multiple, as every viaduct piece is laid).</summary>
    private static bool Contains(StructurePiece p, Vector3 point)
    {
        if (!BuriedPieces.BoundsOf(p.Prefab, out Vector3 min, out Vector3 max)) return false;
        Vector3 d = Quaternion.Euler(0f, -p.YawDegrees, 0f) * (point - p.Position);
        const float e = 1e-3f;
        return d.x > min.x + e && d.x < max.x - e && d.y > min.y + e && d.y < max.y - e && d.z > min.z + e && d.z < max.z - e;
    }
}
