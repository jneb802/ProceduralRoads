using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The structure ends met while an island's roads are planned (<see cref="RoadSpatialGrid.RemembersPlans"/>, with its pieces of
/// road): a bridge's or viaduct's ends met again by the next plan of the same road (a re-route, the last resort, an approach's
/// trial), each met afresh. <see cref="MeetEnds"/> reads the road as searched, every field of its crossings, the world, the
/// earthwork cap, the stair steps its search bought and the road's width, what the road being planned has set on this thread
/// (as a remembered piece reads it, <see cref="RoadSpatialGrid.PlanningState"/>), what its moved viaduct banks are asked
/// beside the network (the search's test of a viaduct's line, RoadPathfinder.MovedViaductAllowed: the bought jumps, the ground
/// each was bought on, the road's bans and the location clearance, RoadPathfinder.MoveAsked, keyed here), and the island's network
/// (<see cref="RoadNetworkGenerator.IslandNetworkVersion"/>); it writes the path and its crossings' fields and nothing else.
/// Asked again with all of those the same, it is answered as it was: the path as it left it, every field of each crossing
/// as it left it, and the same crossing unmet (by its place in the list). Forgotten as the network changes and as the
/// island ends.
/// </summary>
public static partial class StructureEnds
{
    [ThreadStatic] private static Dictionary<EndsAsked, EndsAnswered>? t_remembered;
    [ThreadStatic] private static int t_rememberedNetwork;
    /// <summary>The most ends remembered on one network; past it they are forgotten and remembered again.</summary>
    private const int MostRemembered = 1024;
    private static int s_recalled;
    /// <summary>Structure ends answered from memory this process (tests).</summary>
    internal static int EndsRecalled => s_recalled;

    /// <summary>Forgets every end this thread remembered, the crossings and world they hold with them: as the piece memo is
    /// turned on or off (RoadSpatialGrid.RemembersPlans), at an island's start and its end.</summary>
    internal static void ForgetRememberedEnds()
    {
        t_remembered = null;
        t_rememberedNetwork = 0;
    }

    /// <summary>Whether this thread holds remembered ends (tests).</summary>
    internal static bool HoldsRememberedEnds => t_remembered is { Count: > 0 };

    /// <summary>
    /// Every instance field of a crossing, typed: what <see cref="MeetEnds"/> may read of it, and write. Its lists are copied
    /// as it is taken and as it is put back (what they hold shared: values). Compared as the fields' own values compare
    /// (a float bit for bit, a point exactly, a list item by item). A field added to <see cref="RoadCrossing"/> is added here
    /// too (the memo's tests read every field of a crossing and fail on one this leaves out).
    /// </summary>
    internal sealed class CrossingFields
    {
        private CrossingKind m_kind;
        private FordStyle m_style;
        private int m_island, m_fromIndex, m_toIndex, m_raiseAtMost;
        private Vector2 m_fromBank, m_toBank, m_center, m_direction, m_fairwayCenter;
        private Vector2? m_fromLand, m_toLand, m_shallowSite;
        private bool m_shallow, m_shared, m_invalid, m_valleyJump, m_onTops;
        private bool m_fromEarthwork, m_toEarthwork, m_fromPad, m_toPad, m_fromStands, m_toStands, m_fromShared, m_toShared;
        private string? m_shallowNote;
        private float m_wetLength, m_width, m_waterLevel, m_riverbedHeight, m_fairwayWidth;
        private BridgeStructures.Kit? m_kit;
        private End? m_fromEnd, m_toEnd;
        private List<(RoadSpatialGrid.RoadPoint at, float to)>? m_fromRaised, m_toRaised;
        private List<(Vector2 from, Vector2 to)>? m_searched;

        internal static CrossingFields Of(RoadCrossing c) => new()
        {
            m_kind = c.Kind, m_style = c.Style, m_island = c.Island, m_fromIndex = c.FromIndex, m_toIndex = c.ToIndex,
            m_fromBank = c.FromBank, m_toBank = c.ToBank, m_fromLand = c.FromLand, m_toLand = c.ToLand,
            m_shallow = c.Shallow, m_shared = c.Shared, m_invalid = c.Invalid, m_shallowNote = c.ShallowNote,
            m_shallowSite = c.ShallowSite, m_wetLength = c.WetLength, m_valleyJump = c.ValleyJump, m_onTops = c.OnTops, m_kit = c.Kit,
            m_fromEnd = c.FromEnd, m_toEnd = c.ToEnd, m_raiseAtMost = c.RaiseAtMost,
            m_fromRaised = Copy(c.FromRaised), m_toRaised = Copy(c.ToRaised), m_searched = Copy(c.Searched),
            m_fromEarthwork = c.FromEarthwork, m_toEarthwork = c.ToEarthwork, m_fromPad = c.FromPad, m_toPad = c.ToPad,
            m_fromStands = c.FromStands, m_toStands = c.ToStands, m_fromShared = c.FromShared, m_toShared = c.ToShared,
            m_center = c.Center, m_direction = c.Direction, m_width = c.Width, m_waterLevel = c.WaterLevel,
            m_riverbedHeight = c.RiverbedHeight, m_fairwayCenter = c.FairwayCenter, m_fairwayWidth = c.FairwayWidth,
        };

        /// <summary>Sets every field of <paramref name="c"/> as taken, its lists copied again.</summary>
        internal void Put(RoadCrossing c)
        {
            c.Kind = m_kind; c.Style = m_style; c.Island = m_island; c.FromIndex = m_fromIndex; c.ToIndex = m_toIndex;
            c.FromBank = m_fromBank; c.ToBank = m_toBank; c.FromLand = m_fromLand; c.ToLand = m_toLand;
            c.Shallow = m_shallow; c.Shared = m_shared; c.Invalid = m_invalid; c.ShallowNote = m_shallowNote;
            c.ShallowSite = m_shallowSite; c.WetLength = m_wetLength; c.ValleyJump = m_valleyJump; c.OnTops = m_onTops; c.Kit = m_kit;
            c.FromEnd = m_fromEnd; c.ToEnd = m_toEnd; c.RaiseAtMost = m_raiseAtMost;
            c.FromRaised = Copy(m_fromRaised); c.ToRaised = Copy(m_toRaised); c.Searched = Copy(m_searched);
            c.FromEarthwork = m_fromEarthwork; c.ToEarthwork = m_toEarthwork; c.FromPad = m_fromPad; c.ToPad = m_toPad;
            c.FromStands = m_fromStands; c.ToStands = m_toStands; c.FromShared = m_fromShared; c.ToShared = m_toShared;
            c.Center = m_center; c.Direction = m_direction; c.Width = m_width; c.WaterLevel = m_waterLevel;
            c.RiverbedHeight = m_riverbedHeight; c.FairwayCenter = m_fairwayCenter; c.FairwayWidth = m_fairwayWidth;
        }

        internal bool Same(CrossingFields o) =>
            m_kind == o.m_kind && m_style == o.m_style && m_island == o.m_island && m_fromIndex == o.m_fromIndex && m_toIndex == o.m_toIndex
            && m_raiseAtMost == o.m_raiseAtMost
            && Exact(m_fromBank, o.m_fromBank) && Exact(m_toBank, o.m_toBank) && Exact(m_center, o.m_center)
            && Exact(m_direction, o.m_direction) && Exact(m_fairwayCenter, o.m_fairwayCenter)
            && Exact(m_fromLand, o.m_fromLand) && Exact(m_toLand, o.m_toLand) && Exact(m_shallowSite, o.m_shallowSite)
            && m_shallow == o.m_shallow && m_shared == o.m_shared && m_invalid == o.m_invalid && m_valleyJump == o.m_valleyJump
            && m_onTops == o.m_onTops
            && m_fromEarthwork == o.m_fromEarthwork && m_toEarthwork == o.m_toEarthwork && m_fromPad == o.m_fromPad && m_toPad == o.m_toPad
            && m_fromStands == o.m_fromStands && m_toStands == o.m_toStands && m_fromShared == o.m_fromShared && m_toShared == o.m_toShared
            && string.Equals(m_shallowNote, o.m_shallowNote, StringComparison.Ordinal)
            && Bits(m_wetLength, o.m_wetLength) && Bits(m_width, o.m_width) && Bits(m_waterLevel, o.m_waterLevel)
            && Bits(m_riverbedHeight, o.m_riverbedHeight) && Bits(m_fairwayWidth, o.m_fairwayWidth)
            && m_kit == o.m_kit && Exact(m_fromEnd, o.m_fromEnd) && Exact(m_toEnd, o.m_toEnd)
            && Same(m_fromRaised, o.m_fromRaised) && Same(m_toRaised, o.m_toRaised) && Same(m_searched, o.m_searched);

        private static List<T>? Copy<T>(List<T>? list) => list == null ? null : new List<T>(list);
        private static bool Bits(float a, float b) => TerrainFingerprint.Bits(a) == TerrainFingerprint.Bits(b);
        /// <summary>A point compared as its own Equals compares it: each coordinate exactly (not the game's near ==).</summary>
        private static bool Exact(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        private static bool Exact(Vector2? a, Vector2? b) => a.HasValue == b.HasValue && (a is not Vector2 u || Exact(u, b!.Value));
        /// <summary>An end compared field by field, as a struct's own Equals compares it.</summary>
        private static bool Exact(End? a, End? b) => a.HasValue == b.HasValue && (a is not End u || b is End v
            && Exact(u.Edge, v.Edge) && Exact(u.Up, v.Up) && u.Height.Equals(v.Height) && u.Steps == v.Steps && u.Road == v.Road);
        private static bool Same(List<(Vector2 from, Vector2 to)>? a, List<(Vector2 from, Vector2 to)>? b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!Exact(a[i].from, b[i].from) || !Exact(a[i].to, b[i].to)) return false;
            return true;
        }
        private static bool Same<T>(List<T>? a, List<T>? b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            var same = EqualityComparer<T>.Default;
            for (int i = 0; i < a.Count; i++) if (!same.Equals(a[i], b[i])) return false;
            return true;
        }
    }

    private static bool SameFields(CrossingFields[] a, CrossingFields[] b)
    {
        for (int i = 0; i < a.Length; i++) if (!a[i].Same(b[i])) return false;
        return true;
    }

    private static bool Same(Vector2 a, Vector2 b) => TerrainFingerprint.Bits(a.x) == TerrainFingerprint.Bits(b.x) && TerrainFingerprint.Bits(a.y) == TerrainFingerprint.Bits(b.y);

    private sealed class EndsAsked : IEquatable<EndsAsked>
    {
        public Vector2[] Path = null!;
        public CrossingFields[] Crossings = null!;
        public (Vector2 from, Vector2 to)[] Steps = null!;
        public WorldGenerator World = null!;
        public float Cap, Width;
        public RoadSpatialGrid.PlanningState State = null!;
        /// <summary>What the move test reads beside the network (RoadPathfinder.MoveAsked); null where no move is asked (a move
        /// asked without its key is met afresh, never remembered).</summary>
        public object? MoveAsked;
        public int Hash;

        public bool Equals(EndsAsked? o)
        {
            if (o is null || Hash != o.Hash || !ReferenceEquals(World, o.World) || !State.Same(o.State) || !Equals(MoveAsked, o.MoveAsked)) return false;
            if (TerrainFingerprint.Bits(Cap) != TerrainFingerprint.Bits(o.Cap) || TerrainFingerprint.Bits(Width) != TerrainFingerprint.Bits(o.Width)) return false;
            if (Path.Length != o.Path.Length || Steps.Length != o.Steps.Length || Crossings.Length != o.Crossings.Length) return false;
            for (int i = 0; i < Path.Length; i++) if (!Same(Path[i], o.Path[i])) return false;
            for (int i = 0; i < Steps.Length; i++) if (!Same(Steps[i].from, o.Steps[i].from) || !Same(Steps[i].to, o.Steps[i].to)) return false;
            return SameFields(Crossings, o.Crossings);
        }

        public override bool Equals(object? obj) => obj is EndsAsked o && Equals(o);
        public override int GetHashCode() => Hash;
    }

    private sealed class EndsAnswered
    {
        public Vector2[] Path = null!;
        public CrossingFields[] Crossings = null!;
        public int Unmet;
    }

    /// <summary><see cref="MeetEnds"/>, or, while this thread remembers its plans (<see cref="RoadSpatialGrid.RemembersPlans"/>), the
    /// same ends met already on the same network with the same asking, answered as they were met. <paramref name="steps"/>: the
    /// stair steps the search bought, which <paramref name="isStep"/> tests; <paramref name="moveAsked"/>: what
    /// <paramref name="mayMove"/> reads beside the network, compared by its Equals.</summary>
    internal static RoadCrossing? MeetEndsRemembered(List<Vector2> path, List<RoadCrossing> crossings, WorldGenerator world, float cap,
        IReadOnlyList<(Vector2 from, Vector2 to)> steps, Func<Vector2, Vector2, bool> isStep, float width,
        Func<RoadCrossing, RoadCrossing, bool>? mayMove = null, object? moveAsked = null)
    {
        if (!RoadSpatialGrid.RemembersPlans || world == null || mayMove != null && moveAsked == null)
            return MeetEnds(path, crossings, world!, cap, isStep, width, mayMove);
        int network = RoadNetworkGenerator.IslandNetworkVersion;
        var remembered = t_remembered ??= new Dictionary<EndsAsked, EndsAnswered>();
        if (network != t_rememberedNetwork) { remembered.Clear(); t_rememberedNetwork = network; }
        var asked = new EndsAsked
        {
            Path = path.ToArray(), Crossings = crossings.ConvertAll(CrossingFields.Of).ToArray(), Steps = new List<(Vector2, Vector2)>(steps).ToArray(),
            World = world, Cap = cap, Width = width, State = RoadSpatialGrid.PlanningState.Now(), MoveAsked = mayMove == null ? null : moveAsked,
        };
        unchecked
        {
            int h = asked.Path.Length * 31 + asked.Crossings.Length;
            foreach (Vector2 p in asked.Path) h = (h * 31 + TerrainFingerprint.Bits(p.x)) * 31 + TerrainFingerprint.Bits(p.y);
            foreach (RoadCrossing c in crossings) h = (h * 31 + TerrainFingerprint.Bits(c.FromBank.x)) * 31 + TerrainFingerprint.Bits(c.ToBank.y);
            asked.Hash = h;
        }
        if (remembered.TryGetValue(asked, out EndsAnswered? known))
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            System.Threading.Interlocked.Increment(ref s_recalled);
            path.Clear();
            path.AddRange(known.Path);
            for (int i = 0; i < crossings.Count; i++) known.Crossings[i].Put(crossings[i]);
            return known.Unmet >= 0 ? crossings[known.Unmet] : null;
        }
        RoadCrossing? unmet = MeetEnds(path, crossings, world, cap, isStep, width, mayMove);
        if (RoadNetworkGenerator.IslandNetworkVersion != network || unmet != null && !crossings.Contains(unmet)) return unmet;
        if (remembered.Count >= MostRemembered) remembered.Clear();
        remembered[asked] = new EndsAnswered
        {
            Path = path.ToArray(), Crossings = crossings.ConvertAll(CrossingFields.Of).ToArray(), Unmet = unmet == null ? -1 : crossings.IndexOf(unmet),
        };
        return unmet;
    }
}
