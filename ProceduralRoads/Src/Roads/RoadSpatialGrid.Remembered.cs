using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The pieces planned while an island's roads are planned (<see cref="RemembersPlans"/>), as its pathfinder remembers its
/// searches (RoadPathfinder.RemembersSearches): a structure's end met again by the next approach to it, a road planned again
/// after a refusal, the same piece of road asked of <see cref="PlanRoadPath"/> again and again, each planned it all afresh.
/// A piece reads its asking (its waypoints, width, the ground to meet at each end, whether it follows the terrain, its floor,
/// whether its locations' clearance is checked, the locations it arrives at, the world), what the road being planned has set
/// on this thread (<see cref="LocationEnds"/>, <see cref="StairStarts"/>, <see cref="HeldPoints"/>, <see cref="LevelledEnds"/>,
/// <see cref="SuppressWiggle"/>, RoadWiggle.HeldAt, RoadSwitchbacks' SuppressFillets, SuppressSwitchbacks, SuppressBends and
/// LeastBends, the last resort's pitch (RoadPitches.LastResort), a trial's plan (RoadGrade.Trial), the network as a road not
/// stored yet would leave it (RoadTerrainModifier.StoringNow)), and the island's network
/// (<see cref="RoadNetworkGenerator.IslandNetworkVersion"/>); the terrain it reads does not change in a generation, nor do the
/// settings within an island. Asked again with all of those the same, bit for bit, it is answered as it was: a copy of its
/// plan, and what it leaves on this thread as it left it (<see cref="LastRefusal"/>, <see cref="RefusedAt"/>, RoadSwitchbacks'
/// LastFilleted and, where it shaped its line, LastCurve, LastWidths, LastRefusal and LastRefusalAt; where it found one, the last climb too steep for its rests and the count of
/// them, RoadPitches.RestsRefusals, the steepest grade it records). Only pieces planned on the ground itself (no ground of the
/// caller's own, which a delegate gives and cannot be compared). Forgotten as the network changes and as the island ends.
/// Not remembered: the log's lines and the generation's tallies of the shaping and the sway.
/// </summary>
public static partial class RoadSpatialGrid
{
    /// <summary>On while an island's roads are planned on this thread (RoadNetworkGenerator.GenerateRoutedRoads, where the
    /// generation keeps them: GenerationMemory.KeepsPlans); off, every piece is planned afresh. Turning it on or off forgets
    /// what was remembered, the structure ends met with it too (StructureEnds.ForgetRememberedEnds): at an island's end
    /// nothing of it (no world, no crossing) is left on an idle thread.</summary>
    internal static bool RemembersPlans
    {
        get => t_remembers;
        set { t_remembers = value; t_remembered = null; t_rememberedNetwork = 0; StructureEnds.ForgetRememberedEnds(); }
    }

    /// <summary>Whether this thread holds remembered pieces (tests).</summary>
    internal static bool HoldsRememberedPieces => t_remembered is { Count: > 0 };

    [System.ThreadStatic] private static bool t_remembers;
    [System.ThreadStatic] private static Dictionary<PieceAsked, PieceAnswered>? t_remembered;
    [System.ThreadStatic] private static int t_rememberedNetwork;
    /// <summary>The most pieces remembered on one network; past it they are forgotten and remembered again.</summary>
    private const int MostRemembered = 4096;

    private static int s_recalled;
    /// <summary>Pieces answered from memory this process (tests, the timing summary).</summary>
    internal static int PiecesRecalled => s_recalled;

    private static int Bits(float v) => TerrainFingerprint.Bits(v);
    private static bool Same(float a, float b) => Bits(a) == Bits(b);
    private static bool Same(float? a, float? b) => a.HasValue == b.HasValue && (!a.HasValue || Same(a.Value, b!.Value));
    private static bool Same(Vector2 a, Vector2 b) => Same(a.x, b.x) && Same(a.y, b.y);
    private static bool Same(Vector2? a, Vector2? b) => a.HasValue == b.HasValue && (!a.HasValue || Same(a.Value, b!.Value));
    private static bool Same(Vector2[]? a, Vector2[]? b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!Same(a[i], b[i])) return false;
        return true;
    }

    /// <summary>What the road being planned has set on this thread that a piece of it, and the meeting of its structures' ends
    /// (StructureEnds.MeetEndsRemembered), read: <see cref="LocationEnds"/>, <see cref="StairStarts"/>, <see cref="HeldPoints"/>,
    /// <see cref="LevelledEnds"/>, <see cref="SuppressWiggle"/>, <see cref="ShapedBeforeCrossings"/>, RoadWiggle.HeldAt, RoadSwitchbacks' SuppressFillets,
    /// SuppressSwitchbacks, SuppressBends and LeastBends, the last resort (RoadPitches.LastResort), a trial (RoadGrade.InTrial)
    /// and the network as a road not stored yet would leave it (RoadTerrainModifier.StoringNow, by identity). A staircase end's
    /// carry (RoadStairRuns' CarryKey) is keyed by it too, as the carry's road is one such piece.</summary>
    internal sealed class PlanningState
    {
        private Vector2[]? m_held, m_wiggleHeld;
        private (Vector2 at, float height)[]? m_levelled;
        private object? m_storing;
        internal int Flags;

        internal static PlanningState Now() => new()
        {
            m_held = HeldPoints?.ToArray(), m_wiggleHeld = RoadWiggle.HeldAt?.ToArray(), m_levelled = LevelledEnds?.ToArray(),
            m_storing = RoadTerrainModifier.StoringNow, Flags = FlagsNow(),
        };

        /// <summary>The switches of what the road being planned has set on this thread (<see cref="Flags"/>), the last resort and
        /// a trial among them.</summary>
        private static int FlagsNow() =>
            (LocationEnds.start ? 1 : 0) | (LocationEnds.end ? 2 : 0) | (StairStarts.start ? 4 : 0) | (StairStarts.end ? 8 : 0)
                | (SuppressWiggle ? 16 : 0) | (RoadSwitchbacks.SuppressFillets ? 32 : 0) | (RoadSwitchbacks.SuppressSwitchbacks ? 64 : 0)
                | (RoadSwitchbacks.SuppressBends ? 128 : 0) | (RoadSwitchbacks.LeastBends ? 256 : 0) | (RoadPitches.LastResort ? 512 : 0)
                | (RoadGrade.InTrial ? 1024 : 0) | (ShapedBeforeCrossings ? 2048 : 0);

        internal bool Same(PlanningState o)
        {
            if (Flags != o.Flags || !ReferenceEquals(m_storing, o.m_storing)) return false;
            if (!RoadSpatialGrid.Same(m_held, o.m_held) || !RoadSpatialGrid.Same(m_wiggleHeld, o.m_wiggleHeld)) return false;
            if (m_levelled == null || o.m_levelled == null) return m_levelled == o.m_levelled;
            if (m_levelled.Length != o.m_levelled.Length) return false;
            for (int i = 0; i < m_levelled.Length; i++)
                if (!RoadSpatialGrid.Same(m_levelled[i].at, o.m_levelled[i].at) || !RoadSpatialGrid.Same(m_levelled[i].height, o.m_levelled[i].height)) return false;
            return true;
        }
    }

    /// <summary>What a piece reads besides the terrain, held to compare bit for bit.</summary>
    private sealed class PieceAsked : System.IEquatable<PieceAsked>
    {
        public Vector2[] Path = null!;
        public float Width, MinHeight;
        public float? StartGround, EndGround;
        public Vector2? LocationStart, LocationEnd;
        public WorldGenerator World = null!;
        public bool FollowTerrain, CheckLocations;
        public PlanningState State = null!;
        public int Hash;

        public bool Equals(PieceAsked? o)
        {
            if (o is null || Hash != o.Hash || FollowTerrain != o.FollowTerrain || CheckLocations != o.CheckLocations || !ReferenceEquals(World, o.World)) return false;
            if (!Same(Width, o.Width) || !Same(MinHeight, o.MinHeight) || !Same(StartGround, o.StartGround) || !Same(EndGround, o.EndGround)) return false;
            if (!Same(LocationStart, o.LocationStart) || !Same(LocationEnd, o.LocationEnd)) return false;
            return Same(Path, o.Path) && State.Same(o.State);
        }

        public override bool Equals(object? obj) => obj is PieceAsked o && Equals(o);
        public override int GetHashCode() => Hash;
    }

    private sealed class PieceAnswered
    {
        public PlannedPath? Plan;
        public string? LastRefusal;
        public Vector2? RefusedAt;
        public int Filleted;
        /// <summary>Whether it shaped its line (RoadSwitchbacks.Shape, which rewrites the Last* below; a piece that follows the
        /// terrain does not), and whether it found a climb too steep for its rests (which rewrites the last one).</summary>
        public bool Shaped, Restless;
        public List<bool>? Curve;
        public List<float>? Widths;
        public string? TurnRefusal;
        public Vector2? TurnRefusedAt;
        public List<Vector2>? TooSteepForRests;
        public int RestsRefusals;
        public float Steepest;
    }

    private static PieceAsked Asking(List<Vector2> path, float width, WorldGenerator worldGen, float? startGround, float? endGround,
        bool followTerrain, float minHeight, bool checkLocations, RoadLocationProtection.Targets locations)
    {
        var asked = new PieceAsked
        {
            Path = path.ToArray(), Width = width, MinHeight = minHeight, StartGround = startGround, EndGround = endGround,
            LocationStart = locations.Start, LocationEnd = locations.End, World = worldGen, FollowTerrain = followTerrain, CheckLocations = checkLocations,
            State = PlanningState.Now(),
        };
        unchecked
        {
            int h = (asked.State.Flags * 4 + (followTerrain ? 1 : 0) + (checkLocations ? 2 : 0)) * 31 + asked.Path.Length;
            foreach (Vector2 p in asked.Path) h = (h * 31 + TerrainFingerprint.Bits(p.x)) * 31 + TerrainFingerprint.Bits(p.y);
            h = h * 31 + TerrainFingerprint.Bits(width);
            if (startGround is float s) h = h * 31 + TerrainFingerprint.Bits(s);
            if (endGround is float e) h = h * 31 + TerrainFingerprint.Bits(e);
            asked.Hash = h;
        }
        return asked;
    }

    /// <summary>The piece (<see cref="PlanRoadPathFresh"/>), or, while this thread remembers its pieces (<see cref="RemembersPlans"/>)
    /// and the piece is planned on the ground itself, the one planned already on the same network with the same asking,
    /// answered as it was.</summary>
    private static PlannedPath? PlanRoadPath(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround, float? endGround, bool followTerrain, float minHeight,
        System.Func<Vector2, float>? terrainHeight, bool checkLocations, RoadLocationProtection.Targets locations)
    {
        if (!t_remembers || terrainHeight != null || path == null || worldGen == null)
            return PlanRoadPathFresh(path!, width, worldGen!, startGround, endGround, followTerrain, minHeight, terrainHeight, checkLocations, locations);
        int network = RoadNetworkGenerator.IslandNetworkVersion;
        var remembered = t_remembered ??= new Dictionary<PieceAsked, PieceAnswered>();
        if (network != t_rememberedNetwork) { remembered.Clear(); t_rememberedNetwork = network; }
        PieceAsked asked = Asking(path, width, worldGen, startGround, endGround, followTerrain, minHeight, checkLocations, locations);
        if (remembered.TryGetValue(asked, out PieceAnswered? known)) return Replay(known);

        int rests = RoadPitches.RestsRefusals, shapes = RoadSwitchbacks.Shapes;
        List<Vector2>? restless = RoadPitches.t_tooSteepForRests;
        float steepestBefore = RoadGrade.RecordedSteepest;
        RoadGrade.RecordedSteepest = float.NegativeInfinity;
        PlannedPath? plan;
        float steepest;
        try { plan = PlanRoadPathFresh(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight, checkLocations, locations); }
        finally { steepest = RoadGrade.RecordedSteepest; RoadGrade.RecordedSteepest = Mathf.Max(steepestBefore, steepest); }
        // A piece that wrote to the network (none does) or met another network is not remembered.
        if (RoadNetworkGenerator.IslandNetworkVersion != network) return plan;
        if (remembered.Count >= MostRemembered) remembered.Clear();
        remembered[asked] = new PieceAnswered
        {
            Plan = plan?.Copy(), LastRefusal = LastRefusal, RefusedAt = RefusedAt,
            Filleted = RoadSwitchbacks.LastFilleted, Shaped = RoadSwitchbacks.Shapes != shapes,
            Restless = !ReferenceEquals(RoadPitches.t_tooSteepForRests, restless), Curve = Copy(RoadSwitchbacks.LastCurve), Widths = Copy(RoadSwitchbacks.LastWidths),
            TurnRefusal = RoadSwitchbacks.LastRefusal, TurnRefusedAt = RoadSwitchbacks.LastRefusalAt,
            TooSteepForRests = Copy(RoadPitches.t_tooSteepForRests), RestsRefusals = RoadPitches.RestsRefusals - rests, Steepest = steepest,
        };
        return plan;
    }

    private static List<T>? Copy<T>(List<T>? list) => list == null ? null : new List<T>(list);

    /// <summary>A remembered piece, answered as it was planned: what it leaves on this thread set as it left them.</summary>
    private static PlannedPath? Replay(PieceAnswered known)
    {
        RoadLifecycleManager.ThrowIfCancellationRequested();
        System.Threading.Interlocked.Increment(ref s_recalled);
        LastRefusal = known.LastRefusal;
        RefusedAt = known.RefusedAt;
        RoadSwitchbacks.LastFilleted = known.Filleted;
        if (known.Shaped)
        {
            RoadSwitchbacks.Shapes++;
            RoadSwitchbacks.LastCurve = Copy(known.Curve);
            RoadSwitchbacks.LastWidths = Copy(known.Widths);
            RoadSwitchbacks.LastRefusal = known.TurnRefusal;
            RoadSwitchbacks.LastRefusalAt = known.TurnRefusedAt;
        }
        if (known.Restless) RoadPitches.t_tooSteepForRests = Copy(known.TooSteepForRests);
        RoadPitches.CountRestsRefusals(known.RestsRefusals);
        if (!float.IsNegativeInfinity(known.Steepest)) RoadGrade.RecordSteepest(known.Steepest);
        return known.Plan?.Copy();
    }
}
