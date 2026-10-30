using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Vanilla's structural support rule, for the bridge kits: wood (the railed kits) and stone (the stone kit).
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

    // --- WearNTear.GetMaterialProperties, case MaterialType.Stone (stone_wall_2x1, stone_floor_2x2, stone_arch,
    //     stone_stair and stone_pillar are all MaterialType Stone, ValheimRIP prefabs) ---
    public const float StoneMaxSupport = 1000f;
    public const float StoneMinSupport = 100f;
    public const float StoneVerticalLoss = 0.125f;
    /// <summary>Stone loses all its support a metre sideways: nothing carries a stone piece but what stands under it.</summary>
    public const float StoneHorizontalLoss = 1f;

    /// <summary>What one stone piece keeps of the support of one under it whose centre lies <paramref name="distance"/>
    /// from its own (centre or origin, whichever is nearer, as UpdateSupport takes it), the support point straight down.</summary>
    public static float StoneStepFactor(float distance) => 1f - StoneVerticalLoss * (distance + ComDistanceMargin);

    /// <summary>
    /// A stone kit pier course (<see cref="BridgeLayout.StonePierBlock"/>, 2 x 1 x 1 m blocks) on the course under it:
    /// the courses are bonded, each block over the joint of the two under it, so its centre stands a metre up and half a
    /// metre off along and across from theirs (sqrt 1.5 m), the support point straight down on their top.
    /// </summary>
    public static float StoneCourseFactor => StoneStepFactor(Mathf.Sqrt(1.5f));

    /// <summary>
    /// What an arch keeps of the pier course its tall end stands against: read in game on the stair kit's cantilever
    /// (<see cref="CantileverKit"/>, cli_piece_support), a stone_arch 720 against a stone_pillar at 1000, its tall end on
    /// the pillar and nothing under it. A pier's top course is the weaker of the two courses beside the arch's tall end,
    /// so this is taken off it.
    /// </summary>
    public const float StoneArchOnPier = 0.72f;

    /// <summary>A floor or a railing block on an arch: its centre over the arch's flat top, a metre up and half a metre
    /// across from the arch's origin (sqrt 1.25 m).</summary>
    public static float StoneOnArchFactor => StoneStepFactor(Mathf.Sqrt(1.25f));

    /// <summary>The railing's top course on the course beside the deck: a metre up and a metre along from the origin of
    /// the block under it (sqrt 2 m), the running bond putting its centre over that block's end.</summary>
    public static float StoneRailOnRailFactor => StoneStepFactor(Mathf.Sqrt(2f));

    /// <summary>
    /// A floor, or a top course block, over a 6 m opening's open bay (<see cref="BridgeLayout.StoneBay.Open"/>): half on an
    /// arch's crown, half over the open bay, its centre over the crown's end, a metre along and a metre up from the arch's
    /// origin and half a metre across (sqrt 2.25 m). Its support point is straight down on the crown's flat top (the arch's
    /// top collider runs its whole 2 m), so it keeps <see cref="StoneStepFactor"/> of that distance: 0.8 of the arch, more
    /// than the railing over a crown keeps (<see cref="StoneOnArchFactor"/> then <see cref="StoneRailOnRailFactor"/>), so
    /// the open bay leaves <see cref="StoneMaxPierCourses"/> as it was. Derived, not measured in game.
    /// </summary>
    public static float StoneOnCrownFactor => StoneStepFactor(1.5f);

    /// <summary>
    /// The strip a stone deck ends on over a river's opening, on the crown of the half arch springing from the pier beside
    /// it (<see cref="BridgeLayout.StoneNavigationBays"/>): a block laid across its lane over the arch's crown half, its
    /// centre a metre up, half a metre along and half a metre across from the arch's origin (sqrt 1.5 m), the support point
    /// straight down on the crown's flat top. It keeps more of the arch than the railing over an arch does
    /// (<see cref="StoneOnArchFactor"/> then <see cref="StoneRailOnRailFactor"/>), so the opening leaves
    /// <see cref="StoneMaxPierCourses"/> as it was. Derived, not measured in game.
    /// </summary>
    public static float StoneStripOnCrownFactor => StoneStepFactor(Mathf.Sqrt(1.5f));

    /// <summary>
    /// The most courses a stone kit pier stands (its top course beside the arches, at the deck's underside, down to the one
    /// on the ground). Derived, not measured: the ground course is at <see cref="StoneMaxSupport"/>, each course over it
    /// keeps <see cref="StoneCourseFactor"/>, and the weakest piece the pier carries is the railing's top course over the
    /// crown of an arch (pier top course, arch <see cref="StoneArchOnPier"/>, the railing block on the arch
    /// <see cref="StoneOnArchFactor"/>, the top course on it <see cref="StoneRailOnRailFactor"/>), which has to keep
    /// <see cref="StoneMinSupport"/>. Nine courses (that railing at 116); a tenth would leave it at 97.
    /// </summary>
    public static int StoneMaxPierCourses()
    {
        float over = StoneArchOnPier * StoneOnArchFactor * StoneRailOnRailFactor;
        int n = 1;
        while (StoneMaxSupport * Mathf.Pow(StoneCourseFactor, n) * over >= StoneMinSupport) n++;
        return n;
    }

    /// <summary>
    /// The most stone_pillars a stone kit stair's footing stacks (from the step down to the one on the ground). Derived:
    /// each 2 m pillar keeps <see cref="StoneStepFactor"/> of 2 m of the one under it, and the step and the side wall on the
    /// top pillar (their centres 1.5 m up and about 0.7 m off its own, sqrt 2.75 m) have to keep
    /// <see cref="StoneMinSupport"/>. Seven pillars (the step at 126); an eighth would leave it at 93.
    /// </summary>
    public static int StoneMaxFootingPillars()
    {
        float on = StoneStepFactor(Mathf.Sqrt(2.75f));
        int n = 1;
        while (StoneMaxSupport * Mathf.Pow(StoneStepFactor(BridgeLayout.StonePillarHeight), n) * on >= StoneMinSupport) n++;
        return n;
    }
}
