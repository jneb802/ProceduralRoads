using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// A swamp bridge's banks are walked out over the shelf to dry ground (RoadRules.ShelfReach) where the crossing is a
/// swamp's, and the search and the detector read that biome at the same place: the middle of the jump's two shores
/// (RoadCrossingDetector.ShoreBiome; the owner's decision of 13 Nov 2026). The search used to read it at the middle of its
/// two cells, so at a biome edge it priced a bridge the detector then built at another length.
/// </summary>
[Collection("RoadStatics")]
public sealed class SwampShelfParityTests : System.IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ValheimWorldScope _scope = new();
    public SwampShelfParityTests(ITestOutputHelper output) { _out = output; }
    public void Dispose() => _scope.Dispose();

    /// <summary>A river 24 m wide (bed 27, sailable) in a shelf at 29.5 out to |x| = 40, dry land at 31.5 past it (within a bridge's bank delta of the shelf); the Swamp
    /// west of <see cref="Edge"/>, the Meadows east of it.</summary>
    private sealed class EdgeWorld : WorldGenerator
    {
        public float Edge;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 150f || Mathf.Abs(wy) > 150f) return 20f;
            float a = Mathf.Abs(wx);
            return a < 12f ? 27f : a < 40f ? 29.5f : 31.5f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx < Edge ? Heightmap.Biome.Swamp : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / 24f);
            width = weight > 0f ? 48f : 0f;
        }
        public override int GetSeed() => 5;
    }

    private static readonly MethodInfo TerrainOf = typeof(RoadPathfinder).GetMethod("CrossingTerrainOf", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The search's banks for the jump east from <paramref name="from"/> (its crossing facts), null where it finds none.</summary>
    private static (Vector2 from, Vector2 to, Vector2 landing)? SearchBanks(RoadPathfinder finder, Vector2i from)
    {
        object facts = TerrainOf.Invoke(finder, new object[] { from, RoadPathfinder.CellCentre(from), new Vector2Int(1, 0), false })!;
        System.Type t = facts.GetType();
        if (t.GetField("Kind")!.GetValue(facts)!.ToString() != "Crossing") return null;
        var landing = (Vector2i)t.GetField("Landing")!.GetValue(facts)!;
        return ((Vector2)t.GetField("FromBank")!.GetValue(facts)!, (Vector2)t.GetField("ToBank")!.GetValue(facts)!, RoadPathfinder.CellCentre(landing));
    }

    /// <summary>A swamp-side bridge at a biome edge: the search's west bank is walked out over the shelf where the detector's
    /// is, and not where it is not, whichever side of the edge the cells' middle and the shores' middle fall.</summary>
    [Theory]
    [InlineData(13f)]   // the cells' middle (x = 12) in the Swamp, the shores' (x = 14) in the Meadows: walked out by neither
    [InlineData(20f)]   // both middles in the Swamp: walked out by both
    [InlineData(100f)]  // all Swamp
    public void TheSearchAndTheDetectorWalkASwampBridgesBanksOutAlike(float edge)
    {
        var world = new EdgeWorld { Edge = edge };
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world);
        var banks = SearchBanks(finder, new Vector2i(-2, 0));
        Assert.True(banks.HasValue, "the search finds a crossing east from the shelf");
        var (from, to, landing) = banks!.Value;
        var built = RoadCrossingDetector.Detect(new List<Vector2> { RoadPathfinder.CellCentre(new Vector2i(-2, 0)), landing }, world, bridges: true);
        RoadCrossing c = Assert.Single(built);
        _out.WriteLine($"edge {edge}: search {from}..{to}, detector {c.Kind} {c.FromBank}..{c.ToBank}");
        Assert.Equal(CrossingKind.Bridge, c.Kind);
        // Walked out over the shelf: the west bank on the dry land past it (x <= -40), not on the shelf.
        bool searchWalked = from.x <= -39.5f, detectorWalked = c.FromBank.x <= -39.5f;
        Assert.True(searchWalked == detectorWalked,
            $"edge {edge}: the search priced banks {from}..{to} (walked out: {searchWalked}), the detector built {c.FromBank}..{c.ToBank} (walked out: {detectorWalked})");
    }
}
