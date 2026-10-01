using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Vanilla's structural support rule, for the wooden bridge kit.
///
/// A bridge that stands when it is generated and falls down later is not a
/// bridge, and the height at which that happens is not a matter of taste: it
/// is arithmetic, and the arithmetic is in <c>WearNTear.UpdateSupport</c> and
/// <c>WearNTear.GetMaterialProperties</c>. The numbers below are transcribed
/// from those two methods (Valheim 1.0, buildid 25185596) rather than fitted
/// to observation, so that when a future patch changes them the difference is
/// a diff and not a mystery.
///
/// The rule, for one piece:
///
/// * A piece whose bounds overlap the terrain layer gets <see cref="MaxSupport"/>
///   outright. The buried foot of a pier is therefore always at 100.
/// * Otherwise it takes the BEST value offered by anything it touches. From a
///   neighbour with support <c>S</c> whose support point lies a distance
///   <c>d</c> away (centre of mass to centre of mass, plus
///   <see cref="ComDistanceMargin"/>), the offer is <c>S - loss * d * S</c>.
/// * <c>loss</c> is <see cref="HorizontalLoss"/> for a sideways neighbour and
///   <see cref="VerticalLoss"/> for one directly underneath, interpolated by
///   angle in between. Wood loses support far faster sideways than downwards,
///   which is why a bridge is piers and not a span.
/// * A piece is standing iff its support is at least <see cref="MinSupport"/>.
///   Below that it breaks, and everything it was holding up follows.
///
/// The consequence for this mod: support decays GEOMETRICALLY up a pier, so
/// the ceiling is a height, not a piece count, and no amount of extra
/// structure underneath raises it.
/// </summary>
public static class BridgeSupport
{
    // --- WearNTear.GetMaterialProperties, case MaterialType.Wood ---
    public const float MaxSupport = 100f;
    public const float MinSupport = 10f;
    public const float VerticalLoss = 0.125f;
    public const float HorizontalLoss = 0.2f;

    /// <summary>The constant 0.1 m WearNTear adds to every support distance,
    /// so that a piece resting flush on another still pays something.</summary>
    public const float ComDistanceMargin = 0.1f;

    /// <summary>What one pole keeps of the support of the pole directly
    /// beneath it. Directly beneath means the support point is straight down,
    /// so the angle lerp lands on <see cref="VerticalLoss"/> exactly and no
    /// collider geometry enters into it.</summary>
    public static float ColumnStepFactor(float segment) =>
        1f - VerticalLoss * (segment + ComDistanceMargin);

    /// <summary>Support at the top of a column of <paramref name="poles"/>
    /// poles whose foot is buried. One pole is the buried foot itself.</summary>
    public static float ColumnSupport(int poles, float segment)
    {
        if (poles <= 1) return MaxSupport;
        return MaxSupport * Mathf.Pow(ColumnStepFactor(segment), poles - 1);
    }

    /// <summary>How many poles a column can carry before its top pole is
    /// below <see cref="MinSupport"/> and falls. Exact: it is the largest
    /// <c>n</c> with <c>100 * f^(n-1) &gt;= 10</c>.</summary>
    public static int MaxPoles(float segment)
    {
        float f = ColumnStepFactor(segment);
        if (f <= 0f || f >= 1f) return int.MaxValue;
        // Guard the boundary against float error rather than trusting Log.
        int n = 1;
        while (ColumnSupport(n + 1, segment) >= MinSupport) n++;
        return n;
    }

    /// <summary>
    /// The tallest pier this kit can hold up, measured as
    /// <see cref="BridgeLayout.PierHeight"/> is measured: deck height above the
    /// channel bed.
    ///
    /// <para>MEASURED IN GAME, not derived. The column arithmetic above is
    /// exact and confirmed to four figures, and it is still the wrong model
    /// for a bridge: <see cref="MeasuredCeiling"/> says why.</para>
    /// </summary>
    public static float MaxPierHeight() => MeasuredCeiling;

    /// <summary>
    /// Where a real bridge actually loses its deck: about 18.4 m of column,
    /// so 18 m with a margin.
    ///
    /// <para>MEASURED in game on generated bridges. On one crossing two
    /// <c>wood_beam</c> read support 9.99 and
    /// 9.72 against a minimum of 10 - failing - standing on ground 18.6 and
    /// 18.8 m below the deck, while a beam of the same bridge on ground 17.1 m
    /// below read 12.99 and held. Two other bridges at 15.1 m and 19.9 m of
    /// reported pier height had every one of their 176 and 578 pieces
    /// supported. So the limit is real, the beam is the member that fails
    /// first, and it sits near 18.4 m.</para>
    ///
    /// <para><b>The column model predicted 13.2 m and that was wrong by five
    /// metres.</b> Two reasons, both worth keeping:</para>
    ///
    /// <para>1. A bridge is not a column. It is a lattice: stations every 2 m,
    /// tied by beams and deck plates, and <c>UpdateSupport</c> gives a piece
    /// resting on two support points more than 100 degrees apart their
    /// AVERAGE rather than the worse of them. That sideways sharing is worth
    /// roughly two poles of height over a free-standing stack, which is why
    /// arches and spans work in this game at all.</para>
    ///
    /// <para>2. <see cref="BridgeLayout.PierHeight"/> measures the deck above
    /// the channel's DEEPEST point, but <c>EmitColumn</c> stands each pier on
    /// the ground beneath that pier. Most stations are therefore shorter than
    /// the pier height suggests. Measured, the overstatement is about 0.5 to
    /// 1 m - small enough that pier height stays a usable proxy, and not the
    /// main error.</para>
    ///
    /// <para>What the model DID get right is the mechanism, and it is worth
    /// keeping for that: the per-segment factor was confirmed to four figures
    /// in game (0.7376 observed against 0.7375 predicted), minSupport is 10,
    /// and the beam is the weak link exactly as the derivation said. Only the
    /// number was wrong - which is the usual way, and the reason this constant
    /// is a measurement with its evidence attached rather than a formula.</para>
    /// </summary>
    public const float MeasuredCeiling = 18f;
}
