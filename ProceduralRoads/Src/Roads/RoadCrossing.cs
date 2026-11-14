using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Bridge: long, deep or sailable water, pieces span it. Ford:
/// knee-deep, unsailable water the road goes through in one of the
/// <see cref="FordStyle"/> ways.</summary>
public enum CrossingKind { Ford, Bridge } // Ford first: the value a fords-only save stores

/// <summary>How a ford treats the shallows: WADE paints the natural bed without levelling it, RAISE
/// levels the road up through the shallows, SPAN builds a short low footbridge with a step at each end. Chosen per
/// site for variety on rivers; standing pools try Wade, Span, then Raise.</summary>
public enum FordStyle { None, Wade, Raise, Span }

/// <summary>Whether a wade may be built, and if not why (<see cref="RoadCrossingDetector.WadeEligibility"/>).</summary>
internal enum WadeVerdict
{
    Eligible,
    /// <summary>A river deeper than its bed is waded, outside a swamp.</summary>
    TooDeep,
    /// <summary>Water a wade takes, but banks too steep to walk as road (<see cref="RoadCrossingDetector.WadeBanksWithinGrade"/>).</summary>
    BanksTooSteep,
}

/// <summary>
/// One river crossing on a generated road (fords and bridges prototypes):
/// where the road leaves each bank, the river profile between the banks,
/// and the fairway, the deepest contiguous stretch, which a bridge leaves
/// open so boats can still sail through.
/// </summary>
public sealed class RoadCrossing
{
    /// <summary>The shortest crossing there is, bank to bank: water whose banks stand closer is crossed as ordinary road, not
    /// recorded as a crossing (<see cref="HasSpan"/>).</summary>
    public const float MinSpan = 1f;

    /// <summary>Whether banks this far apart make a crossing (<see cref="MinSpan"/>). The one rule for every producer
    /// (<see cref="RoadCrossingDetector.Build"/>, its deck-line walk, standing water) and for the crossing record
    /// (<see cref="RoadNetworkPersistence.CrossingRecordFault"/>), so no crossing is stored that the save cannot read back.</summary>
    public static bool HasSpan(float span) => span >= MinSpan;

    public CrossingKind Kind = CrossingKind.Ford;
    public FordStyle Style = FordStyle.None;
    /// <summary>The island worker that recorded it (<see cref="CrossIslandReads.Tag"/>): another island's worker does not see it.
    /// 0 for none. Not saved.</summary>
    public int Island;

    /// <summary>Path vertices bracketing the water: the crossing is the path
    /// segment [FromIndex, ToIndex]; once its ends are met (StructureEnds.MeetEnds), a structure's
    /// are its two feet. Not persisted; only the generation pass that paints the crossing needs them.</summary>
    public int FromIndex;
    public int ToIndex;

    /// <summary>Last road ground on each side: where the crossing starts and ends.</summary>
    public Vector2 FromBank;
    public Vector2 ToBank;
    /// <summary>Where the bank was before an end over land
    /// standing above the deck was walked in (<see cref="RoadCrossingDetector.OffLand"/>);
    /// the strip from here to the bank is road, cut down to a straight ramp.
    /// Null when the end did not move. Not persisted.</summary>
    public Vector2? FromLand;
    public Vector2? ToLand;
    /// <summary>Standing water, not a river jump. Saved in the crossing flags.</summary>
    public bool Shallow;
    /// <summary>Snapped to a crossing another road built (SnapToExistingCrossings). Not persisted.</summary>
    public bool Shared;
    /// <summary>Water no eligible treatment can cross, including a pool whose treatments this plan exhausted: the road must go round.</summary>
    public bool Invalid;
    /// <summary>Why this shallow ford got its style (length, depth, heading, eligible styles). Not persisted.</summary>
    public string? ShallowNote;
    /// <summary>The measured wet run's centre before its treatment moves the banks. Only for this plan's bounded retries.</summary>
    internal Vector2? ShallowSite;
    /// <summary>The measured wet length, before treatment moves its banks. Used by the search's shallow-water rate;
    /// the dry approaches a wade carries are road, not more water. Not persisted.</summary>
    internal float WetLength;
    /// <summary>A Mistlands viaduct the search bought over a dry valley (DvergrViaduct.ValleyCrossing),
    /// not a river jump. Saved with the crossing (its flags, RoadNetworkPersistence.CrossingsFormat), so a loaded
    /// network knows its valley viaducts: their keep-out comes back with them. Its pieces are planned
    /// the same either way, by DvergrViaduct.Applies, from its banks and bed.</summary>
    public bool ValleyJump;
    /// <summary>Its banks were moved from the water's edge to the bank tops (the detector's optional bank-top climb, kept
    /// past the heading rule and the support ceiling's trim). The search's bridge height share is given only where it is not
    /// (RoadPathfinder.BridgeStoneReach): the search detects on the jump's straight line, the builder on the road, which may
    /// bend within the climb's reach and lay the deck at the water's edge instead.</summary>
    public bool OnTops;
    /// <summary>The kit it is built in, saved with it. Its kind is decided when the crossing is stored
    /// (BridgeStructures.DecideKind): a viaduct's kit, or Undecided for any other deck until it is first built, when its
    /// kit is chosen among the kits installed then (BridgeStructures.ChooseKit) and kept from then on. Null for a crossing
    /// that is road, until it is stored, and for a record saved without it (chosen as an undecided one is).</summary>
    public BridgeStructures.Kit? Kit;
    /// <summary>Its two ends as the road meets them, near (from-bank) end first: each a point and height on the road, the
    /// foot of the stairs laid down to it (or the deck's edge, or the bank where the crossing is road). Decided by the one
    /// owner (StructureEnds.MeetEnds) when the road is searched, among the ways the road reaches, and saved with the
    /// crossing: every kit lays its stairs down to them. Null until decided, and for a record saved without them (each
    /// end then as the bank takes it, StructureEnds.Natural).</summary>
    public StructureEnds.End? FromEnd, ToEnd;
    /// <summary>The most steps a viaduct's deck may stand over the deck at grade (DvergrViaduct.RaisedSteps): lowered a
    /// step at a time where the road reaches no way of meeting a raised end (StructureEnds.MeetEnds). Not saved: the ends
    /// met carry the deck's height.</summary>
    internal int RaiseAtMost = DvergrViaduct.MostRaisedSteps;
    /// <summary>Other roads on the ground the landing at an end takes, and the height each point is raised to
    /// (RoadNetworkGenerator.CrossingGround.RaiseOthers): moved in the network when the road is laid with the crossing (RoadNetworkGenerator).
    /// Decided with the end; not saved, the network saves them as raised.</summary>
    internal List<(RoadSpatialGrid.RoadPoint at, float to)>? FromRaised, ToRaised;
    /// <summary>The search's own moves the crossing lies on: its path's steps and jumps from <see cref="FromIndex"/> to
    /// <see cref="ToIndex"/> as searched, before its ends were met (RoadNetworkGenerator.PlanFromPath). A crossing not built
    /// with the road is searched again round them (RoadPathfinder.BanJump): its banks alone match no move of a ford walked
    /// cell by cell, nor of a bridge whose end was moved along the path. Not saved.</summary>
    internal List<(Vector2 from, Vector2 to)>? Searched;
    /// <summary>An end met by earthwork: its landing filled over ground under the road's floor (StructureEnds.MeetEnds).
    /// Decided with the end, counted when the road is laid; not saved.</summary>
    internal bool FromEarthwork, ToEarthwork;
    /// <summary>An end the road leaves from its landing pad, off its line (StructureEnds.PadLength). Decided with the end,
    /// counted when the road is laid; not saved.</summary>
    internal bool FromPad, ToPad;
    /// <summary>An end the road meets where its last point before the next structure or its own end stands, no point of
    /// it clear of the end's apron or pad (StructureEnds.Approach). Decided with the end, counted when the road is laid;
    /// not saved.</summary>
    internal bool FromStands, ToStands;
    /// <summary>An end met on a landing it shares with the facing end of the next structure along the road
    /// (StructureEnds.Share). Decided with the end, counted when the road is laid; not saved.</summary>
    internal bool FromShared, ToShared;

    public override string ToString() => System.FormattableString.Invariant(
        $"{Kind}{(Kind == CrossingKind.Ford ? "/" + Style : "")} {FromBank} -> {ToBank}, {Width:F1} m{(Kit is BridgeStructures.Kit k ? ", " + k : "")}{(ValleyJump ? ", valley" : "")}");

    /// <summary>Midpoint between the banks.</summary>
    public Vector2 Center;

    /// <summary>Normalized FromBank -> ToBank.</summary>
    public Vector2 Direction;

    /// <summary>Bank-to-bank distance in metres.</summary>
    public float Width;

    public float WaterLevel = RoadConstants.SeaLevel;

    /// <summary>Lowest terrain height between the banks.</summary>
    public float RiverbedHeight;

    /// <summary>Centre of the deepest contiguous stretch (depth at least
    /// <see cref="RoadCrossingDetector.FairwayMinDepth"/>), or the single
    /// deepest sample when no stretch qualifies.</summary>
    public Vector2 FairwayCenter;

    /// <summary>Length of that stretch in metres; 0 when the river is too shallow to sail.</summary>
    public float FairwayWidth;

    /// <summary>Two routes over the same jump each record a crossing; a
    /// crossing whose banks both lie within this distance of another's (in
    /// either order) is the same site.</summary>
    public const float SharedBankRadius = 4f;

    public static bool SameBanks(RoadCrossing a, RoadCrossing b) => SameBanks(a, b, SharedBankRadius);

    public static bool SameBanks(RoadCrossing a, RoadCrossing b, float radius) => SameBanks(a, b.FromBank, b.ToBank, radius);

    /// <summary>Whether a crossing's banks are these two, either way round, within <paramref name="radius"/>.</summary>
    public static bool SameBanks(RoadCrossing a, Vector2 fromBank, Vector2 toBank, float radius)
    {
        float r2 = radius * radius;
        return ((a.FromBank - fromBank).sqrMagnitude <= r2 && (a.ToBank - toBank).sqrMagnitude <= r2)
            || ((a.FromBank - toBank).sqrMagnitude <= r2 && (a.ToBank - fromBank).sqrMagnitude <= r2);
    }

    /// <summary>
    /// Makes this crossing the same site as <paramref name="existing"/>: its
    /// banks (in this crossing's own order), profile, kind and style. The
    /// road that recorded this crossing is then painted up to the shared
    /// banks, so it meets the one bridge built there.
    /// </summary>
    public void SnapTo(RoadCrossing existing)
    {
        bool sameOrder = (FromBank - existing.FromBank).sqrMagnitude <= (FromBank - existing.ToBank).sqrMagnitude;
        FromBank = sameOrder ? existing.FromBank : existing.ToBank;
        ToBank = sameOrder ? existing.ToBank : existing.FromBank;
        Center = existing.Center;
        Direction = ToBank - FromBank;
        Direction.Normalize();
        Width = existing.Width;
        RiverbedHeight = existing.RiverbedHeight;
        FairwayCenter = existing.FairwayCenter;
        FairwayWidth = existing.FairwayWidth;
        Kind = existing.Kind;
        Style = existing.Style;
        Kit = existing.Kit;
        FromEnd = sameOrder ? existing.FromEnd : existing.ToEnd;
        ToEnd = sameOrder ? existing.ToEnd : existing.FromEnd;
        FromRaised = ToRaised = null;
        FromEarthwork = ToEarthwork = false;
        // These describe the adopted crossing, not the discarded candidate.
        // Otherwise an invalid shallow ford stays invalid after it has been
        // replaced by a valid bridge already carrying the network.
        Shallow = existing.Shallow;
        Invalid = existing.Invalid;
        ShallowNote = existing.ShallowNote;
        ShallowSite = existing.ShallowSite;
        WetLength = existing.WetLength;
        ValleyJump = existing.ValleyJump;
    }

    /// <summary>A crossing between two banks with its derived fields filled in.</summary>
    public static RoadCrossing Between(Vector2 fromBank, Vector2 toBank, float riverbedHeight,
        Vector2 fairwayCenter, float fairwayWidth, CrossingKind kind = CrossingKind.Ford, FordStyle style = FordStyle.None)
    {
        Vector2 direction = toBank - fromBank;
        direction.Normalize();
        return new RoadCrossing
        {
            Kind = kind,
            Style = style,
            FromBank = fromBank,
            ToBank = toBank,
            Center = (fromBank + toBank) * 0.5f,
            Direction = direction,
            Width = Vector2.Distance(fromBank, toBank),
            RiverbedHeight = riverbedHeight,
            FairwayCenter = fairwayCenter,
            FairwayWidth = fairwayWidth,
        };
    }

    /// <summary>Signed distance of a point along the crossing line, from FromBank toward ToBank.</summary>
    public float Along(Vector2 p) => Vector2.Dot(p - FromBank, Direction);
}

/// <summary>
/// Finds the river crossings on a finished path. With fords or bridges on,
/// the pathfinder jumps a river in one straight segment from dry cell to
/// dry cell; this walks each such segment to the water's edge on both
/// sides, profiles the river between them and decides whether it is a ford
/// (and in which style) or, with bridges on, a bridge. Pure logic: the same
/// code runs in the game and in the headless test harness.
/// </summary>
public static class RoadCrossingDetector
{
    /// <summary>
    /// A bridge spans water, not land. The scan crosses the whole river band,
    /// dry shore included, so a bar, hump or ridge inside it was decked over
    /// and the deck ran through it. A run of ground above
    /// <see cref="Waterline"/> at least <see cref="MinLand"/> long inside a
    /// jump splits it, the land between built as road, and a deck end over
    /// land standing above the deck is walked in (<see cref="OffLand"/>).
    /// Walking every end in to the water instead, and raising the shore to
    /// meet it, built a causeway out to the bridge. Settable for tests.
    /// </summary>
    internal static bool ToWater = true;
    /// <summary>Ground this far under the water line or deeper is channel; above it, land that splits a jump.</summary>
    public const float WaterDepth = 0.4f;
    public const float Waterline = RoadConstants.SeaLevel - WaterDepth;
    /// <summary>Land inside a jump at least this long splits the crossing.</summary>
    public const float MinLand = 8f;

    private static bool AboveWaterline(Vector2 p, WorldGenerator world) =>
        BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) >= Waterline;

    /// <summary>Waypoints at each end of every run of ground above the waterline
    /// at least <see cref="MinLand"/> long lying between water on a river jump,
    /// so the jump is detected as two crossings with land between.</summary>
    internal static void SplitAtLand(List<Vector2> path, WorldGenerator world)
    {
        for (int i = path.Count - 1; i >= 1; i--)
        {
            Vector2 a = path[i - 1], b = path[i];
            float length = Vector2.Distance(a, b);
            if (length < 2f * MinLand || !SegmentCrossesRiver(a, b, world)) continue;
            int n = Mathf.CeilToInt(length / 2f);
            var dry = new bool[n + 1];
            for (int k = 0; k <= n; k++) dry[k] = AboveWaterline(a + (b - a) * (k / (float)n), world);
            var inserts = new List<Vector2>();
            bool seenWet = false;
            for (int k = 0; k <= n; k++)
            {
                if (!dry[k]) { seenWet = true; continue; }
                int start = k;
                while (k <= n && dry[k]) k++;
                int end = k - 1;
                bool wetAfter = k <= n;
                if (seenWet && wetAfter && (end - start) * length / n >= MinLand)
                {
                    inserts.Add(a + (b - a) * (start / (float)n));
                    inserts.Add(a + (b - a) * (end / (float)n));
                }
                seenWet = true;
            }
            path.InsertRange(i, inserts);
        }
    }

    /// <summary>Ground this far above a deck pokes through it: every kit's deck, the viaduct's too (DvergrViaduct.ValleyCrossing).</summary>
    public const float DeckClip = 0.25f;

    /// <summary>
    /// From a bank toward the other, the last road ground
    /// before the water -- but only when the land between stands above the
    /// deck; otherwise the bank itself. The bank is the first dip under road
    /// ground, so a shore that dips and then rises into a hump before the
    /// water was decked over and the deck ran through the hump (measured: 28 m
    /// of dry land under a deck, peaking 4.6 m above it). Unlike walking every
    /// end in to the water, nothing is filled: the strip left behind is road
    /// cut down to a straight ramp between the old and new banks, and a low
    /// shore that merely dips is left alone.
    /// </summary>
    internal static Vector2 OffLand(Vector2 bank, Vector2 other, float deck, WorldGenerator world)
    {
        float length = Vector2.Distance(bank, other);
        Vector2 dir = (other - bank) * (1f / Mathf.Max(length, 1e-4f));
        bool clips = false;
        float last = 0f;
        for (float d = 1f; d <= length - 4f; d += 1f)
        {
            Vector2 p = bank + dir * d;
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (h < Waterline) break;
            if (h > deck + DeckClip) clips = true;
            if (IsRoadGround(p, world)) last = d;
        }
        return clips ? bank + dir * last : bank;
    }

    /// <summary>
    /// The step a deck line is sampled at between its banks (<see cref="GroundThroughDeck"/>): 1 m, the valley
    /// viaduct's own (DvergrViaduct.ValleyCrossing). Every other look at a crossing's ground is coarser or looks
    /// elsewhere: the router's cells are 8 m apart, the bank searches walk the ROAD rather than the deck, and
    /// <see cref="OffLand"/> walks only from a bank to the water. A 103 m bridge's deck
    /// ran through a ridge 12 m over it between the river and its far bank.
    /// </summary>
    public const float DeckLineStep = 1f;

    /// <summary>
    /// Where a deck line meets the ground: the first point, every <see cref="DeckLineStep"/> from
    /// <paramref name="from"/> to <paramref name="to"/>, whose ground stands more than <see cref="DeckClip"/> over the
    /// deck's walking surface <paramref name="deck"/>; null when the line is clear. The valley viaduct's rule
    /// (DvergrViaduct.ValleyCrossing, "ground over the deck") for every crossing that carries a deck. Measured against
    /// the walking surface, not the underside: a deck stands at its higher bank's ground by construction, so that
    /// bank's own ground is already in the planks' depth, and the ground in under each end is cut below the deck when
    /// it is built (BridgeStructures.GroundUnderDeck). Ground over the walking surface is ground a player walks
    /// into, and no earthwork under the deck is planned to remove it.
    /// </summary>
    internal static Vector2? GroundThroughDeck(Vector2 from, Vector2 to, float deck, WorldGenerator world)
    {
        float length = Vector2.Distance(from, to);
        int samples = Mathf.Max(1, Mathf.CeilToInt(length / DeckLineStep));
        for (int i = 0; i <= samples; i++)
        {
            Vector2 p = Vector2.Lerp(from, to, i / (float)samples);
            if (BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) > deck + DeckClip) return p;
        }
        return null;
    }

    /// <summary>What the deck-line rule did, for the generator's summary: bridges whose ends were walked in to the
    /// water's edge, bridges refused (the road goes round or is not built), ford spans laid as another ford style.</summary>
    public static int DeckLineWalkedIn, DeckLineRefused, DeckLineSpanDropped;

    /// <summary>
    /// The last word on where a bridge's banks are: its deck line, sampled every <see cref="DeckLineStep"/>, meets no
    /// ground over the deck (<see cref="GroundThroughDeck"/>). Where it does, each end whose land stands over the deck
    /// before the water is walked in to the last road ground there (<see cref="OffLand"/>, with this deck), so the bridge
    /// ends at the first dry ground and the road goes on over the land it gave up (FromLand / ToLand: the strip is cut
    /// to a ramp and held to the earthwork cap, as for any end walked in). A bank-top climb gives way to it: a bridge
    /// springing from a top is not worth a deck through a ridge. Where the line still meets ground (a bar or hump in the
    /// water itself), the crossing is INVALID: the generator searches round it and refuses the road if it cannot
    /// (RoadNetworkGenerator.InvalidCrossings), as for shallow water no ford may cross. Runs on the geometry every
    /// other rule here has settled, and the line it walks along is the crossing's own, so the heading holds.
    /// </summary>
    private static RoadCrossing ClearOfGround(RoadCrossing crossing, WorldGenerator world)
    {
        float deck = BridgeStructures.DeckHeight(crossing, world);
        if (GroundThroughDeck(crossing.FromBank, crossing.ToBank, deck, world) == null) return crossing;
        Vector2 from = OffLand(crossing.FromBank, crossing.ToBank, deck, world);
        Vector2 to = OffLand(crossing.ToBank, from, deck, world);
        if ((from != crossing.FromBank || to != crossing.ToBank) && RoadCrossing.HasSpan(Vector2.Distance(from, to)))
        {
            (float bed, Vector2 fairwayCenter, float fairwayWidth) = Profile(from, to, world);
            if (RoadWater.Wet(bed))
            {
                RoadCrossing walked = RoadCrossing.Between(from, to, bed, fairwayCenter, fairwayWidth, crossing.Kind, crossing.Style);
                walked.FromIndex = crossing.FromIndex;
                walked.ToIndex = crossing.ToIndex;
                walked.OnTops = crossing.OnTops;
                // The strip the road takes over runs from the outermost end the bridge ever had.
                walked.FromLand = from != crossing.FromBank ? crossing.FromLand ?? crossing.FromBank : crossing.FromLand;
                walked.ToLand = to != crossing.ToBank ? crossing.ToLand ?? crossing.ToBank : crossing.ToLand;
                if (GroundThroughDeck(from, to, BridgeStructures.DeckHeight(walked, world), world) == null)
                {
                    System.Threading.Interlocked.Increment(ref DeckLineWalkedIn);
                    return walked;
                }
            }
        }
        System.Threading.Interlocked.Increment(ref DeckLineRefused);
        crossing.Invalid = true;
        return crossing;
    }

    /// <summary>A river wade keeps its natural-bed depth allowance. Standing water may raise its bed within
    /// the existing pool depth limit. Both judge the surface the builder will lay against the bank-grade rule.
    /// <paramref name="anyDepth"/>: the crossing's biome wades at any depth (RoadRules.WadesAnyDepth).</summary>
    internal static WadeVerdict WadeEligibility(float depth, bool anyDepth, Vector2 from, Vector2 to, WorldGenerator world,
        bool standing = false)
    {
        if (!(standing ? depth <= ShallowMaxDepth : anyDepth || depth <= RoadConstants.FordWadeMaxDepth)) return WadeVerdict.TooDeep;
        return WadeBanksWithinGrade(from, to, world, WadeSurface(standing, depth).floor) ? WadeVerdict.Eligible : WadeVerdict.BanksTooSteep;
    }

    /// <summary>Shallow wades paint the natural bed; a deeper standing pool raises it only to the wade limit.</summary>
    internal static (bool followTerrain, float floor) WadeSurface(bool standing = false, float depth = 0f) =>
        standing && depth > RoadConstants.FordWadeMaxDepth
            ? (false, RoadConstants.SeaLevel - RoadConstants.FordWadeMaxDepth)
            : (true, float.NegativeInfinity);

    /// <summary>Whether the waded surface stays within the road grade from bank to bank, with its selected floor, met at each
    /// bank by the road on land as it is built there (no lower than the dry-land floor, RoadConstants.DryRoadFloor): a wade
    /// whose bed drops off the bank's road past the cap is not a wade (BRIEF-RULES 17), and the pool takes the next
    /// treatment.</summary>
    internal static bool WadeBanksWithinGrade(Vector2 from, Vector2 to, WorldGenerator world, float floor = float.NegativeInfinity)
    {
        const float step = 2f;
        float length = Vector2.Distance(from, to);
        int n = Mathf.Max(1, Mathf.CeilToInt(length / step));
        float Bed(Vector2 p)
        {
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            return floor > float.NegativeInfinity ? Mathf.Max(h, floor) : h;
        }
        float Bank(Vector2 p) => Mathf.Max(BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world), RoadConstants.DryRoadFloor);
        float run = length / n;
        if (!StairLayout.Walkable(Bed(from), Bank(from))) return false;
        float last = Bed(from);
        for (int k = 1; k <= n; k++)
        {
            Vector2 p = Vector2.Lerp(from, to, (float)k / n);
            float h = Bed(p);
            if (Mathf.Abs(h - last) > RoadGrade.Configured * run) return false;
            last = h;
        }
        return StairLayout.Walkable(Bank(to), last);
    }

    /// <summary>Minimum water depth that counts as sailable fairway (<see cref="Afloat"/>).</summary>
    public const float FairwayMinDepth = 1.2f;

    /// <summary>Whether a ship floats over <paramref name="ground"/> under water at <paramref name="waterLevel"/>: at least
    /// <see cref="FairwayMinDepth"/> deep. The one test of sailable water: the detector's fairway (its profile, at sea level,
    /// the level of every crossing it makes) and the bridge's boat lane (BridgeLayout.NavigationChannel, at the crossing's
    /// RoadCrossing.WaterLevel) both ask it.</summary>
    internal static bool Afloat(float waterLevel, float ground) => waterLevel - ground >= FairwayMinDepth;

    private const float SampleSpacing = 2f;

    /// <summary>Steps for the trimmed-climb search: how finely the target deck
    /// is walked down, and how low it is worth walking before the water's edge
    /// is the better answer anyway.</summary>
    private const float TrimStep = 0.5f;
    private const float MinTrimmedPier = 2f;
    private const float ShoreStep = 0.5f;

    /// <summary>Relative odds of each ford style among the styles a site
    /// allows, equal by default. 0 removes a style; when every allowed style
    /// is 0 the site raises the road (always allowed). Settable for tests.</summary>
    public static float ConfiguredWadeWeight = RoadConstants.DefaultFordStyleWeight;
    public static float ConfiguredRaiseWeight = RoadConstants.DefaultFordStyleWeight;
    public static float ConfiguredSpanWeight = RoadConstants.DefaultFordStyleWeight;

    /// <summary>Decline a bank-top climb that would put the deck out of
    /// reach of vanilla's structural support (RoadConstants.MaxBridgePierHeight).
    /// Settable for tests.</summary>
    internal static bool DeclineTallClimbs = true;

    /// <summary>Which arm of the trim ladder each over-tall climb took.
    /// Reported so a rule that never fires is visible as a zero rather than
    /// as an unchanged result that looks like agreement.</summary>
    public static int TrimSeen, TrimAlreadyFine, TrimNoBanks, TrimStillTall, TrimTrimmed, TrimToEdge, TrimStuck;

    public static void SetFordStyleWeights(float wade, float raise) => SetFordStyleWeights(wade, raise, ConfiguredSpanWeight);

    public static void SetFordStyleWeights(float wade, float raise, float span)
    {
        ConfiguredWadeWeight = Mathf.Max(0f, wade);
        ConfiguredRaiseWeight = Mathf.Max(0f, raise);
        ConfiguredSpanWeight = Mathf.Max(0f, span);
    }

    /// <summary>The crossings on a path, classified as the pathfinder
    /// accepted them: with <paramref name="bridges"/> a jump longer than a
    /// ford, or over water too deep or sailable for one, is a bridge and a
    /// ford may be spanned; without <paramref name="fords"/> every crossing
    /// is a bridge; without bridges every crossing is a ford.</summary>
    public static List<RoadCrossing> Detect(List<Vector2> path, WorldGenerator world, bool bridges = false, bool fords = true,
        IReadOnlyList<(Vector2 site, FordStyle style)>? refusedShallows = null)
    {
        List<RoadCrossing> crossings = new();
        if (path == null || path.Count < 2 || world == null)
            return crossings;
        if (bridges && ToWater)
            SplitAtLand(path, world);

        for (int i = 1; i < path.Count; i++)
        {
            if (!SegmentCrossesRiver(path[i - 1], path[i], world))
                continue;
            RoadCrossing? crossing = Build(path, i - 1, i, world, bridges, fords);
            if (crossing != null)
                crossings.Add(crossing);
        }
        if ((fords || bridges) && Shallows)
        {
            DetectShallows(path, world, bridges, fords, crossings, refusedShallows);
            crossings.Sort((x, y) => x.FromIndex.CompareTo(y.FromIndex));
            var spacingRefusals = refusedShallows == null
                ? new List<(Vector2 site, FordStyle style)>() : new List<(Vector2 site, FordStyle style)>(refusedShallows);
            while (KeepStructuresApart(crossings) is RoadCrossing crowded)
            {
                // Reclassify a deep pool through its owner, with full bank context and its prior treatment refusals.
                spacingRefusals.Add((crowded.ShallowSite!.Value, FordStyle.Span));
                crossings.RemoveAll(c => c.Shallow);
                DetectShallows(path, world, bridges, fords, crossings, spacingRefusals);
                crossings.Sort((x, y) => x.FromIndex.CompareTo(y.FromIndex));
            }
            // Spacing may replace a shallow span with a raise. It cannot bring back a treatment this plan already refused.
            if (refusedShallows != null)
                foreach (var c in crossings)
                    foreach (var refused in refusedShallows)
                        if (c.ShallowSite == refused.site && c.Style == refused.style) c.Invalid = true;
        }
        return crossings;
    }

    /// <summary>
    /// Two structures on one road with less than a stub of road between them (StructureEnds.StubLength: a span's end apron
    /// and its approach need that much) would stand on each other's ends: the later shallow one (else the earlier) is
    /// raised instead when shallow, or returned for reclassification through DetectShallows when deep. Each pool keeps its own crossing: merged, two pools would make a
    /// crossing longer than a ford may be.
    /// </summary>
    internal static RoadCrossing? KeepStructuresApart(List<RoadCrossing> crossings)
    {
        static bool Structure(RoadCrossing c) => BridgeStructures.HasDeck(c);
        for (int k = 1; k < crossings.Count; k++)
        {
            RoadCrossing a = crossings[k - 1], b = crossings[k];
            if (!Structure(a) || !Structure(b) || Vector2.Distance(a.ToBank, b.FromBank) >= StructureEnds.StubLength) continue;
            RoadCrossing? raised = b.Shallow && b.Kind == CrossingKind.Ford ? b : a.Shallow && a.Kind == CrossingKind.Ford ? a : null;
            if (raised == null) continue;
            if (!WadeSurface(raised.Shallow, RoadConstants.SeaLevel - raised.RiverbedHeight).followTerrain) return raised;
            raised.Style = FordStyle.Raise;
        }
        return null;
    }

    /// <summary>Standing water is detected on the natural ground by the same owner as river crossings. Settable for tests.</summary>
    internal static bool Shallows = true;
    /// <summary>A wet run shorter than one path cell is a puddle: the dry-land floor raises it.</summary>
    public const float ShallowMinLength = RoadConstants.PathfindingCellSize;
    /// <summary>The deepest standing water a ford may raise. Deeper water needs a bridge.</summary>
    public const float ShallowMaxDepth = 3f;

    /// <summary>The far bank of a deck from <paramref name="from"/> on the nearest buildable
    /// heading that reaches dry ground within 8 m of <paramref name="to"/>, or null.</summary>
    internal static Vector2? TurnedShallowBank(Vector2 from, Vector2 to, float length, WorldGenerator world)
    {
        float yaw = BridgeLayout.YawDegrees((to - from).normalized);
        foreach (float h in BridgeLayout.NearestPlaceableHeadings(yaw, 3))
        {
            float r = h * Mathf.PI / 180f;
            var dir = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            bool sawWater = false;
            for (float d = 1f; d <= length + 12f; d += 1f)
            {
                Vector2 p = from + dir * d;
                bool wet = RoadWater.Wet(BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
                if (wet) { sawWater = true; continue; }
                if (!sawWater) continue;
                if (Vector2.Distance(p, to) <= 8f && WithinFordCap(d)) return p;
                break;
            }
        }
        return null;
    }

    private static void DetectShallows(List<Vector2> path, WorldGenerator world, bool bridges, bool fords, List<RoadCrossing> crossings,
        IReadOnlyList<(Vector2 site, FordStyle style)>? refusedShallows)
    {
        // Measure the whole pool at a wet destination as at a through crossing. Only a wade can end
        // inside it; its banks are clipped back to the requested ends after the same eligibility decision.
        var original = path;
        const float step = 1f;
        Vector2? ShoreBeyond(int index, int inward)
        {
            Vector2 end = path[index];
            if (!RoadWater.Wet(BiomeBlendedHeight.GetBlendedHeight(end.x, end.y, world))) return null;
            int next = index + inward;
            while (next >= 0 && next < path.Count && path[next] == end) next += inward;
            if (next < 0 || next >= path.Count) return null;
            Vector2 direction = (end - path[next]).normalized;
            bool leftWater = false;
            for (float d = step; d <= RoadConstants.MaxBridgeSpan; d += step)
            {
                Vector2 p = end + direction * d;
                float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
                if (RoadWater.BuiltDry(h)) return p;
                if (leftWater && RoadWater.Wet(h)) return end + direction * (d - step);
                leftWater |= !RoadWater.Wet(h);
            }
            return null;
        }
        Vector2? before = ShoreBeyond(0, 1), after = ShoreBeyond(path.Count - 1, -1);
        int offset = before.HasValue ? 1 : 0, firstCrossing = crossings.Count;
        if (before.HasValue || after.HasValue)
        {
            path = new List<Vector2>(path);
            if (before is Vector2 a) path.Insert(0, a);
            if (after is Vector2 b) path.Add(b);
        }
        // Walk the path at 1 m and collect the runs whose natural ground is under the water line,
        // outside every crossing already found.
        bool Taken(int seg)
        {
            for (int i = 0; i < crossings.Count; i++)
            {
                var c = crossings[i];
                int at = i < firstCrossing ? seg - offset : seg;
                if (at >= c.FromIndex && at < c.ToIndex) return true;
            }
            return SegmentCrossesRiver(path[seg], path[seg + 1], world);
        }
        int runSeg = -1; Vector2 runStart = default; float runLen = 0f, bed = float.MaxValue;
        Vector2 last = path[0];
        float lastHeight = BiomeBlendedHeight.GetBlendedHeight(last.x, last.y, world);
        void Close(int endSeg, Vector2 end)
        {
            if (runSeg < 0) return;
            float length = runLen;
            if (length >= ShallowMinLength)
            {
                (float measuredBed, Vector2 fairwayCenter, float fairwayWidth) = Profile(runStart, end, world);
                bed = Mathf.Min(bed, measuredBed);
                var c = RoadCrossing.Between(runStart, end, bed, fairwayCenter, fairwayWidth);
                Vector2? spanTo = null;
                c.FromIndex = runSeg;
                c.ToIndex = endSeg + 1;
                c.Shallow = true;
                float depth = RoadConstants.SeaLevel - bed;
                bool ford = fords && IsFord(length, RoadRules.For(RoadBiome.At(world, c.Center)).FordsDeepBed, bed, fairwayWidth, standing: true);
                c.Kind = ford ? CrossingKind.Ford : CrossingKind.Bridge;
                c.Invalid = !ford && !bridges;
                var eligible = new List<FordStyle>();
                var wadeFrom = BankTop(path, runStart, runSeg, -1, world, RoadConstants.DryRoadFloor);
                var wadeTo = BankTop(path, end, endSeg + 1, +1, world, RoadConstants.DryRoadFloor);
                WadeVerdict wade = WadeEligibility(depth, false, wadeFrom.top, wadeTo.top, world, standing: true);
                if (!RoadWater.BuiltDry(wadeFrom.height) || !RoadWater.BuiltDry(wadeTo.height)) wade = WadeVerdict.BanksTooSteep;
                if (ford)
                {
                    if (WadeSurface(true, depth).followTerrain) eligible.Add(FordStyle.Raise);
                    if (wade == WadeVerdict.Eligible) eligible.Add(FordStyle.Wade);
                    if (bridges && length >= RoadConstants.FordSpanMinWidth)
                    {
                        if (BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(c.Direction))) spanTo = end;
                        else spanTo = TurnedShallowBank(runStart, end, length, world);
                        if (spanTo.HasValue) eligible.Add(FordStyle.Span);
                    }
                }
                Vector2 site = c.Center;
                bool recovering = false, raiseRefused = false;
                if (refusedShallows != null)
                    foreach (var refused in refusedShallows)
                        if (refused.site == site)
                        {
                            eligible.Remove(refused.style);
                            recovering = true;
                            raiseRefused |= refused.style == FordStyle.Raise;
                        }
                if (ford && eligible.Count == 0) c.Invalid = true;
                c.Style = !ford || eligible.Count == 0 ? FordStyle.None
                    : PickFordStyle(eligible, SiteHash(site), standing: true);
                // The deck-line rule (ClearOfGround) for the span this pool would get.
                if (c.Style == FordStyle.Span && spanTo.HasValue)
                {
                    var span = RoadCrossing.Between(runStart, spanTo.Value, bed, fairwayCenter, fairwayWidth, CrossingKind.Ford, FordStyle.Span);
                    if (GroundThroughDeck(span.FromBank, span.ToBank, BridgeStructures.DeckHeight(span, world), world) != null)
                    {
                        System.Threading.Interlocked.Increment(ref DeckLineSpanDropped);
                        eligible.Remove(FordStyle.Span);
                        spanTo = null;
                        c.Style = eligible.Count == 0 ? FordStyle.None : PickFordStyle(eligible, SiteHash(site), standing: true);
                        if (eligible.Count == 0) c.Invalid = true;
                    }
                }
                // A deeper pool keeps its dry floor only as recovery after its other treatments failed.
                // Never offer it to the style picker while a wade or span remains, even at zero weight.
                bool recoveryRaise = ford && !WadeSurface(true, depth).followTerrain
                    && eligible.Count == 0 && recovering && !raiseRefused;
                if (recoveryRaise)
                {
                    c.Style = FordStyle.Raise;
                    c.Invalid = false;
                }
                if (c.Style == FordStyle.Wade)
                {
                    // The painted bed carries on to dry-floor ground, so its approach has no raised lip at the water line.
                    c = RoadCrossing.Between(wadeFrom.top, wadeTo.top, bed, fairwayCenter, fairwayWidth, CrossingKind.Ford, FordStyle.Wade);
                    c.FromIndex = wadeFrom.index; c.ToIndex = wadeTo.index; c.Shallow = true;
                }
                if (c.Style == FordStyle.Span && spanTo.HasValue && spanTo.Value != end)
                {
                    var turned = RoadCrossing.Between(runStart, spanTo.Value, bed, fairwayCenter, fairwayWidth, CrossingKind.Ford, FordStyle.Span);
                    turned.FromIndex = c.FromIndex; turned.ToIndex = c.ToIndex; turned.Shallow = true;
                    c = turned;
                }
                c.ShallowNote = System.FormattableString.Invariant(
                    $"standing water at ({c.Center.x:F0},{c.Center.y:F0}): {length:F0} m, {depth:F2} m deep, heading {BridgeLayout.YawDegrees(c.Direction):F0}, eligible {string.Join("/", eligible)} -> {c.Style}{(recoveryRaise ? " (recovery Raise)" : "")}{(c.Invalid ? eligible.Count == 0 && ford ? " INVALID (no eligible treatment)" : " INVALID (too long or deep)" : "")}");
                if (c.Kind == CrossingKind.Bridge && !c.Invalid)
                {
                    if (!BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(c.Direction)))
                    {
                        Vector2 from = c.FromBank, to = c.ToBank;
                        if (SnapToPlaceableHeading(ref from, ref to, world,
                                q => RoadWater.AtOrOverSea(BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world))))
                        {
                            (float turnedBed, Vector2 turnedFairway, float turnedWidth) = Profile(from, to, world);
                            var turned = RoadCrossing.Between(from, to, turnedBed, turnedFairway, turnedWidth, CrossingKind.Bridge);
                            turned.FromIndex = c.FromIndex; turned.ToIndex = c.ToIndex; turned.Shallow = true;
                            c = turned;
                        }
                    }
                    c = ClearOfGround(c, world);
                    c.Shallow = true;
                    if (c.Width > RoadConstants.MaxBridgeSpan
                        || !BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(c.Direction))) c.Invalid = true;
                }
                c.ShallowSite = site;
                c.WetLength = length;
                // A road that turns back in the pool walks both wade banks to the same dry floor: it never crosses the
                // water, so there is no crossing (the wet stretch keeps the dry floor, as a puddle does).
                if (RoadCrossing.HasSpan(c.Width)) crossings.Add(c);
            }
            runSeg = -1; runLen = 0f; bed = float.MaxValue;
        }
        for (int i = 1; i < path.Count; i++)
        {
            Vector2 a = path[i - 1], b = path[i];
            float len = Vector2.Distance(a, b);
            if (Taken(i - 1)) { Close(i - 2, a); last = b; lastHeight = BiomeBlendedHeight.GetBlendedHeight(b.x, b.y, world); continue; }
            int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
            for (int k = (i == 1 ? 0 : 1); k <= n; k++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)k / n);
                float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
                bool wet = RoadWater.Wet(h);
                if (wet)
                {
                    if (runSeg < 0) { runSeg = i - 1; runStart = last; }
                    // Count only the wet part between samples. Interpolate the water line at either bank;
                    // counting the two dry sample intervals turns a pool shorter than the minimum into a crossing.
                    runLen += Vector2.Distance(last, p) * (RoadWater.Wet(lastHeight) ? 1f
                        : (RoadConstants.SeaLevel - h) / (lastHeight - h));
                    bed = Mathf.Min(bed, h);
                }
                else if (runSeg >= 0)
                {
                    runLen += Vector2.Distance(last, p) * (RoadConstants.SeaLevel - lastHeight) / (h - lastHeight);
                    Close(i - 1, p);
                }
                last = p;
                lastHeight = h;
            }
        }
        Close(path.Count - 2, last);
        if (!ReferenceEquals(path, original))
            for (int i = crossings.Count - 1; i >= firstCrossing; i--)
            {
                var c = crossings[i];
                if (c.ToIndex <= offset || c.FromIndex >= original.Count - 1 + offset) { crossings.RemoveAt(i); continue; }
                bool clippedFrom = c.FromIndex < offset, clippedTo = c.ToIndex >= original.Count + offset;
                if (clippedFrom || clippedTo)
                {
                    if (c.Style != FordStyle.Wade) c.Invalid = true;
                    else
                    {
                        if (clippedFrom) c.FromBank = original[0];
                        if (clippedTo) c.ToBank = original[original.Count - 1];
                        c.Center = (c.FromBank + c.ToBank) * 0.5f;
                        c.Direction = (c.ToBank - c.FromBank).normalized;
                        c.Width = Vector2.Distance(c.FromBank, c.ToBank);
                        // Clipped to the requested ends, a wade those ends do not cross is no crossing.
                        if (!RoadCrossing.HasSpan(c.Width)) { crossings.RemoveAt(i); continue; }
                    }
                }
                c.FromIndex = Mathf.Max(0, c.FromIndex - offset);
                c.ToIndex = Mathf.Min(original.Count - 1, c.ToIndex - offset);
            }
    }


    /// <summary>
    /// Ground a road may stand on, so also where a crossing ends. Outside
    /// swamps: the shallow-water line plus the bank clearance, the same
    /// ground a ford jump may land on. In swamps the road wades down to
    /// DeepWaterHeight, so everything shallower is road, not crossing.
    /// </summary>
    public static bool IsRoadGround(Vector2 p, WorldGenerator world) =>
        RoadWater.OnLand(world, p);

    /// <summary>A segment with river water under it somewhere: the jump the
    /// pathfinder made. A splined road dipping into a puddle between two dry
    /// cells is not a river crossing. With <paramref name="standing"/>, read any natural water on the same samples:
    /// a search's long ordinary step cannot skip a pool between dry cells.</summary>
    internal static bool SegmentCrossesRiver(Vector2 a, Vector2 b, WorldGenerator world, bool standing = false,
        System.Func<Vector2, float>? natural = null)
    {
        float length = Vector2.Distance(a, b);
        int samples = Mathf.Max(1, Mathf.CeilToInt(length / SampleSpacing));
        for (int s = 0; s <= samples; s++)
        {
            float t = (float)s / samples;
            float x = a.x + (b.x - a.x) * t;
            float y = a.y + (b.y - a.y) * t;
            float weight = 0f;
            if (!standing) world.GetRiverWeight(x, y, out weight, out _);
            if ((standing || RoadWater.River(weight))
                && RoadWater.Wet(natural != null ? natural(new Vector2(x, y)) : BiomeBlendedHeight.GetBlendedHeight(x, y, world)))
                return true;
        }
        return false;
    }

    private static RoadCrossing? Build(List<Vector2> path, int fromIndex, int toIndex, WorldGenerator world, bool bridges, bool fords)
    {
        Vector2 a = path[fromIndex], b = path[toIndex];
        float jumpLength = Vector2.Distance(a, b);
        // The crossing spans the water, not the dry approaches: each bank is
        // the last road ground along the jump before the water, so the road
        // runs down to the water's edge and the crossing lies on the road.
        Vector2 from = Shore(a, b, world);
        Vector2 to = Shore(b, a, world);
        // The water's edge is kept as a whole CANDIDATE -- banks and the path
        // interval it consumes -- because the optional climb below changes
        // both. Restoring the banks without the interval leaves the rejected
        // tops still eating the land in front of the crossing, and the painter
        // paints no approach there at all.
        Vector2 edgeFrom = from, edgeTo = to;
        int edgeFromIndex = fromIndex, edgeToIndex = toIndex;
        bool onTops = false;

        // High bridge: when the road climbs a cliff on both sides of the
        // water, the deck springs from the bank tops instead of the water's
        // edge. Both banks move together, and only when the tops are level
        // enough for one deck.
        //
        // This placement is OPTIONAL -- the crossing is sound either way -- and
        // it lengthens the deck, so it is taken only while the result still fits
        // the span the search is allowed to accept. Springing from the tops of a
        // gorge whose rims stand back from the water can add metres at each end:
        // a 128 m jump came back as a 136.05 m bridge, past a cap the router had
        // already checked and priced against. When the tops do not fit, the
        // water's-edge banks stand, and those are the ones routing measured.
        if (bridges)
        {
            float fromH = BiomeBlendedHeight.GetBlendedHeight(from.x, from.y, world);
            float toH = BiomeBlendedHeight.GetBlendedHeight(to.x, to.y, world);
            (Vector2 topFrom, int topFromIndex, float topFromH) = BankTop(path, from, fromIndex, -1, world);
            (Vector2 topTo, int topToIndex, float topToH) = BankTop(path, to, toIndex, +1, world);
            float cap = RoadConstants.MaxBridgeSpan;
            bool bothRise = topFromH >= fromH + RoadConstants.HighBankRise
                         && topToH >= toH + RoadConstants.HighBankRise;
            if (bothRise
                && Mathf.Abs(topFromH - topToH) <= RoadConstants.MaxBridgeBankDelta
                && Vector2.Distance(topFrom, topTo) <= cap)
            {
                from = topFrom;
                to = topTo;
                fromIndex = topFromIndex;
                toIndex = topToIndex;
                onTops = true;
            }
        }

        float width = Vector2.Distance(from, to);
        if (!RoadCrossing.HasSpan(width))
            return null;

        (float riverbed, Vector2 fairwayCenter, float fairwayWidth) = Profile(from, to, world);

        // A crossing needs water under it; a dry river valley is ordinary road.
        if (RoadWater.AtOrOverSea(riverbed))
            return null;

        Vector2 center = (from + to) * 0.5f;
        // The crossing's biome at the middle of its shores (ShoreBiome's place), as the search reads it.
        RoadRules.Rules water = RoadRules.For(RoadBiome.OfCrossing(edgeFrom, edgeTo, world));

        // Knee-deep and unsailable: a FORD, in one of the styles the site
        // allows, chosen per site so roads vary: wading only where the water
        // is ankle deep (always in a swamp), raising always, a span only
        // where there is room for a deck. Swamps wade deeper: a swamp channel
        // whose bed stays at wading depth (what the pathfinder already wades)
        // is a ford in the same style mix, unless a stretch of it is sailable
        // for at least a boat's length, which keeps it a bridge. Without
        // bridges every crossing is a ford.
        // The class the pathfinder accepted is kept: the search classes the
        // jump by the same rule (IsFord), and a feature that is off never
        // produces its kind.
        bool ford = IsFord(jumpLength, water.FordsDeepBed, riverbed, fairwayWidth);
        if (!fords) ford = false;
        // Without bridges, water a ford may not cross is not a crossing: a
        // segment dipping into a deeper channel is left as it is today.
        if (!ford && !bridges)
            return null;
        FordStyle style = FordStyle.None;
        List<FordStyle> eligible = new();
        if (ford)
        {
            float depth = RoadConstants.SeaLevel - riverbed;
            eligible.Add(FordStyle.Raise);
            WadeVerdict wade = WadeEligibility(depth, water.WadesAnyDepth, from, to, world);
            if (wade == WadeVerdict.Eligible) eligible.Add(FordStyle.Wade);
            if (bridges && width >= RoadConstants.FordSpanMinWidth) eligible.Add(FordStyle.Span);
            style = PickFordStyle(eligible, SiteHash(center));
            // A wade its banks rule out is a short bridge where one fits.
            if (wade == WadeVerdict.BanksTooSteep && eligible.Contains(FordStyle.Span)) style = FordStyle.Span;
        }

        // A crossing that carries PIECES is laid out on a heading the vanilla
        // hammer can produce, not on the road's own bearing: the ghost turns in
        // BridgeLayout.PlaceableHeadingStep steps and nothing between, so a
        // deck on any other line is one no player can ever repair.
        //
        // Two rules govern doing that HERE, in the detector, after FindPath has
        // accepted and priced this jump, with nothing downstream that
        // re-searches:
        //
        //   0. The bank-top climb above is an OPTIONAL adjustment and it walks
        //      the ROAD, which bends; it can turn a placeable water-edge
        //      crossing into an unplaceable one. So the line is turned first --
        //      which keeps a high bridge high -- and only if that fails is the
        //      water's edge taken instead of an unplaceable top-to-top layout.
        //   1. If the accepted line is ALREADY on the grid, leave it alone.
        //      Re-finding banks to satisfy a constraint that is already
        //      satisfied can only move a crossing the router measured -- and it
        //      did: a 32 m jump between banks at 33 m and 33 m came back as a
        //      52 m one ending on a 50 m cliff, a 17 m bank delta against a
        //      2.5 m limit, on a 90 degree heading that never needed turning.
        //   2. If it must be turned, the turned crossing has to pass the same
        //      limits the router applied to the one it accepted, and stay the
        //      same crossing -- within a pathfinder cell of the banks that were
        //      priced. Otherwise the accepted geometry stands.
        //
        // And a crossing is never DELETED for a heading: returning null does
        // not send the router anywhere, it just omits the crossing and leaves
        // the accepted path painted as ordinary road ACROSS THE WATER. A ford
        // drops Span and wades or raises instead (terrain carries no pieces); a
        // bridge keeps the accepted line and road_bridge_repairs prints HEADING
        // NOT PLACEABLE. That is honest reporting, not repairability: such a
        // site does NOT meet the ordinary-hammer criterion and says so.
        bool carriesPieces = !ford || style == FordStyle.Span;
        if (carriesPieces && !BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees((to - from).normalized)))
        {
            bool settled = false;

            // (a) Turn the line this crossing actually has. A bank-top climb
            //     that came back off the grid is usually still a good bridge --
            //     it springs from the cliff tops instead of standing in the
            //     gorge -- and turning it keeps that. The displacement leash
            //     compares against the geometry the candidate is DERIVED from:
            //     the tops when the climb happened, the water's edge otherwise.
            //     Judging a turned top-to-top line against the water's edge
            //     instead rejects it for the 12 m the climb deliberately walked,
            //     and quietly demotes a high bridge to a low one.
            Vector2 turnedFrom = edgeFrom, turnedTo = edgeTo;
            if (SnapToPlaceableHeading(ref turnedFrom, ref turnedTo, world))
            {
                if (onTops)
                    ClimbToBankTops(ref turnedFrom, ref turnedTo, world);
                if (TurnedCrossingHoldsUp(from, to, turnedFrom, turnedTo, world,
                        out float turnedBed, out Vector2 turnedCentre, out float turnedFairway))
                {
                    from = turnedFrom;
                    to = turnedTo;
                    // Derived from the water-edge line, and any climb on it
                    // walked the TURNED line rather than the path, so the
                    // tops' path interval means nothing here.
                    fromIndex = edgeFromIndex;
                    toIndex = edgeToIndex;
                    riverbed = turnedBed;
                    fairwayCenter = turnedCentre;
                    fairwayWidth = turnedFairway;
                    settled = true;
                }
            }

            // (b) The turn could not save it. The bank-top climb is OPTIONAL --
            //     "the crossing is sound either way" -- so rather than keep a
            //     layout no hammer can reproduce, fall back to the line routing
            //     accepted and priced. No leash here: this IS that line, and
            //     taking it re-finds nothing.
            if (!settled && onTops
                && BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees((edgeTo - edgeFrom).normalized))
                && Vector2.Distance(edgeFrom, edgeTo) >= 1f)
            {
                (float edgeBed, Vector2 edgeCentre, float edgeFairway) = Profile(edgeFrom, edgeTo, world);
                if (RoadWater.Wet(edgeBed))
                {
                    from = edgeFrom;
                    to = edgeTo;
                    fromIndex = edgeFromIndex;   // the interval this candidate consumes,
                    toIndex = edgeToIndex;       // not the one the rejected tops did
                    riverbed = edgeBed;
                    fairwayCenter = edgeCentre;
                    fairwayWidth = edgeFairway;
                    settled = true;
                }
            }

            if (!settled && ford)
            {
                // A span nobody can repair is worse than a ford they can wade.
                eligible.Remove(FordStyle.Span);
                style = PickFordStyle(eligible, SiteHash(center));
            }
        }

        // PIER HEIGHT. The climb buys a shorter approach by standing the
        // structure taller, and vanilla's support rule has a ceiling on that:
        // support decays geometrically up a pole column, so above
        // MaxBridgePierHeight the deck falls under the material minimum and the
        // bridge comes down some time after it is generated -- once a player is
        // near enough to put it in the active area, which is why it looks fine
        // from the map and falls over when you walk to it.
        //
        // The climb is TRIMMED, not abandoned: meeting the banks where they
        // pass through the tallest deck the rule can hold keeps most of what
        // the climb was for, where falling back to the water's edge stands the
        // bridge up but digs the approach back down to the river.
        //
        // AFTER the heading block, not before it. Trimming first looks right
        // and is not: a trimmed line is a new line, usually off the placeable
        // grid, and the heading fallback then rescues it by re-running
        // ClimbToBankTops -- which is the UNTRIMMED climb. The rule would
        // quietly undo itself on exactly the crossings it had just fixed.
        // Running last means the trim sees the final geometry and can hold it
        // to the same placeable-heading rule everything else here obeys.
        // A free span stands on its banks with nothing in the water, so the
        // pier ceiling has no column to apply to. Exempting it is what lets a
        // narrow deep channel be crossed at all.
        if (!ford && onTops && DeclineTallClimbs
            && Vector2.Distance(from, to) > BridgeLayout.FreeSpanMaxWidth)
            TrimClimbToSupportCeiling(path, world, carriesPieces,
                ref from, ref to, ref fromIndex, ref toIndex, ref onTops,
                ref riverbed, ref fairwayCenter, ref fairwayWidth,
                edgeFrom, edgeTo, edgeFromIndex, edgeToIndex);

        if (!ford && water.ShelfReach > 0f)
        {
            // The profile keeps its riverbed and fairway: the added deck is
            // over the shelf, which is shallower than both. The extension runs
            // along the line, so the heading survives it.
            (from, to) = ExtendOverShelf(from, to, water.ShelfReach, world);
        }

        Vector2? fromLand = null, toLand = null;
        if (!ford && !onTops && water.BridgesOffLand && ToWater)
        {
            // Along the same line, so the heading and its placeability hold.
            float deck = Mathf.Max(Mathf.Max(BiomeBlendedHeight.GetBlendedHeight(from.x, from.y, world),
                BiomeBlendedHeight.GetBlendedHeight(to.x, to.y, world)), BridgeLayout.MinDeck(RoadConstants.SeaLevel));
            Vector2 newFrom = OffLand(from, to, deck, world);
            Vector2 newTo = OffLand(to, newFrom, deck, world);
            if (newFrom != from) { fromLand = from; from = newFrom; }
            if (newTo != to) { toLand = to; to = newTo; }
        }

        RoadCrossing crossing = RoadCrossing.Between(from, to, riverbed, fairwayCenter, fairwayWidth,
            ford ? CrossingKind.Ford : CrossingKind.Bridge, style);
        crossing.FromIndex = fromIndex;
        crossing.ToIndex = toIndex;
        crossing.FromLand = fromLand;
        crossing.ToLand = toLand;
        crossing.OnTops = onTops;

        // Last, on the settled geometry: no deck through the ground between its banks (ClearOfGround). A ford span
        // whose deck would meet ground is laid as another ford style, as for one no hammer can place.
        if (!ford)
            crossing = ClearOfGround(crossing, world);
        else if (style == FordStyle.Span && GroundThroughDeck(from, to, BridgeStructures.DeckHeight(crossing, world), world) != null)
        {
            System.Threading.Interlocked.Increment(ref DeckLineSpanDropped);
            eligible.Remove(FordStyle.Span);
            crossing.Style = PickFordStyle(eligible, SiteHash(center));
        }
        return crossing;
    }

    /// <summary>
    /// Ford or bridge: the one rule, for the search pricing a jump (<see cref="IsFordJump"/>) and the detector building it.
    /// A FORD is a jump within the ford cap (<see cref="WithinFordCap"/>) over water a ford may cross: in a
    /// biome that fords a deep bed (<paramref name="deepBed"/>, RoadRules.FordsDeepBed of the biome at its middle: a swamp), a bed
    /// down to the deep line with no sailable stretch a boat's length long (RoadRules.PotholeFairway); elsewhere, a bed at
    /// wading depth with nothing sailable. Anything else is a bridge. <paramref name="riverbed"/> and <paramref name="fairwayWidth"/>
    /// are the <see cref="Profile"/> of the bank-to-bank line.
    /// The one owner of ford or bridge (docs/OWNERS.md).
    /// </summary>
    internal static bool IsFord(float jumpLength, bool deepBed, float riverbed, float fairwayWidth, bool standing = false) =>
        standing
            ? WithinFordCap(jumpLength) && RoadConstants.SeaLevel - riverbed <= ShallowMaxDepth
                && fairwayWidth < RoadRules.PotholeFairway
            : WithinFordCap(jumpLength) && (deepBed
            ? fairwayWidth < RoadRules.PotholeFairway && riverbed >= RoadConstants.DeepWaterHeight
            : fairwayWidth <= 0f && riverbed >= RoadConstants.SeaLevel - RoadConstants.FordWadeDepth);

    /// <summary>
    /// <see cref="IsFord"/> for the jump between <paramref name="a"/> and <paramref name="b"/>, measured as
    /// <see cref="Build"/> measures a path segment: the water's edge on each side (<see cref="Shore"/>), the river along
    /// the line between them at 1 m (<see cref="Profile"/>), the biome at its middle. A jump past the ford cap is a bridge
    /// without reading the water.
    /// The one owner of ford or bridge (docs/OWNERS.md).
    /// </summary>
    internal static bool IsFordJump(Vector2 a, Vector2 b, WorldGenerator world)
    {
        if (Shallows && !SegmentCrossesRiver(a, b, world))
        {
            var crossings = Detect(new List<Vector2> { a, b }, world, bridges: true);
            return crossings.Count > 0 && crossings.TrueForAll(c => !c.Invalid && c.Kind == CrossingKind.Ford);
        }
        float jumpLength = Vector2.Distance(a, b);
        if (!WithinFordCap(jumpLength))
            return false;
        Vector2 from = Shore(a, b, world);
        Vector2 to = Shore(b, a, world);
        (float riverbed, _, float fairwayWidth) = Profile(from, to, world);
        return IsFord(jumpLength, RoadRules.For(RoadBiome.OfCrossing(from, to, world)).FordsDeepBed, riverbed, fairwayWidth);
    }

    /// <summary>Whether a jump or span is no longer than a ford may be (<see cref="RoadConstants.MaxFordSpan"/>): the one ford
    /// cap, asked by the detector (a river jump, a standing-water run), by the search's "longer than a ford" and by a fords-only
    /// search's longest crossing. No slack: the snap onto the network never lengthens a jump the search priced
    /// (RoadNetworkGenerator.SnapToNetwork).</summary>
    internal static bool WithinFordCap(float jumpLength) => jumpLength <= RoadConstants.MaxFordSpan;

    /// <summary>The river along the bank-to-bank line at about 1 m spacing:
    /// the lowest bed, and the longest sailable stretch.</summary>
    private static (float riverbed, Vector2 fairwayCenter, float fairwayWidth) Profile(Vector2 from, Vector2 to, WorldGenerator world)
    {
        float width = Vector2.Distance(from, to);
        int samples = Mathf.Max(2, Mathf.CeilToInt(width));
        float riverbed = float.MaxValue;
        Vector2 deepestPoint = from;
        int bestRunStart = -1, bestRunLength = 0, runStart = -1;
        for (int s = 0; s <= samples; s++)
        {
            float t = (float)s / samples;
            Vector2 p = new(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t);
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (h < riverbed)
            {
                riverbed = h;
                deepestPoint = p;
            }

            bool sailable = Afloat(RoadConstants.SeaLevel, h);
            if (sailable && runStart < 0)
                runStart = s;
            if ((!sailable || s == samples) && runStart >= 0)
            {
                int runEnd = sailable ? s : s - 1;
                int runLength = runEnd - runStart + 1;
                if (runLength > bestRunLength)
                {
                    bestRunLength = runLength;
                    bestRunStart = runStart;
                }
                runStart = -1;
            }
        }

        Vector2 fairwayCenter = deepestPoint;
        float fairwayWidth = 0f;
        if (bestRunLength > 0)
        {
            float t = (bestRunStart + (bestRunLength - 1) / 2f) / samples;
            fairwayCenter = new Vector2(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t);
            fairwayWidth = bestRunLength * (width / samples);
        }
        return (riverbed, fairwayCenter, fairwayWidth);
    }

    private static float WeightOf(FordStyle style) => style switch
    {
        FordStyle.Wade => ConfiguredWadeWeight,
        FordStyle.Raise => ConfiguredRaiseWeight,
        FordStyle.Span => ConfiguredSpanWeight,
        _ => 0f,
    };

    /// <summary>Weighted pick driven by the site hash, so a world regenerates
    /// with the same river fords. Standing pools always try Wade, Span, then Raise.</summary>
    internal static FordStyle PickFordStyle(List<FordStyle> eligible, int hash, bool standing = false)
    {
        if (standing)
            return eligible.Contains(FordStyle.Wade) ? FordStyle.Wade
                : eligible.Contains(FordStyle.Span) ? FordStyle.Span : eligible[0];
        float total = 0f, first = -1f;
        bool equal = true;
        foreach (FordStyle s in eligible)
        {
            float w = Mathf.Max(0f, WeightOf(s));
            total += w;
            if (first < 0f) first = w;
            else if (Mathf.Abs(w - first) > 1e-6f) equal = false;
        }
        if (total <= 0f)
            return eligible.Contains(FordStyle.Raise) ? FordStyle.Raise : eligible[0];
        if (equal)
            return eligible[hash % eligible.Count];

        float r = (hash & 0xFFFF) / 65536f * total;
        foreach (FordStyle s in eligible)
        {
            float w = Mathf.Max(0f, WeightOf(s));
            if (w <= 0f) continue;
            if (r < w) return s;
            r -= w;
        }
        return eligible.Contains(FordStyle.Raise) ? FordStyle.Raise : eligible[0]; // float tail
    }

    /// <summary>Deterministic per-site hash so a ford keeps its style across
    /// loads and worlds regenerate identically.</summary>
    private static int SiteHash(Vector2 center)
    {
        int x = Mathf.RoundToInt(center.x), y = Mathf.RoundToInt(center.y);
        return (int)(RoadPathfinder.HashCell(x, y) & 0x7FFFFFFF);
    }

    /// <summary>
    /// The water's edge walking from <paramref name="start"/> toward
    /// <paramref name="end"/>: the last point that stands on road ground
    /// before the ground turns wet and stays wet. A wet start is its own
    /// bank; a walk that never meets water keeps the start as its bank.
    /// </summary>
    private static Vector2 Shore(Vector2 start, Vector2 end, WorldGenerator world)
    {
        float length = Vector2.Distance(start, end);
        if (length < 0.01f || !IsRoadGround(start, world))
            return start;

        Vector2 dir = (end - start) * (1f / length);
        Vector2 last = start;
        for (float d = ShoreStep; d <= length; d += ShoreStep)
        {
            Vector2 p = start + dir * d;
            if (IsRoadGround(p, world))
            {
                last = p;
                continue;
            }
            // A 1-2 m pothole on the approach is not the shore: the ground
            // must still be wet 1 m and 2 m further on.
            bool wetAhead = !IsRoadGround(start + dir * Mathf.Min(length, d + 1f), world)
                && !IsRoadGround(start + dir * Mathf.Min(length, d + 2f), world);
            if (wetAhead)
                return last;
        }
        return start;
    }

    /// <summary>Walks straight out from a bank along <paramref name="outward"/>
    /// and returns the first point whose ground is at least <paramref name="floor"/>
    /// high, or the bank itself when none lies within reach.</summary>
    /// <summary>
    /// A BRIDGE starts and ends on land above the water, but in a swamp the
    /// wade shelf is road-legal, so the banks the search landed on can sit
    /// under the waterline. Walk each wet bank straight out along the crossing
    /// line until the ground clears the waterline, even though that makes the
    /// deck longer. The painted lead still reaches the abutment.
    ///
    /// The pathfinder calls this too, BEFORE it accepts the jump. It has to:
    /// moving a bank afterwards would change a span the search had already
    /// measured against its limit and already priced, and a 64 m jump can come
    /// out over 150 m long once a long shelf is walked. Both sides must see the
    /// same geometry, and the side that can still choose a different route is
    /// the one that has to see it first.
    /// </summary>
    /// <summary>
    /// Turn a crossing onto the nearest admissible heading and find its banks
    /// there: from the crossing's centre, which is over water, walk outward
    /// along the line until the ground is land on both sides. The nearest line
    /// that reaches land on both banks within the bridge cap wins; if none
    /// does, this is not a crossing.
    /// </summary>
    /// <param name="land">Where a bank may stand: road ground (<see cref="IsRoadGround"/>) for a river; a pool's bank is its
    /// natural water line, as its run is measured (DetectShallows), since a swamp's road ground reaches down to the deep line
    /// and a pool's middle would be its own bank.</param>
    private static bool SnapToPlaceableHeading(ref Vector2 from, ref Vector2 to, WorldGenerator world, System.Func<Vector2, bool>? land = null)
    {
        float cap = RoadConstants.MaxBridgeSpan;
        Vector2 centre = (from + to) * 0.5f;
        float bearing = BridgeLayout.YawDegrees((to - from).normalized);

        foreach (float heading in BridgeLayout.NearestPlaceableHeadings(bearing))
        {
            Vector2 d = BridgeLayout.HeadingDirection(heading);
            if (!BankOutward(centre, d, world, cap, out Vector2 far, land)) continue;
            if (!BankOutward(centre, -d, world, cap, out Vector2 near, land)) continue;
            if (Vector2.Distance(near, far) > cap) continue;

            // Keep the crossing pointing the way the road was going, so
            // FromBank stays the bank the road arrives at.
            bool flipped = Vector2.Dot(far - near, to - from) < 0f;
            from = flipped ? far : near;
            to = flipped ? near : far;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a turned crossing may replace the one the router accepted. The
    /// router priced a particular line after checking its span, its bank delta
    /// and what lies under it; a line chosen afterwards inherits none of that,
    /// so it has to earn it again -- and it has to still be the SAME crossing,
    /// not a longer one ending somewhere else. The bank bound is one pathfinder
    /// cell, the resolution at which the route was chosen: past that, the
    /// router never looked at this geometry at all.
    /// </summary>
    private static bool TurnedCrossingHoldsUp(Vector2 acceptedFrom, Vector2 acceptedTo,
        Vector2 turnedFrom, Vector2 turnedTo, WorldGenerator world,
        out float riverbed, out Vector2 fairwayCenter, out float fairwayWidth)
    {
        (riverbed, fairwayCenter, fairwayWidth) = Profile(turnedFrom, turnedTo, world);

        // Still a crossing at all.
        if (RoadWater.AtOrOverSea(riverbed))
            return false;
        // Still the crossing the router priced: neither bridgehead has walked
        // further than the cell size the route was chosen at. Either end may
        // pair with either, since turning can reverse the line. This is a
        // DISPLACEMENT bound and nothing more -- it says the router looked at
        // ground this close, not that the ground between there and here can be
        // walked.
        float leash = RoadPathfinder.CellSize;
        bool sameOrder = Vector2.Distance(acceptedFrom, turnedFrom) <= leash
            && Vector2.Distance(acceptedTo, turnedTo) <= leash;
        bool reversed = Vector2.Distance(acceptedFrom, turnedTo) <= leash
            && Vector2.Distance(acceptedTo, turnedFrom) <= leash;
        if (!sameOrder && !reversed)
            return false;
        // Still inside the limits the router applies to a bridge.
        float span = Vector2.Distance(turnedFrom, turnedTo);
        if (span < 1f || span > RoadConstants.MaxBridgeSpan)
            return false;
        float bankDelta = Mathf.Abs(
            BiomeBlendedHeight.GetBlendedHeight(turnedTo.x, turnedTo.y, world)
            - BiomeBlendedHeight.GetBlendedHeight(turnedFrom.x, turnedFrom.y, world));
        if (bankDelta > RoadConstants.MaxBridgeBankDelta)
            return false;
        // And it is actually on the grid, which was the whole point.
        return BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees((turnedTo - turnedFrom).normalized));
    }

    /// <summary>
    /// Spring the deck from the bank tops instead of the water's edge, along
    /// the crossing's own line: the same gate as the path-walking version --
    /// the road stands HighBankRise above both banks, the tops are level
    /// enough for one deck, and the result still fits the bridge cap.
    /// </summary>
    private static void ClimbToBankTops(ref Vector2 from, ref Vector2 to, WorldGenerator world)
    {
        Vector2 d = (to - from).normalized;
        float fromH = BiomeBlendedHeight.GetBlendedHeight(from.x, from.y, world);
        float toH = BiomeBlendedHeight.GetBlendedHeight(to.x, to.y, world);
        (Vector2 topFrom, float topFromH) = HighestAlong(from, -d, world);
        (Vector2 topTo, float topToH) = HighestAlong(to, d, world);
        float cap = RoadConstants.MaxBridgeSpan;
        if (topFromH >= fromH + RoadConstants.HighBankRise && topToH >= toH + RoadConstants.HighBankRise
            && Mathf.Abs(topFromH - topToH) <= RoadConstants.MaxBridgeBankDelta
            && Vector2.Distance(topFrom, topTo) <= cap)
        {
            from = topFrom;
            to = topTo;
        }
    }

    /// <summary>The highest ground within HighBankReach outward of a bank,
    /// nearest such point first (the path version's rule, on a line).</summary>
    private static (Vector2 top, float height) HighestAlong(Vector2 bank, Vector2 outward, WorldGenerator world)
    {
        List<Vector2> samples = new() { bank };
        for (float d = 0.5f; d <= RoadConstants.HighBankReach; d += 0.5f)
            samples.Add(bank + outward * d);

        float best = float.MinValue;
        foreach (Vector2 p in samples)
            best = Mathf.Max(best, BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
        foreach (Vector2 p in samples)
        {
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (h >= best - 0.05f)
                return (p, h);
        }
        return (bank, BiomeBlendedHeight.GetBlendedHeight(bank.x, bank.y, world));
    }

    /// <summary>The first land going outward from a point over the water:
    /// road ground that is still road ground 1 m and 2 m further on, so a
    /// sandbar mid-channel is not mistaken for a bank (the mirror of the
    /// pothole rule in <see cref="Shore"/>).</summary>
    private static bool BankOutward(Vector2 centre, Vector2 dir, WorldGenerator world, float maxDistance, out Vector2 bank,
        System.Func<Vector2, bool>? land = null)
    {
        bank = centre;
        bool Land(Vector2 q) => land != null ? land(q) : IsRoadGround(q, world);
        for (float d = ShoreStep; d <= maxDistance; d += ShoreStep)
        {
            Vector2 p = centre + dir * d;
            if (!Land(p))
                continue;
            if (Land(p + dir * 1f) && Land(p + dir * 2f))
            {
                bank = p;
                return true;
            }
        }
        return false;
    }

    /// <summary>The biome of the jump from <paramref name="a"/> to <paramref name="b"/> as the detector reads a crossing's: at
    /// the middle of its two shores (<see cref="Shore"/> each way, RoadBiome.OfCrossing). The search reads a jump's here
    /// (RoadPathfinder's crossing facts), so it prices the bridge the detector builds (the owner's decision of 13 Nov 2026).</summary>
    internal static Heightmap.Biome ShoreBiome(Vector2 a, Vector2 b, WorldGenerator world) =>
        RoadBiome.OfCrossing(Shore(a, b, world), Shore(b, a, world), world);

    /// <summary>A bridge's wet banks walked out along its line over the shelf, each up to <paramref name="reach"/> (its biome's
    /// RoadRules.ShelfReach), to the first dry ground.</summary>
    public static (Vector2 from, Vector2 to) ExtendOverShelf(Vector2 from, Vector2 to, float reach, WorldGenerator world)
    {
        Vector2 direction = (to - from).normalized;
        float dryFloor = RoadWater.LandingFloor;
        if (BiomeBlendedHeight.GetBlendedHeight(from.x, from.y, world) < dryFloor)
            from = FirstDryAlongLine(from, -direction, reach, world, dryFloor);
        if (BiomeBlendedHeight.GetBlendedHeight(to.x, to.y, world) < dryFloor)
            to = FirstDryAlongLine(to, direction, reach, world, dryFloor);
        return (from, to);
    }

    private static Vector2 FirstDryAlongLine(Vector2 bank, Vector2 outward, float reach, WorldGenerator world, float floor)
    {
        for (float d = 0.5f; d <= reach; d += 0.5f)
        {
            Vector2 p = bank + outward * d;
            if (BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) >= floor)
                return p;
        }
        return bank;
    }

    /// <summary>
    /// The highest ground within HighBankReach of a bank, walking outward
    /// from it along the path (step -1 toward the path start from the from
    /// bank, +1 toward the path end from the to bank). Returns the nearest
    /// point where that height is reached, the path index that brackets it
    /// on the water side (FromIndex / ToIndex semantics), and its height.
    /// </summary>
    /// <summary>
    /// The first point along the road, walking inland from a bank, whose ground
    /// reaches <paramref name="wanted"/>. Where the two banks' tops are too
    /// far apart for one deck, this is what lets the higher side meet the lower
    /// side's height instead of both falling back to the water's edge.
    /// Returns null when the bank does not reach that height within the reach.
    /// </summary>
    /// <summary>
    /// Bring a bank-top climb back inside the structural ceiling, preferring
    /// the tallest deck that still stands to the lowest one available.
    ///
    /// Walks the target deck DOWN from the highest the ceiling allows until a
    /// pair of banks exists at that height and the crossing they make measures
    /// inside it. Aiming at ONE height does not work, and both ways it fails
    /// are ordinary terrain: a bank sampled every 0.5 m steps over a cliff and
    /// overshoots the target by metres, failing the bank-delta rule; and the
    /// deck sits at whichever bank is higher, so the bank height that yields a
    /// given deck is not the deck height. Walking down handles both without
    /// having to model either.
    ///
    /// The water's edge is the last resort, for a gorge whose banks never pass
    /// through a workable height, and is taken only when it is genuinely lower
    /// than what we already have -- a failed trim must not leave the bridge
    /// worse than it started.
    ///
    /// The chosen candidate's PROFILE is carried back out. Recomputing it at
    /// the call site would sample a different line from the one measured here.
    /// </summary>
    private static void TrimClimbToSupportCeiling(
        List<Vector2> path, WorldGenerator world, bool carriesPieces,
        ref Vector2 from, ref Vector2 to, ref int fromIndex, ref int toIndex, ref bool onTops,
        ref float riverbed, ref Vector2 fairwayCenter, ref float fairwayWidth,
        Vector2 edgeFrom, Vector2 edgeTo, int edgeFromIndex, int edgeToIndex)
    {
        float ceiling = RoadConstants.MaxBridgePierHeight;
        float cap = RoadConstants.MaxBridgeSpan;

        // Deck, bed and profile for a candidate pair of banks, or null if it is
        // not a crossing this mod would build. A piece-carrying crossing on an
        // unplaceable heading is one no player can ever repair, so it does not
        // count as a candidate at all.
        (float Deck, float Bed, Vector2 Centre, float Fairway)? Measure(Vector2 a, Vector2 b)
        {
            if (Vector2.Distance(a, b) < 1f) return null;
            if (carriesPieces && !BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees((b - a).normalized)))
                return null;
            (float bed, Vector2 centre, float fairway) = Profile(a, b, world);
            if (RoadWater.AtOrOverSea(bed)) return null;
            RoadCrossing c = RoadCrossing.Between(a, b, bed, centre, fairway, CrossingKind.Bridge, FordStyle.None);
            return (BridgeLayout.DeckHeight(c, world), bed, centre, fairway);
        }

        // Walk each bank outward along the CROSSING's own line, never along the
        // road. Walking the path is what the level-tops rule does,
        // and it is wrong here: the road bends away from the water, so
        // moving both banks along it ROTATES the crossing -- by 82 to 88
        // degrees in the test fixture, off the placeable grid every time, so
        // the trim found a candidate at every height and could use none of
        // them. Walking the line keeps the heading exactly.
        Vector2 line = (edgeTo - edgeFrom).sqrMagnitude > 1e-6f
            ? (edgeTo - edgeFrom).normalized
            : (to - from).normalized;

        System.Threading.Interlocked.Increment(ref TrimSeen);
        RoadCrossing current = RoadCrossing.Between(from, to, riverbed, fairwayCenter, fairwayWidth,
            CrossingKind.Bridge, FordStyle.None);
        float climbPier = BridgeLayout.PierHeight(current, world) ;
        if (climbPier <= ceiling)
        {
            System.Threading.Interlocked.Increment(ref TrimAlreadyFine);
            return;
        }

        for (float target = riverbed + ceiling; target > riverbed + MinTrimmedPier; target -= TrimStep)
        {
            var a = BankAtHeightAlong(edgeFrom, -line, world, target);
            var b = BankAtHeightAlong(edgeTo, line, world, target);
            if (!a.HasValue || !b.HasValue
                || Mathf.Abs(a.Value.height - b.Value.height) > RoadConstants.MaxBridgeBankDelta
                || Vector2.Distance(a.Value.point, b.Value.point) > cap)
                continue;

            var trimmed = Measure(a.Value.point, b.Value.point);
            if (trimmed == null || trimmed.Value.Deck - trimmed.Value.Bed > ceiling)
                continue;

            System.Threading.Interlocked.Increment(ref TrimTrimmed);
            from = a.Value.point;
            to = b.Value.point;
            // The banks moved along the CROSSING, not along the path, so the
            // path interval this candidate consumes is the water's-edge one --
            // the same bookkeeping the heading fallback uses when it turns a
            // line off the path.
            fromIndex = edgeFromIndex;
            toIndex = edgeToIndex;
            riverbed = trimmed.Value.Bed;
            fairwayCenter = trimmed.Value.Centre;
            fairwayWidth = trimmed.Value.Fairway;
            return;   // still sprung above the water: onTops stands
        }
        System.Threading.Interlocked.Increment(ref TrimNoBanks);

        var edge = Measure(edgeFrom, edgeTo);
        if (edge != null && edge.Value.Deck - edge.Value.Bed < climbPier)
        {
            System.Threading.Interlocked.Increment(ref TrimToEdge);
            from = edgeFrom;
            to = edgeTo;
            fromIndex = edgeFromIndex;
            toIndex = edgeToIndex;
            riverbed = edge.Value.Bed;
            fairwayCenter = edge.Value.Centre;
            fairwayWidth = edge.Value.Fairway;
            onTops = false;
        }
        else
        {
            System.Threading.Interlocked.Increment(ref TrimStuck);
        }
    }

    /// <summary>
    /// The first point at or above <paramref name="wanted"/>, walking outward
    /// from a bank along a fixed direction. It cannot rotate the crossing,
    /// which is what makes it usable on a crossing that carries pieces.
    ///
    /// Starts at the bank itself, so a target the bank already meets returns
    /// the bank rather than a point half a metre inland. That matters when the
    /// caller steps a target downward: a walk along the road's path sticks on its
    /// first sample once the target falls below the bank height and returns the same
    /// point for every lower target, which looks like progress and is not.
    /// </summary>
    private static (Vector2 point, float height)? BankAtHeightAlong(
        Vector2 bank, Vector2 outward, WorldGenerator world, float wanted)
    {
        for (float d = 0f; d <= RoadConstants.HighBankReach; d += 0.5f)
        {
            Vector2 p = bank + outward * d;
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (h >= wanted - 0.05f) return (p, h);
        }
        return null;
    }

    private static (Vector2 top, int index, float height) BankTop(List<Vector2> path, Vector2 bank, int bankIndex, int step, WorldGenerator world, float? floor = null)
    {
        List<(Vector2 p, int index)> samples = new() { (bank, bankIndex) };
        Vector2 pos = bank;
        int next = bankIndex;
        float budget = RoadConstants.HighBankReach;
        while (budget > 0f && next >= 0 && next < path.Count)
        {
            Vector2 target = path[next];
            float length = Vector2.Distance(pos, target);
            if (length > 0.01f)
            {
                Vector2 dir = (target - pos) * (1f / length);
                for (float d = 0.5f; d < length && d <= budget; d += 0.5f)
                    samples.Add((pos + dir * d, next));
                if (length <= budget)
                    samples.Add((target, next));
                budget -= length;
            }
            pos = target;
            next += step;
        }

        if (floor.HasValue)
        {
            foreach (var sample in samples)
            {
                float h = BiomeBlendedHeight.GetBlendedHeight(sample.p.x, sample.p.y, world);
                if (RoadWater.Wet(h)) break;
                if (h >= floor.Value) return (sample.p, sample.index, h);
            }
            return (bank, bankIndex, BiomeBlendedHeight.GetBlendedHeight(bank.x, bank.y, world));
        }
        float best = float.MinValue;
        foreach ((Vector2 p, int _) in samples)
            best = Mathf.Max(best, BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
        foreach ((Vector2 p, int index) in samples)
        {
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (h >= best - 0.05f)
                return (p, index, h);
        }
        return (bank, bankIndex, BiomeBlendedHeight.GetBlendedHeight(bank.x, bank.y, world));
    }
}
