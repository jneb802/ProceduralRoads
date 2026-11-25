using System;
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
    public string LogTag => "[BRIDGES]";

    public IEnumerable<StructurePiece> Plan(WorldGenerator world, int seed)
    {
        foreach (RoadCrossing crossing in BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()))
        {
            List<StructurePiece> bridge = PiecesOf(crossing, world, seed);
            // Held back whole by every zone its pieces' bodies reach (StructurePlans.HoldWhole); started by the zones they are
            // laid in, debris on the riverbed not counted.
            StructurePlans.HoldWhole(bridge);
            var bridgeZones = new HashSet<Vector2s>();
            foreach (StructurePiece piece in bridge)
                if (piece.Kind != StructurePieceKind.Debris)
                    bridgeZones.Add(ZoneSystem.GetZone(piece.Position));
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
    /// pieces were solved in. A crossing with its kit keeps it, but for the kit its pieces were <paramref name="built"/> in
    /// (<see cref="ChooseKits"/>): the support rule's response to its saved kit read again (<see cref="KitAmong"/>) is the
    /// kit it stands in. Whether it was saved now.</summary>
    internal static bool KeepKit(RoadCrossing crossing, Kit kit, IEnumerable<RoadCrossing> network, bool built = false)
    {
        if (crossing == null || !Chosen(kit) || crossing.Kit == kit || (Chosen(crossing.Kit) && !built)) return false;
        crossing.Kit = kit;
        foreach (RoadCrossing other in network)
            if (other != crossing && (built || !Chosen(other.Kit)) && RoadCrossing.SameBanks(other, crossing, BridgeLayout.SameSiteRadius))
                other.Kit = kit;
        return true;
    }

    /// <summary>How far a kit's structure stands over its walking surface beside the ship's way, which the ship's way
    /// measures from the deck's underside up (<see cref="BridgeLayout.NavigationChannel"/>), the same for every kit: the
    /// railed kits' handrail (<see cref="BridgeLayout.RailHeight"/>, the beam's measured top over it, <see cref="BridgeLayout.BeamTop"/>), the stone kit's top
    /// course (<see cref="BridgeLayout.StoneRailOverDeck"/>), the Dvergr wood kit's piles and their mist lights
    /// (<see cref="DvergrWoodBridge.TopOverDeck"/>), the viaduct's parapet arches (<see cref="DvergrViaduct.TopOverDeck"/>).</summary>
    public static float TopOverDeck(Kit kit) => kit switch
    {
        Kit.WoodRail or Kit.WoodBraced => BridgeLayout.RailHeight + BridgeLayout.BeamTop,
        Kit.Stone => BridgeLayout.StoneRailOverDeck,
        Kit.DvergrWood => DvergrWoodBridge.TopOverDeck,
        Kit.DvergrViaduct => DvergrViaduct.TopOverDeck,
        _ => 0f,
    };

    /// <summary>How far a kit's deck reaches under its walking surface, to its underside, as the ship's way measures it
    /// (<see cref="BridgeLayout.NavigationChannel"/>): the railed kits' beams (<see cref="BridgeLayout.DeckDepth"/>), the stone
    /// kit's floor and its arches' tall ends (<see cref="BridgeLayout.StoneDeckDepth"/>), the Dvergr wood kit's planks and
    /// stringers (<see cref="DvergrWoodBridge.DeckDepth"/>), the viaduct's course down to its floors
    /// (<see cref="DvergrViaduct.FloorDrop"/>). The one owner of the ground under a deck (docs/OWNERS.md).</summary>
    public static float DeckDepth(Kit kit) => kit switch
    {
        Kit.WoodRail or Kit.WoodBraced => BridgeLayout.DeckDepth,
        Kit.Stone => BridgeLayout.StoneDeckDepth,
        Kit.DvergrWood => DvergrWoodBridge.DeckDepth,
        Kit.DvergrViaduct => DvergrViaduct.FloorDrop,
        _ => 0f,
    };

    /// <summary>The deepest deck a crossing may be built in, the envelope every kit lays its deck within (as
    /// <see cref="DeckHalfWidth"/>), asked without choosing a kit: its saved kit's, a viaduct's, else the deepest of the kits
    /// that fit it (<see cref="KitsThatFit"/>). 0 where none does. The one owner of the ground under a deck (docs/OWNERS.md).</summary>
    public static float DeckDepth(RoadCrossing crossing, WorldGenerator world)
    {
        if (Chosen(crossing.Kit)) return DeckDepth(crossing.Kit!.Value);
        if (IsViaduct(crossing, world)) return DeckDepth(Kit.DvergrViaduct);
        float depth = 0f;
        foreach (Kit kit in KitsThatFit(crossing, world)) depth = Mathf.Max(depth, DeckDepth(kit));
        return depth;
    }

    /// <summary>
    /// The ground under a crossing's deck, as cuts of <paramref name="reach"/> (RoadNetworkGenerator.CrossingGround.Cuts):
    /// from each end's edge in to the deck's middle, one every <see cref="RoadCrossingDetector.DeckLineStep"/>, at the deck's
    /// underside (the end's walking surface less <see cref="DeckDepth(RoadCrossing, WorldGenerator)"/>), and nearer the
    /// edge no lower than the cut's batter (<see cref="RoadTerrainModifier.CutBatterSlope"/>) climbs back to the walking
    /// surface at the edge. So no ground stands over a deck's underside but the wedge its batter leaves inside the deck's
    /// first metre or so, under its walking surface, and the road before the edge keeps its height. A cut only lowers:
    /// over water, or where the ground already lies lower, it changes nothing. The road run on under an end met level, at
    /// its walking surface (StructureEnds.JoinOverlap), is cut down here too. The one owner of the ground under a deck
    /// (docs/OWNERS.md), every kit's and the viaduct's.
    /// </summary>
    public static List<Vector3> GroundUnderDeck(RoadCrossing crossing, WorldGenerator world, float reach)
    {
        var cuts = new List<Vector3>();
        if (!HasDeck(crossing)) return cuts;
        var (near, far) = Ends(crossing, world);
        if (!near.IsStructure || !far.IsStructure) return cuts;
        float depth = DeckDepth(crossing, world);
        float half = Vector2.Distance(near.Edge, far.Edge) * 0.5f;
        foreach (StructureEnds.End end in new[] { near, far })
            for (float along = RoadCrossingDetector.DeckLineStep; along <= half + 1e-3f; along += RoadCrossingDetector.DeckLineStep)
            {
                float down = Mathf.Min(depth, (along - reach) * RoadTerrainModifier.CutBatterSlope);
                if (down <= 0f) continue;
                Vector2 at = end.Edge + end.Up * along;
                cuts.Add(new Vector3(at.x, end.Height - down, at.y));
            }
        return cuts;
    }

    /// <summary>Whether a crossing's saved kit is one it is built in: not none, not <see cref="Kit.Undecided"/>.</summary>
    public static bool Chosen(Kit? kit) => kit is Kit k && k != Kit.Undecided;

    /// <summary>
    /// The one owner of a crossing's kit: the kit saved with it (<see cref="ChooseKit"/>, or a record that stored one when
    /// it was generated; read again by the support rule where its bridge is solved again, <see cref="KitAmong"/>), else the
    /// kit its first build picks among the kits installed now. A Dvergr viaduct where the
    /// network made it one (<see cref="IsViaduct"/>). Else a crossing with a deck is built in one of the installed kits
    /// that fit its saved shape (<see cref="KitsThatFit"/>), at even odds by the world seed and the crossing
    /// (<see cref="BridgeLayout.PickKit{T}"/>): in the Mistlands the Dvergr pier's wooden pieces (its piles stack down to
    /// the ground at any height); elsewhere the two railed kits, which fit at any height and over any span (a station whose
    /// column would stand over the wood support ceiling has none, and a free span has none in its channel,
    /// <see cref="BridgeLayout.SolveRailed"/>), and the stone kit outside the Meadows where it fits. A ford laid as a span
    /// (<see cref="FordStyle.Span"/>, the short low footbridge) takes its kit by the same rule as a bridge. A wading or
    /// raised ford is road and has no kit (null), nor has a crossing without a world to read. Each kit's fit is a cheap
    /// estimate (the stone kit's <see cref="BridgeLayout.StoneFits"/>, the wooden kits' measured ceiling,
    /// <see cref="BridgeSupport.MeasuredCeiling"/>): the bridge the drawn kit would lay is read by the game's support rule
    /// first (<see cref="CheckSupport"/>, as laid and as repaired), and one the rule breaks a piece of, in any kit, is not built in
    /// that kit: another kit that fits is drawn by the same rule and read the same way, else none (the kit's response,
    /// docs/OWNERS.md, a structure's final support). One not read, the ground under a piece of it not known, is no verdict:
    /// the estimate's stands.
    /// <para>Only <see cref="Solve"/> reads which of these kits: every kit fills the envelope the network saved (deck
    /// height, width, ends, ship's way), so <see cref="DeckHeight"/>, <see cref="DeckHalfWidth"/>, <see cref="Ends"/> and
    /// the generator ask only whether it is a viaduct.</para>
    /// </summary>
    public static Kit? KitFor(RoadCrossing crossing, WorldGenerator world) => KitAmong(crossing, world, Installed);

    /// <summary>
    /// The mounds a crossing's road stores for its stone bridge (RoadNetworkGenerator.GroundOf): where the stone kit belongs
    /// (<see cref="Fits"/> but for its estimate) and is past its limits only by what mounds under its piers and footings
    /// make up (<see cref="BridgeLayout.StoneMounds"/>), and with the stone kit among those that fit it is the kit drawn
    /// (<see cref="BridgeLayout.PickKit"/>, as <see cref="KitFor"/> draws it once it is built on that ground). None for a
    /// viaduct, a crossing whose kit is chosen, or one the stone kit fits without them. The bridge on them is read by the
    /// support rule before its kit is chosen, as any stone bridge (<see cref="CheckSupport"/>).
    /// </summary>
    internal static List<StructureGround.Mound> StoneMounds(RoadCrossing crossing, WorldGenerator world)
    {
        var none = new List<StructureGround.Mound>();
        if (crossing == null || world == null || Chosen(crossing.Kit) || !HasDeck(crossing) || IsViaduct(crossing, world)) return none;
        if (InMistlands(crossing, world) || !LeansTo(crossing, world, StairKit.StoneOverWoodBelongs) || BridgeLayout.StoneFits(crossing, world)) return none;
        if (BridgeLayout.StoneMounds(crossing, world) is not { Count: > 0 } mounds) return none;
        var fit = new List<Kit>();
        foreach (Kit kit in Installed)
            if (kit == Kit.Stone || Fits(kit, crossing, world)) fit.Add(kit);
        return BridgeLayout.PickKit(crossing, world.GetSeed(), fit) == Kit.Stone ? mounds : none;
    }

    /// <summary><see cref="KitFor"/> with these kits installed.
    /// <para>A crossing whose kit is saved (<see cref="ChooseKit"/>) is laid as saved where its bridge is
    /// (<see cref="BuiltBridges"/>: its pieces, and the ground they were solved on held under them,
    /// <see cref="StructureGround.Held"/>, which every terrain write and finished-ground reader levels to): its saved kit,
    /// the one its record was laid in. Where no record holds that ground (none saved yet, or forgotten for a respawn), its saved kit's bridge is
    /// solved again on the ground as it stands now, which may have been lowered since (a road cut under a pier, the stone
    /// kit's courses stopped at its limit short of the ground), so it is read by the support rule as at kit choice and
    /// responded to the same way (<see cref="Respond"/>): the saved kit where the rule does not break it, else another kit
    /// that fits, else none; the kit it is built in is saved as its pieces go out (<see cref="ChooseKits"/>). Every kit the
    /// rule answers alike, stone and wooden; the viaduct reads its own at every solve (<see cref="DvergrViaduct.Solve"/>).</para></summary>
    internal static Kit? KitAmong(RoadCrossing crossing, WorldGenerator world, IReadOnlyList<Kit> installed, bool say = true)
    {
        if (crossing == null) return null;
        if (crossing.Kit is Kit stored && stored != Kit.Undecided)
        {
            if (stored == Kit.DvergrViaduct || world == null) return stored;
            if (BuiltBridges.Of(crossing) != null) return stored;
            return Respond(crossing, world, stored, KitsThatFit(crossing, world, installed), saved: true, say);
        }
        if (!HasDeck(crossing)) return null;
        if (IsViaduct(crossing, world)) return Kit.DvergrViaduct;
        if (world == null) return null;
        IReadOnlyList<Kit> fit = KitsThatFit(crossing, world, installed);
        if (fit.Count == 0) return null;
        return Respond(crossing, world, BridgeLayout.PickKit(crossing, world.GetSeed(), fit), fit, say: say);
    }

    /// <summary>
    /// Whether a crossing with a deck is built in no kit (docs/OWNERS.md, a structure's final support): its kit read as at kit
    /// choice (<see cref="KitAmong"/>, its record not said: that is its build's) is none, the support rule breaking a piece of
    /// the bridge of every kit that fits it; or it is a viaduct, which reads its own (<see cref="KitAmong"/> keeps its kit), and
    /// the viaduct's own solve refuses it, laying nothing (<see cref="DvergrViaduct.Refuses"/>). Not one whose reading was not
    /// made for ground not known under a piece (the estimate's kit stands, a viaduct's pieces stand unread): ground not known
    /// is never a verdict. A stone bridge drawn on
    /// the mounds its road stores (<see cref="StoneMounds"/>) is read on them as at its first build: the network asks inside
    /// RoadTerrainModifier.AsStored, the road's mound points in the ground (RoadNetworkGenerator.WithNoKit), so it needs no
    /// exemption. What the network asks of a crossing before it keeps a road over it (RoadNetworkGenerator.WithNoKit): a road is never
    /// stored over a deck with no structure, nor up to a viaduct that lays nothing.
    /// </summary>
    internal static bool BuiltInNoKit(RoadCrossing crossing, WorldGenerator world)
    {
        if (!HasDeck(crossing) || world == null) return false;
        Kit? kit = KitAmong(crossing, world, Installed, say: false);
        if (kit == Kit.DvergrViaduct) return DvergrViaduct.Refuses(crossing, world, world.GetSeed());
        return kit == null;
    }

    /// <summary>
    /// The builder's response to the support rule over <paramref name="pick"/>'s bridge (docs/OWNERS.md, a structure's final
    /// support): read (<see cref="CheckSupport"/>); where the rule breaks a piece of it, another of <paramref name="fit"/> is
    /// drawn (<see cref="BridgeLayout.PickKit"/>) and read the same way, until one the rule does not break or none is left
    /// (null). A reading not made (ground not known under a piece) is no verdict: the kit read stands. The record is the
    /// first candidate's, with what was built in the end (<see cref="SaySupport"/>); a <paramref name="saved"/> kit read again
    /// says its own, once. The kit drawn at kit choice and a saved kit solved again (<see cref="KitAmong"/>) are answered by
    /// this one response; asked before a road is stored (<see cref="BuiltInNoKit"/>), it says nothing (<paramref name="say"/>).
    /// </summary>
    private static Kit? Respond(RoadCrossing crossing, WorldGenerator world, Kit pick, IReadOnlyList<Kit> fit, bool saved = false, bool say = true)
    {
        SupportCheck check = CheckSupport(crossing, world, pick);
        // Every kit's admission is a cheap estimate: the bridge it would lay is read by the game's support rule before it is
        // chosen, and where the rule breaks a piece of it, in any kit, another kit that fits is drawn and read, until one the
        // rule does not break or none is left. A reading not made (ground not known under a piece) is no verdict: the
        // estimate's stands. The record is the drawn candidate's, with what was built in the end.
        // The wooden kits are answered as the stone one: read in game (Valheim 1.0.21, a dedicated server, both railed kits as
        // the solve lays them, their deck 13, 15 and 17 m over the lowest ground), the game holds a tall wooden bridge as
        // built far over the rule's reading (WearNTear's support cache keeps what a piece first read off pieces laid after
        // it), but after its zone unloads and loads its readings fall to about the rule's, and it broke three pieces of the
        // braced bridge 15 m tall, each one the rule breaks and none the rule holds. The rule reads the game's state after a
        // reload, the one a bridge must stand in.
        Kit? built = pick;
        var rest = new List<Kit>(fit);
        for (SupportCheck read = check; read.Breaks;)
        {
            rest.Remove(read.Kit);
            built = rest.Count == 0 ? null : BridgeLayout.PickKit(crossing, world.GetSeed(), rest);
            if (built is not Kit other) break;
            read = CheckSupport(crossing, world, other);
        }
        if (say)
            SaySupport(crossing, check, (!check.Breaks ? "built" : built is Kit k ? $"refused, built in {k}" : "refused, no kit")
                + (saved ? SavedReadAgain : ""));
        return built;
    }

    /// <summary>The crossings whose support record was said (<see cref="SaySupport"/>): once each, and once more when a record
    /// said on ground not known (<see cref="SupportCheck.GroundNotKnown"/>, marked <see cref="s_saidOnUnknownGround"/>) is read.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoadCrossing, object> s_saidSupport = new();
    private static readonly object s_saidOnUnknownGround = new();

    /// <summary>
    /// The calibration record of one selected candidate (decision 6p; docs/OWNERS.md, a structure's final support), said once
    /// per crossing to the log, where the offline maps tool and a game log can read it: its kit, the cheap estimate's verdict
    /// and margin, the support rule's reading of the same pieces (or that it was not read), and what the builder did. One line,
    /// <c>[SUPPORT]</c> first. It decides nothing.
    /// </summary>
    internal static string SupportLine(RoadCrossing crossing, SupportCheck check, string response) => System.FormattableString.Invariant(
        $"[SUPPORT] {check.Kit} ({crossing.FromBank.x:F1}, {crossing.FromBank.y:F1}) -> ({crossing.ToBank.x:F1}, {crossing.ToBank.y:F1}): estimate {(check.EstimateFits ? "fits" : "refuses")} ({check.EstimateMargin:F1} {check.EstimateUnit}); ")
        + (check.FullRead
            ? System.FormattableString.Invariant($"full {(check.Holds ? "holds" : "breaks")} {check.Falling} of {check.Pieces} (weakest {check.Weakest:F2} of its least, {check.WeakestPrefab}")
                + (check.Unjudged > 0 ? System.FormattableString.Invariant($"; {check.Unjudged} falling against pieces not read, no verdict") : "") + "); "
            : $"full not read ({check.Unread}); ")
        + (check.FullRead && check.EstimateFits != check.Holds ? (check.EstimateFits ? "estimate optimistic; " : "estimate pessimistic; ") : "")
        + "response " + response;

    /// <summary>Whether <paramref name="crossing"/>'s support record was said already (<see cref="SaySupport"/>).</summary>
    internal static bool SupportSaid(RoadCrossing crossing)
    {
        lock (s_saidSupport) return s_saidSupport.TryGetValue(crossing, out _);
    }

    /// <summary>What a saved kit's record read again says after its response (<see cref="KitAmong"/>).</summary>
    internal const string SavedReadAgain = " (its saved kit read again on the ground as it stands)";

    /// <summary>The crossings whose saved kit's record read again was said (<see cref="SaySupport"/>): once each.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoadCrossing, object> s_saidSavedRead = new();

    /// <summary>Say <see cref="SupportLine"/>, once per crossing; one said while its ground was not known is said again once
    /// its reading is made. A saved kit's reading again (<see cref="SavedReadAgain"/>) is said once more, on its own.</summary>
    internal static void SaySupport(RoadCrossing crossing, SupportCheck check, string response)
    {
        System.Runtime.CompilerServices.ConditionalWeakTable<RoadCrossing, object> table =
            response.EndsWith(SavedReadAgain, System.StringComparison.Ordinal) ? s_saidSavedRead : s_saidSupport;
        lock (table)
        {
            if (table.TryGetValue(crossing, out object? said))
            {
                if (said != s_saidOnUnknownGround || check.GroundNotKnown) return;
                table.Remove(crossing);
            }
            table.Add(crossing, check.GroundNotKnown ? s_saidOnUnknownGround : crossing);
        }
        ProceduralRoadsPlugin.ProceduralRoadsLogger?.LogInfo(SupportLine(crossing, check, response));
    }

    /// <summary>
    /// One selected candidate's support read two ways (docs/OWNERS.md, a structure's final support): its kit's cheap estimate
    /// (<see cref="EstimateFits"/>, by <see cref="EstimateMargin"/> in <see cref="EstimateUnit"/>) and the game's support rule
    /// over the repairable complete layout and the pieces the kit lays on the finished ground (<see cref="FullRead"/>; not read, and why
    /// (<see cref="Unread"/>), where the kit's prefabs have no measured support data, <see cref="BuriedPieces.DataOf"/>, or
    /// where the ground under a piece is not known, <see cref="GroundNotKnown"/>): how many pieces it breaks
    /// (<see cref="Falling"/>) and the weakest share of its least (<see cref="Weakest"/>, <see cref="WeakestPrefab"/>).
    /// </summary>
    internal readonly struct SupportCheck
    {
        public readonly Kit Kit;
        public readonly bool EstimateFits;
        public readonly float EstimateMargin;
        public readonly string EstimateUnit;
        /// <summary>Why the support rule did not read it; null where it did.</summary>
        public readonly string? Unread;
        /// <summary>Whether it was not read because the ground under a piece is not known
        /// (<see cref="StructureGround.FirstOnUnknownGround"/>).</summary>
        public readonly bool GroundNotKnown;
        public readonly int Pieces, Falling;
        /// <summary>Pieces the rule reads as falling that touch a piece it does not read (<see cref="ViaductSupport.Held.ThroughUnread"/>):
        /// no verdict, neither in <see cref="Falling"/> nor in <see cref="Weakest"/>.</summary>
        public readonly int Unjudged;
        public readonly float Weakest;
        public readonly string WeakestPrefab;
        public SupportCheck(Kit kit, bool estimateFits, float estimateMargin, string estimateUnit, string? unread, bool groundNotKnown,
            int pieces, int falling, float weakest, string weakestPrefab, int unjudged = 0)
        {
            Kit = kit; EstimateFits = estimateFits; EstimateMargin = estimateMargin; EstimateUnit = estimateUnit; Unread = unread;
            GroundNotKnown = groundNotKnown; Pieces = pieces; Falling = falling; Weakest = weakest; WeakestPrefab = weakestPrefab;
            Unjudged = unjudged;
        }
        /// <summary>Whether the support rule read it.</summary>
        public bool FullRead => Unread == null;
        /// <summary>Whether the support rule read it and broke none of it.</summary>
        public bool Holds => FullRead && Falling == 0;
        /// <summary>Whether the support rule read it and broke a piece of it: the verdict a builder responds to. Not read is
        /// neither this nor <see cref="Holds"/>.</summary>
        public bool Breaks => FullRead && Falling > 0;
    }

    /// <summary>Why a kit's candidate is not read by the support rule where its prefabs have no measured support data.</summary>
    internal const string NoMeasuredData = "no measured support data";

    /// <summary>
    /// <paramref name="kit"/>'s candidate over <paramref name="crossing"/> read as <see cref="SupportCheck"/> says. The stone
    /// kit: its estimate <see cref="BridgeLayout.StoneFitMargin"/>; the bridge laid whole and ruined
    /// (<see cref="BridgeLayout.SolveStoneComplete"/>, <see cref="BridgeLayout.SolveStone"/>) read by the support rule on the
    /// finished ground (<see cref="ViaductSupport.Of(RoadCrossing, WorldGenerator, IReadOnlyList{StructurePiece})"/>, each piece
    /// from its measured data). Where the finished ground under any of those pieces is not known
    /// (<see cref="StructureGround.FirstOnUnknownGround"/>: a location placed after its roads, its ground not read yet) the rule
    /// does not read it: ground not known is never a verdict, so its estimate stands (<see cref="SupportCheck.GroundNotKnown"/>).
    /// The wooden kits (the railed kits, the Dvergr wood kit) the same way, complete as repaired and in the variant
    /// the builder lays (SolveIn, StructureWear.Bridges: ruined or whole with the navigation gap open); their estimate is the measured ceiling (<see cref="BridgeSupport.MeasuredCeiling"/>) against the pier
    /// height (<see cref="BridgeLayout.PierHeight"/>). The braces and the end stairs' stringers and handrails are read as their
    /// pitched boxes (<see cref="BuriedPieces.Measured.Pitched"/>); a piece whose measured data has no material (the end
    /// stairs, the mist lights) is not read.
    /// </summary>
    internal static SupportCheck CheckSupport(RoadCrossing crossing, WorldGenerator world, Kit kit)
    {
        int seed = world.GetSeed();
        bool fits;
        float spare;
        string unit;
        if (kit == Kit.Stone)
        {
            int? courses = BridgeLayout.StoneFitMargin(crossing, world);
            fits = courses is int m && m >= 0;
            spare = courses ?? int.MinValue;
            unit = "course(s) or pillar(s) to spare";
        }
        else
        {
            fits = true;
            spare = BridgeLayout.RailedMaxColumn - BridgeLayout.PierHeight(crossing, world);
            unit = "m under the measured ceiling";
        }
        List<StructurePiece> whole = SolveCompleteIn(kit, crossing, world, seed);
        List<StructurePiece> built = SolveIn(kit, crossing, world, seed, kit == Kit.Stone || StructureWear.Bridges);
        var laid = new List<StructurePiece>(whole);
        laid.AddRange(built);
        if (StructureGround.FirstOnUnknownGround(laid, StructureGround.Vertices(world)) is StructurePiece unknown)
            return new SupportCheck(kit, fits, spare, unit, System.FormattableString.Invariant(
                $"ground not known under its piece at ({unknown.Position.x:F1}, {unknown.Position.z:F1})"), true, 0, 0, float.NaN, "");
        var check = new List<ViaductSupport.Held>();
        check.AddRange(ViaductSupport.Of(crossing, world, whole));
        check.AddRange(ViaductSupport.Of(crossing, world, built));
        return Read(kit, fits, spare, unit, check);
    }

    /// <summary>A candidate's <see cref="SupportCheck"/> from its estimate and the support rule's reading.</summary>
    internal static SupportCheck Read(Kit kit, bool fits, float margin, string unit, IReadOnlyList<ViaductSupport.Held> held)
    {
        int falling = 0, unjudged = 0;
        float weakest = float.PositiveInfinity;
        string prefab = "";
        foreach (ViaductSupport.Held h in held)
        {
            // One the rule finds falling against a piece it does not read (an end stair) may be held
            // by that piece in the game: no verdict, counted apart.
            if (h.Falls && h.ThroughUnread) { unjudged++; continue; }
            if (h.Falls) falling++;
            if (h.Share < weakest) { weakest = h.Share; prefab = h.Piece.Prefab; }
        }
        return new SupportCheck(kit, fits, margin, unit, held.Count > 0 ? null : NoMeasuredData, false, held.Count, falling, weakest, prefab, unjudged);
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
            Kit.Stone => StoneBelongs(crossing, world) && BridgeLayout.StoneFits(crossing, world),
            _ => false,
        };
    }

    /// <summary>Whether the stone kit belongs where a crossing stands, by its biome alone (<see cref="Fits"/>'s first part,
    /// before the kit's support estimate, <see cref="BridgeLayout.StoneFits"/>): outside the Mistlands, and stone over wood
    /// wherever at least the share stands outside the Meadows (a boundary leans to the stone). The search's height price asks
    /// it too (RoadPathfinder.BridgeHeightFactor), so a bridge is priced for a stone deck only where one may stand.</summary>
    public static bool StoneBelongs(RoadCrossing crossing, WorldGenerator world) =>
        !InMistlands(crossing, world) && LeansTo(crossing, world, StairKit.StoneOverWoodBelongs);

    /// <summary>Whether a crossing is a Mistlands one, as a staircase is (<see cref="StairKit.InMistlands"/>: at least
    /// <see cref="RoadBiome.Share"/> of it in the Mistlands), over its length from bank to bank (its pieces are chosen
    /// by the answer, so they cannot be what it counts). The one owner for the kit, the viaduct's refusal and the deck's width. The one owner of structure kit by biome (docs/OWNERS.md).</summary>
    public static bool InMistlands(RoadCrossing crossing, WorldGenerator world) => InMistlands(crossing.FromBank, crossing.ToBank, world);

    /// <summary><see cref="InMistlands(RoadCrossing, WorldGenerator)"/> of a crossing from <paramref name="fromBank"/> to
    /// <paramref name="toBank"/>: the banks are all it reads, so a valley probe asks it before it reads any ground
    /// (DvergrViaduct.ValleyCrossing).</summary>
    public static bool InMistlands(Vector2 fromBank, Vector2 toBank, WorldGenerator world) => LeansTo(fromBank, toBank, world, StairKit.MistlandsKitBelongs);

    /// <summary>Whether a crossing leans to the more advanced kit (<see cref="RoadBiome.Leans"/>) over its length from bank to bank.</summary>
    public static bool LeansTo(RoadCrossing crossing, WorldGenerator world, System.Func<Heightmap.Biome, bool> advanced) =>
        LeansTo(crossing.FromBank, crossing.ToBank, world, advanced);

    /// <summary><see cref="LeansTo(RoadCrossing, WorldGenerator, System.Func{Heightmap.Biome, bool})"/> from bank to bank.</summary>
    private static bool LeansTo(Vector2 fromBank, Vector2 toBank, WorldGenerator world, System.Func<Heightmap.Biome, bool> advanced) =>
        RoadBiome.Leans(RoadBiome.Samples(new[] { fromBank, toBank }), (x, z) => RoadBiome.At(world, x, z), advanced);

    /// <summary>
    /// The pieces of one crossing, in its kit (<see cref="KitFor"/>), none without one. The one place the pieces are chosen:
    /// the plan, the append queue and the counts all come through here.
    /// Every piece carries its crossing and the kit it was solved in (<see cref="StructurePiece.Kit"/>): the kit saved when
    /// the first is built (<see cref="ChooseKits"/>), whatever the kits that fit by then.
    /// The one owner of bridge and viaduct structure (docs/OWNERS.md).
    /// <para>A solve that laid a piece on finished ground not known (<see cref="StructureGround.FirstOnUnknownGround"/>: a
    /// location whose ground is not published, under a pier, a column or an end's stairs: a bug under the preload-list rule)
    /// lays nothing: its pieces are no verdict. Said as an error once (<see cref="StructureGround.NotLaid"/>), as a staircase
    /// is; nothing waits for it.</para>
    /// </summary>
    public static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int seed) =>
        Solve(crossing, world, seed, StructureWear.Bridges);

    /// <inheritdoc cref="Solve(RoadCrossing, WorldGenerator, int)"/>
    internal static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int seed, bool weathered)
    {
        System.Threading.Interlocked.Increment(ref s_solves);
        long started = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Kit? kit = KitFor(crossing, world);
        List<StructurePiece> pieces = SolveIn(kit, crossing, world, seed, weathered);
        Func<int, int, float> vertex = StructureGround.Vertices(world);
        // Ground not known under any piece: none is laid, said as an error (a kit lays no column, shaft or screen towards
        // NaN, so the solve itself finishes).
        if (pieces.Count > 0 && StructureGround.FirstOnUnknownGround(pieces, vertex) is StructurePiece on)
        {
            StructureGround.NotLaid(crossing, System.FormattableString.Invariant(
                $"[BRIDGES] {crossing.Kind} {kit} ({crossing.FromBank.x:F1}, {crossing.FromBank.y:F1}) -> ({crossing.ToBank.x:F1}, {crossing.ToBank.y:F1})"), on);
            pieces = new List<StructurePiece>();
        }
        else
        {
            // Not in the world whole (StructureSites.SpatialFault over its pieces and the ground its record would hold, as
            // BuiltBridges.Keep measures it): one verdict for laying and saving, so none of it is laid, in any zone.
            if (pieces.Count > 0 && StructureSites.Refuses(crossing, "bridge", crossing.FromBank, crossing.ToBank, pieces, StructureGround.Measure(pieces, vertex)))
                pieces = new List<StructurePiece>();
        }
        foreach (StructurePiece piece in pieces)
        {
            piece.Crossing = crossing;
            piece.Kit = kit;
            if (!weathered) piece.HealthFraction = 1f;
        }
        if (SearchStats.Enabled && HasDeck(crossing)) SolveTimes.Record(crossing, kit, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        return pieces;
    }

    /// <summary>The pieces <paramref name="kit"/> lays over <paramref name="crossing"/>, as they are (<see cref="Solve"/>'s one
    /// switch over the kits), none for no kit. Weathered (StructureWear, the Structures.Weathered setting): pieces missing and
    /// worn, as a ruin; else whole and at full health, the gap over a ship's way still open for boats.</summary>
    private static List<StructurePiece> SolveIn(Kit? kit, RoadCrossing crossing, WorldGenerator world, int seed, bool weathered) =>
        kit switch
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

    /// <summary>
    /// Every piece <paramref name="crossing"/> may be laid with, before its kit is chosen: its kit is chosen only as it is
    /// first built (<see cref="ChooseKit"/>), so the pieces of every kit it may be built in (its saved kit, a viaduct's, else
    /// each kit that fits, <see cref="KitsThatFit"/>), weathered and whole (<see cref="SolveIn"/>). None for a crossing with no
    /// deck. What the network asks of a crossing's structure before it keeps the road (RoadNetworkGenerator.InAnotherLocation).
    /// </summary>
    internal static IEnumerable<StructurePiece> EveryPieceItMayLay(RoadCrossing crossing, WorldGenerator world)
    {
        if (!HasDeck(crossing)) yield break;
        IReadOnlyList<Kit> kits = Chosen(crossing.Kit) ? new[] { crossing.Kit!.Value }
            : IsViaduct(crossing, world) ? new[] { Kit.DvergrViaduct } : KitsThatFit(crossing, world);
        int seed = world.GetSeed();
        foreach (Kit kit in kits)
            foreach (bool weathered in new[] { true, false })
                foreach (StructurePiece piece in SolveIn(kit, crossing, world, seed, weathered))
                    yield return piece;
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
    internal static List<StructurePiece> SolveComplete(RoadCrossing crossing, WorldGenerator world, int seed) =>
        SolveCompleteIn(KitFor(crossing, world), crossing, world, seed);

    /// <summary>The repairable complete layout, shared by the support check and the repair plan.</summary>
    private static List<StructurePiece> SolveCompleteIn(Kit? kit, RoadCrossing crossing, WorldGenerator world, int seed) => kit switch
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
            chosen |= piece.Kit is Kit planned ? KeepKit(piece.Crossing, planned, network, built: true) : ChooseKit(piece.Crossing, world, network);
        }
        return chosen;
    }

    /// <summary>Pieces the plan puts at a crossing (0 for a ford that is not spanned).</summary>
    public static int PiecesAt(RoadCrossing crossing) =>
        WorldGenerator.instance == null ? 0 : PiecesOf(crossing, WorldGenerator.instance, WorldGenerator.instance.GetSeed()).Count;

    /// <summary>None: a bridge keeps vegetation off its walking surface (<see cref="AddWalks"/>), not off squares round its
    /// pieces.</summary>
    public void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas) { }

    /// <summary>The walking surface of these laid pieces (<see cref="WalkOf"/>), each piece's own, so neither their order nor
    /// a piece beside them changes another's.</summary>
    public void AddWalks(List<StructurePiece> pieces, List<StairSupport.Walk> walks) => Walked(pieces, (_, walk) => walks.Add(walk));

    /// <summary>The walks of the buildable bridges' pieces in each near zone (<see cref="AddWalks"/>), zone by zone.</summary>
    public void AddWalksNear(IReadOnlyList<Vector2s> near, System.Func<Vector2s, List<StructurePiece>?> buildable, List<StairSupport.Walk> walks)
    {
        foreach (Vector2s zone in near)
            if (buildable(zone) is { } pieces) AddWalks(pieces, walks);
    }

    /// <summary>Each walked piece of <paramref name="pieces"/> with its walk (<see cref="WalkOf"/>), a viaduct's deck read at its
    /// walking surface (DvergrViaduct.DeckHeight).</summary>
    private static void Walked(List<StructurePiece> pieces, System.Action<StructurePiece, StairSupport.Walk> walked)
    {
        WorldGenerator? world = WorldGenerator.instance;
        RoadCrossing? viaduct = null;
        float deck = float.NaN;
        foreach (StructurePiece piece in pieces)
        {
            if (piece.Kind == StructurePieceKind.ViaductDeck && piece.Crossing != viaduct)
            {
                viaduct = piece.Crossing;
                deck = viaduct != null && world != null ? DvergrViaduct.DeckHeight(viaduct, world) : float.NaN;
            }
            if (WalkOf(piece, deck) is StairSupport.Walk walk) walked(piece, walk);
        }
    }

    /// <summary>
    /// The walking surface of one laid bridge piece as its footprint (<see cref="StairSupport.Walk"/>), null for a piece nobody
    /// walks on. Walked: every kit's deck (<see cref="StructurePieceKind.Deck"/>: the railed kits' floors, the stone kit's
    /// floors and strips, the Dvergr wood kit's planks), the viaduct's course and its iron walk (<see cref="DvergrViaduct.Walked"/>,
    /// <paramref name="viaductDeck"/> its walking surface), and every kit's end steps (<see cref="StructurePieceKind.Stair"/>,
    /// the staircases' stair, <see cref="StairSupport.StairWalk"/>). Never a post, beam, pier, parapet, arch, debris or
    /// flair, nor the viaduct's floors under its course. A deck piece's footprint is its prefab's measured bounds
    /// (<see cref="BuriedPieces.BoundsOf"/>, the Dvergr plank's too) turned by its yaw:
    /// its underside their bottom, its walking surface their top. The one producer of a bridge's walking geometry
    /// (docs/OWNERS.md): its vegetation (<see cref="AddWalks"/>) and its rock (<see cref="AddClearances"/>) read it.
    /// </summary>
    internal static StairSupport.Walk? WalkOf(StructurePiece piece, float viaductDeck)
    {
        switch (piece.Kind)
        {
            case StructurePieceKind.Stair:
                return StairSupport.StairWalk(piece);
            case StructurePieceKind.Deck:
            case StructurePieceKind.ViaductDeck when DvergrViaduct.Walked(piece, viaductDeck):
                return BuriedPieces.BoundsOf(piece.Prefab, out Vector3 min, out Vector3 max) ? DeckWalk(piece, min, max) : null;
            default:
                return null;
        }
    }

    /// <summary>A level deck piece's footprint from its bounds about its origin (<paramref name="min"/>, <paramref name="max"/>),
    /// turned by its yaw: its local z along the walk's heading, its local x across.</summary>
    private static StairSupport.Walk DeckWalk(StructurePiece piece, Vector3 min, Vector3 max)
    {
        float heading = piece.YawDegrees;
        Vector2 back = new Vector2(piece.Position.x, piece.Position.z) + StairLayout.Right(heading) * ((min.x + max.x) / 2f)
            + StairLayout.Forward(heading) * min.z;
        return new StairSupport.Walk(back, heading, max.z - min.z, max.x - min.x, piece.Position.y + min.y, false, max.y - min.y);
    }

    /// <summary>
    /// The rock a walker on a bridge needs cleared, within reach of a point: a box over every walked piece
    /// (<see cref="WalkOf"/>) of the buildable bridges near it (StructurePlans.BuildableNear), its footprint as laid, from the
    /// bottom of the deck in its kit (its walking surface less <see cref="DeckDepth(Kit)"/>; an end step from its own foot)
    /// to a walker's headroom over its highest walk (<see cref="RockClearances.Top"/>). A rock there goes whole
    /// (<see cref="StructureClearance.WholeOnly"/>: a deck's clearance takes it, RoadRockPolicy.DeckTakes). Nothing for a
    /// rail, parapet, arch or support: only the walk. The one owner of which rocks a road clears or carves (docs/OWNERS.md).
    /// </summary>
    public void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances)
    {
        var c = new Vector2(centre.x, centre.z);
        Walked(StructurePlans.BuildableNear(this, centre, radius + LongestPiece), (piece, walk) =>
        {
            float highest = walk.Walking(walk.Depth), top = RockClearances.Top(highest);
            float bottom = walk.Stair ? walk.Underside : highest - DeckDepth(piece.Kit ?? Kit.Undecided);
            Vector2 middle = walk.At(walk.Depth / 2f, 0f);
            var half = new Vector3(walk.Across / 2f, (top - bottom) / 2f, walk.Depth / 2f);
            if (Vector2.Distance(middle, c) > radius + new Vector2(half.x, half.z).magnitude) return;
            clearances.Add(new StructureClearance(new Vector3(middle.x, (bottom + top) / 2f, middle.y), half, walk.Heading, highest,
                wholeOnly: true, keepsLocationRocks: false));
        });
    }

    /// <summary>The longest walked piece of any kit, the Dvergr plank: how far past a probe's reach a piece's origin may stand
    /// and its walk still reach in.</summary>
    private static float LongestPiece => DvergrWoodBridge.BeamLength;
}
