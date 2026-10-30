using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Low Mistlands bridges built from the vanilla Dvergr harbour pier's wooden pieces, for lower bridges in the Mistlands. Pure logic; the
/// game places the plan through the bridges' structure source (<see cref="BridgeStructures"/>), so the old-world cutoff,
/// the spawned-zone record and the append queue are the wooden bridges' own. Which crossings get it is
/// <see cref="BridgeStructures.KitFor"/>'s choice, nowhere else.
///
/// <para><b>Only the look changes.</b> The deck height, its length and both ends are the wooden kit's
/// (<see cref="BridgeLayout.DeckHeight"/>, <see cref="BridgeLayout.BuiltLength"/>, <see cref="BridgeLayout.NaturalEnds"/>), so the
/// road meets the bridge where it met the wooden one and the network is the same on the same world (ideally at
/// similar locations, so before and after can be compared). The bank stairs are the wooden kit's run, laid in
/// <see cref="StairPrefab"/>.</para>
///
/// <para><b>The template is the vanilla pier</b> (a Dvergr
/// harbour at LocationProxy (2238.7, 30.0, -6218.0), yaw 135), turned onto the pier's own axes:</para>
/// <list type="bullet">
/// <item>bents of two <see cref="Pile"/>s, 5.75 m apart across the deck, every 5 m along it (<see cref="BentSpacing"/>),
/// the piles stacked 4 m apart (y 28.5 and 32.5);</item>
/// <item>planks: <see cref="Plank"/> laid ACROSS the deck every metre (<see cref="PlankPitch"/>) at y 31.5, alternating
/// end for end (yaw 0 / 180);</item>
/// <item>stringers: the same beam laid ALONG the deck under the planks, two of them 4 m apart, centres 6 m apart along
/// the pier, each <see cref="StringerDrop"/> under the planks' centres so it reaches 0.3 m up into them (Mistlands_Harbour1
/// fort (1)/pier: planks y 1.5, stringers y 1.0);</item>
/// <item>no lanterns: the harbour's <c>dvergrprops_lantern</c>s hang from its buildings, not the dock; but mist lights (<see cref="Light"/>) on some piles of some bridges, as the harbour's
/// (<see cref="LitBridgeChance"/>, <see cref="LightChance"/>).</item>
/// </list>
/// <para>The pieces' sizes and origins are MEASURED in game (cli_piece_geometry; see each constant).
/// The walking surface is the planks' top, the wooden kit's deck height; the stringers lie under the planks; the piles'
/// tops stand <see cref="PileAboveDeck"/> over the walking surface and the lowest pile's foot is in the ground.</para>
///
/// <para><b>The navigation gap and the free span</b> are the wooden kit's rules (<see cref="BridgeLayout.NavigationChannel"/>,
/// <see cref="BridgeLayout.IsFreeSpan"/>), the gap opened in whole bays (<see cref="NavigationBays"/>): a bent in it is
/// never built, and a free span has bents only at its banks with
/// its stringers carried bank to bank. <b>The ruin</b> is every kit's (<see cref="BridgeLayout.BridgeRuin"/>): every piece
/// worn as a vanilla location's, a plank missing at the deck share, more toward mid-span, never two running and never
/// over an end bay; fixed per crossing and world seed. Where it departs from that profile, the plan says why.</para>
///
/// <para><b>Mistlands ford spans</b> are laid the same way on the wooden footbridge's deck
/// height (<see cref="BridgeLayout.SpanDeckHeight"/>), length and stairs, so the road meets them as it meets any ford span.</para>
///
/// <para><b>Every piece is grounded or rests on one that is, by construction</b>: a bent's piles go down until the lowest
/// one's foot is in the ground, and no bent stands where they would stand over the wood support ceiling (the railed kits'
/// <see cref="BridgeLayout.RailedMaxColumn"/>: the deck carried over such a run within the free span, else the run left
/// open), a stringer lies under planks that rest on a bent's piles (as on the vanilla pier, where the
/// stringers stand inside the piles and hang from the planks), and a plank is laid only over a bay whose two bents stand.</para>
/// </summary>
public static class DvergrWoodBridge
{
    // Sizes are the colliders measured in game (cli_piece_geometry); the layout is read off the vanilla
    // Mistlands_Harbour1 pier (fort (1)/pier).

    // ---------------------------------------------------------------- pieces
    /// <summary>The pier's plank and stringer: a <see cref="BeamLength"/> x <see cref="BeamDepth"/> x
    /// <see cref="BeamWidth"/> collider (its length along local x), origin at its centre.</summary>
    public const string Plank = "dvergrprops_wood_beam";
    /// <summary>The pier's pile: a <see cref="PileWidth"/> x <see cref="PileLength"/> x <see cref="PileWidth"/> collider,
    /// origin at its centre.</summary>
    public const string Pile = "dvergrprops_wood_pole";
    /// <summary>The bank stairs: the Dvergr wooden stair, the same collider and origin as wood_stair, laid on the wooden
    /// kit's 2 m run and 1 m rise (<see cref="BridgeLayout.NaturalEnds"/>); a step over the ground is carried on a
    /// <see cref="Pile"/> column (<see cref="BridgeLayout.LayEndStairs"/>).</summary>
    public const string StairPrefab = "dvergrprops_wood_stair";

    // -------------------------------------------------- the pier, read off

    /// <summary>Most bent to bent along the deck: the pier's 5 m.</summary>
    public const float BentSpacing = 5f;
    /// <summary>A pile's centre either side of the crossing line: the pier's two piles stand 5.75 m apart.</summary>
    public const float PileSideOffset = 2.875f;
    /// <summary>A stringer's centre either side of the crossing line: the pier's stand 4 m apart.</summary>
    public const float StringerSideOffset = 2f;
    /// <summary>A stringer's centre under the planks' centres, as on the pier: the 0.8 m beams overlap by 0.3 m.</summary>
    public const float StringerDrop = 0.5f;
    /// <summary>Plank to plank along the deck.</summary>
    public const float PlankPitch = 1f;
    /// <summary>The beam's length (along its local x), width and depth.</summary>
    public const float BeamLength = 6f, BeamWidth = 0.8f, BeamDepth = 0.8f;
    /// <summary>One pile's length and its square side. Its collar and LOD children's RandomSpawn runs only while a
    /// location spawns, so a pole spawned on its own shows both.</summary>
    public const float PileLength = 4f, PileWidth = 0.8f;
    /// <summary>How far a pile's top stands over the walking surface (the pier stands its top piles 2.6 m over the
    /// planks' top; this is an open choice).</summary>
    public const float PileAboveDeck = 0.75f;

    // --------------------------------------------------------- mist lights
    /// <summary>The mist light: vanilla's wisp light, which also clears the mist around it (15 m; its light 10 m).
    /// Networked, WearNTear 200 / Wood, needs no support. Its box hangs under its origin (y -0.65..0.09).</summary>
    public const string Light = "dverger_demister";
    /// <summary>A mist light's origin over the top of the pile it stands on, as on the pier: its box sits 0.15 m into the
    /// pile's top.</summary>
    public const float LightOverPile = 0.5f;
    /// <summary>A mist light's box top over its origin.</summary>
    public const float LightBoxTop = 0.09f;
    /// <summary>The kit's highest point over the walking surface: a mist light's box on a pile's top.</summary>
    public const float TopOverDeck = PileAboveDeck + LightOverPile + LightBoxTop;
    /// <summary>The share of Dvergr wood bridges that have mist lights at all (only some do, as in the harbour).</summary>
    public const float LitBridgeChance = 1f / 3f;
    /// <summary>On a bridge that has them, the chance each top pile carries one (the pier's demisters' RandomSpawn); one
    /// pile always carries one.</summary>
    public const float LightChance = 0.33f;
    /// <summary>Of the top piles that rate lights, in order along the bridge, every this many carries its light (the
    /// first always): half the harbour's, which strung a long bridge with them.</summary>
    public const int LightSpacing = 2;
    /// <summary>Ruin roles of the mist lights (this kit's 11-14).</summary>
    public const int RoleLitBridge = 12, RoleLight = 13;

    // ------------------------------------------------------------ the ruin
    /// <summary>The kit's ruin: every kit's (<see cref="BridgeLayout.BridgeRuin"/>).</summary>
    public static BridgeLayout.RuinProfile Profile => BridgeLayout.BridgeRuin;
    /// <summary>Ruin roles of this kit's own decisions, past <see cref="BridgeLayout.Ruin"/>'s.</summary>
    public const int RolePlank = 11;

    /// <summary>Whether the crossing is laid in this kit's shape at all: a bridge, or a ford's spanned footbridge.</summary>
    private static bool IsSpanFord(RoadCrossing crossing) => crossing.Kind == CrossingKind.Ford && crossing.Style == FordStyle.Span;

    /// <summary>The planks' walking surface: the wooden kits' deck for the same crossing (a bridge's
    /// <see cref="BridgeLayout.DeckHeight"/>, a ford span's <see cref="BridgeLayout.SpanDeckHeight"/>), so the ends do not move.</summary>
    public static float DeckHeight(RoadCrossing crossing, WorldGenerator world) =>
        IsSpanFord(crossing) ? BridgeLayout.SpanDeckHeight(crossing, world) : BridgeLayout.DeckHeight(crossing, world);

    /// <summary>The bents along the deck, from the near deck end (0) to the far one (<see cref="BridgeLayout.BuiltLength"/>):
    /// as many equal bays as keep each at most <see cref="BentSpacing"/>. The same over a river: the navigation gap opens
    /// whole bays of it (<see cref="NavigationBays"/>), so the complete bridge is one regular structure.</summary>
    public static float[] BentAlongs(float builtLength)
    {
        int bays = Mathf.Max(1, Mathf.CeilToInt(builtLength / BentSpacing - 1e-3f));
        var a = new float[bays + 1];
        for (int i = 0; i <= bays; i++) a[i] = builtLength * i / bays;
        return a;
    }

    /// <summary>The deck from the planks' top (the walking surface) to the stringers' bottom.</summary>
    public const float DeckDepth = BeamDepth + StringerDrop;

    /// <summary>
    /// The bays left open over a river: the first, how many, and whether their deck is open too (<paramref name="deck"/>
    /// false: the ship passes under the deck, and only its bents are left out, the deck carried over within the free span),
    /// or null with no
    /// navigation gap (off a river, a free span). The fewest whole bays of the bent grid whose opening holds the ship's way
    /// for this kit's deck (<see cref="BridgeLayout.NavigationChannel"/>) as every kit's bays do
    /// (<see cref="BridgeLayout.OpenBays"/>), clear of what reaches past the bents either side (half a pile; with the deck
    /// open, a bay's stringer, centred on its bay, past both its bents).
    /// </summary>
    public static (int first, int count, bool deck)? NavigationBays(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null || crossing.Kind != CrossingKind.Bridge || BridgeLayout.IsFreeSpan(crossing, world))
            return null;
        float deckH = DeckHeight(crossing, world);
        if (BridgeLayout.NavigationChannel(crossing, world, deckH - DeckDepth, deckH, BridgeStructures.Kit.DvergrWood) is not BridgeLayout.ShipWay way)
            return null;
        float[] bents = BentAlongs(BridgeLayout.BuiltLength(crossing.Width));
        int bays = bents.Length - 1;
        float bay = bents[1] - bents[0];
        bool deck = way.DeckWidth > 0f;
        float reach = deck ? Mathf.Max(PileWidth * 0.5f, (BeamLength - bay) * 0.5f) : PileWidth * 0.5f;
        var (first, count) = BridgeLayout.OpenBays(way, bay, bays, reach);
        return (first, count, deck);
    }

    /// <summary>Whether a point this far along the deck lies in the navigation gap's open bays (<see cref="NavigationBays"/>),
    /// strictly between the bents that bound it. None under a deck the ship passes under.</summary>
    public static bool InNavigationGap(RoadCrossing crossing, WorldGenerator world, float along)
    {
        if (NavigationBays(crossing, world) is not (int first, int count, true)) return false;
        float[] bents = BentAlongs(BridgeLayout.BuiltLength(crossing.Width));
        return along > bents[first] + 1e-3f && along < bents[first + count] - 1e-3f;
    }

    internal static List<StructurePiece> SolveComplete(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        Solve(crossing, world, worldSeed, ruin: false);

    public static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        Solve(crossing, world, worldSeed, ruin: true);

    private static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin)
    {
        var pieces = new List<StructurePiece>();
        if (crossing == null || world == null || !BridgeStructures.HasDeck(crossing)
            || crossing.Width < BridgeLayout.DeckSpan)
            return pieces;

        var r = new BridgeLayout.Ruin(worldSeed, crossing);
        Vector2 from = crossing.FromBank, dir = crossing.Direction, side = new(-dir.y, dir.x);
        float yaw = BridgeLayout.YawDegrees(dir);              // a beam's length (local x) across the deck
        float alongYaw = yaw + 90f;                            // ... and along it
        float deckH = DeckHeight(crossing, world);            // the wooden kit's, so the ends are its own
        float length = BridgeLayout.BuiltLength(crossing.Width);
        bool freeSpan = crossing.Kind == CrossingKind.Bridge && BridgeLayout.IsFreeSpan(crossing, world);
        (int first, int count, bool deck)? gap = IsSpanFord(crossing) ? null : NavigationBays(crossing, world);
        // Under a deck the ship passes under, the opening's bents are never built, repaired or not, and its deck is carried
        // over them from the bents either side within the free span.
        bool deckCarried = gap is (_, _, false);
        BridgeLayout.RuinProfile profile = Profile;
        // The finished ground (StructureGround): the piles stand on it, and the end stairs' columns are carried down to it.
        var (nearEnd, farEnd) = StructureEnds.Of(crossing, world);
        System.Func<Vector2, float> groundAt = StructureGround.Of(world, nearEnd, farEnd, BeamLength * 0.5f + BuriedPieces.Growth);

        float[] bents = BentAlongs(length);
        int n = bents.Length;
        // No bent where its piles would stand over the wood support ceiling (BridgeLayout.OverWoodCeiling, the railed kits'
        // rule). The deck is carried over a run of bents left out (too tall, or under a deck the ship passes under) from
        // the bents either side within the free span (BridgeLayout.RunCarried), as the railed kits carry theirs; a longer run
        // is left open.
        var tooTall = new bool[n];
        var carriedOver = new bool[n];
        if (!freeSpan)
        {
            for (int i = 0; i < n; i++)
            {
                Vector2 at = from + dir * bents[i];
                tooTall[i] = BridgeLayout.OverWoodCeiling(deckH - Mathf.Min(groundAt(at + side * PileSideOffset), groundAt(at - side * PileSideOffset)));
            }
        }
        var alive = new bool[n];
        // Each standing bent's top piles outside the navigation gap, where a mist light may stand: the same in the complete
        // and the ruined bridge (every Dvergr wood bent outside the gap stands).
        var tops = new List<(int bent, int side, Vector3 top)>();
        // Every bent outside the navigation gap stands, ruined or not: the kit departs from the profile's falling columns
        // because the vanilla harbour it is copied from keeps every pile, and its stringers, which are its deck's frame, stand
        // with them. So no bent leaves a stub or debris.
        for (int i = 0; i < n; i++)
        {
            Vector2 at = from + dir * bents[i];
            bool bank = i == 0 || i == n - 1;
            bool gapBent = gap is (int g0, int gn, _) && i > g0 && i < g0 + gn;
            bool inGap = gapBent && (ruin || deckCarried);
            alive[i] = !inGap && (!freeSpan || bank) && !tooTall[i];
            if (!alive[i]) continue;
            foreach (int s in new[] { BridgeLayout.Ruin.Left, BridgeLayout.Ruin.Right })
            {
                Vector2 p = at + side * (s * PileSideOffset);
                EmitPiles(pieces, p, groundAt(p), deckH + PileAboveDeck, yaw);
                if (!gapBent) tops.Add((i, s, new Vector3(p.x, deckH + PileAboveDeck, p.y)));
            }
        }

        bool LeftOut(int bent) => !alive[bent] && (tooTall[bent] || (deckCarried && gap is (int g0, int gn, _) && bent > g0 && bent < g0 + gn));
        for (int i = 0; i < n; i++)
        {
            if (!LeftOut(i)) continue;
            int j = i;
            while (j < n - 1 && LeftOut(j + 1)) j++;
            bool held = BridgeLayout.RunCarried(bents, i, j) && alive[i - 1] && alive[j + 1];
            for (int k = i; k <= j; k++) carriedOver[k] = held;
            i = j;
        }

        // A bay is carried when both its bents stand or are carried over; a free span's one bay is carried from its two banks.
        bool Held(int bent) => alive[bent] || carriedOver[bent];
        bool Carried(int bay) => freeSpan || (Held(bay) && Held(bay + 1));
        int BayOf(float along)
        {
            for (int i = 0; i + 1 < n; i++) if (along <= bents[i + 1] + 1e-3f) return i;
            return n - 2;
        }

        // Stringers: two lines along the deck under the planks, one beam per bay centred on it (the pier's overlap the
        // bents too), or, over a free span, a chain of beams from bank to bank.
        float stringerY = deckH - BeamDepth * 0.5f - StringerDrop; // 0.3 m up into the planks, as the harbour's
        if (freeSpan)
        {
            int k = Mathf.Max(1, Mathf.CeilToInt(length / BeamLength - 1e-3f));
            for (int j = 0; j < k; j++)
                EmitStringers(pieces, from + dir * (length * (j + 0.5f) / k), side, stringerY, alongYaw);
        }
        else
        {
            for (int i = 0; i + 1 < n; i++)
                if (Carried(i))
                    EmitStringers(pieces, from + dir * ((bents[i] + bents[i + 1]) * 0.5f), side, stringerY, alongYaw);
        }

        // Planks: one across the deck every metre, end for end in turn, over carried bays only. The end bays keep every
        // plank (the way on from the stairs); elsewhere a plank is lost at the profile's deck share. A plank spans the deck
        // from side to side, so a lost one is already a whole bay open: the kit rolls no holes of its own, and keeps the
        // profile's rule for them instead, never two running (where two would be, the second is put back).
        int planks = Mathf.RoundToInt(length / PlankPitch);
        var plankAt = new bool[planks];
        for (int j = 0; j < planks; j++)
        {
            float along = (j + 0.5f) * PlankPitch;
            int bay = BayOf(along);
            if (!Carried(bay)) continue;
            bool endBay = bay == 0 || bay == n - 2;
            float closeness = length > 0f ? 1f - Mathf.Abs(along - length * 0.5f) / (length * 0.5f) : 0f;
            bool lost = ruin && !endBay
                && r.Roll(j, BridgeLayout.Ruin.Centre, RolePlank) >= BridgeLayout.RuinProfile.Survives(profile.Span(closeness), profile.DeckShare);
            bool lastLost = j > 0 && !plankAt[j - 1] && Carried(BayOf((j - 0.5f) * PlankPitch));
            if (lost && !lastLost) continue;
            plankAt[j] = true;
            Vector2 p = from + dir * along;
            pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.Deck,
                Prefab = Plank,
                Position = new Vector3(p.x, deckH - BeamDepth * 0.5f, p.y), // its top is the walking surface
                YawDegrees = j % 2 == 0 ? yaw : yaw + 180f,
            });
        }

        // The bank stairs: the wooden kits' ends as the road meets them (BridgeLayout.LayEndStairs); a step over the ground
        // stands on a Dvergr pile column, carried to the ground.
        BridgeLayout.LayEndStairs(pieces, groundAt, nearEnd, side, r, 0, StairPrefab, Pile, PileLength);
        BridgeLayout.LayEndStairs(pieces, groundAt, farEnd, side, r, BridgeLayout.Bays(crossing.Width) + 1, StairPrefab, Pile, PileLength);

        EmitLights(pieces, r, tops, yaw);

        // Every piece worn as the vanilla harbour's (Location.m_applyRandomDamage), in the one ruin owner.
        if (ruin) r.ApplyLocationDamage(pieces);
        return pieces;
    }

    /// <summary>
    /// Mist lights on a bridge's top piles, as the harbour has them (only some bridges have them, and on those only some posts): a bridge has them at <see cref="LitBridgeChance"/>;
    /// on one that does, one top pile always carries one and each other at <see cref="LightChance"/>, and of those, in order
    /// along the bridge, every <see cref="LightSpacing"/>th keeps its light, <see cref="LightOverPile"/> over the pile's
    /// top, turned as the pile. Fixed per crossing and world seed.
    /// </summary>
    private static void EmitLights(List<StructurePiece> pieces, BridgeLayout.Ruin r, List<(int bent, int side, Vector3 top)> tops, float yaw)
    {
        if (tops.Count == 0 || r.Roll(0, BridgeLayout.Ruin.Centre, RoleLitBridge) >= LitBridgeChance) return;
        int always = Mathf.Min(tops.Count - 1, Mathf.FloorToInt(r.Roll(0, BridgeLayout.Ruin.Centre, RoleLight) * tops.Count));
        int lit = 0;
        for (int k = 0; k < tops.Count; k++)
        {
            var (bent, s, top) = tops[k];
            if (k != always && r.Roll(bent, s, RoleLight, 1) >= LightChance) continue;
            if (lit++ % LightSpacing != 0) continue;
            pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.Flair,
                Prefab = Light,
                Position = new Vector3(top.x, top.y + LightOverPile, top.z),
                YawDegrees = yaw,
            });
        }
    }

    /// <summary>A pile column from <paramref name="top"/> down until the lowest pile's foot is in the ground. Each pile is
    /// <see cref="PileLength"/> with its origin at its centre; none is laid wholly under the ground.</summary>
    private static void EmitPiles(List<StructurePiece> pieces, Vector2 pos, float ground, float top, float yaw)
    {
        for (float t = top; t > ground; t -= PileLength)
        {
            pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.Post,
                Prefab = Pile,
                Position = new Vector3(pos.x, t - PileLength * 0.5f, pos.y),
                YawDegrees = yaw,
            });
            if (t - PileLength <= ground) break;
        }
    }

    private static void EmitStringers(List<StructurePiece> pieces, Vector2 centre, Vector2 side, float y, float alongYaw)
    {
        foreach (int s in new[] { BridgeLayout.Ruin.Left, BridgeLayout.Ruin.Right })
        {
            Vector2 p = centre + side * (s * StringerSideOffset);
            pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.Beam,
                Prefab = Plank,
                Position = new Vector3(p.x, y, p.y),
                YawDegrees = alongYaw,
            });
        }
    }

    /// <summary>One line for road_bridge_repairs and road_crossings.</summary>
    public static string Describe(RoadCrossing crossing, WorldGenerator world) =>
        System.FormattableString.Invariant(
            $"{(IsSpanFord(crossing) ? "ford span, " : "")}deck {DeckHeight(crossing, world):F1} m, {BentAlongs(BridgeLayout.BuiltLength(crossing.Width)).Length} bents over {BridgeLayout.BuiltLength(crossing.Width):F0} m{(BridgeLayout.IsFreeSpan(crossing, world) ? ", free span" : "")}");
}
