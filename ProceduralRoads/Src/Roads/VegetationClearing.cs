using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Trees and rocks on the roads of zones that were generated before the road.
///
/// The game keeps vegetation off a road only while it generates a zone: the
/// road's clear areas are handed to PlaceVegetation, and nothing is placed
/// inside them. A zone generated earlier -- before the mod was installed, or
/// before this network existed -- already has everything it grew, as
/// persistent objects, and nothing ever takes them away. So the server
/// removes, once per zone and network, exactly what generation would have left
/// out: objects of the game's own vegetation prefabs, inside the same clear
/// areas, tested the way the game tests them (ZoneSystem.InsideClearArea, an
/// axis-aligned square), and the grown trees whose trunks reach the road
/// (<see cref="TrunkReachesRoad"/>). Anything that is not a vegetation prefab -- player
/// builds, containers, bridge pieces -- is never touched, and objects within a
/// location's exterior radius that may be its own parts are left to the location. The game's vegetation
/// list also holds the large rocks; on a road those the rock clearing takes
/// there belong to it (<see cref="LeftToRockClearing"/>), so the
/// rock clearing carves them, and a structure's
/// clear areas keep every rock out. Small stones (the pickable ones) are
/// vegetation and go with the trees.
///
/// Pure logic; ServerTerrainBake finds and removes the objects.
/// </summary>
public static class VegetationClearing
{
    /// <summary>A clear area as the game tests it: an axis-aligned square of half-size Radius.</summary>
    public readonly struct Area
    {
        public readonly Vector2 Centre;
        public readonly float Radius;
        /// <summary>A staircase's area: it keeps vegetation out, but a rock the rock clearing takes there is left to it, which
        /// clears only the staircase's walking box (StairLayout.AddClearance) as it clears a road's.</summary>
        public readonly bool LeavesRocks;

        public Area(Vector2 centre, float radius, bool leavesRocks = false)
        {
            Centre = centre;
            Radius = radius;
            LeavesRocks = leavesRocks;
        }
    }

    /// <summary>A location's footprint: a circle of its exterior radius (a player's ground: a circle round their object).</summary>
    public readonly struct Footprint
    {
        public readonly Vector2 Centre;
        public readonly float Radius;
        /// <summary>For a location: whether an object standing at a point is one of its own parts
        /// (LocationPrefabLevelling.IsPart), null where that is not known. Without it, or where it is not known, everything in
        /// the footprint is taken to be the location's.</summary>
        public readonly Func<Vector3, bool?>? PartAt;

        public Footprint(Vector2 centre, float radius, Func<Vector3, bool?>? partAt = null)
        {
            Centre = centre;
            Radius = radius;
            PartAt = partAt;
        }
    }

    /// <summary>
    /// How far around a player's own object its vegetation is left alone. A
    /// planted grove stands beside the thing it was planted for -- a hut, a
    /// fence, a workbench -- so ground a player has built on is treated as
    /// theirs. Leaving a natural tree on the road is a blemish; taking a
    /// planted one is somebody's work gone.
    /// </summary>
    public const float PlayerGroundRadius = 8f;

    /// <summary>A road point as the grown-tree rule reads it: its centre and half its width, and the road's height there
    /// (NaN where it is not known; only the armour a road runs through reads it, <see cref="Lie"/>).</summary>
    public readonly struct RoadCentre
    {
        public readonly Vector2 Centre;
        public readonly float HalfWidth;
        public readonly float Height;

        public RoadCentre(Vector2 centre, float halfWidth, float height = float.NaN)
        {
            Centre = centre;
            HalfWidth = halfWidth;
            Height = height;
        }
    }

    /// <summary>How the game laid one lying along the ground: its tilt about its own x and z, in degrees (Euler angles as
    /// the game gives them: PlaceVegetation's draws, or a saved rotation's, 0 to 360), and the height of its origin.</summary>
    public readonly struct Lean
    {
        public readonly float TiltX, TiltZ, Origin;

        public Lean(float tiltX, float tiltZ, float origin)
        {
            TiltX = tiltX;
            TiltZ = tiltZ;
            Origin = origin;
        }
    }

    // ---- grown trees ----
    //
    // The clear areas test an object's origin and keep the middle 90 % of the road clear
    // (RoadConstants.VegetationClearMultiplier): bushes, saplings, small trees and small stones may stand on the outer
    // edge. A grown tree is wider than that edge: one whose origin stands just past the clear area keeps a metre or more
    // of trunk over the road. So a grown tree goes when its trunk reaches the road: its origin is nearer a road point
    // than half the road's width, plus the trunk's reach at the tree's scale, plus TrunkMargin.
    //
    // A grown tree is a vegetation entry that grows to full height: every TreeBase prefab in the game's vegetation lists
    // (chopped, it falls as a log), and the swamp's indestructible SwampTree2. The small ones -- Beech_small*,
    // FirTree_small*, YggaShoot_small1, the bushes and stumps -- are Destructibles and stay creep.
    //
    // Trunk reach: the furthest a solid collider of the prefab (viewblock meshes left out) stands from its origin across
    // the ground, between 0.25 m and 2 m up -- the height a player walks into -- at scale 1, measured on the game's
    // prefabs. A placed tree's scale replaces the prefab's own (ZoneSystem.PlaceVegetation), so the reach is multiplied
    // by the tree's scale. It is the reach in the trunk's widest direction, roots and flare included, whichever way the
    // road lies. The lean the game gives a tree (up to its entry's m_randTilt) and the canopy are not counted: a leaning
    // trunk may still overhang the road above head height.
    private static readonly Dictionary<string, float> s_trunkReach = new()
    {
        ["Beech1"] = 0.72f,
        ["Birch1"] = 0.76f,
        ["Birch1_aut"] = 0.76f,
        ["Birch2"] = 0.84f,
        ["Birch2_aut"] = 0.84f,
        ["Oak1"] = 2.06f,
        ["FirTree"] = 0.4f,
        ["Pinetree_01"] = 0.33f,
        ["Pinetree_Snow"] = 0.33f,
        ["Pinetree_Snow_dead"] = 0.33f,
        ["SnowFirTree"] = 1f,
        ["SnowFirTree 2"] = 1f,
        ["SwampTree1"] = 2.41f,
        ["SwampTree2"] = 3.04f,
        ["SwampTree2_darkland"] = 3.04f,
        ["YggaShoot1"] = 1.41f,
        ["YggaShoot2"] = 1.41f,
        ["YggaShoot3"] = 1.41f,
        ["AshlandsTree1_0"] = 0.75f,
        ["AshlandsTree3"] = 0.5f,
        ["AshlandsTree6"] = 4.32f,
    };

    private static readonly Dictionary<int, float> s_trunkReachByHash =
        s_trunkReach.ToDictionary(tree => tree.Key.GetStableHashCode(), tree => tree.Value);

    /// <summary>How far past the road's edge a grown tree's trunk is kept.</summary>
    public const float TrunkMargin = 0.25f;

    /// <summary>The largest scale the game's vegetation lists give a grown tree (SwampTree2_darkland, 3 to 5).</summary>
    public const float LargestTreeScale = 5f;

    /// <summary>How far past a road point's half-width a grown tree can stand and still reach it, or a lying one
    /// (<see cref="Lying"/>) lie over it: the widest trunk at the largest scale, or the furthest a lying one reaches. A zone's
    /// trees are tested against the road points this far beyond it too.</summary>
    public static float TreeReach => s_treeReach ??= Mathf.Max(s_trunkReach.Values.Max() * LargestTreeScale + TrunkMargin, LyingReach);
    private static float? s_treeReach;

    // ---- lying vegetation ----
    //
    // A fallen log, a rooted stump or a Mistlands root lies along the ground: its origin may stand well off the road while
    // its length lies across it (swamp logs blocking roads, then the Mistlands roots). Each is judged by its
    // footprint seen from above, boxes along its length (its local z, or x for the root) in metres at scale 1, measured on
    // the game's prefabs (the mesh collider, through the prefab's own child transforms). Where the footprint at the yaw the
    // game drew crosses the road, the nearest yaw that keeps it off the road is taken instead, so the road seems to pass
    // beside it (LyingYawStep at a time, up to half a turn either way); where none does, it is removed. The lean the game
    // gives a log (up to its entry's m_randTilt) is not counted: it only shortens the footprint. Each comes with the largest
    // scale the game's vegetation lists give it.
    private static readonly Dictionary<string, (float largestScale, (float zLo, float zHi, float xLo, float xHi)[] boxes)> s_lying = new()
    {
        // The swamp's giant dead tree lying down (indestructible; scale 1.5 to 2: 36 to 48 m long), trunk and branches.
        ["SwampTree2_log"] = (2f, new[]
        {
            (-14.5f, -12.5f, -3.3f, 0.4f), (-12.5f, -10.5f, -2.4f, 1.8f), (-10.5f, -8.5f, -1.9f, 0.4f), (-8.5f, -6.4f, -5.4f, 1.3f),
            (-6.4f, -4.4f, -4.2f, 1.7f), (-4.4f, -2.4f, -1.0f, 1.9f), (-2.4f, -0.4f, -0.8f, 1.5f), (-0.4f, 1.6f, -1.3f, 2.0f),
            (1.6f, 3.6f, -0.2f, 2.6f), (3.6f, 5.6f, 0.2f, 2.6f), (5.6f, 7.6f, -1.5f, 2.9f), (7.6f, 9.7f, -2.6f, 4.0f),
        }),
        // An old fallen fir (the swamp and the Black Forest; scale 1 to 1.2): 7 m by 1.2 m.
        ["FirTree_oldLog"] = (1.2f, new[] { (-3.5f, 3.5f, -0.6f, 0.6f) }),
        // The swamp's rotten stump and its roots (scale 1 to 2).
        ["stubbe"] = (2f, new[] { (-3.1f, -1.9f, -0.1f, 0.6f), (-1.9f, -0.6f, -0.5f, 0.9f), (-0.6f, 0.6f, -3.0f, 3.9f), (0.6f, 1.8f, -0.3f, 2.9f) }),
        // The Mistlands' ancient root, the one sap is drawn from (indestructible; scale 1 to 1.5), 28 m by up to 14 m with
        // the branch to one side. Its ends dive up to 11 m into the ground: what lies more than 2 m below the ground at its
        // origin is left out, buried wherever the ground beside a road does not fall away steeply.
        ["YggdrasilRoot"] = (1.5f, new[]
        {
            (-2.6f, 2.2f, -15.9f, -13.9f), (-2.6f, 3.2f, -13.9f, -11.9f), (-3.1f, 2.9f, -11.9f, -9.9f), (-4.1f, 2.6f, -9.9f, -7.9f),
            (-4.8f, 2.2f, -7.9f, -5.9f), (-4.8f, 1.9f, -5.9f, -3.9f), (-4.7f, 2.0f, -3.9f, -1.9f), (-5.9f, 2.1f, -1.9f, 0.1f),
            (-8.5f, 2.0f, 0.1f, 2.1f), (-10.5f, 1.9f, 2.1f, 4.1f), (-10.6f, 2.3f, 4.1f, 6.1f), (-10.3f, 2.7f, 6.1f, 8.1f),
            (-2.8f, 2.9f, 8.1f, 10.1f), (-2.7f, 1.8f, 10.1f, 12.1f),
        }),
        // The Mistlands' ancient armour (scale 1): two giant helmets, up to 10 m tall, and a rib cage 19 m long.
        // The game tilts each by up to its entry's m_randTilt about its x and z (33 degrees for the helmets, 10 for the
        // ribs), so each footprint is the union over every tilt the game can draw (a 2.75 degree grid for the helmets,
        // 0.83 for the ribs, each widened by what a tilt between grid points can move: 0.5 m, 0.2 m), of what stands
        // higher than half a metre below the ground at its origin.
        ["giant_helmet1"] = (1f, new[]
        {
            (-7.9f, -6f, -4.6f, 4.6f), (-6f, -4f, -6.1f, 6.1f), (-4f, -2f, -6.6f, 6.1f), (-2f, 0f, -6.6f, 6.1f),
            (0f, 2f, -6.6f, 5.9f), (2f, 4f, -6.6f, 5.9f), (4f, 6f, -5.9f, 5.4f), (6f, 8.1f, -4.1f, 4.1f),
        }),
        ["giant_helmet2"] = (1f, new[]
        {
            (-4.9f, -3f, -5.6f, 5.6f), (-3f, -1f, -5.9f, 6.6f), (-1f, 1f, -6.1f, 6.6f), (1f, 3f, -5.9f, 6.6f),
            (3f, 5f, -5.1f, 5.4f), (5f, 6.4f, -3.4f, 3.4f),
        }),
        ["giant_ribs"] = (1f, new[]
        {
            (-10.1f, -8f, -5.8f, 5.8f), (-8f, -6f, -7.6f, 7.6f), (-6f, -4f, -8.1f, 8.1f), (-4f, -2f, -8.6f, 8.6f),
            (-2f, 0f, -8.8f, 8.8f), (0f, 2f, -8.8f, 8.8f), (2f, 4f, -8.8f, 8.8f), (4f, 6f, -8.6f, 8.6f),
            (6f, 8f, -8.6f, 8.6f), (8f, 10f, -8.6f, 8.6f), (10f, 10.6f, -6.8f, 6.8f),
        }),
    };

    // ---- armour a road runs through ----
    //
    // A player can walk through the ancient armour at some tilts, so armour across a road is
    // first turned so the road runs through its opening, and only then beside it, or removed. An opening is a straight walk
    // through the piece: a direction in its own frame, the yaw the game turns it by left out (phi, degrees from its local z
    // towards its local x), and the offsets across it (sLo to sHi, metres) at which a player standing on the ground walks
    // the whole way through touching nothing. Measured on the game's prefabs (the mesh collider through the prefab's child
    // transform) in the Unity Editor with the physics engine's own capsule test: the player's capsule (0.49 m radius,
    // 1.85 m tall) on ground half a metre higher or lower than the ground at the piece's origin, so a road within half a
    // metre of that ground (ThroughHeight), at 0.1 m spacing, along every whole degree of direction.
    //
    // The opening moves with the tilt. The tilts the game draws are cut into 12 by 12 cells; each cell's walk is the
    // widest one clear at every tilt of a grid of 3 by 3 tilts over the cell, the capsule's radius widened by what a tilt
    // between grid points can move the piece (the grid's spacing in radians times the capsule's furthest point from the
    // origin). A cell with no walk listed has none. Each entry: tilt cell about x, about z (0 at the most negative tilt),
    // then phi, sLo, sHi.
    private static readonly Dictionary<string, (float originBelowGround, float tiltRange, (int x, int z, float phi, float sLo, float sHi)[] walks)> s_openings = new()
    {
        // Open only tilted back about x (its first seven cells).
        ["giant_helmet1"] = (0f, 33f, new[]
        {
            (0, 0, 124f, -3.15f, -1.15f), (0, 1, 127f, -2.95f, -1.05f), (0, 2, 130f, -2.75f, -0.95f), (0, 3, 132f, -2.65f, -0.9f),
            (0, 4, 134f, -2.5f, -0.95f), (0, 5, 136f, -2.4f, -1f), (0, 6, 138f, -2.25f, -1.05f), (0, 7, 140f, -2.1f, -1.15f),
            (0, 8, 141f, -2f, -1.2f), (0, 9, 142f, -1.95f, -1.2f), (0, 10, 143f, -1.85f, -1.2f), (0, 11, 143f, -1.85f, -1.25f),
            (1, 0, 125f, -3.05f, -1.15f), (1, 1, 127f, -2.9f, -1.1f), (1, 2, 129f, -2.8f, -1.05f), (1, 3, 132f, -2.6f, -0.95f),
            (1, 4, 134f, -2.4f, -0.95f), (1, 5, 136f, -2.3f, -1f), (1, 6, 137f, -2.2f, -1.05f), (1, 7, 137f, -2.2f, -1.15f),
            (1, 8, 138f, -2.1f, -1.1f), (1, 9, 138f, -2.05f, -1.15f),
            (2, 0, 125f, -3.05f, -1.3f), (2, 1, 127f, -2.95f, -1.2f), (2, 2, 129f, -2.8f, -1.1f), (2, 3, 131f, -2.65f, -1.05f),
            (2, 4, 133f, -2.5f, -1.05f), (2, 5, 135f, -2.35f, -0.95f), (2, 6, 135f, -2.25f, -1.05f), (2, 7, 136f, -2.1f, -0.95f),
            (3, 0, 125f, -3.15f, -1.45f), (3, 1, 127f, -3f, -1.25f), (3, 2, 129f, -2.85f, -1.15f), (3, 3, 131f, -2.75f, -1.1f),
            (3, 4, 133f, -2.5f, -0.95f), (3, 5, 135f, -2.05f, -1.05f),
            (4, 0, 124f, -3.35f, -1.65f), (4, 1, 128f, -3.05f, -1.4f), (4, 2, 130f, -2.85f, -1.35f), (4, 3, 132f, -2.4f, -1.35f),
            (5, 0, 127f, -3.15f, -1.8f), (5, 1, 129f, -2.8f, -1.7f), (5, 2, 133f, -2f, -1.75f),
            (6, 0, 131f, -2.4f, -2.25f),
        }),
        // The game sinks it a metre (its entry's m_groundOffset); open only at the steepest tilts.
        ["giant_helmet2"] = (1f, 33f, new[]
        {
            (0, 0, 140f, -0.5f, 1.45f), (0, 1, 145f, -0.15f, 1.55f), (0, 2, 157f, 0.55f, 1.8f), (0, 3, 156f, 0.8f, 1.6f),
            (0, 8, 56f, -0.2f, -0.1f), (0, 9, 35f, 0.35f, 1.4f), (0, 10, 37f, -0.05f, 1.5f), (0, 11, 42f, -0.5f, 1.35f),
            (1, 0, 144f, -0.4f, 1.4f), (1, 1, 153f, 0.2f, 1.55f), (1, 2, 163f, 1f, 1.75f), (1, 9, 33f, 0.7f, 1.15f),
            (1, 10, 38f, 0f, 1.1f), (1, 11, 45f, -0.6f, 0.95f),
            (2, 0, 146f, -0.25f, 1.1f), (2, 1, 157f, 0.65f, 1.35f), (2, 10, 34f, 0.45f, 0.9f), (2, 11, 38f, -0.15f, 0.95f),
            (3, 0, 146f, -0.05f, 0.6f), (3, 11, 46f, -0.35f, 0.05f),
            (10, 11, 113f, 0.15f, 0.8f), (11, 11, 113f, 0f, 1.1f),
        }),
        // Along the cage, or slanting out through its side, at the tilts back about x.
        ["giant_ribs"] = (0f, 10f, new[]
        {
            (0, 0, 15f, 2.2f, 3.1f), (0, 1, 14f, 2.05f, 2.9f), (0, 2, 13f, 2.05f, 2.55f), (0, 3, 179f, -0.2f, 0.05f),
            (0, 4, 179f, -0.2f, -0.05f), (0, 5, 179f, -0.15f, 0f), (0, 6, 179f, -0.2f, -0.05f), (0, 7, 179f, -0.2f, -0.05f),
            (0, 8, 1f, -0.2f, 0.05f), (0, 9, 168f, 2.05f, 2.55f), (0, 10, 166f, 2.05f, 2.9f), (0, 11, 166f, 2.1f, 3f),
            (1, 0, 14f, 2.05f, 2.75f), (1, 1, 15f, 2.2f, 2.85f), (1, 2, 15f, 2.15f, 2.8f), (1, 3, 14f, 2.25f, 2.6f),
            (1, 4, 179f, -0.2f, -0.05f), (1, 5, 179f, -0.15f, 0f), (1, 6, 179f, -0.2f, -0.05f), (1, 7, 179f, -0.2f, -0.05f),
            (1, 8, 167f, 2.15f, 2.5f), (1, 9, 165f, 2.15f, 2.8f), (1, 10, 165f, 2.2f, 2.85f), (1, 11, 166f, 2.05f, 2.75f),
            (2, 0, 15f, 2.2f, 2.8f), (2, 1, 15f, 2.2f, 2.8f), (2, 2, 17f, 2.45f, 3.05f), (2, 3, 16f, 2.4f, 2.85f),
            (2, 4, 15f, 2.4f, 2.65f), (2, 5, 179f, -0.2f, -0.05f), (2, 6, 179f, -0.2f, -0.05f), (2, 7, 165f, 2.4f, 2.65f),
            (2, 8, 164f, 2.4f, 2.85f), (2, 9, 163f, 2.45f, 3.05f), (2, 10, 165f, 2.2f, 2.8f), (2, 11, 166f, 2.05f, 2.65f),
            (3, 0, 179f, 0f, 0.1f), (3, 1, 179f, -0.05f, 0.05f), (3, 2, 0f, 0f, 0.1f), (3, 3, 0f, 0f, 0.1f),
            (3, 4, 0f, 0f, 0.1f), (3, 5, 0f, -0.1f, 0.1f), (3, 6, 0f, -0.1f, 0.1f), (3, 7, 0f, -0.1f, 0f),
            (3, 8, 0f, -0.1f, 0f), (3, 9, 0f, -0.1f, 0f), (3, 10, 1f, -0.05f, 0.05f), (3, 11, 1f, 0f, 0.1f),
        }),
    };

    /// <summary>The tilt cells across each axis of <see cref="s_openings"/>.</summary>
    private const int TiltCells = 12;
    /// <summary>How far above or below the ground at an armour piece's origin a road may run through it: the band its
    /// openings were measured for.</summary>
    public const float ThroughHeight = 0.5f;
    /// <summary>The turns tried for a road through armour, nearest the drawn yaw first, either way.</summary>
    public const float ThroughYawStep = 1f;

    /// <summary>Each armour piece by its prefab's hash: the ground's height above its origin, the tilt its cells span either
    /// way, and each cell's walk (NaN phi: none).</summary>
    private static readonly Dictionary<int, (float originBelowGround, float tiltRange, (float phi, float sLo, float sHi)[,] walks)> s_openingsByHash =
        s_openings.ToDictionary(opening => opening.Key.GetStableHashCode(), opening =>
        {
            var walks = new (float phi, float sLo, float sHi)[TiltCells, TiltCells];
            for (int i = 0; i < TiltCells; i++)
                for (int j = 0; j < TiltCells; j++)
                    walks[i, j] = (float.NaN, 0f, 0f);
            foreach (var w in opening.Value.walks) walks[w.x, w.z] = (w.phi, w.sLo, w.sHi);
            return (opening.Value.originBelowGround, opening.Value.tiltRange, walks);
        });

    /// <summary>The walk through <paramref name="prefab"/> laid at <paramref name="lean"/>, and the ground at its origin;
    /// false where it is not armour, or has none at that tilt.</summary>
    private static bool WalkThrough(int prefab, Lean lean, out (float phi, float sLo, float sHi) walk, out float ground)
    {
        walk = default;
        ground = float.NaN;
        if (!s_openingsByHash.TryGetValue(prefab, out var opening)) return false;
        int x = TiltCell(lean.TiltX, opening.tiltRange), z = TiltCell(lean.TiltZ, opening.tiltRange);
        if (x < 0 || z < 0 || float.IsNaN(opening.walks[x, z].phi)) return false;
        walk = opening.walks[x, z];
        ground = lean.Origin + opening.originBelowGround;
        return !float.IsNaN(ground);
    }

    /// <summary>The cell a tilt (Euler degrees, 0 to 360 or signed) falls in; -1 if it is not known or past the range.</summary>
    private static int TiltCell(float tilt, float range)
    {
        if (float.IsNaN(tilt)) return -1;
        float signed = (tilt % 360f + 540f) % 360f - 180f;
        if (signed < -range - 0.01f || signed > range + 0.01f) return -1;
        return Mathf.Clamp(Mathf.FloorToInt((signed + range) / (2f * range / TiltCells)), 0, TiltCells - 1);
    }

    /// <summary>Each by its prefab's hash, with the furthest its footprint reaches from its origin along and across, at scale 1.</summary>
    private static readonly Dictionary<int, (string name, (float zLo, float zHi, float xLo, float xHi)[] boxes, float along, float across)> s_lyingByHash =
        s_lying.ToDictionary(lying => lying.Key.GetStableHashCode(),
            lying => (lying.Key, lying.Value.boxes, lying.Value.boxes.Max(b => Mathf.Max(-b.zLo, b.zHi)), lying.Value.boxes.Max(b => Mathf.Max(-b.xLo, b.xHi))));

    private static float Furthest((float zLo, float zHi, float xLo, float xHi)[] boxes) =>
        boxes.Max(b => Mathf.Sqrt(Mathf.Max(b.zLo * b.zLo, b.zHi * b.zHi) + Mathf.Max(b.xLo * b.xLo, b.xHi * b.xHi)));

    /// <summary>How far past the road's edge a lying one is kept.</summary>
    public const float LyingMargin = 0.5f;
    /// <summary>The turns tried, nearest the drawn yaw first, either way.</summary>
    public const float LyingYawStep = 5f;
    /// <summary>The furthest any lying one reaches from its origin at its largest scale, plus <see cref="LyingMargin"/>.</summary>
    public static readonly float LyingReach = s_lying.Values.Max(lying => Furthest(lying.boxes) * lying.largestScale) + LyingMargin;

    /// <summary>What becomes of a piece of vegetation lying by a road.</summary>
    public enum Lying
    {
        /// <summary>Not one that lies along the ground.</summary>
        NotLying,
        /// <summary>Off the road as the game laid it.</summary>
        Clear,
        /// <summary>Off the road turned to the yaw given.</summary>
        Turned,
        /// <summary>Armour with the road through its opening at the yaw given (the drawn one, or a turn).</summary>
        Through,
        /// <summary>Across the road whichever way it lies.</summary>
        Remove,
    }

    /// <summary>Whether a lying one at <paramref name="position"/>, drawn at <paramref name="yaw"/> and
    /// <paramref name="scale"/>, stays off <paramref name="road"/> as it is, turned (to <paramref name="turned"/>), or not
    /// at all. A turn is taken only where none of its footprint lies on ground <paramref name="kept"/> says is kept clear
    /// besides the road (a staircase's or bridge's clear area, a location, a player's ground): turned off the road, a 40 m
    /// log could otherwise swing across a staircase's foot. Armour laid at <paramref name="lean"/> is first turned, the
    /// least turn first, so the road runs through its opening (<see cref="RunsThrough"/>), and only then beside the road;
    /// with no lean known it is only turned beside it.</summary>
    public static Lying Lie(int prefab, Vector2 position, float yaw, float scale, IReadOnlyList<RoadCentre> road, out float turned,
        Func<Vector2, bool>? kept = null, Lean? lean = null)
    {
        turned = yaw;
        if (!s_lyingByHash.TryGetValue(prefab, out var lying)) return Lying.NotLying;
        var boxes = lying.boxes;
        // The furthest a road point OffRoad finds on it can stand: its footprint at this scale along and across, each
        // widened by the road's half-width and the margin, the corner of the two.
        List<RoadCentre> near = s_near ??= new List<RoadCentre>();
        near.Clear();
        foreach (RoadCentre point in road)
        {
            float wide = point.HalfWidth + LyingMargin, along = lying.along * scale + wide, across = lying.across * scale + wide;
            if ((point.Centre - position).sqrMagnitude < along * along + across * across) near.Add(point);
        }
        if (near.Count == 0 || OffRoad(boxes, position, yaw, scale, near)) return Lying.Clear;
        // The openings were measured at the armour's only scale, 1.
        if (lean is Lean laid && Mathf.Abs(scale - 1f) < 0.001f && WalkThrough(prefab, laid, out var walk, out float ground))
        {
            for (int k = 0; k * ThroughYawStep <= 180f; k++)
            {
                for (int side = 1; side >= (k == 0 ? 1 : -1); side -= 2)
                {
                    float y = ((yaw + side * k * ThroughYawStep) % 360f + 360f) % 360f;
                    if (!RunsThrough(boxes, position, y, walk, ground, near) || kept != null && OnKept(boxes, position, y, scale, kept)) continue;
                    turned = y;
                    return Lying.Through;
                }
            }
        }
        for (int k = 1; k * LyingYawStep <= 180f; k++)
        {
            for (int side = 1; side >= -1; side -= 2)
            {
                float y = ((yaw + side * k * LyingYawStep) % 360f + 360f) % 360f;
                if (!OffRoad(boxes, position, y, scale, near) || kept != null && OnKept(boxes, position, y, scale, kept)) continue;
                turned = y;
                return Lying.Turned;
            }
        }
        return Lying.Remove;
    }

    /// <summary>Whether, at <paramref name="yaw"/>, every road point on the footprint (as OffRoad finds it) lies inside
    /// <paramref name="walk"/>, its centre within <see cref="ThroughHeight"/> of <paramref name="ground"/>, and at least one
    /// does. The walk's offsets are across the direction phi in the piece's frame: its local z along the yaw's forward. Road
    /// points stand a quarter of the road's width apart, nearer than the half-width the footprint is widened by, so the
    /// road's centre line between any two that reach the piece stays inside the walk with them.</summary>
    private static bool RunsThrough((float zLo, float zHi, float xLo, float xHi)[] boxes, Vector2 position, float yaw,
        (float phi, float sLo, float sHi) walk, float ground, List<RoadCentre> road)
    {
        float r = yaw * Mathf.Deg2Rad, sin = Mathf.Sin(r), cos = Mathf.Cos(r);
        float w = walk.phi * Mathf.Deg2Rad, wSin = Mathf.Sin(w), wCos = Mathf.Cos(w);
        bool through = false;
        foreach (RoadCentre point in road)
        {
            Vector2 d = point.Centre - position;
            float along = d.x * sin + d.y * cos, across = d.x * cos - d.y * sin;
            if (!OnFootprint(boxes, along, across, 1f, point.HalfWidth + LyingMargin)) continue;
            float offset = across * wCos - along * wSin;
            if (offset < walk.sLo || offset > walk.sHi || !(Mathf.Abs(point.Height - ground) <= ThroughHeight)) return false;
            through = true;
        }
        return through;
    }

    /// <summary>Lie's road points near the one it judges; generation and the server clearing judge one at a time.</summary>
    [ThreadStatic] private static List<RoadCentre>? s_near;

    /// <summary>Whether any corner or middle of the footprint at <paramref name="yaw"/> stands on kept ground.</summary>
    private static bool OnKept((float zLo, float zHi, float xLo, float xHi)[] boxes, Vector2 position, float yaw, float scale,
        Func<Vector2, bool> kept)
    {
        float r = yaw * Mathf.Deg2Rad, sin = Mathf.Sin(r), cos = Mathf.Cos(r);
        Vector2 forward = new Vector2(sin, cos), right = new Vector2(cos, -sin);
        foreach (var b in boxes)
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    float z = Mathf.Lerp(b.zLo, b.zHi, i * 0.5f), x = Mathf.Lerp(b.xLo, b.xHi, j * 0.5f);
                    if (kept(position + forward * (z * scale) + right * (x * scale))) return true;
                }
        return false;
    }

    /// <summary>What both clearings did with lying ones this session (<see cref="Note"/>), for looking in game: each turned,
    /// run through or removed one's prefab, place, verdict and yaws, the first <see cref="NotedMost"/>; read with cli_call
    /// ProceduralRoads.VegetationClearing.get_LyingNoted.</summary>
    public static IReadOnlyList<string> LyingNoted { get { lock (s_noted) return s_noted.ToArray(); } }
    public static int LyingTurned, LyingThrough, LyingRemoved;
    private const int NotedMost = 200;
    private static readonly List<string> s_noted = new();

    /// <summary>Counts a turned, run-through or removed lying one, and notes the first <see cref="NotedMost"/>. Called where a verdict is
    /// carried out (generation's placing, the server's clearing), never by a count or a preview.</summary>
    internal static void Note(int prefab, Vector2 position, Lying verdict, float yaw, float turned)
    {
        if (verdict != Lying.Turned && verdict != Lying.Through && verdict != Lying.Remove || !s_lyingByHash.TryGetValue(prefab, out var lying)) return;
        lock (s_noted)
        {
            if (verdict == Lying.Turned) LyingTurned++; else if (verdict == Lying.Through) LyingThrough++; else LyingRemoved++;
            if (s_noted.Count >= NotedMost) return;
            s_noted.Add(FormattableString.Invariant($"{lying.name} at ({position.x:F1}, {position.y:F1}) {verdict} yaw {yaw:F0} -> {turned:F0}"));
        }
    }

    /// <inheritdoc cref="Lie(int, Vector2, float, float, IReadOnlyList{RoadCentre}, out float, Func{Vector2, bool}?, Lean?)"/>
    public static Lying Lie(string prefab, Vector2 position, float yaw, float scale, IReadOnlyList<RoadCentre> road, out float turned,
        Func<Vector2, bool>? kept = null, Lean? lean = null) =>
        Lie(prefab.GetStableHashCode(), position, yaw, scale, road, out turned, kept, lean);

    /// <summary>Whether no road point lies on the footprint widened by its road's half-width and <see cref="LyingMargin"/>.
    /// Its local z (length) lies along the yaw's forward, (sin, cos), and its local x along (cos, -sin), as the game turns it.</summary>
    private static bool OffRoad((float zLo, float zHi, float xLo, float xHi)[] boxes, Vector2 position, float yaw, float scale,
        List<RoadCentre> road)
    {
        float r = yaw * Mathf.Deg2Rad, sin = Mathf.Sin(r), cos = Mathf.Cos(r);
        foreach (RoadCentre point in road)
        {
            Vector2 d = point.Centre - position;
            if (OnFootprint(boxes, d.x * sin + d.y * cos, d.x * cos - d.y * sin, scale, point.HalfWidth + LyingMargin))
                return false;
        }
        return true;
    }

    /// <summary>Whether a point <paramref name="along"/> and <paramref name="across"/> the footprint's frame lies on it at
    /// <paramref name="scale"/>, widened by <paramref name="wide"/>.</summary>
    private static bool OnFootprint((float zLo, float zHi, float xLo, float xHi)[] boxes, float along, float across, float scale, float wide)
    {
        foreach (var b in boxes)
            if (along > b.zLo * scale - wide && along < b.zHi * scale + wide && across > b.xLo * scale - wide && across < b.xHi * scale + wide)
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="prefab"/> lies along the ground (<see cref="Lie"/>). Both clearings ask every candidate,
    /// and build the kept-ground test a turn needs for such a one only.</summary>
    public static bool IsLying(int prefab) => s_lyingByHash.ContainsKey(prefab);

    /// <summary>A grown tree, and its trunk reach at scale 1; false for everything else, which keeps to the clear areas.</summary>
    public static bool IsGrownTree(string prefab, out float trunkReach) => s_trunkReach.TryGetValue(prefab, out trunkReach);

    /// <inheritdoc cref="IsGrownTree(string, out float)"/>
    public static bool IsGrownTree(int prefab, out float trunkReach) => s_trunkReachByHash.TryGetValue(prefab, out trunkReach);

    /// <summary>
    /// Whether a grown tree's trunk, <paramref name="trunkReach"/> at scale 1, reaches the road: the tree stands nearer
    /// a road point than half its width plus the trunk's reach at <paramref name="scale"/> plus TrunkMargin.
    /// </summary>
    public static bool TrunkReachesRoad(IReadOnlyList<RoadCentre> road, Vector2 position, float trunkReach, float scale)
    {
        float reach = trunkReach * scale + TrunkMargin;
        // By index: a foreach over the interface would box its enumerator for every tree generation places.
        for (int i = 0; i < road.Count; i++)
        {
            RoadCentre point = road[i];
            float within = point.HalfWidth + reach;
            if ((position - point.Centre).sqrMagnitude < within * within)
                return true;
        }
        return false;
    }

    private static readonly Dictionary<int, string> s_rockClearingRocks =
        RoadRockPolicy.KnownRocks.ToDictionary(name => name.GetStableHashCode(), name => name);

    /// <summary>
    /// A large rock the rock clearing judges (<see cref="RoadRockPolicy.KnownRocks"/>: boulders, Mistlands rocks and
    /// cliffs, Destructibles a pickaxe breaks into a MineRock5). Small stones are Pickables picked up by hand
    /// (Pickable_Stone, Pickable_Flint) and stay vegetation.
    /// </summary>
    public static bool IsRock(int prefab) => s_rockClearingRocks.ContainsKey(prefab);

    /// <summary>
    /// A large rock the rock clearing takes off a road in <paramref name="biome"/>
    /// (<see cref="RoadRockPolicy.ClearingTakes"/>): never removed with the vegetation from a road's clear areas. A rock
    /// it would leave goes with the vegetation, as generation keeps it off the road.
    /// </summary>
    public static bool LeftToRockClearing(int prefab, Heightmap.Biome biome) =>
        s_rockClearingRocks.TryGetValue(prefab, out string? name) && RoadRockPolicy.ClearingTakes(name, biome);

    /// <summary>ZoneSystem.InsideClearArea, the test PlaceVegetation uses: strict, and square. The one owner of vegetation clearing and lying logs (docs/OWNERS.md).</summary>
    public static bool InsideClearArea(IReadOnlyList<Area> areas, Vector2 point, bool leftToRockClearing = false)
    {
        foreach (Area area in areas)
        {
            if (leftToRockClearing && area.LeavesRocks) continue;
            if (InSquare(point.x, point.y, area.Centre.x, area.Centre.y, area.Radius))
                return true;
        }
        return false;
    }

    /// <summary>The game's clear-area square (ZoneSystem.InsideClearArea), the one owner of it for generation and the server:
    /// (<paramref name="px"/>, <paramref name="pz"/>) strictly inside the square <paramref name="r"/> each way from
    /// (<paramref name="cx"/>, <paramref name="cz"/>). The one owner of vegetation clearing and lying logs (docs/OWNERS.md).</summary>
    public static bool InSquare(float px, float pz, float cx, float cz, float r) =>
        px > cx - r && px < cx + r && pz > cz - r && pz < cz + r;

    /// <summary>
    /// Whether an existing object is one generation would not have placed on
    /// the road.
    ///
    /// What this CANNOT tell, and why the two guards below exist: a tree a
    /// player planted and grew is the same prefab as one the game grew, at an
    /// ordinary position, and vanilla writes nothing to tell them apart --
    /// ZoneSystem.PlaceVegetation spawns its vegetation under a ghost init and
    /// sets only the scale, and Plant.Grow instantiates the grown prefab
    /// without carrying the sapling's creator or plantTime onto it. So on a
    /// world that already exists there is no saved fact that says "a player
    /// grew this". What can be honoured is what IS marked: an object carrying
    /// a creator is somebody's, and vegetation standing on ground a player has
    /// built on is treated as theirs (PlayerGroundRadius). A grown tree alone
    /// on the road in a zone that predates the network is still taken.
    /// The one owner of vegetation clearing and lying logs (docs/OWNERS.md).
    /// </summary>
    /// <param name="road">the road points near the object, for a grown tree (<see cref="TrunkReachesRoad"/>)</param>
    /// <param name="scale">the object's scale</param>
    /// <param name="structureAreas">the structures' clear areas (bridges, staircases): a bridge's keeps every rock out as well, a
    /// staircase's (<see cref="Area.LeavesRocks"/>) leaves a rock the rock clearing takes to it</param>
    /// <param name="biomeAt">the biome at a position, asked for a large rock only (<see cref="LeftToRockClearing"/>)</param>
    public static bool ShouldRemove(int prefab, Vector2 position, bool playerCreated,
        ICollection<int> vegetationPrefabs, IReadOnlyList<Area> clearAreas,
        IReadOnlyList<Footprint> locations, IReadOnlyList<Footprint> playerGround,
        IReadOnlyList<RoadCentre>? road = null, float scale = 1f, IReadOnlyList<Area>? structureAreas = null,
        Func<Vector2, Heightmap.Biome>? biomeAt = null, float yaw = 0f,
        Lean? lean = null) =>
        Decide(prefab, position, playerCreated, vegetationPrefabs, clearAreas, locations, playerGround, road, scale, structureAreas,
            biomeAt, yaw, out _, lean) == Lying.Remove;

    /// <summary>
    /// <see cref="ShouldRemove"/>, and for one lying along the ground at <paramref name="yaw"/> and <paramref name="lean"/>
    /// (<see cref="Lie"/>), whether it is turned off the road, or so the road runs through it, instead (to
    /// <paramref name="turned"/>). Anything else is <see cref="Lying.Clear"/> or <see cref="Lying.Remove"/>.
    /// The one owner of vegetation clearing and lying logs (docs/OWNERS.md).
    /// </summary>
    public static Lying Decide(int prefab, Vector2 position, bool playerCreated,
        ICollection<int> vegetationPrefabs, IReadOnlyList<Area> clearAreas,
        IReadOnlyList<Footprint> locations, IReadOnlyList<Footprint> playerGround,
        IReadOnlyList<RoadCentre>? road, float scale, IReadOnlyList<Area>? structureAreas,
        Func<Vector2, Heightmap.Biome>? biomeAt, float yaw, out float turned, Lean? lean = null)
    {
        turned = yaw;
        // Somebody's own object, whatever it is: never ours to take.
        if (playerCreated || !vegetationPrefabs.Contains(prefab))
            return Lying.Clear;
        bool leftToRocks = IsRock(prefab) && LeftToRockClearing(prefab, biomeAt?.Invoke(position) ?? Heightmap.Biome.None);
        // A staircase's area (Area.LeavesRocks) leaves such a rock to the rock clearing, as a road's does.
        bool inside = structureAreas != null && InsideClearArea(structureAreas, position, leftToRocks) ||
            InsideClearArea(clearAreas, position) && !leftToRocks;
        Lying verdict = OnRoad(inside, prefab, scale, road ?? Array.Empty<RoadCentre>(), position, yaw, out turned,
            IsLying(prefab) ? KeptBy(structureAreas, locations, playerGround) : null, lean);
        // Left to the location, and left to the player who built there. Within a location's footprint only what may be one of
        // its own parts is left to it: the game grows vegetation there too where the location clears no area (a crypt), and a
        // zone placed before its roads (SitesFirstLoad) has had it judged by nothing else.
        if (verdict != Lying.Clear && (LocationKeeps(locations, position, lean) || Inside(playerGround, position)))
        {
            turned = yaw;
            return Lying.Clear;
        }
        return verdict;
    }

    /// <summary>What both clearings make of a piece of vegetation: off the road (an object inside a clear area, a grown
    /// tree whose trunk reaches the road, or one lying across it whichever way it lies), turned off it or so the road runs
    /// through it, or left.</summary>
    public static Lying OnRoad(bool insideClearArea, int prefab, float scale, IReadOnlyList<RoadCentre> road, Vector2 position,
        float yaw, out float turned, Func<Vector2, bool>? kept = null, Lean? lean = null)
    {
        turned = yaw;
        if (insideClearArea) return Lying.Remove;
        Lying lying = Lie(prefab, position, yaw, scale, road, out turned, kept, lean);
        if (lying != Lying.NotLying) return lying;
        return IsGrownTree(prefab, out float trunkReach) && TrunkReachesRoad(road, position, trunkReach, scale) ? Lying.Remove : Lying.Clear;
    }

    /// <summary>The server's kept ground for a turn: a structure's clear area, a location, a player's ground.</summary>
    private static Func<Vector2, bool> KeptBy(IReadOnlyList<Area>? structureAreas, IReadOnlyList<Footprint> locations,
        IReadOnlyList<Footprint> playerGround) =>
        p => structureAreas != null && InsideClearArea(structureAreas, p) || Inside(locations, p) || Inside(playerGround, p);

    /// <summary>Whether the object at <paramref name="position"/>, its origin at <paramref name="lean"/>'s height, stands in a
    /// location's footprint and may be one of its parts: kept unless the location's parts are known and it is none of them
    /// (<see cref="Footprint.PartAt"/>).</summary>
    private static bool LocationKeeps(IReadOnlyList<Footprint> locations, Vector2 position, Lean? lean)
    {
        foreach (Footprint location in locations)
        {
            if ((position - location.Centre).sqrMagnitude >= location.Radius * location.Radius) continue;
            if (location.PartAt == null || lean is not Lean laid) return true;
            if (location.PartAt(new Vector3(position.x, laid.Origin, position.y)) != false) return true;
        }
        return false;
    }

    private static bool Inside(IReadOnlyList<Footprint> footprints, Vector2 position)
    {
        foreach (Footprint footprint in footprints)
        {
            if ((position - footprint.Centre).sqrMagnitude < footprint.Radius * footprint.Radius)
                return true;
        }
        return false;
    }

    // ---- which zones' vegetation already matches the current network ----
    //
    // A zone generated with the road's clear areas matches from the start; a
    // zone generated before it matches once it has been cleared. Clearing
    // happens once per zone and network, so a tree a player plants on the road
    // later is theirs to keep. The set is saved with the network
    // (RoadNetworkPersistence) and belongs to one network version.

    private static readonly HashSet<Vector2s> s_cleared = new();

    /// <summary>The network version the cleared zones belong to (0 = none).</summary>
    public static int Version { get; private set; }

    public static IReadOnlyCollection<Vector2s> ClearedZones => s_cleared;
    /// <summary>The zones owed only the roads appended since they were cleared, by the appends they were cleared through
    /// (<see cref="OwedSince"/>): saved with <see cref="ClearedZones"/>.</summary>
    public static IReadOnlyDictionary<Vector2s, int> ClearedThrough => s_clearedThrough;

    /// <summary>
    /// Zones generated by the time the network was loaded, when the saved record of cleared zones could not be read:
    /// whether each was cleared already, and planted on since, is not known. None is cleared again on that guess for the
    /// roads it had then, until a build that reads the record says, or a new network starts the record afresh
    /// (<see cref="Reset"/>); a road appended since that reaches one is cleared there as in any other zone, its own
    /// roads only (<see cref="OwedSince"/>).
    /// </summary>
    private static readonly HashSet<Vector2s> s_unknown = new();
    /// <summary>The network version, and how many appends it had, when the record could not be read.</summary>
    private static int s_unknownVersion, s_unknownAppends;

    /// <summary>Whether the zone's vegetation is taken to match the network: cleared for it, or with its clearing unknown and
    /// no road appended into it since.</summary>
    public static bool IsCleared(Vector2s zone, int version) =>
        UnknownStillCurrent(zone) ||
        version != 0 && Version != 0 && s_cleared.Contains(zone) && (version == Version || StillCurrent(zone, version));

    private static bool UnknownStillCurrent(Vector2s zone) =>
        s_unknown.Contains(zone) && RoadTerrainModifier.CarriesCurrentRoads(zone, s_unknownVersion);

    private static bool StillCurrent(Vector2s zone, int version) =>
        version == RoadSpatialGrid.RoadNetworkVersion && RoadTerrainModifier.CarriesCurrentRoads(zone, Version);

    /// <summary>The zones explored before the network whose lying vegetation has been judged, by the network version
    /// (<see cref="ServerBakePlanner.LyingOwed"/>). Not saved: judging again leaves what was turned or removed as it is.</summary>
    private static readonly Dictionary<Vector2s, int> s_lyingJudged = new();

    public static bool LyingJudged(Vector2s zone, int version) => s_lyingJudged.TryGetValue(zone, out int judged) && judged == version;

    public static void MarkLyingJudged(Vector2s zone, int version) => s_lyingJudged[zone] = version;

    public static void MarkCleared(Vector2s zone, int version)
    {
        if (version == 0)
            return;
        CarryOver(version);
        s_cleared.Add(zone);
        s_clearedThrough.Remove(zone);
        s_unknown.Remove(zone);
    }

    /// <summary>Zones cleared for an earlier network that appends then changed, by how many appends the network had when
    /// they were cleared: such a zone owes only the clearing of the roads appended since (<see cref="OwedSince"/>), so the
    /// older roads' edges, which players may have planted since, are not cleared again.</summary>
    private static readonly Dictionary<Vector2s, int> s_clearedThrough = new();
    /// <summary>How many appends the network had when <see cref="Version"/> was taken.</summary>
    private static int s_versionAppends;

    /// <summary>The appends whose roads a zone still owes clearing for: all of them (-1), or those after the number it was
    /// cleared through, or, its clearing unknown, after the number the network had when the record could not be read.</summary>
    public static int OwedSince(Vector2s zone) =>
        s_clearedThrough.TryGetValue(zone, out int through) ? through : s_unknown.Contains(zone) ? s_unknownAppends : -1;

    /// <summary>
    /// Move the record to <paramref name="version"/>. A zone a manual road left unchanged
    /// (RoadTerrainModifier.CarriesCurrentRoads, the terrain's own test) stays cleared; any other network starts afresh.
    /// </summary>
    public static void CarryOver(int version)
    {
        if (version == Version) return;
        if (Version != 0)
            s_cleared.RemoveWhere(zone =>
            {
                if (StillCurrent(zone, version)) return false;
                // Changed by an append: it was cleared for every road up to then.
                if (version == RoadSpatialGrid.RoadNetworkVersion && RoadSpatialGrid.AppendCount > s_versionAppends)
                    s_clearedThrough[zone] = s_clearedThrough.TryGetValue(zone, out int was) ? Math.Min(was, s_versionAppends) : s_versionAppends;
                return true;
            });
        Version = version;
        s_versionAppends = RoadSpatialGrid.AppendCount;
    }

    /// <summary>Take the saved record, unless it belongs to another network: the cleared zones, and the zones owed only the
    /// roads appended since (<see cref="ClearedThrough"/>), so a reload does not clear round their older roads again.</summary>
    public static void Load(int savedVersion, IEnumerable<Vector2s> zones, int currentVersion,
        IEnumerable<KeyValuePair<Vector2s, int>>? through = null)
    {
        Reset();
        if (savedVersion == 0 || savedVersion != currentVersion)
            return;
        Version = savedVersion;
        s_versionAppends = RoadSpatialGrid.AppendCount;
        foreach (Vector2s zone in zones)
            s_cleared.Add(zone);
        if (through != null)
            foreach (var owed in through)
                s_clearedThrough[owed.Key] = owed.Value;
    }

    /// <summary>The saved record could not be read: every zone generated by now has an unknown clearing (<see cref="s_unknown"/>).</summary>
    public static void HistoryUnread()
    {
        Reset();
        s_unknownVersion = RoadSpatialGrid.RoadNetworkVersion;
        s_unknownAppends = RoadSpatialGrid.AppendCount;
        if (ZoneSystem.instance != null) s_unknown.UnionWith(ZoneSystem.instance.m_generatedZones);
    }

    public static void Reset()
    {
        s_unknown.Clear();
        s_lyingJudged.Clear();
        s_unknownVersion = s_unknownAppends = 0;
        s_cleared.Clear();
        s_clearedThrough.Clear();
        Version = 0;
        s_versionAppends = 0;
    }
}
