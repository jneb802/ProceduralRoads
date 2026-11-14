using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One owner of "beside another road at its height": how near across, how near in height, and how long side by side
/// before it is refused. A road beside a road (RoadNetworkGenerator.BraidsNetwork: a lens, a duplicate, and a road that
/// comes in beside a trunk and ends on it), and a staircase beside a road (RoadStairRuns.BesideARoad) ask it. Roads avoid running parallel even if that costs POIs, and a road
/// ending in a junction after a shallow run beside the trunk is capped in length like every other parallel check, with
/// one owner for the rule.
/// </summary>
public static class RoadBeside
{
    /// <summary>The visual beside-road corridor for a road of <paramref name="width"/>: a centreline (or a staircase's walked
    /// piece) this far or less across from a road's runs beside it, the road's width and one terrain blend margin (6 m at
    /// the default width). A rule of how roads read: nearer, two roads read as one smeared road (a road wavering just past a
    /// narrower reach would slip through it). It is not where two roads' terrain stops meeting: each road's levelled ground
    /// and blend reach half its width and a blend margin from its centre (two default roads 8 m apart still touch), and batter
    /// and fill spread reach further; the corridor is not widened to cover them. The one owner of the visual beside-road
    /// corridor distance (docs/OWNERS.md).</summary>
    public static float CorridorDistance(float width) => width + RoadConstants.TerrainBlendMargin;

    /// <summary>The corridor (<see cref="CorridorDistance"/>) at the Roads/RoadWidth setting: what the beside-road rules ask. The one owner of road width, and every rule that follows it (docs/OWNERS.md).</summary>
    public static float Current => CorridorDistance(RoadNetworkGenerator.RoadWidth);
    /// <summary>Nearer than this a road is on the other road: the trunk the two share, not beside it.</summary>
    public const float OnRoad = 1f;
    /// <summary>Within this of the other road's surface it is beside it at its height.</summary>
    public const float Height = 1.5f;
    /// <summary>The longest a road may run beside another at its height, away from its own ends (its junctions): this far or
    /// more is refused, whether it leaves the other road again (a lens), never meets it (a duplicate) or ends on it (a Y
    /// with a long parallel run in).</summary>
    public const float MostRun = 12f;
    /// <summary>The same cap for a staircase, counted in its walked pieces in a row (about 2 m each): three is a staircase
    /// redundant there, the road could have been laid instead (at -5853,5918 a staircase ran parallel to a road in mostly
    /// flat pieces and would be better connected further up the road). A staircase is refused sooner than a road because a
    /// flight beside a level road at its height is the road's job, where a road beside a road at least goes somewhere.</summary>
    public const int MostStairPieces = 3;

    /// <summary>Where a point standing at <paramref name="height"/> is against the network's roads.</summary>
    public enum Place { Off, Beside, On }

    /// <summary>Whether <paramref name="p"/>, at <paramref name="height"/>, is on a road (within <see cref="OnRoad"/>),
    /// beside one at its height (within <see cref="Current"/> across and <see cref="Height"/> of its surface), or off. The
    /// road is the network's line (<see cref="RoadRide.Lines"/>): a structure in a road's place (a crossing, a staircase)
    /// is road here, so a road along it is on it or beside it as along any road. The one owner of A road riding or lying beside another (docs/OWNERS.md).</summary>
    public static Place At(RoadRide.Lines lines, Vector2 p, float height)
    {
        if (!lines.Nearest(p, Current, out RoadRide.Hit other)) return Place.Off;
        if (other.Distance <= OnRoad) return Place.On;
        return AtItsHeight(other.Height, height) ? Place.Beside : Place.Off;
    }

    /// <summary>Whether a road within <see cref="Current"/> of <paramref name="p"/>, on it or not, stands at <paramref name="height"/>:
    /// a staircase's walked piece there runs beside it (a staircase shares no trunk with a road).</summary>
    public static bool NearAtItsHeight(Vector2 p, float height) =>
        RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(p, Current, out Vector2 other)
        && RoadSpatialGrid.TryGetRoadHeightWithin(other, OnRoad, out float h) && AtItsHeight(h, height);

    /// <summary>Whether a surface at <paramref name="height"/> is at the height of a road at <paramref name="roadHeight"/>.</summary>
    public static bool AtItsHeight(float roadHeight, float height) => Mathf.Abs(roadHeight - height) <= Height;

    /// <summary>Whether a stretch this long beside another road at its height is past the cap.</summary>
    public static bool TooLong(float metres) => metres >= MostRun;
}
