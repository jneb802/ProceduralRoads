using System;
using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>What a judged object is to the road: cleared, kept, or kept because another peer owns it. Decisions read this;
/// <see cref="RoadRockHit.Verdict"/> is the log's wording.</summary>
internal enum RoadRockHitKind { Keep, KeepNotOwner, Clear }

/// <summary>
/// What the rock pass (RoadRockProbe) decided about one object in the clearance boxes: judged once, by the first box to meet
/// it (<see cref="Judged"/>), and looked at again by every later one (<see cref="Revisited"/>). How it is then taken is
/// <see cref="RoadRockPolicy.HowTaken"/>'s alone.
/// </summary>
internal readonly struct RoadRockHit
{
    public readonly string Name, Verdict;
    public readonly float Size;
    public readonly bool Listed;
    public readonly RoadRockHitKind Kind;
    /// <summary>A deck's clearance takes it, whatever the road's rule (<see cref="RoadRockPolicy.DeckTakes"/>).</summary>
    public readonly bool DeckTakes;
    /// <summary>It stands in a location's protected area (RoadLocationProtection): an approach to the location may keep it as its own.</summary>
    public readonly bool InLocationArea;
    /// <summary>A Location's stakes in a way (<see cref="LocationRock.ClearedForAWay"/>): removed whole, never carved, never kept as
    /// the Location's own on its approach.</summary>
    public readonly bool LocationStakes;

    public RoadRockHit(string name, string verdict, float size, bool listed, RoadRockHitKind kind = RoadRockHitKind.Keep,
        bool deckTakes = false, bool inLocationArea = false, bool locationStakes = false)
    { Name = name; Verdict = verdict; Size = size; Listed = listed; Kind = kind; DeckTakes = deckTakes; InLocationArea = inLocationArea; LocationStakes = locationStakes; }

    public bool Clears => Kind == RoadRockHitKind.Clear;

    public static string KeptForApproach(string why) => $"keep: the location's own rock ({why}), on its approach";

    /// <summary>
    /// A rock as the first box to meet it judges it, by the clearing rules (<see cref="RoadRockPolicy.CanClear"/>,
    /// <see cref="RoadRockPolicy.CanClearMistlands"/>, <see cref="RoadRockPolicy.DeckTakes"/>) under each of its
    /// <paramref name="policyNames"/> (<see cref="RoadRockPolicy.PolicyNames"/>). <paramref name="keptOnApproach"/>: why the
    /// location keeps it on this box's approach (LocationRock.KeptAt), or null: a location's rock, a location's part, a player's piece
    /// and another peer's rock are taken by no rule, a deck's neither. The one exception, a Location's stakes
    /// (<see cref="LocationRock.ClearedForAWay"/>, the one owner of which Location pieces a way may clear): taken whole by any box
    /// that meets them, a Location's part or not, unless a player placed them (<paramref name="piece"/>: placed by a player) or
    /// another peer owns them. Where a road or staircase goes through the spikes, the spikes go.
    /// </summary>
    public static RoadRockHit Judged(string name, float size, IEnumerable<string> policyNames,
        ICollection<string> natural, bool mist, bool cliffs, bool locationChild, bool inLocationArea, string? keptOnApproach,
        bool piece, bool hasView, bool validView, bool owned)
    {
        if (LocationRock.ClearedForAWay(name))
        {
            bool free = !piece && (!hasView || (validView && owned));
            string why = free ? "a Location's stakes in the way: remove whole" : piece ? "keep: player piece"
                : hasView && !validView ? "keep: not loaded (no valid network view)" : "keep: not owner";
            RoadRockHitKind stakes = free ? RoadRockHitKind.Clear : piece || !validView ? RoadRockHitKind.Keep : RoadRockHitKind.KeepNotOwner;
            return new RoadRockHit(name, why, size, false, stakes, inLocationArea: inLocationArea, locationStakes: true);
        }
        bool protectedLocation = locationChild || keptOnApproach != null;
        bool listed = false, ok = false, deckTakes = false;
        foreach (string judged in policyNames)
        {
            listed |= RoadRockPolicy.IsNaturalBoulder(judged) || RoadRockPolicy.IsMistlandsRock(judged, mist, cliffs);
            ok |= RoadRockPolicy.CanClear(judged, natural.Contains(judged), protectedLocation, piece, hasView, validView, owned) ||
                RoadRockPolicy.CanClearMistlands(judged, mist, cliffs, natural.Contains(judged), protectedLocation, piece, hasView, validView, owned);
            deckTakes |= RoadRockPolicy.DeckTakes(judged, mist, cliffs, protectedLocation, piece, hasView, validView, owned);
        }
        string verdict = ok ? (inLocationArea ? "boulder in a location area: carve the road through it" : "boulder: clear") :
            !listed ? (RoadRockPolicy.IsMistlandsBoulder(name) ? "keep: Mistlands rock (add 'mist' to include)" :
                RoadRockPolicy.IsMistlandsCliff(name) ? "keep: Mistlands cliff (add 'cliffs' to include)" : "keep: not a listed natural boulder") :
            locationChild ? "keep: part of a location" :
            keptOnApproach != null ? KeptForApproach(keptOnApproach) :
            piece ? "keep: player piece" :
            hasView && !validView ? "keep: not loaded (no valid network view)" :
            hasView && !owned ? "keep: not owner" : "keep: vegetation rule";
        RoadRockHitKind kind = ok ? RoadRockHitKind.Clear
            : listed && !protectedLocation && !piece && hasView && !owned ? RoadRockHitKind.KeepNotOwner
            : RoadRockHitKind.Keep;
        return new RoadRockHit(name, verdict, size, listed, kind, deckTakes, inLocationArea);
    }

    /// <summary>
    /// The rock as a later box finds it, judged by an earlier one. On an approach to a location (<paramref name="keepsLocationRocks"/>)
    /// a rock in a location area that the location keeps (<paramref name="keptOnApproach"/>: why, or null; asked only for a rock some
    /// rule would take) is kept, whatever the first box decided: a road's clear as much as a deck's taking (a road box, visited
    /// first, judged a location rock the road kept but a deck takes, and the deck then removed it whole through the location's
    /// approach). Anything else stays as first judged.
    /// </summary>
    public RoadRockHit Revisited<T>(bool keepsLocationRocks, T rock, Func<T, string?> keptOnApproach)
    {
        if (LocationStakes || !keepsLocationRocks || !InLocationArea || !(Clears || DeckTakes)) return this;
        string? why = keptOnApproach(rock);
        return why == null ? this : new RoadRockHit(Name, KeptForApproach(why), Size, Listed);
    }
}
