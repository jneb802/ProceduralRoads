using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A flat piece a turn's wedge may be filled with: <see cref="Across"/>
/// metres across its heading, <see cref="Depth"/> along it.</summary>
public readonly struct FillerKind
{
    public readonly string Prefab;
    public readonly float Across;
    public readonly float Depth;

    public FillerKind(string prefab, float across, float depth)
    {
        Prefab = prefab; Across = across; Depth = depth;
    }
}

/// <summary>
/// The pieces that hold a flight out from a steep face instead of standing it
/// on columns down to the ground below (a hand-built reference at
/// -2300.4, 278.6, 1554.5: two 1x1x2 pillars clipped into the wall as the
/// foundation, arches out from them to support the stairs, the stairs placed
/// on those arches, and a horizontal 2x1x1 block next to the stair as filler so it looks better).
/// The flight's wall side stands <see cref="Strip"/> off the face: in that strip
/// a <see cref="Pillar"/> per metre of flight, clipped into the face, its top at
/// the piece's underside; from each, an <see cref="Arch"/> a metre wide out
/// under the piece, its tall end against the pillar and its top under the
/// piece; on the pillars, <see cref="Beside"/> laid along the flight, its top
/// at the piece's top edge. Measured extents from the reference's snap points.
///
/// What holds it is the pillars sunk into the face: Valheim grounds a piece that touches
/// terrain, and the rest takes its support from them. Read in game (cli_piece_support)
/// on a 6:1 face: stone with the arches' tall ends against the face, pillars and
/// arches 1000, stair 848; with the arches 1.6 m clear of it, arches 720, stair 610, all
/// holding (minimum 100); black marble 0.9 m clear, columns 1500, arches 415, stair 684.
/// </summary>
public sealed class CantileverKit
{
    /// <summary>1 x 1 m, <see cref="PillarHeight"/> tall, origin at its centre.</summary>
    public readonly string Pillar;
    public readonly float PillarHeight;
    /// <summary><see cref="ArchLength"/> along its local x (across the flight), a metre
    /// along the flight, <see cref="ArchHeight"/> tall at its tall end and thin at the
    /// other, flat on top; origin at the centre of its bounds.</summary>
    public readonly string Arch;
    public readonly float ArchLength, ArchHeight;
    /// <summary>The local x end (+1 or -1) of the arch that is tall: it goes against the pillar.</summary>
    public readonly float ArchTallEnd;
    /// <summary>A block <see cref="BesideLength"/> along its local x, 1 m tall and deep,
    /// origin at its centre: laid on the pillars beside the piece.</summary>
    public readonly string Beside;
    public readonly float BesideLength;
    /// <summary>The support strip between the face and the flight's wall side.</summary>
    public const float Strip = 1f;
    /// <summary>The pillars in the strip reach at least this far under the piece's underside ...</summary>
    public const float StripDepth = 2f;
    /// <summary>... and are stacked on down until sunk into the face or the ground below, at most this far
    /// (hand-built turn examples: where the top pillars did not reach the face, the stacks were carried on
    /// down to the ground, up to four stone pillars). The support each pillar passes on falls with the height
    /// of the stack (WearNTear verticalLoss 0.125 a metre): four stone pillars leave about 400 at the top, and
    /// the arch and stair over it about 290 and 250, over stone's minimum of 100; black marble's 1 m columns
    /// lose more per metre of their 1500, so six.</summary>
    public readonly float MaxDepth;

    public CantileverKit(string pillar, float pillarHeight, string arch, float archLength, float archHeight, float archTallEnd, string beside, float besideLength,
        float maxDepth)
    {
        Pillar = pillar; PillarHeight = pillarHeight; Arch = arch; ArchLength = archLength; ArchHeight = archHeight;
        ArchTallEnd = archTallEnd; Beside = beside; BesideLength = besideLength; MaxDepth = maxDepth;
    }
}

/// <summary>
/// The pieces one staircase is built of. Every kit's stair is the same 2 x 2 m
/// with a 1 m rise, so the layout is the same whatever it is built of; the
/// floors, fillers and supports differ in thickness and height. Sizes from
/// cli_piece_geometry, Valheim 1.0.16.
///
/// Materials by biome: Mistlands stairs are black marble or similar pieces, meadows wood.
/// The Black Forest can be either, with stone more likely further out; its stone resolves
/// to 0 in the early centre game, and wood stairs never fail to resolve, the probability just lowers.
/// </summary>
public sealed class StairKit
{
    public readonly string Name;
    /// <summary>2 x 2 m, 1 m rise; origin at the foot, centred; climbs toward local -z.</summary>
    public readonly string Stair;
    /// <summary>2 x 2 m floor; its walking surface stands <see cref="FloorTop"/> above its origin.</summary>
    public readonly string Floor;
    public readonly float FloorTop;
    public readonly float FloorThickness;
    /// <summary>Flat pieces for a turn's wedge, as thick as the floor and level with it.</summary>
    public readonly FillerKind[] Fillers;
    /// <summary>Stacked under a piece that does not reach the ground; origin at its centre.</summary>
    public readonly string Support;
    public readonly float SupportHeight;

    /// <summary>How a flight along a steep face is held out from it, or null where the
    /// kit has no such pieces: then it stands on <see cref="Support"/> columns as ever.</summary>
    public readonly CantileverKit? Cantilever;

    private StairKit(string name, string stair, string floor, float floorTop, float floorThickness, FillerKind[] fillers, string support, float supportHeight,
        CantileverKit? cantilever = null)
    {
        Name = name; Stair = stair; Floor = floor; FloorTop = floorTop; FloorThickness = floorThickness;
        Fillers = fillers; Support = support; SupportHeight = supportHeight; Cantilever = cantilever;
    }

    /// <summary>Highest a flat run of any kit stands over the ground under it, on its columns: a short walkway joining
    /// stairs, crossing a valley within it level instead of down and up again; deeper, the staircase climbs or goes round,
    /// and a valley wants a bridge. Low, so a walkway keeps the support players build on it with: stone loses all its
    /// support sideways and black marble half, so a tall column leaves its top nearly unsupported.</summary>
    public const float WalkwayCap = 5f;

    public static readonly StairKit Stone = new("stone", "stone_stair", "stone_floor_2x2", 0.5f, 1f,
        new[] { new FillerKind("stone_wall_2x1", 2f, 1f), new FillerKind("stone_wall_1x1", 1f, 1f), new FillerKind("stone_floor_2x2", 2f, 2f) },
        "stone_pillar", 2f,
        new CantileverKit("stone_pillar", 2f, "stone_arch", 2f, 1f, 1f, "stone_wall_2x1", 2f, maxDepth: 8f));

    /// <summary>Mistlands. Every piece here is a 1 m thick block like stone's;
    /// the 1 m column stacks where stone stacks a 2 m pillar. Its cantilever (cli_piece_geometry):
    /// blackmarble_arch is 2 m long, 2 m tall at its local -x end (snaps top and bottom
    /// there, only the top at +x), a metre deep; two 1 m columns per metre clip into the face.</summary>
    public static readonly StairKit BlackMarble = new("blackmarble", "blackmarble_stair", "blackmarble_floor", 0.5f, 1f,
        new[] { new FillerKind("blackmarble_2x1x1", 2f, 1f), new FillerKind("blackmarble_1x1", 1f, 1f), new FillerKind("blackmarble_floor", 2f, 2f) },
        "blackmarble_column_1", 1f,
        new CantileverKit("blackmarble_column_1", 1f, "blackmarble_arch", 2f, 2f, -1f, "blackmarble_2x1x1", 2f, maxDepth: 6f));

    /// <summary>Meadows, and the Black Forest's centre. No cantilever: there is no wood_arch;
    /// darkwood_arch and the beams are not a bracket off a clipped pillar, so wood
    /// flights stand on poles as ever. A wood floor is a 13 cm
    /// board whose origin is its walking surface (it snaps level with the
    /// stair's top edge); wood has no 2 x 1 floor, so wedges take 1 x 1s.</summary>
    public static readonly StairKit Wood = new("wood", "wood_stair", "wood_floor", 0f, 0.13f,
        new[] { new FillerKind("wood_floor_1x1", 1f, 1f), new FillerKind("wood_floor", 2f, 2f) },
        "wood_pole2", 2f);

    /// <summary>Every kit.</summary>
    internal static readonly StairKit[] All = { Stone, BlackMarble, Wood };

    /// <summary>The walking surface of a floor piece over its origin, by the kit its prefab names (stone and black marble
    /// floors stand half a metre under their surface, wood floors at it): the one owner every caller asks.</summary>
    public static float FloorTopOf(string prefab) => (Named(prefab.Split('_')[0]) ?? Stone).FloorTop;

    public static StairKit? Named(string name) => name switch
    {
        "stone" => Stone,
        "blackmarble" or "marble" => BlackMarble,
        "wood" => Wood,
        _ => null,
    };

    /// <summary>In the Black Forest no stair is stone this close to the centre ...</summary>
    public const float ForestStoneFrom = 1200f;
    /// <summary>... and from here out stone's share holds at <see cref="ForestStoneShareMax"/>.</summary>
    public const float ForestStoneFull = 3000f;
    /// <summary>Wood never runs out: its chance only falls, to one in five.</summary>
    public const float ForestStoneShareMax = 0.8f;

    /// <summary>Chance a Black Forest staircase this far from the world's centre is
    /// stone. Its own curve, wider and further out than the road surface's
    /// 1000 m handover (a bit wider range and a bit further out).</summary>
    public static float ForestStoneShare(float distanceFromCentre)
    {
        float t = Mathf.Clamp01((distanceFromCentre - ForestStoneFrom) / (ForestStoneFull - ForestStoneFrom));
        return ForestStoneShareMax * t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The kit for a staircase starting at <paramref name="at"/> in
    /// <paramref name="biome"/>: deterministic from the world seed and the
    /// spot, so the same world builds the same stairs. Biomes nobody has asked
    /// about yet build stone.
    /// </summary>
    public static StairKit ForSite(Heightmap.Biome biome, Vector2 at, int seed)
    {
        switch (biome)
        {
            case Heightmap.Biome.Mistlands: return BlackMarble;
            case Heightmap.Biome.Meadows: return Wood;
            case Heightmap.Biome.BlackForest:
                return Draw(at, seed) < ForestStoneShare(at.magnitude) ? Stone : Wood;
            default: return Stone;
        }
    }

    /// <summary>Share of a staircase in the Mistlands from which it is black marble.</summary>
    public const float MistlandsShare = 0.4f;

    /// <summary>
    /// The kit for a staircase over <paramref name="line"/>: black marble where at
    /// least <see cref="MistlandsShare"/> of it is in the Mistlands; otherwise the
    /// kit of the biome most of it lies in. The Black Forest's draw is made at the
    /// line's first point.
    /// </summary>
    public static StairKit ForLine(IReadOnlyList<Vector2> line, Func<float, float, Heightmap.Biome> biomeAt, int seed)
    {
        var counts = new Dictionary<Heightmap.Biome, int>();
        int samples = 0;
        for (int i = 0; i < line.Count; i++)
        {
            Vector2 a = line[i], b = i + 1 < line.Count ? line[i + 1] : line[i];
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 2f));
            for (int k = 0; k < n; k++)
            {
                Vector2 p = Vector2.Lerp(a, b, k / (float)n);
                Heightmap.Biome biome = biomeAt(p.x, p.y);
                counts[biome] = counts.TryGetValue(biome, out int c) ? c + 1 : 1;
                samples++;
            }
            if (i + 1 >= line.Count) break;
        }
        if (samples == 0) return Stone;
        int mist = counts.TryGetValue(Heightmap.Biome.Mistlands, out int m) ? m : 0;
        Heightmap.Biome most = Heightmap.Biome.Mountain;
        int best = -1;
        foreach (var kv in counts)
            if (kv.Key != Heightmap.Biome.Mistlands && (kv.Value > best || (kv.Value == best && kv.Key < most))) { most = kv.Key; best = kv.Value; }
        if (mist >= MistlandsShare * samples || best < 0) return BlackMarble;
        return ForSite(most, line[0], seed);
    }

    /// <summary>A number in [0, 1) from the seed and the 4 m cell of the spot.</summary>
    internal static float Draw(Vector2 at, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 2654435761u;
            h ^= (uint)Mathf.FloorToInt(at.x / 4f) * 374761393u;
            h = (h ^ (h >> 15)) * 2246822519u;
            h ^= (uint)Mathf.FloorToInt(at.y / 4f) * 668265263u;
            h = (h ^ (h >> 13)) * 3266489917u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
