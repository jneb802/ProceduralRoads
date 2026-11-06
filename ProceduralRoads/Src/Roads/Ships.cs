using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The vanilla ship a bridge over a river makes way for: the one owner of how wide a boat's way under a bridge is, in its
/// deck and between its piers (<see cref="NavigationGapWidth"/>, read by the wooden kit, the Dvergr wood bridge and the
/// Dvergr viaduct alike), and how high a deck has to stand to pass over one (<see cref="Clears"/>).
///
/// <para>The ship a river is sized for is the <see cref="Longship"/>: the largest that sails the rivers bridges are built
/// over. Its width is measured by height off the game's VikingShip prefab: its solid (non-trigger) colliders, metres over
/// the ship's origin at about the waterline. The sail is cloth, not solid: a longship sails through a gap as wide as its
/// hull with its sail brushing the piers, and only a deck as high as its yard meets the yard's width.</para>
/// </summary>
public static class Ships
{
    /// <summary>A height band of a ship and how wide its solid parts are in it.</summary>
    public readonly struct Band
    {
        public readonly float Bottom, Top, Width;

        public Band(float bottom, float top, float width)
        {
            Bottom = bottom;
            Top = top;
            Width = width;
        }
    }

    public readonly struct Ship
    {
        public readonly string Name;
        public readonly float Length;
        /// <summary>Its solid width by height over the waterline, lowest first; the first is the hull.</summary>
        public readonly Band[] Bands;

        public Ship(string name, float length, params Band[] bands)
        {
            Name = name;
            Length = length;
            Bands = bands;
        }

        /// <summary>The hull's width: what passes between two piers under any deck.</summary>
        public float Hull => Bands[0].Width;
        /// <summary>Its highest solid point over the waterline.</summary>
        public float Top => Bands[Bands.Length - 1].Top;
    }

    /// <summary>VikingShip: hull, rails and ladders 5.8 m across to 4 m; the mast (1.3 m) to the yard's box collider,
    /// 9.2 m across from 8 to 9 m; the mast's top to 11 m. Nothing solid above.</summary>
    public static readonly Ship Longship = new("Longship", 21.6f,
        new Band(-1f, 4f, 5.8f), new Band(4f, 8f, 1.3f), new Band(8f, 9f, 9.2f), new Band(9f, 11f, 1.3f));

    /// <summary>The ship a bridge over a river makes way for.</summary>
    public static readonly Ship RiverShip = Longship;

    /// <summary>
    /// Room each side of the ship's widest point: a ship steered against a river's current drifts while it lines up on
    /// the gap, and 2 m a side is one wooden deck span (<see cref="BridgeLayout.DeckSpan"/>), so a helmsman off by a
    /// plank still passes.
    /// </summary>
    public const float PassingMargin = 2f;

    /// <summary>The narrowest way a river ship asks for (its hull and <see cref="PassingMargin"/> each side, 9.8 m): a
    /// river narrower is no river it sails under a bridge.</summary>
    public static float NarrowestWay => RiverShip.Hull + 2f * PassingMargin;

    /// <summary>
    /// What a bridge over a river leaves open for <see cref="RiverShip"/>, its deck's <paramref name="underside"/> and the
    /// structure's <paramref name="top"/> beside the way (its railing, piles or parapet over the walking surface,
    /// BridgeStructures.TopOverDeck) in metres over the water, each with <see cref="PassingMargin"/> each side:
    /// <list type="bullet">
    /// <item><b>deck</b>: the deck missing over the ship's way: the hull, or the widest solid part at the structure's own
    /// heights where that is wider (the deck or what stands on it meets it). Under the yard 9.8 m; a deck at the yard's height 13.2 m; 0 when the deck
    /// clears the ship (<see cref="Clears"/>) and it passes under.</item>
    /// <item><b>piers</b>: the way kept free of piers: at least the hull's (<see cref="NarrowestWay"/>), which passes between
    /// the piers under any deck, and never narrower than the deck's.</item>
    /// </list>
    /// </summary>
    public static (float deck, float piers) NavigationGapWidth(float underside, float top)
    {
        Ship ship = RiverShip;
        if (Clears(underside, ship)) return (0f, NarrowestWay);
        float width = ship.Hull;
        foreach (Band b in ship.Bands)
            if (b.Bottom < top && b.Top > underside) width = Mathf.Max(width, b.Width);
        float deck = width + 2f * PassingMargin;
        return (deck, deck);
    }

    /// <summary>Whether a deck whose underside stands <paramref name="heightOverWater"/> over the water clears
    /// <paramref name="ship"/> passing under it.</summary>
    public static bool Clears(float heightOverWater, Ship ship) => heightOverWater >= ship.Top;
}
