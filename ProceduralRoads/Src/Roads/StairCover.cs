namespace ProceduralRoads;

/// <summary>
/// How deep the ground may stand over a staircase's walking surface: one owner, a table of named cases with reasons.
/// Metres of ground over the walking surface (the tread's top, a landing's top).
/// </summary>
public static class StairCover
{
    /// <summary>A road's staircase, in the search and its top finishes and its valley spans: beyond it the piece is refused, not dug
    /// out. 1.5 m let leg-by-leg search cut 1.8 m deep on the real face; with backtracking it routes round instead.</summary>
    public const float RoadStaircase = 1f;

    /// <summary>The search for an approach to a placed place: its footprint takes no earthwork, so it is kept shallower than a road's
    /// staircase and the cuts under its treads are never made. 0.5 m shows too much terrain overlap.</summary>
    public const float ApproachSearch = 0.3f;

    /// <summary>A finished approach, every piece, its fillers, top landing and finish included, is refused or finished no deeper than
    /// this. Looser than the search's: 0.4 refused the first one on real ground for a turn filler at 0.42.</summary>
    public const float ApproachRefusal = 0.45f;

    /// <summary>A tread's row across it is dug out when any part of it is buried deeper than this, even if most of the walkway is
    /// clear: a stair cut into a bank showed too much terrain overlap at 0.5 m. The same 0.3 as the approach's
    /// search: one value for the one look of the thing.</summary>
    public const float TreadEdge = ApproachSearch;
}
