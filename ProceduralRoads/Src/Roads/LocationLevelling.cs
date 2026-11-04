using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One levelling operation a location carries, as the road generator needs it.
///
/// A location is a prefab with TerrainModifier components in it. When the game
/// places the location, Heightmap.ApplyModifier levels every vertex inside
/// m_levelRadius of the modifier to the modifier's own world height plus
/// m_levelOffset - absolutely, with no clamp, because the +/-8 m clamp applies
/// only to player modifications and a location's are not. That is how a road
/// end can sit nine metres off the height the world generator reports there:
/// the generator is not what the player walks on.
///
/// Heights here are relative to the ground under the location's centre, which
/// is where the game reads its placement height. A location only ever rotates
/// about Y, so this height does not depend on the rotation - only the footprint
/// does.
/// </summary>
public readonly struct LevelOp
{
    /// <summary>Horizontal offset from the location's centre, in the prefab's
    /// own frame. A location's rotation turns this, so an operation with a
    /// non-zero offset sits somewhere on a circle we cannot pin down.</summary>
    public readonly float OffsetX;
    public readonly float OffsetZ;

    /// <summary>Metres above the ground under the location's centre.</summary>
    public readonly float Height;

    public readonly float Radius;
    public readonly bool Square;

    /// <summary>How far round the operation the game blends the ground towards
    /// its height after levelling it (0: no smoothing), and how sharply: at
    /// distance d the ground moves by 1 - (d / SmoothRadius)^SmoothPower of the
    /// way (Heightmap.SmoothTerrain2).</summary>
    public readonly float SmoothRadius;
    public readonly float SmoothPower;

    public LevelOp(float offsetX, float offsetZ, float height, float radius, bool square,
        float smoothRadius = 0f, float smoothPower = 3f)
    {
        OffsetX = offsetX; OffsetZ = offsetZ; Height = height; Radius = radius; Square = square;
        SmoothRadius = smoothRadius; SmoothPower = smoothPower;
    }

    /// <summary>Whether this operation sits on the location's own centre, so
    /// its footprint is the same whichever way the location is turned.</summary>
    public bool Concentric => Mathf.Abs(OffsetX) <= ConcentricTolerance
                           && Mathf.Abs(OffsetZ) <= ConcentricTolerance;

    public const float ConcentricTolerance = 0.5f;
}

/// <summary>
/// What ground a location will leave behind once it is placed.
///
/// Road generation runs at world load; locations are placed later, zone by
/// zone, and level the ground under themselves when they are. A road built to
/// the generator's height therefore meets a doorstep that has since moved.
/// This works out where that doorstep will be, ahead of time.
///
/// It answers only where it can be right. A location's rotation is drawn from
/// the global random stream at placement and is not reproducible from world
/// data, so an operation offset from the location's centre could be anywhere
/// on a circle: those are declined rather than guessed at. The caller then
/// falls back to the natural terrain under the road's own end, which is where
/// a road that stops outside the levelled ground should meet it anyway.
/// </summary>
public static class LocationLevelling
{
    /// <summary>
    /// Where the levelling of the location centred at a point comes from.
    /// Set once by LocationPrefabLevelling, which reads it off the prefab; left
    /// null wherever prefabs cannot be read - the test suite, or a headless
    /// server without the asset bundles - and the road then falls back to the
    /// natural terrain under its own end, as it did before any of this.
    /// </summary>
    public static System.Func<Vector2, IReadOnlyList<LevelOp>?>? Source;

    /// <summary>Saved placement, when the location has already spawned.
    /// Its root can differ from a fresh procedural query (other modifiers or
    /// a saved world). Copy only the numeric height, never a live component.</summary>
    public static System.Func<Vector2, float?>? PlacementHeightSource;
    public static System.Action? ResetPlacements;

    /// <summary>Build every lazy cache behind Source and PlacementHeightSource
    /// NOW, on the calling thread. Islands are planned on worker threads and
    /// these caches read Unity assets and the ZDO table, neither of which may
    /// be touched off the main thread or filled by two threads at once.</summary>
    public static System.Action? Prime;

    /// <summary>Set once Prime has run, on the main thread, before any island
    /// worker starts; cleared when they are all finished.
    ///
    /// While it is set, preparation is closed: a lookup that misses must not
    /// load a Unity asset or write into a shared dictionary (both fatal off the
    /// main thread); it names what it could not find and gives up.</summary>
    public static volatile bool Sealed;

    /// <summary>Lookups that missed after preparation was closed. Reported at
    /// the end of a generation: a miss that nothing counts is a miss nobody
    /// hears about.</summary>
    public static int MissesAfterSealing;

    private static readonly System.Collections.Generic.HashSet<string> m_reported = new();
    private static readonly object m_reportGate = new object();

    public static void Seal()
    {
        MissesAfterSealing = 0;
        lock (m_reportGate) m_reported.Clear();
        Sealed = true;
    }
    public static void Unseal() => Sealed = false;

    /// <summary>Set when a lookup on THIS thread was refused for want of
    /// preparation. An island worker builds one road at a time, so this says
    /// "the road being built now is missing something it needed": it is
    /// refused rather than built through a POI it could not see.</summary>
    [System.ThreadStatic] public static bool PreparationMissedHere;

    /// <summary>Called as a road starts, so the flag describes that road.</summary>
    public static void BeginRoad() => PreparationMissedHere = false;

    /// <summary>Record a lookup that arrived too late to be served, naming it
    /// once per generation however often it is asked for. Returns true if the
    /// caller should give up rather than build anything.</summary>
    public static bool RefuseAfterSealing(string what)
    {
        if (!Sealed) return false;
        PreparationMissedHere = true;
        System.Threading.Interlocked.Increment(ref MissesAfterSealing);
        bool first;
        lock (m_reportGate) first = m_reported.Add(what);
        if (first)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(
                $"'{what}' was not prepared before road generation started, and cannot be read now: " +
                "roads near it meet natural terrain. This is a gap in preparation, not in the world.");
        return true;
    }

    public static float CentreHeight(Vector2 centre, WorldGenerator world)
    {
        float? saved = PlacementHeightSource?.Invoke(centre);
        return saved.HasValue && !float.IsNaN(saved.Value) && !float.IsInfinity(saved.Value)
            ? saved.Value : BiomeBlendedHeight.GetBlendedHeight(centre.x, centre.y, world);
    }

    /// <summary>The levelling a location at this centre will do, or null.</summary>
    public static IReadOnlyList<LevelOp>? OpsAt(Vector2 centre) => Source?.Invoke(centre);

    /// <summary>A single authored platform elevation, when all levelling
    /// operations agree. Offsets affect its footprint but not its height.
    /// Multi-level sites deliberately have no guessed common doorstep.</summary>
    public static float? PlatformHeight(float centreGround, IReadOnlyList<LevelOp>? ops)
    {
        float? height = null;
        if (ops == null) return null;
        foreach (var op in ops)
        {
            if (op.Radius <= 0f) continue;
            float candidate = centreGround + op.Height;
            if (float.IsNaN(candidate) || float.IsInfinity(candidate)) return null;
            if (height.HasValue && Mathf.Abs(candidate - height.Value) > 0.25f) return null;
            height = candidate;
        }
        return height;
    }

    /// <summary>An exterior arrival should reach the site, not descend into
    /// its excavation. Raised platforms matter, but a below-root modifier
    /// (a cave cut, well or pit) is not an instruction to lower its access road.</summary>
    public static float? ApproachHeight(float rootHeight, IReadOnlyList<LevelOp>? ops)
    {
        float? level = PlatformHeight(rootHeight, ops);
        return level.HasValue ? Mathf.Max(rootHeight, level.Value) : null;
    }

    /// <summary>
    /// The height a location will level the ground to at <paramref name="point"/>,
    /// or null when the point is outside every footprint this can place.
    ///
    /// Operations are applied in order and each sets the height outright, so
    /// where several cover a point the last one wins - the same order the game
    /// applies them in.
    /// </summary>
    public static float? GroundAt(Vector2 point, Vector2 centre, float centreGround, IReadOnlyList<LevelOp>? ops)
    {
        if (ops == null || ops.Count == 0)
            return null;

        float? height = null;
        Vector2 offset = point - centre;
        for (int i = 0; i < ops.Count; i++)
        {
            LevelOp op = ops[i];
            if (op.Radius <= 0f || !op.Concentric)
                continue;
            bool covered = op.Square
                ? Mathf.Abs(offset.x) <= op.Radius && Mathf.Abs(offset.y) <= op.Radius
                : offset.sqrMagnitude <= op.Radius * op.Radius;
            if (covered)
                height = centreGround + op.Height;
        }
        return height;
    }

    /// <summary>Where an operation stands from the location's centre once it is turned to <paramref name="yaw"/>.</summary>
    public static Vector2 OffsetAt(LevelOp op, float yaw)
    {
        float r = yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(op.OffsetX * c + op.OffsetZ * s, -op.OffsetX * s + op.OffsetZ * c);
    }

    /// <summary>
    /// The ground a placed location leaves at <paramref name="point"/>, from the
    /// <paramref name="natural"/> ground there: each concentric operation in turn
    /// levels inside its radius and blends towards its height out to its
    /// smoothing radius, as Heightmap.ApplyModifier does. Offset operations are
    /// left out (their place depends on the location's rotation), as in
    /// <see cref="GroundAt"/>. Smoothing is always round: the game ignores
    /// m_square there.
    /// </summary>
    public static float PlacedGroundAt(Vector2 point, Vector2 centre, float centreGround, IReadOnlyList<LevelOp>? ops, float natural)
    {
        if (ops == null) return natural;
        float h = natural;
        Vector2 offset = point - centre;
        float d = offset.magnitude;
        foreach (LevelOp op in ops)
        {
            if (!op.Concentric) continue;
            float level = centreGround + op.Height;
            if (op.Radius > 0f && (op.Square
                    ? Mathf.Abs(offset.x) <= op.Radius && Mathf.Abs(offset.y) <= op.Radius
                    : d <= op.Radius))
                h = level;
            if (op.SmoothRadius > 0f && d <= op.SmoothRadius)
                h = Mathf.Lerp(h, level, 1f - Mathf.Pow(d / op.SmoothRadius, op.SmoothPower));
        }
        return h;
    }
}
