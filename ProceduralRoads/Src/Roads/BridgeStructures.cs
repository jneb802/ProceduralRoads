using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Bridges as a structure source: the ruined bridges at the network's recorded crossings, each in its kit
/// (<see cref="KitFor"/>), chosen when it is first built (<see cref="ChooseKit"/>), and laid as its pieces were solved
/// when its first zone was built (<see cref="Build"/>). Pure logic.
/// </summary>
public sealed class BridgeStructures : IStructureSource
{
    /// <summary>How far below a deck the deck clearance starts, metres: the
    /// deck's own beams and a rock the deck runs into.</summary>
    public const float DeckBelow = 1f;

    public string LogTag => "[BRIDGES]";

    public IEnumerable<StructurePiece> Plan(WorldGenerator world, int seed)
    {
        foreach (RoadCrossing crossing in BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()))
        {
            List<StructurePiece> bridge = PiecesOf(crossing, world, seed);
            var bridgeZones = new HashSet<Vector2s>();
            foreach (StructurePiece piece in bridge)
            {
                // Debris on the riverbed is not part of the bridge: it never holds one back.
                if (piece.Kind != StructurePieceKind.Debris)
                    bridgeZones.Add(ZoneSystem.GetZone(piece.Position));
                piece.StructureZones = bridgeZones;
            }
            RoadCrossing site = crossing;
            var planned = new PlannedSite(bridge, () => BuiltBridges.Of(site) != null, w => Build(site, w, w.GetSeed(), bridge), bridgeZones);
            foreach (StructurePiece piece in bridge)
                piece.Site = planned;
            foreach (StructurePiece piece in bridge)
                yield return piece;
        }
    }

    /// <summary>The kits a crossing is built in (<see cref="KitFor"/>). The numbers are the crossings record's (stored
    /// as the number and one, <see cref="RoadNetworkPersistence"/>): fixed, never reused; 0 names no kit.</summary>
    public enum Kit
    {
        /// <summary>A crossing with a deck whose material kit is chosen when it is first built (<see cref="ChooseKit"/>): the
        /// network saved its shape (deck, ends, ship's way), not its kit. Stored as -1.</summary>
        Undecided = -1,
        /// <summary>The Dvergr harbour pier's wooden pieces (<see cref="DvergrWoodBridge"/>): a Mistlands bridge that is not a
        /// viaduct, and a Mistlands ford's spanned footbridge.</summary>
        DvergrWood = 1,
        /// <summary>The Dvergr viaduct (<see cref="DvergrViaduct"/>): the network's kind of crossing as well as its kit, so
        /// decided with the network (<see cref="DecideKind"/>).</summary>
        DvergrViaduct = 2,
        /// <summary>The railed wooden kit on 2 m bays (<see cref="BridgeLayout.Rail"/>): a bridge or a ford's span outside
        /// the Mistlands.</summary>
        WoodRail = 3,
        /// <summary>The railed wooden kit on 4 m bays with braces (<see cref="BridgeLayout.Braced"/>): a bridge or a ford's
        /// span outside the Mistlands.</summary>
        WoodBraced = 4,
        /// <summary>The stone kit (<see cref="BridgeLayout.SolveStone"/>): a bridge or a ford's span from the Black Forest on,
        /// where it fits (<see cref="BridgeLayout.StoneFits"/>).</summary>
        Stone = 5,
    }

    /// <summary>The railed kit a crossing's kit names, or null for any other kit.</summary>
    public static BridgeLayout.RailedKit? RailedKitOf(Kit? kit) => kit switch
    {
        Kit.WoodRail => BridgeLayout.Rail,
        Kit.WoodBraced => BridgeLayout.Braced,
        _ => null,
    };

    /// <summary>The material kits this build has, in their fixed order, a new kit last: those a crossing not yet built may
    /// be built in (<see cref="KitsThatFit"/>). The viaduct is not among them: it is a kind of crossing the network
    /// decides.</summary>
    public static readonly IReadOnlyList<Kit> Installed = new[] { Kit.DvergrWood, Kit.WoodRail, Kit.WoodBraced, Kit.Stone };

    /// <summary>
    /// A crossing's kind decided as it is stored with the network, once: a viaduct (<see cref="DvergrViaduct.Applies"/>),
    /// whose deck height and ends are its own, keeps the viaduct as its kit; any other crossing with a deck is saved
    /// <see cref="Kit.Undecided"/>, its kit chosen when it is first built (<see cref="ChooseKit"/>); a wading or raised ford
    /// has none. A crossing that has a kit already (one it shares with the crossing it snapped to) keeps it.
    /// </summary>
    public static void DecideKind(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || crossing.Kit.HasValue || !HasDeck(crossing)) return;
        crossing.Kit = IsViaduct(crossing, world) ? Kit.DvergrViaduct : Kit.Undecided;
    }

    /// <summary>
    /// A crossing's kit chosen as its pieces are first built, by the server (<see cref="StructurePlacement"/>, or a manual
    /// road's append): the kit <see cref="KitFor"/> picks among the kits installed now, saved on every record of the site
    /// (<see cref="BridgeLayout.DistinctSites"/>), so its other zones, later visits, every player and the repair report
    /// read the same. A crossing with its kit keeps it, whatever kits are installed later. Whether a kit was chosen now.
    /// </summary>
    public static bool ChooseKit(RoadCrossing crossing, WorldGenerator world, IEnumerable<RoadCrossing> network) =>
        ChooseKit(crossing, world, network, Installed);

    internal static bool ChooseKit(RoadCrossing crossing, WorldGenerator world, IEnumerable<RoadCrossing> network, IReadOnlyList<Kit> installed)
    {
        if (crossing == null || Chosen(crossing.Kit) || world == null) return false;
        return KitAmong(crossing, world, installed) is Kit kit && KeepKit(crossing, kit, network);
    }

    /// <summary>A kit saved on a crossing not built yet and every record of its site (<see cref="ChooseKit"/>): the kit its
    /// pieces were solved in. A crossing with its kit keeps it. Whether it was saved now.</summary>
    internal static bool KeepKit(RoadCrossing crossing, Kit kit, IEnumerable<RoadCrossing> network)
    {
        if (crossing == null || Chosen(crossing.Kit) || !Chosen(kit)) return false;
        crossing.Kit = kit;
        foreach (RoadCrossing other in network)
            if (other != crossing && !Chosen(other.Kit) && RoadCrossing.SameBanks(other, crossing, BridgeLayout.SameSiteRadius))
                other.Kit = kit;
        return true;
    }

    /// <summary>How far a kit's structure stands over its walking surface beside the ship's way, which the ship's way
    /// measures from the deck's underside up (<see cref="BridgeLayout.NavigationChannel"/>), the same for every kit: the
    /// railed kits' handrail (<see cref="BridgeLayout.RailHeight"/>, the beam's half depth over it), the stone kit's top
    /// course (<see cref="BridgeLayout.StoneRailOverDeck"/>), the Dvergr wood kit's piles and their mist lights
    /// (<see cref="DvergrWoodBridge.TopOverDeck"/>), the viaduct's parapet arches (<see cref="DvergrViaduct.TopOverDeck"/>).</summary>
    public static float TopOverDeck(Kit kit) => kit switch
    {
        Kit.WoodRail or Kit.WoodBraced => BridgeLayout.RailHeight + BridgeLayout.RailHalfDepth,
        Kit.Stone => BridgeLayout.StoneRailOverDeck,
        Kit.DvergrWood => DvergrWoodBridge.TopOverDeck,
        Kit.DvergrViaduct => DvergrViaduct.TopOverDeck,
        _ => 0f,
    };

    /// <summary>Whether a crossing's saved kit is one it is built in: not none, not <see cref="Kit.Undecided"/>.</summary>
    public static bool Chosen(Kit? kit) => kit is Kit k && k != Kit.Undecided;

    /// <summary>
    /// The one owner of a crossing's kit: the kit saved with it (<see cref="ChooseKit"/>, or a record that stored one when
    /// it was generated), else the kit its first build picks among the kits installed now. A Dvergr viaduct where the
    /// network made it one (<see cref="IsViaduct"/>). Else a crossing with a deck is built in one of the installed kits
    /// that fit its saved shape (<see cref="KitsThatFit"/>), at even odds by the world seed and the crossing
    /// (<see cref="BridgeLayout.PickKit{T}"/>): in the Mistlands the Dvergr pier's wooden pieces (its piles stack down to
    /// the ground at any height); elsewhere the two railed kits, which fit at any height and over any span (a station whose
    /// column would stand over the wood support ceiling has none, and a free span has none in its channel,
    /// <see cref="BridgeLayout.SolveRailed"/>), and the stone kit outside the Meadows where it fits. A ford laid as a span
    /// (<see cref="FordStyle.Span"/>, the short low footbridge) takes its kit by the same rule as a bridge. A wading or
    /// raised ford is road and has no kit (null), nor has a crossing without a world to read.
    /// <para>Only <see cref="Solve"/> reads which of these kits: every kit fills the envelope the network saved (deck
    /// height, width, ends, ship's way), so <see cref="DeckHeight"/>, <see cref="DeckHalfWidth"/>, <see cref="Ends"/> and
    /// the generator ask only whether it is a viaduct.</para>
    /// </summary>
    public static Kit? KitFor(RoadCrossing crossing, WorldGenerator world) => KitAmong(crossing, world, Installed);

    /// <summary><see cref="KitFor"/> with these kits installed.</summary>
    internal static Kit? KitAmong(RoadCrossing crossing, WorldGenerator world, IReadOnlyList<Kit> installed)
    {
        if (crossing == null) return null;
        if (crossing.Kit is Kit stored && stored != Kit.Undecided) return stored;
        if (!HasDeck(crossing)) return null;
        if (IsViaduct(crossing, world)) return Kit.DvergrViaduct;
        if (world == null) return null;
        IReadOnlyList<Kit> fit = KitsThatFit(crossing, world, installed);
        return fit.Count == 0 ? null : BridgeLayout.PickKit(crossing, world.GetSeed(), fit);
    }

    /// <summary>Whether a crossing is a Dvergr viaduct: the network's kind, saved with it (<see cref="DecideKind"/>), or for a
    /// record saved without its kind or not yet stored, <see cref="DvergrViaduct.Applies"/>. Asked without choosing among
    /// the other kits: the deck height and the ends (<see cref="DeckHeight"/>, <see cref="Ends"/>) are the viaduct's or else
    /// every other kit's alike.</summary>
    public static bool IsViaduct(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null) return false;
        if (crossing.Kit is Kit stored) return stored == Kit.DvergrViaduct;
        return HasDeck(crossing) && DvergrViaduct.Applies(crossing, world);
    }

    /// <summary>Whether a crossing is a structure with a deck, whatever its kit: a bridge, or a ford laid as a span (its
    /// footbridge). A wading or raised ford is road. The one test every rule about a crossing's deck, ends or the ground
    /// under them asks; a rule for bridges alone is about what only a bridge has (a ship's way under it).</summary>
    public static bool HasDeck(RoadCrossing? crossing) =>
        crossing != null && (crossing.Kind == CrossingKind.Bridge || (crossing.Kind == CrossingKind.Ford && crossing.Style == FordStyle.Span));

    /// <summary>The installed kits that fit a crossing that is not a viaduct, in their fixed order (<see cref="Installed"/>,
    /// <see cref="Fits"/>).</summary>
    public static IReadOnlyList<Kit> KitsThatFit(RoadCrossing crossing, WorldGenerator world) => KitsThatFit(crossing, world, Installed);

    internal static IReadOnlyList<Kit> KitsThatFit(RoadCrossing crossing, WorldGenerator world, IReadOnlyList<Kit> installed)
    {
        var kits = new List<Kit>();
        foreach (Kit kit in installed)
            if (Fits(kit, crossing, world)) kits.Add(kit);
        return kits;
    }

    /// <summary>Whether a kit can be laid over a crossing that is not a viaduct, as the network saved it: the Dvergr wood
    /// kit in the Mistlands; the railed kits anywhere else; the stone kit from the Black Forest on (every biome but the
    /// Meadows and the Mistlands) where it fits (<see cref="BridgeLayout.StoneFits"/>).</summary>
    public static bool Fits(Kit kit, RoadCrossing crossing, WorldGenerator world)
    {
        bool mistlands = InMistlands(crossing, world);
        return kit switch
        {
            Kit.DvergrWood => mistlands,
            Kit.WoodRail or Kit.WoodBraced => !mistlands,
            // Stone over wood wherever at least the share stands outside the Meadows (a boundary leans to the stone).
            Kit.Stone => !mistlands && LeansTo(crossing, world, StairKit.StoneOverWoodBelongs) && BridgeLayout.StoneFits(crossing, world),
            _ => false,
        };
    }

    /// <summary>Whether a crossing is a Mistlands one, as a staircase is (<see cref="StairKit.InMistlands"/>: at least
    /// <see cref="StairKit.BiomeShare"/> of it in the Mistlands), over its length from bank to bank (its pieces are chosen
    /// by the answer, so they cannot be what it counts). The one owner for the kit, the viaduct's refusal and the deck's width. The one owner of structure kit by biome (docs/OWNERS.md).</summary>
    public static bool InMistlands(RoadCrossing crossing, WorldGenerator world) => LeansTo(crossing, world, StairKit.MistlandsKitBelongs);

    /// <summary>Whether a crossing leans to the more advanced kit (<see cref="StairKit.LeansTo"/>) over its length from bank to bank.</summary>
    public static bool LeansTo(RoadCrossing crossing, WorldGenerator world, System.Func<Heightmap.Biome, bool> advanced) =>
        StairKit.LeansTo(StairKit.Samples(new[] { crossing.FromBank, crossing.ToBank }), (x, z) => world.GetBiome(x, z), advanced);

    /// <summary>
    /// The pieces of one crossing, in its kit (<see cref="KitFor"/>), none without one. The one place the pieces are chosen:
    /// the plan, the append queue and the counts all come through here.
    /// Every piece carries its crossing and the kit it was solved in (<see cref="StructurePiece.Kit"/>): the kit saved when
    /// the first is built (<see cref="ChooseKits"/>), whatever the kits that fit by then.
    /// The one owner of bridge and viaduct structure (docs/OWNERS.md).
    /// </summary>
    public static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int seed) =>
        Solve(crossing, world, seed, StructureWear.Bridges);

    /// <inheritdoc cref="Solve(RoadCrossing, WorldGenerator, int)"/>
    internal static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int seed, bool weathered)
    {
        System.Threading.Interlocked.Increment(ref s_solves);
        long started = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Kit? kit = KitFor(crossing, world);
        // Weathered (StructureWear, the Structures.Weathered setting): pieces missing and worn, as a ruin; else whole and at
        // full health, the gap over a river still open for boats.
        List<StructurePiece> pieces = kit switch
        {
            Kit.DvergrViaduct => weathered ? DvergrViaduct.Solve(crossing, world, seed) : DvergrViaduct.SolveComplete(crossing, world, seed),
            Kit.DvergrWood => weathered ? DvergrWoodBridge.Solve(crossing, world, seed) : DvergrWoodBridge.SolveWhole(crossing, world, seed),
            Kit.WoodRail => weathered ? BridgeLayout.SolveRailed(crossing, world, seed, BridgeLayout.Rail)
                : BridgeLayout.SolveRailedWhole(crossing, world, seed, BridgeLayout.Rail),
            Kit.WoodBraced => weathered ? BridgeLayout.SolveRailed(crossing, world, seed, BridgeLayout.Braced)
                : BridgeLayout.SolveRailedWhole(crossing, world, seed, BridgeLayout.Braced),
            Kit.Stone => weathered ? BridgeLayout.SolveStone(crossing, world, seed) : BridgeLayout.SolveStoneComplete(crossing, world, seed),
            _ => new List<StructurePiece>(),
        };
        foreach (StructurePiece piece in pieces)
        {
            piece.Crossing = crossing;
            piece.Kit = kit;
            if (!weathered) piece.HealthFraction = 1f;
        }
        if (SearchStats.Enabled && HasDeck(crossing)) SolveTimes.Record(crossing, kit, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        return pieces;
    }

    private static int s_solves;

    /// <summary>How many sites <see cref="Solve"/> has solved this process.</summary>
    internal static int Solves => System.Threading.Volatile.Read(ref s_solves);

    /// <summary>
    /// The wall time of every whole-site solve (<see cref="Solve"/>), by kind: viaduct, stone bridge, wood bridge (the railed
    /// and Dvergr wood kits), ford span; a crossing laid as road is not counted. Recorded only with PROCEDURALROADS_TIMINGS
    /// (<see cref="SearchStats.Enabled"/>).
    /// </summary>
    public static class SolveTimes
    {
        private static readonly Dictionary<string, List<double>> s_ms = new();

        internal static void Record(RoadCrossing crossing, Kit? kit, long ticks)
        {
            string kind = crossing.Kind == CrossingKind.Ford ? "ford span"
                : kit == Kit.DvergrViaduct ? "viaduct" : kit == Kit.Stone ? "stone bridge" : "wood bridge";
            lock (s_ms)
            {
                if (!s_ms.TryGetValue(kind, out List<double>? times)) s_ms[kind] = times = new List<double>();
                times.Add(ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }

        public static void Reset() { lock (s_ms) s_ms.Clear(); }

        /// <summary>Per kind: solves, total, p95 and max, milliseconds; "none timed" for none.</summary>
        public static string Summary()
        {
            var parts = new List<string>();
            lock (s_ms)
                foreach (var kv in s_ms)
                {
                    var sorted = new List<double>(kv.Value);
                    sorted.Sort();
                    double total = 0;
                    foreach (double t in sorted) total += t;
                    double p95 = sorted[Mathf.Clamp(Mathf.CeilToInt(sorted.Count * 0.95f) - 1, 0, sorted.Count - 1)];
                    parts.Add(System.FormattableString.Invariant(
                        $"{kv.Key} {sorted.Count} solves, total {total:F0} ms, p95 {p95:F1} ms, max {sorted[sorted.Count - 1]:F1} ms"));
                }
            parts.Sort(System.StringComparer.Ordinal);
            return parts.Count == 0 ? "none timed" : string.Join("; ", parts);
        }
    }

    /// <summary>The pieces of a crossing site as they are laid: those saved when its first zone was built
    /// (<see cref="BuiltBridges"/>, <see cref="StructureSites"/>), else solved now (<see cref="Solve"/>): a site nobody has reached, or one a world built
    /// zones of before sites were saved, which is laid zone by zone as it is solved.</summary>
    public static List<StructurePiece> PiecesOf(RoadCrossing crossing, WorldGenerator world, int seed) =>
        BuiltBridges.Of(crossing)?.PiecesFor(crossing) ?? Solve(crossing, world, seed);

    /// <summary>
    /// The pieces of a crossing site whose first zone is built now: <paramref name="planned"/>, the site's pieces as the plan
    /// being laid solved them, else solved now (<see cref="Solve"/>), in the layout and kits of this build; saved with the
    /// ground they are laid on (<see cref="BuiltBridges.Keep"/>), so every later zone, on this build or a later one, lays the
    /// same pieces on the same ground. A site saved already keeps its pieces.
    /// </summary>
    public static List<StructurePiece> Build(RoadCrossing crossing, WorldGenerator world, int seed, List<StructurePiece>? planned = null)
    {
        if (BuiltBridges.Of(crossing) is BuiltBridge built) return built.PiecesFor(crossing);
        List<StructurePiece> pieces = planned ?? Solve(crossing, world, seed);
        if (pieces.Count > 0) BuiltBridges.Keep(crossing, pieces, StructureGround.Vertices(world));
        return pieces;
    }

    /// <summary>The pieces of one crossing in its kit with nothing missing, the navigation gap filled in: what a player can
    /// repair it to (<see cref="Solve"/>'s plan, unruined).</summary>
    internal static List<StructurePiece> SolveComplete(RoadCrossing crossing, WorldGenerator world, int seed) => KitFor(crossing, world) switch
    {
        Kit.DvergrViaduct => DvergrViaduct.SolveComplete(crossing, world, seed),
        Kit.DvergrWood => DvergrWoodBridge.SolveComplete(crossing, world, seed),
        Kit.WoodRail => BridgeLayout.SolveRailedComplete(crossing, world, seed, BridgeLayout.Rail),
        Kit.WoodBraced => BridgeLayout.SolveRailedComplete(crossing, world, seed, BridgeLayout.Braced),
        Kit.Stone => BridgeLayout.SolveStoneComplete(crossing, world, seed),
        _ => new List<StructurePiece>(),
    };

    /// <summary>The deck's walking surface in the crossing's kit and kind (<see cref="Solve"/>'s choice): a viaduct's, else
    /// the wooden kits' (<see cref="BridgeLayout.WoodDeckHeight"/>: a ford span's footbridge, or the bridge's), which every
    /// other kit shares.</summary>
    public static float DeckHeight(RoadCrossing crossing, WorldGenerator world) =>
        IsViaduct(crossing, world)
            ? DvergrViaduct.DeckHeight(crossing, world) : BridgeLayout.WoodDeckHeight(crossing, world);

    /// <summary>Half the deck's width, the network's envelope every kit that fits lays its deck within, asked without
    /// choosing a kit: a viaduct's; in the Mistlands the Dvergr wood kit's (its planks are
    /// <see cref="DvergrWoodBridge.BeamLength"/> beams laid across the deck); else the railed and stone kits'.</summary>
    public static float DeckHalfWidth(RoadCrossing crossing, WorldGenerator world) =>
        IsViaduct(crossing, world) ? DvergrViaduct.HalfWidth
        : world != null && Fits(Kit.DvergrWood, crossing, world) ? DvergrWoodBridge.BeamLength * 0.5f
        : BridgeLayout.DeckHalfWidth;

    /// <summary>Both ends of a crossing structure as they meet the road, near (from-bank) end first: the one owner's
    /// (<see cref="StructureEnds.Of"/>), the same for every kit. Unset (default) for a waded or raised ford, which is road.</summary>
    public static (StructureEnds.End near, StructureEnds.End far) Ends(RoadCrossing crossing, WorldGenerator world) => StructureEnds.Of(crossing, world);

    /// <summary>The kits of the bridges these pieces belong to saved, as they are built: the kit the pieces were solved in
    /// (<see cref="StructurePiece.Kit"/>), so the bridge stands in the kit it is built in even where the kits that fit have
    /// changed since its plan was solved; a piece that carries none has its crossing's kit chosen now
    /// (<see cref="ChooseKit"/>). Whether any was saved now.</summary>
    public static bool ChooseKits(IEnumerable<StructurePiece> pieces, WorldGenerator world, IEnumerable<RoadCrossing> network)
    {
        bool chosen = false;
        RoadCrossing? last = null;
        foreach (StructurePiece piece in pieces)
        {
            if (piece.Crossing == null || piece.Crossing == last) continue;
            last = piece.Crossing;
            chosen |= piece.Kit is Kit planned ? KeepKit(piece.Crossing, planned, network) : ChooseKit(piece.Crossing, world, network);
        }
        return chosen;
    }

    /// <summary>Pieces the plan puts at a crossing (0 for a ford that is not spanned).</summary>
    public static int PiecesAt(RoadCrossing crossing) =>
        WorldGenerator.instance == null ? 0 : PiecesOf(crossing, WorldGenerator.instance, WorldGenerator.instance.GetSeed()).Count;

    /// <summary>
    /// Clear-areas around the plan so vegetation does not spawn through the
    /// deck (a bridge zone often carries no painted road points of its own).
    /// </summary>
    public void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas)
    {
        Vector3? last = null;
        foreach (StructurePiece piece in zonePieces)
        {
            // Flair hangs on the structure's own pieces, whose areas already clear it: none of its own.
            if (piece.Kind == StructurePieceKind.Flair) continue;
            if (last.HasValue && Vector3.Distance(last.Value, piece.Position) < 2f)
                continue;
            // Wide enough for a two-lane deck: pieces sit up to DeckHalfWidth
            // either side of the crossing line, and the clearing has to keep
            // trees out of BOTH lanes, not just the centreline. A viaduct's
            // pieces reach a metre further (its course and arches, 3 m out).
            areas.Add((piece.Position, BridgeLayout.DeckHalfWidth + 1.5f));
            last = piece.Position;
        }
    }

    /// <summary>
    /// Bridge decks are not road points, so rocks on a bridge were never looked
    /// at. Each span in reach is walked every metre at deck height (the road's
    /// stored height at each bank, interpolated), from just under the deck; a
    /// rock there goes whole: bridges are few, and no rock is missed.
    /// </summary>
    public void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances)
    {
        var deck = new List<RoadSpatialGrid.RoadPoint>();
        var c = new Vector2(centre.x, centre.z);
        foreach (var crossing in RoadNetworkGenerator.GetRoadCrossings())
        {
            // Bridges of every kit (wood, Dvergr wood, viaduct) and a ford built as a footbridge.
            if (!HasDeck(crossing)) continue;
            Vector2 a = crossing.FromBank, b = crossing.ToBank;
            float length = Vector2.Distance(a, b);
            if (length < 1f) continue;
            // Nearest point of the span to the probe centre.
            float t0 = Mathf.Clamp01(Vector2.Dot(c - a, b - a) / (length * length));
            if (Vector2.Distance(a + (b - a) * t0, c) > radius) continue;
            // The deck's own height in its kit: the road ends at a stair's foot, below it.
            float height = WorldGenerator.instance != null
                ? DeckHeight(crossing, WorldGenerator.instance)
                : RoadSpatialGrid.TryGetRoadHeightWithin(a, 4f, out float road) ? road : BridgeLayout.MinDeck(RoadConstants.SeaLevel);
            int n = Mathf.CeilToInt(length);
            for (int i = 0; i <= n; i++)
                deck.Add(new RoadSpatialGrid.RoadPoint(a + (b - a) * (i / (float)n), BridgeLayout.EndStairsWidth, height)); // the deck's own width, not the road setting
        }
        // Headings come from every deck point in reach.
        foreach (var point in deck)
            clearances.Add(new StructureClearance(point, deck, point.h - DeckBelow, DeckBelow + RoadRockPolicy.ClearanceHeight, wholeOnly: true));
    }
}
