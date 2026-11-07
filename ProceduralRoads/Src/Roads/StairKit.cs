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
/// The pieces one staircase is built of. Every kit's stair is the same 2 x 2 m
/// with a 1 m rise, so the layout is the same whatever it is built of; the
/// floors, fillers and supports differ in thickness and height. Sizes from
/// cli_piece_geometry, Valheim 1.0.16.
///
/// Materials by biome: black marble in the Mistlands, stone everywhere else.
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

    /// <summary>A flat piece's footprint by its prefab: one of <see cref="Fillers"/>, else a 2 x 2 floor.</summary>
    internal FillerKind FlatOf(string prefab)
    {
        foreach (FillerKind flat in Fillers) if (flat.Prefab == prefab) return flat;
        return new FillerKind(prefab, StairLayout.PieceRun, StairLayout.PieceRun);
    }

    /// <summary>Whether its flights are held up by <see cref="StairSupport"/> (walls, arches, cornices), so may climb a sheer
    /// face; false for wood, read from old saves only, whose layouts stood on their own columns.</summary>
    public readonly bool HoldsOut;

    /// <summary>The kit's number in the built staircases' record (<see cref="BuiltStairs"/>): fixed, never reused; 0 names no
    /// kit.</summary>
    public readonly int Number;

    private StairKit(int number, string name, string stair, string floor, float floorTop, float floorThickness, FillerKind[] fillers, bool holdsOut)
    {
        Number = number; Name = name; Stair = stair; Floor = floor; FloorTop = floorTop; FloorThickness = floorThickness;
        Fillers = fillers; HoldsOut = holdsOut;
    }

    /// <summary>The version of the kits' laying over a staircase's layout (<see cref="Dress"/>), stamped on every staircase
    /// it lays (<see cref="BuiltSite.Layout"/>): raised with any change to the pieces a kit lays.
    /// <para>2: supports laid by <see cref="StairSupport"/> at first build; the layout's own columns and brackets left out.</para>
    /// <para>3: the railing bare where the road meets it, posts square to their pieces, a turn's wedge railed; weathered
    /// (<see cref="StairWear"/>).</para>
    /// <para>4: supports chosen on the ground as places will leave it (<see cref="PlacedSites.GroundAt(float, float, WorldGenerator)"/>),
    /// a placed place's levelling included.</para>
    /// <para>5: stone held from the slope by a block and arch under both halves of a stair; the block under the first stair
    /// past a turn kept, stone's and black marble's, and the wall's course there; a 2 m support under a 1 x 1 filler half a
    /// metre toward the wall, a wall right under it.</para>
    /// <para>6: a lower rail a metre under every flight's handrail; no railing on an end step in the ground; a switchback's
    /// legs railed along the line they share, their crossed rails one roof top; a square turn's corner railed on its outside;
    /// a landing at a place railed but on the side facing it; bare where the road really meets the staircase.</para>
    /// <para>7: black marble's square turn's outer corner held by a column of blackmarble_outcorner, not straight cornices.</para>
    /// <para>8: stone's walled floor walled across its open end too, the walls bonded at their corners with 2x1s turned
    /// along the other wall, not 1x1s.</para>
    /// <para>9: a wall's courses down to the finished ground however deep a road's cut lowers it, none where that ground
    /// is not known; a piece nothing within reach holds left unheld, not hung with supports; supports chosen on the ground
    /// the layout was laid out on (RoadStairRuns.PlanningGround: a road's 1 m lattice, an approach's expected levelling).</para>
    /// <para>10: a flight's side railed beside a switchback's join filler too; a turn's runs stopped only by a walked piece
    /// at their heights, not a switchback's other leg over or under them, what still stands apart closed post to post; a
    /// switchback's join closed by a handrail; no post wholly in a support; a floor at a road's end railed on an edge over
    /// the ledge.</para>
    /// <para>11: a road's end met on the ring round it (RoadEndRing): the floors at a road's end (StairRailing.AtEnd) are those
    /// within the ring's outer edge of the road's end (its radius, RoadEndRing.Delta and 0.3 m), not 0.3 m, and with 10 they
    /// are railed only on an edge over the ledge.</para>
    /// <para>12: black marble's corner column of outcorners steps a metre in on both faces a course, back from the open end
    /// as well as into the slope, each course's top flush under the block of the one above, down to its own block in the
    /// ground; a straight cornice wholly in an outcorner of its course left out. The laid staircase read with the game's
    /// support rule (StairSupport.HoldUp): a support piece it would break left out, a walking piece it holds under the
    /// margin said; black marble's cornice columns hung at most StairSupport.MostHungCourses courses over the first that
    /// touches the ground (deeper, refused).
    /// Never a rail across the walk where it leaves a floor it ends on (StairRailing.WalkOut), read from the walk, not
    /// from where the end was recorded: a place's top floor laid on past the end recorded at the last stair's top was railed
    /// across the way onto the landing (in game: a stone tower's approach at -1930.0,6720.0). A floor at a road's end
    /// the walk carries on across onto another staircase met there end to end (StairStructures.MetAtEnds) railed on every
    /// edge not walked on across but where the ground stands level with it (in game: two black marble staircases'
    /// connector at -6921.0,-3653.8, one side bare), and so every floor of the landing at that end (in game: two staircases
    /// meeting at the top at 3913.5,-6145.4, railed across the walk onto the lower and bare to the north and east).</para>
    /// <para>13: that landing railed on every edge not walked on across, however level the ground beside it stands (no road
    /// comes onto it from any side): in game, the same top at 3913.5,-6144.4, its north floor bare to the north and east,
    /// the ground 0.4 m over it; and a ridge where two black marble staircases meet at 1601.0,-6187.5, three floors in a
    /// row bare all round, the ground 0.16 m under them.</para>
    /// <para>14: the road takes the walk over only on the floors its end meets, within its ring's reach (StairRailing.Ways.Road),
    /// not on the floors laid on past them level with them, which are railed on every edge not walked on across as any floor
    /// is: in game, a black marble staircase's six flat floors from its foot at 1807.9,-6613.5 to its first stair railed on
    /// one side only, and its four at the top at 1776.4,-6554.4 bare on both.</para>
    /// <para>15: black marble's cornice columns hung with vines as the Dvergr viaduct hangs its corbels' (StairSupport's
    /// vines, DvergrViaduct.CorbelVine): one column of a walking piece at the viaduct's curtain chance, down to the ground
    /// (none with its origin in it), none in the headroom over a walked surface, each only where the game's biome
    /// at it is one vanilla's vines grow in (DvergrViaduct.VineBiomes: the Meadows and the Mistlands).</para>
    /// <para>16: the floors a staircase's end meets, at a road or on a place's landing, railed by one rule
    /// (StairRailing.Ways.Road): open on every edge where the finished ground half a metre out stands at walking height, or is
    /// not known, and railed only over the ledge, a drop of more than StairRailing.LedgeDrop (a metre); the walk straight on
    /// off a floor it ends on (StairRailing.WalkOut) open by the same rule; a floor the layout laid out onto the place's ground
    /// (StairLayout.AddPlaceFloor) open where that ground meets it: in game, a cave's approach's last floor on its landing,
    /// 0.4-0.7 m over the ground beside it, was railed on both sides.</para></summary>
    public const int DressingVersion = 16;

    /// <summary>
    /// The pieces this kit lays over a staircase's layout, when its first zone is built (<see cref="StairStructures.Build"/>):
    /// the network saved the layout (its steps, floors and the fillers of its turns and joins, settled on the finished
    /// ground), and the kit lays them and what holds them up here, with this build's kits, and saved then
    /// (<see cref="StairSupport.Lay"/> on the <paramref name="natural"/> ground, as places leave it before the network's earthwork,
    /// and the <paramref name="finished"/> ground), and its railing
    /// (<see cref="StairRailing"/>). Supports an
    /// older layout saved with it are left out for the kit's own. Wood, laid by older builds only, keeps its layout's poles.
    /// Raise <see cref="DressingVersion"/> with any change to what is laid.
    /// </summary>
    public List<StructurePiece> Dress(StairRun run, Func<float, float, float> natural, Func<int, int, float> finished, bool? weathered = null,
        StairSupport.Shortfall? shortfall = null, IReadOnlyList<StructurePiece>? beside = null,
        Func<float, float, Heightmap.Biome>? biomeAt = null)
    {
        if (!HoldsOut) return run.Pieces.ConvertAll(Copy);
        List<StructurePiece> walking = run.Pieces.FindAll(p => p.Kind != StructurePieceKind.StairSupport).ConvertAll(Copy);
        var pieces = new List<StructurePiece>(walking);
        var vines = new List<StructurePiece>();
        List<StructurePiece> supports = StairSupport.Lay(this, walking, natural, finished, shortfall, vines, biomeAt);
        pieces.AddRange(supports);
        pieces.AddRange(StairRailing.Lay(this, walking, run.Ends, finished, supports, beside));
        pieces.AddRange(vines);
        // Weathered as a ruin where the Structures.Weathered setting lists stairs (StructureWear).
        if (weathered ?? StructureWear.Stairs) StairWear.Weather(pieces, run.From, run.To);
        return pieces;
    }

    private static StructurePiece Copy(StructurePiece p) => new()
    {
        Kind = p.Kind, Prefab = p.Prefab, Position = p.Position,
        YawDegrees = p.YawDegrees, PitchDegrees = p.PitchDegrees, RollDegrees = p.RollDegrees,
        HealthFraction = p.HealthFraction, Cantilevered = p.Cantilevered,
    };

    /// <summary>Highest a flat run of any kit stands over the ground under it, on its columns: a short walkway joining
    /// stairs, crossing a valley within it level instead of down and up again; deeper, the staircase climbs or goes round,
    /// and a valley wants a bridge. Low, so a walkway keeps the support players build on it with: stone loses all its
    /// support sideways and black marble half, so a tall column leaves its top nearly unsupported.</summary>
    public const float WalkwayCap = 5f;

    public static readonly StairKit Stone = new(1, "stone", BridgeLayout.StoneStairPrefab, BridgeLayout.StoneFloorPrefab, 0.5f, 1f,
        new[] { new FillerKind("stone_wall_2x1", 2f, 1f), new FillerKind("stone_wall_1x1", 1f, 1f), new FillerKind(BridgeLayout.StoneFloorPrefab, 2f, 2f) },
        holdsOut: true);

    /// <summary>Mistlands. Every piece here is a 1 m thick block like stone's.</summary>
    public static readonly StairKit BlackMarble = new(2, "blackmarble", DvergrViaduct.StepPrefab, "blackmarble_floor", 0.5f, 1f,
        new[] { new FillerKind("blackmarble_2x1x1", 2f, 1f), new FillerKind("blackmarble_1x1", 1f, 1f), new FillerKind("blackmarble_floor", 2f, 2f) },
        holdsOut: true);

    /// <summary>Wood, no longer laid (stone everywhere but the Mistlands): kept to read and lay the wood
    /// staircases older worlds saved, which stand on their layout's own poles. A wood floor is a 13 cm board whose origin is
    /// its walking surface; wood has no 2 x 1 floor, so wedges take 1 x 1s.</summary>
    public static readonly StairKit Wood = new(3, "wood", "wood_stair", "wood_floor", 0f, 0.13f,
        new[] { new FillerKind("wood_floor_1x1", 1f, 1f), new FillerKind("wood_floor", 2f, 2f) },
        holdsOut: false);

    /// <summary>Every kit.</summary>
    internal static readonly StairKit[] All = { Stone, BlackMarble, Wood };

    /// <summary>The walking surface of a floor piece over its origin, by the kit its prefab names (stone and black marble
    /// floors stand half a metre under their surface, wood floors at it): the one owner every caller asks.</summary>
    public static float FloorTopOf(string prefab) => OfPrefab(prefab).FloorTop;

    /// <summary>The kit with this record number (<see cref="Number"/>), null for none this build has.</summary>
    public static StairKit? Numbered(int number) => Array.Find(All, k => k.Number == number);

    public static StairKit? Named(string name) => name switch
    {
        "stone" => Stone,
        "blackmarble" or "marble" => BlackMarble,
        "wood" => Wood,
        _ => null,
    };

    /// <summary>The kit named <paramref name="name"/>; stone for none: the one default every caller reads. The one owner of structure kit by biome (docs/OWNERS.md).</summary>
    public static StairKit Of(string name) => Named(name) ?? Stone;

    /// <summary>The kit a piece's prefab names (the part before its first underscore); stone for none.</summary>
    public static StairKit OfPrefab(string prefab) => Of(prefab.Split('_')[0]);

    /// <summary>Where the black-marble and Dvergr kits belong: the Mistlands. Read by the share rule (<see cref="LeansTo"/>)
    /// for staircases and bridges, and by <see cref="ForSite"/>.</summary>
    public static bool MistlandsKitBelongs(Heightmap.Biome biome) => biome == Heightmap.Biome.Mistlands;

    /// <summary>Where a stone bridge belongs over a wooden one: outside the Meadows (<see cref="LeansTo"/>).</summary>
    public static bool StoneOverWoodBelongs(Heightmap.Biome biome) => biome != Heightmap.Biome.Meadows;

    /// <summary>The kit for a staircase at one place in <paramref name="biome"/> (the search's per-cell prediction, before a
    /// staircase has places to share): the share rule over that one place.</summary>
    public static StairKit ForSite(Heightmap.Biome biome) => MistlandsKitBelongs(biome) ? BlackMarble : Stone;

    /// <summary>Share of a staircase in the Mistlands from which it is black marble.</summary>
    public const float BiomeShare = 0.4f;

    /// <summary>
    /// The kit for a staircase over <paramref name="line"/>, sampled every 2 m: black marble where at
    /// least <see cref="BiomeShare"/> of it is in the Mistlands, stone otherwise. Before a staircase is laid this is
    /// a guess from the line it is expected to take (its road, or its ends); the kit it is built in is the one its own
    /// pieces stand in (<see cref="ForPlan"/>).
    /// </summary>
    public static StairKit ForLine(IReadOnlyList<Vector2> line, Func<float, float, Heightmap.Biome> biomeAt) =>
        ForPlaces(Samples(line), biomeAt);

    /// <summary><paramref name="line"/> sampled every 2 m.</summary>
    public static List<Vector2> Samples(IReadOnlyList<Vector2> line)
    {
        var samples = new List<Vector2>();
        for (int i = 0; i < line.Count; i++)
        {
            Vector2 a = line[i], b = i + 1 < line.Count ? line[i + 1] : line[i];
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 2f));
            for (int k = 0; k < n; k++) samples.Add(Vector2.Lerp(a, b, k / (float)n));
            if (i + 1 >= line.Count) break;
        }
        return samples;
    }

    /// <summary>
    /// The kit a laid staircase is built in: the one its walked pieces (stairs, floors, fillers) stand in by
    /// <see cref="ForPlaces"/>. Its road's line is no measure of that: a layout that finds its own way between its ends
    /// may leave it. The staircase from (-9344, -792) to (-9224, -792) turned south at (-9253, -804) through Black
    /// Forest and Mountain and came out black marble (none of it in the Mistlands), though
    /// the game's biome along the straight line between its ends is a quarter Mistlands: a band at the Mistlands' edge,
    /// wider to the north-west, which its road's line had to cross for longer to make it so.
    /// </summary>
    public static StairKit ForPlan(StairPlan plan, Func<float, float, Heightmap.Biome> biomeAt)
    {
        var walked = new List<Vector2>();
        foreach (StairPiece p in plan.Pieces)
            if (p.Kind != StairPieceKind.Support) walked.Add(new Vector2(p.Position.x, p.Position.z));
        return ForPlaces(walked, biomeAt);
    }

    /// <summary>The one rule: black marble where at least <see cref="BiomeShare"/> of <paramref name="places"/> are in
    /// the Mistlands, stone otherwise (and for none).</summary>
    private static StairKit ForPlaces(IReadOnlyList<Vector2> places, Func<float, float, Heightmap.Biome> biomeAt) =>
        InMistlands(places, biomeAt) ? BlackMarble : Stone;

    /// <summary>The one owner of "this structure is Mistlands": at least <see cref="BiomeShare"/> of
    /// <paramref name="places"/> (a staircase's walked pieces, a bridge's length) are in the Mistlands; none are not. The one owner of structure kit by biome (docs/OWNERS.md).</summary>
    public static bool InMistlands(IReadOnlyList<Vector2> places, Func<float, float, Heightmap.Biome> biomeAt) =>
        LeansTo(places, biomeAt, MistlandsKitBelongs);

    /// <summary>The one rule for a structure across a biome edge: it takes the more advanced kit where at least
    /// <see cref="BiomeShare"/> of <paramref name="places"/> stand where that kit belongs (<paramref name="advanced"/>), so a
    /// structure on a boundary leans to the more advanced kit (black marble over stone, stone over wood).</summary>
    public static bool LeansTo(IReadOnlyList<Vector2> places, Func<float, float, Heightmap.Biome> biomeAt, Func<Heightmap.Biome, bool> advanced)
    {
        int count = 0;
        foreach (Vector2 p in places)
            if (advanced(biomeAt(p.x, p.y))) count++;
        return places.Count > 0 && count >= BiomeShare * places.Count;
    }
}
