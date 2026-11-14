using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Pieces wholly under the ground, which the game does not ground: one nothing else carries breaks as soon as the game
/// weighs it (fully buried supports pop, leaving rocks behind), one a neighbour carries stands. A buried piece is left out
/// only where its structure stands without it (<see cref="Omit"/>).
///
/// The game's rule (assembly_valheim, decompiled). WearNTear.UpdateSupport grounds a piece, at its
/// material's full support, when Physics.OverlapBox over any of its non-trigger colliders' boxes, each grown
/// 0.15 m on every side (SetupColliders: size + 0.3, halved; a box collider's own centre, size and turn, any other
/// collider its world bounds), finds a collider on the terrain layer (or any collider that is not a piece's). The
/// terrain's collider is the heightmap's MeshCollider, a non-convex triangle mesh over the 1 m vertices, two
/// triangles a cell split from (x+1, z) to (x, z+1) (Heightmap.RebuildCollisionMesh), and an overlap with a
/// non-convex mesh meets its triangles only: a box wholly under the surface touches none. Not grounded, the piece
/// takes support only from pieces it touches, losing horizontalLoss of it per metre between centres whatever the
/// direction (stone 1.0, marble 0.5: one 2 m pillar under another gets none); under its material's minimum
/// (stone and marble 100) UpdateWear gives it 100 % damage (m_noSupportWear), from 30 s after it was made
/// (ShouldUpdate), and it breaks, dropping its resources. Seen in game: a stone_pillar placed with
/// its top 1.5 m under the ground broke about 30 s on and left its Stone on the pillar above; one on a slope,
/// its top 0.3 m under the ground at its middle but out of it downhill, stood.
///
/// A piece is wholly buried here where its box grown by <see cref="SupportGraph.Reach"/> is wholly in the ground (the ground
/// over its whole grown footprint higher than its top by more than the growth): the game's own test, its grown box touching
/// none of the mesh. A piece flush with the ground, or into it by up to the growth, is grounded and laid (a flush viaduct
/// deck end module shows). It is left out only where the rest of its structure stands
/// without it (<see cref="Omit"/>, the support rule's reading): a piece standing on it mostly stands on its top, which is in
/// the ground, so it is grounded itself, but not always (one beside it under unknown ground, or held through it).
/// </summary>
public static class BuriedPieces
{
    /// <summary>
    /// One prefab's measured physical facts, the one owner every consumer reads them from (docs/OWNERS.md, measured prefab
    /// data): its collider's bounds about its origin, unturned (an asymmetric one kept as measured, never a symmetric box
    /// round it); its centre of mass on its own axes (WearNTear.m_comOffset), apart from its origin, which a neighbour may
    /// measure from too (<see cref="SupportGraph.Piece.Origin"/>); whether its collider is a convex mesh, not a box (the
    /// game then overlaps its bounds on the world's axes, <see cref="SupportGraph.Piece.WorldBounds"/>); and what it is
    /// made of for the support rule, null for a piece the rule is not read for here (a collider turned off the level inside
    /// the prefab not measured as boxes here, or a piece that carries nothing: its bounds are then the box round its colliders,
    /// for burial only); and, for a piece whose colliders are pitched inside the prefab, those boxes
    /// (<see cref="Pitched"/>; its bounds are then the box round them). A caller turns them into its own frame; it keeps no
    /// copy of them.
    /// </summary>
    public readonly struct Measured
    {
        public readonly Vector3 Min, Max, ComOffset;
        public readonly bool MeshCollider;
        public readonly SupportGraph.Material? Material;
        /// <summary>Its colliders where they are pitched inside the prefab (each a <see cref="PitchedBox"/>); null for one level
        /// box, <see cref="Min"/> to <see cref="Max"/>.</summary>
        public readonly PitchedBox[]? Pitched;
        public Measured(Vector3 min, Vector3 max, SupportGraph.Material? material, Vector3 comOffset = default, bool meshCollider = false,
            PitchedBox[]? pitched = null)
        {
            Min = min; Max = max; Material = material; ComOffset = comOffset; MeshCollider = meshCollider; Pitched = pitched;
        }
    }

    /// <summary>
    /// One box collider pitched inside its prefab, on the prefab's own axes: its middle (<see cref="Centre"/>), and half its
    /// size along its length (x), its depth (y) and its width (z); its length rising <see cref="PitchDegrees"/> from the
    /// prefab's local +x toward its +y (turned about the prefab's z), its depth square to that in the same upright plane, its
    /// width along the prefab's z. A box the game grows and overlaps on its own axes, as any box collider
    /// (WearNTear.SetupColliders), which the support rule's boxes turned only about the vertical are not.
    /// </summary>
    public readonly struct PitchedBox
    {
        public readonly Vector3 Centre, Half;
        public readonly float PitchDegrees;
        public PitchedBox(Vector3 centre, Vector3 half, float pitchDegrees) { Centre = centre; Half = half; PitchDegrees = pitchDegrees; }
    }

    /// <summary>Half a metre over a stair's foot: its centre of mass (stone_stair, blackmarble_stair, wood_stair,
    /// dvergrprops_wood_stair).</summary>
    private static readonly Vector3 StairCom = new(0f, 0.5f, 0f);

    /// <summary>
    /// Every piece the stair kits, the bridge kits and the Dvergr viaduct lay that the burial and support rules read.
    /// Provenance: the stair kits' bounds from cli_piece_geometry (Valheim 1.0.x); the centres of mass and the convex-mesh
    /// colliders as read off the game's prefabs (a stair's half a metre over its foot, stone's arch 0.6 m toward its tall
    /// end, every other piece's at its transform); "Rip" the ripped prefabs; the viaduct's 2 m blocks, band and 8 m footing
    /// as its layout has them (vanilla's viaduct, DvergrVanillaTests), not a fresh capture.
    /// </summary>
    private static readonly Dictionary<string, Measured> s_measured = new()
    {
        ["stone_stair"] = new(new Vector3(-1.021f, 0f, -1f), new Vector3(1.021f, 1f, 1f), SupportGraph.Stone, StairCom, meshCollider: true),
        ["stone_floor_2x2"] = new(new Vector3(-1f, -0.5f, -1f), new Vector3(1f, 0.5f, 1f), SupportGraph.Stone),
        ["stone_wall_2x1"] = new(new Vector3(-1f, -0.5f, -0.5f), new Vector3(1f, 0.5f, 0.5f), SupportGraph.Stone),
        ["stone_wall_1x1"] = new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f), SupportGraph.Stone),
        ["stone_arch"] = new(new Vector3(-1f, -0.5f, -0.5f), new Vector3(1f, 0.5f, 0.5f), SupportGraph.Stone, new Vector3(0.6f, 0f, 0f)),
        ["stone_pillar"] = new(new Vector3(-0.5f, -1f, -0.5f), new Vector3(0.5f, 1f, 0.5f), SupportGraph.Stone),
        ["blackmarble_stair"] = new(new Vector3(-1f, 0f, -1f), new Vector3(1.001f, 1f, 1f), SupportGraph.Marble, StairCom, meshCollider: true),
        ["blackmarble_floor"] = new(new Vector3(-1f, -0.5f, -1f), new Vector3(1f, 0.5f, 1f), SupportGraph.Marble),
        ["blackmarble_2x1x1"] = new(new Vector3(-1f, -0.5f, -0.5f), new Vector3(1f, 0.5f, 0.5f), SupportGraph.Marble),
        ["blackmarble_1x1"] = new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f), SupportGraph.Marble),
        // Rip: its convex collider 2 x 2 x 1, its top 1 m deep at local -x and its face sloping to the bottom at +x.
        ["blackmarble_base_1"] = new(new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f), SupportGraph.Marble),
        ["blackmarble_column_1"] = new(new Vector3(-0.5f, -0.501f, -0.5f), new Vector3(0.5f, 0.501f, 0.5f), SupportGraph.Marble),
        ["blackmarble_arch"] = new(new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f), SupportGraph.Marble),
        ["blackmarble_out_1"] = new(new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f), SupportGraph.Marble, meshCollider: true),
        // Its 2 x 2 top over a 1 m block in one quarter (the rip's snap points): the box round both.
        ["blackmarble_outcorner"] = new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), SupportGraph.Marble, meshCollider: true),
        // The Dvergr viaduct's own (DvergrViaduct): blackmarble_column_3 2 x 8 x 2, the 2 m blocks and band.
        ["blackmarble_column_3"] = new(new Vector3(-1f, -4f, -1f), new Vector3(1f, 4f, 1f), SupportGraph.Marble),
        ["blackmarble_2x2x2"] = new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), SupportGraph.Marble),
        ["blackmarble_2x2_enforced"] = new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), SupportGraph.Marble),
        ["blackmarble_2x2x1"] = new(new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f), SupportGraph.Marble),
        // Rip: blackmarble_head_big01 / big02 are 2 x 2 x 2.
        ["blackmarble_head_big01"] = new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), SupportGraph.Marble),
        ["blackmarble_head_big02"] = new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), SupportGraph.Marble),
        // Rip: iron_floor_2x2 is 0.1 m thick, iron_wall_2x2 0.1 m thick across its local z.
        ["iron_floor_2x2"] = new(new Vector3(-1f, -0.05f, -1f), new Vector3(1f, 0.05f, 1f), SupportGraph.Iron),
        ["iron_wall_2x2"] = new(new Vector3(-1f, -1f, -0.05f), new Vector3(1f, 1f, 0.05f), SupportGraph.Iron),
        // The wooden kits' pieces (the railed kits, the Dvergr wood kit, their end stairs) and the staircases' railing: the
        // Unity rip of the installed game, read 9 Nov 2026 (WearNTear m_materialType, m_supports, m_noSupportWear true for
        // every one but the mist light, m_comOffset, and each non-trigger collider about the prefab's origin); checked in
        // game 9 Nov 2026 (Valheim 1.0.21, a dedicated server: every one's collider bounds by cli_piece_geometry and its
        // material's most and least support by cli_piece_support, the same to the millimetre). Each is one box collider,
        // level, its bounds as the rip has them (wood_beam's sits 0.0145 m toward its local +x), but where said.
        ["wood_beam"] = new(new Vector3(-0.9855f, -0.2f, -0.2f), new Vector3(1.0145f, 0.2f, 0.2f), SupportGraph.Wood),
        ["wood_beam_1"] = new(new Vector3(-0.5f, -0.2f, -0.2f), new Vector3(0.5f, 0.2f, 0.2f), SupportGraph.Wood),
        ["wood_pole"] = new(new Vector3(-0.2f, -0.5f, -0.2f), new Vector3(0.2f, 0.5f, 0.2f), SupportGraph.Wood),
        ["wood_pole2"] = new(new Vector3(-0.2f, -1f, -0.2f), new Vector3(0.2f, 1f, 0.2f), SupportGraph.Wood),
        ["wood_floor"] = new(new Vector3(-1.01f, -0.033f, -1.015f), new Vector3(0.99f, 0.097f, 0.985f), SupportGraph.Wood),
        ["wood_floor_1x1"] = new(new Vector3(-0.51f, -0.033f, -0.515f), new Vector3(0.49f, 0.097f, 0.485f), SupportGraph.Wood),
        // The Dvergr pier's plank and pile are iron to the game (m_materialType Iron, health 800), not wood.
        ["dvergrprops_wood_beam"] = new(new Vector3(-3f, -0.4f, -0.4f), new Vector3(3f, 0.4f, 0.4f), SupportGraph.Iron),
        ["dvergrprops_wood_pole"] = new(new Vector3(-0.4f, -2f, -0.4f), new Vector3(0.4f, 2f, 0.4f), SupportGraph.Iron),
        // The pitched beams (wood), each read as its pitched box colliders (PitchedBox), as the rip has them: the box's own
        // middle, its size (the collider's size times its scale) and its pitch (the collider's turn about the prefab's z),
        // matched by the box round them (cli_piece_geometry) to the millimetre. wood_beam_26 (a railed kit's end-stair
        // stringer and handrail, a staircase's sloped rail): one 2.24 x 0.6 x 0.4 m box pitched 26.5 degrees up its local x
        // (Euler 333.5, 90, 0), its middle 0.5 m over its origin. wood_beam_67 (a braced kit's brace): one 4.577 x 0.621 x
        // 0.4 m box pitched 63.7 degrees (Euler 296.3, 90, 0), its middle at (0.052, 1.041, 0) plus its own centre
        // (0, 0.48, 0.81) on its axes. wood_wall_roof_top (a staircase railing's crossed rails): two boxes 0.6 x 0.4 m in
        // section, 2.53 m pitched 26.5 degrees up its local x and 2.4 m pitched 26.5 degrees up its local -x (Euler 333.5, 270, 0).
        ["wood_beam_26"] = new(new Vector3(-1.136f, -0.268f, -0.2f), new Vector3(1.136f, 1.268f, 0.2f), SupportGraph.Wood, new Vector3(0f, 0.5f, 0f),
            pitched: new[] { new PitchedBox(new Vector3(0f, 0.5f, 0f), new Vector3(1.12f, 0.3f, 0.2f), 26.5f) }),
        ["wood_beam_67"] = new(new Vector3(-1.312f, -0.21f, -0.2f), new Vector3(1.273f, 4.169f, 0.2f), SupportGraph.Wood,
            pitched: new[] { new PitchedBox(new Vector3(-0.0196f, 1.9795f, 0f), new Vector3(2.2885f, 0.3107f, 0.2f), 63.7f) }),
        ["wood_wall_roof_top"] = new(new Vector3(-1.266f, -0.333f, -0.2f), new Vector3(1.266f, 1.333f, 0.205f), SupportGraph.Wood,
            pitched: new[]
            {
                new PitchedBox(new Vector3(0f, 0.5f, 0f), new Vector3(1.265f, 0.3f, 0.2f), 26.5f),
                new PitchedBox(new Vector3(0f, 0.5f, 0.005f), new Vector3(1.2f, 0.3f, 0.2f), 153.5f),
            }),
        // Not read by the support rule (no material here): wood_stair and dvergrprops_wood_stair (wood), four boxes, two of
        // them pitched 26.5 degrees up the flight, not measured as boxes here. The bounds are the box round them on the
        // prefab's own axes, for burial only: wholly in the ground, every collider is.
        ["wood_stair"] = new(new Vector3(-1f, -0.073f, -1.024f), new Vector3(1f, 1.048f, 1.005f), null, StairCom),
        ["dvergrprops_wood_stair"] = new(new Vector3(-1f, -0.073f, -1.024f), new Vector3(1f, 1.048f, 1.005f), null, StairCom),
        // The mist light (wood, m_supports false: it carries nothing, which the support rule here does not model; its box
        // hangs under its origin).
        ["dverger_demister"] = new(new Vector3(-0.485f, -0.648f, -0.478f), new Vector3(0.448f, 0.09f, 0.47f), null),
    };

    /// <summary>A prefab's measured physical facts (<see cref="Measured"/>); false for a piece they are not known of. The one
    /// owner of measured prefab data (docs/OWNERS.md).</summary>
    public static bool DataOf(string prefab, out Measured data) => s_measured.TryGetValue(prefab, out data);

    /// <summary>Whether the piece's bounds are known (an unknown piece is never dropped).</summary>
    public static bool Known(string prefab) => s_measured.ContainsKey(prefab);

    /// <summary>A piece's bounds about its origin, unturned (<see cref="DataOf"/>'s); false for a piece whose bounds are not known.</summary>
    public static bool BoundsOf(string prefab, out Vector3 min, out Vector3 max)
    {
        bool known = s_measured.TryGetValue(prefab, out Measured b);
        min = b.Min; max = b.Max;
        return known;
    }

    /// <summary>
    /// Whether the piece, turned <paramref name="yawDegrees"/> about its origin at <paramref name="position"/>, lies
    /// wholly under the ground: its box, grown <see cref="SupportGraph.Reach"/> as the game grows it, wholly in the game's
    /// terrain mesh (<see cref="SupportGraph.Against"/>: <see cref="SupportGraph.Terrain.Under"/>, the mesh over its footprint,
    /// grown too, higher than its top by more than the growth everywhere), so it touches none of the mesh and the game does not
    /// ground it. A piece flush with the ground, or into it by no more than the growth, is not: the game grounds it and it
    /// stands. A piece whose colliders are pitched inside its prefab (<see cref="Measured.Pitched"/>) is buried where every
    /// one of them, grown, is wholly in the ground (<see cref="SupportGraph.OnTerrain"/>'s sloped contact test).
    /// <paramref name="vertex"/>: the ground at a whole-metre vertex (NaN where not known: then never). A piece tipped
    /// off level or of unknown bounds is never buried. The one owner of Supports, foundations and burial (docs/OWNERS.md).
    /// </summary>
    public static bool Buried(string prefab, Vector3 position, float yawDegrees, Func<int, int, float> vertex, float pitchDegrees = 0f)
    {
        if (pitchDegrees != 0f || !DataOf(prefab, out Measured data)) return false;
        // A piece whose colliders are pitched inside its prefab: every one of them, grown, wholly in the ground (the sloped
        // contact test, SupportGraph.OnTerrain).
        if (data.Pitched != null)
            return SupportGraph.OnTerrain(new SupportGraph.Piece(position, data.ComOffset, yawDegrees, data.Pitched, SupportGraph.Wood, false),
                vertex, skipUnknown: false) == SupportGraph.Terrain.Under;
        Vector3 min = data.Min, max = data.Max;
        float bottom = position.y + min.y, top = position.y + max.y;
        Vector2 at = new Vector2(position.x, position.z), right = StairLayout.Right(yawDegrees), forward = StairLayout.Forward(yawDegrees);
        // Its middle first: most pieces stand out of the ground there.
        Vector2 middle = at + right * ((min.x + max.x) / 2f) + forward * ((min.z + max.z) / 2f);
        float h = MeshHeight(middle.x, middle.y, vertex);
        if (SupportGraph.Against(h, h, bottom, top) != SupportGraph.Terrain.Under) return false;
        float x0 = min.x - SupportGraph.Reach, x1 = max.x + SupportGraph.Reach, z0 = min.z - SupportGraph.Reach, z1 = max.z + SupportGraph.Reach;
        var corners = new[]
        {
            at + right * x0 + forward * z0, at + right * x1 + forward * z0,
            at + right * x1 + forward * z1, at + right * x0 + forward * z1,
        };
        var (lowest, highest) = RangeOver(corners, vertex);
        return SupportGraph.Against(lowest, highest, bottom, top) == SupportGraph.Terrain.Under;
    }

    /// <summary>
    /// The game's terrain at a point, from its vertices: the heightmap's collision mesh, two triangles a 1 m cell,
    /// (x, z), (x, z+1), (x+1, z) and (x+1, z), (x, z+1), (x+1, z+1).
    /// </summary>
    public static float MeshHeight(float x, float z, Func<int, int, float> vertex)
    {
        int i = Mathf.FloorToInt(x), k = Mathf.FloorToInt(z);
        return BetweenVertices(x - i, z - k, vertex(i, k), vertex(i + 1, k), vertex(i, k + 1), vertex(i + 1, k + 1));
    }

    /// <summary>
    /// The one owner of the ground between four vertices of a 1 m cell: the game's two triangles, split on the diagonal from
    /// (x, z+1) to (x+1, z). (<paramref name="fx"/>, <paramref name="fz"/>) is the point inside the cell, 0 to 1 from
    /// (<paramref name="h00"/>) at (x, z); h10 is at (x+1, z), h01 at (x, z+1), h11 at (x+1, z+1). The corner a point's
    /// triangle does not use is not read, so it may be NaN. Every reader of a heightmap-style lattice between its vertices
    /// calls this. Each branch is one whole float expression.
    /// </summary>
    public static float BetweenVertices(float fx, float fz, float h00, float h10, float h01, float h11)
    {
        if (fx + fz <= 1f) return h00 + fx * (h10 - h00) + fz * (h01 - h00);
        return h11 + (1f - fx) * (h01 - h11) + (1f - fz) * (h10 - h11);
    }

    /// <summary>
    /// The lowest and the highest of the terrain mesh over a convex polygon (or a segment: two points), exactly: a surface
    /// flat over each triangle is lowest and highest at a corner of the polygon, at a mesh vertex inside it, or where its
    /// edges cross the mesh's edges (lines x = n, z = n and x + z = n). Both NaN where any of those reads NaN; with
    /// <paramref name="skipUnknown"/>, those are left out instead (both NaN where none is known). The one reader of the
    /// mesh over a footprint: burial, foundations and terrain contact (<see cref="SupportGraph.OnTerrain"/>).
    /// </summary>
    public static (float lowest, float highest) RangeOver(IReadOnlyList<Vector2> polygon, Func<int, int, float> vertex,
        bool skipUnknown = false)
    {
        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        bool unknown = false;
        void Take(float h)
        {
            if (float.IsNaN(h)) { if (!skipUnknown) unknown = true; return; }
            if (h < low) low = h;
            if (h > high) high = h;
        }
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (Vector2 p in polygon)
        {
            Take(MeshHeight(p.x, p.y, vertex));
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
        }
        for (int i = Mathf.CeilToInt(minX); i <= Mathf.FloorToInt(maxX); i++)
            for (int k = Mathf.CeilToInt(minZ); k <= Mathf.FloorToInt(maxZ); k++)
                if (Inside(polygon, new Vector2(i, k))) Take(vertex(i, k));
        for (int e = 0; e < polygon.Count; e++)
        {
            Vector2 a = polygon[e], d = polygon[(e + 1) % polygon.Count] - a;
            // x = n, z = n, x + z = n along the edge a + t d, 0 < t < 1.
            Cross(a.x, d.x);
            Cross(a.y, d.y);
            Cross(a.x + a.y, d.x + d.y);
            void Cross(float start, float change)
            {
                if (Mathf.Abs(change) < 1e-6f) return;
                float end = start + change;
                for (int n = Mathf.CeilToInt(Mathf.Min(start, end)); n <= Mathf.FloorToInt(Mathf.Max(start, end)); n++)
                {
                    float t = (n - start) / change;
                    if (t <= 0f || t >= 1f) continue;
                    Vector2 p = a + d * t;
                    Take(MeshHeight(p.x, p.y, vertex));
                }
            }
        }
        return unknown || low > high ? (float.NaN, float.NaN) : (low, high);
    }

    private static bool Inside(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        int sign = 0;
        for (int e = 0; e < polygon.Count; e++)
        {
            Vector2 a = polygon[e], b = polygon[(e + 1) % polygon.Count];
            float cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            int s = cross > 1e-6f ? 1 : cross < -1e-6f ? -1 : 0;
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>
    /// Drop the run's pieces wholly under <paramref name="vertex"/>'s ground where the rest stands without them
    /// (<see cref="Omit"/>, the staircase read by the support rule on the finished mesh as its kit lays it:
    /// <see cref="StairSupport.Holding(IReadOnlyList{StructurePiece}, Func{int, int, float})"/>); returns how many, adding each
    /// to <paramref name="byPrefab"/> when given.
    /// </summary>
    public static int Drop(StairRun run, Func<int, int, float> vertex, IDictionary<string, int>? byPrefab = null)
    {
        List<StructurePiece> dropped = Omit(run.Pieces, vertex, pieces => StairSupport.Holding(pieces, vertex));
        if (byPrefab != null)
            foreach (StructurePiece p in dropped) byPrefab[p.Prefab] = byPrefab.TryGetValue(p.Prefab, out int n) ? n + 1 : 1;
        return dropped.Count;
    }

    /// <summary>
    /// The one way a laid structure leaves out its buried pieces (docs/OWNERS.md), a staircase's and the Dvergr viaduct's:
    /// of <paramref name="pieces"/> (all of them, or those <paramref name="candidate"/> picks), each wholly under
    /// <paramref name="vertex"/>'s ground (<see cref="Buried"/>: never where that ground is not known, nor a piece tipped or
    /// of unknown bounds) is left out only where the rest stands without it, as the structure's own reading of the support
    /// rule (<paramref name="support"/>) has it. The game grounds no wholly buried piece but lets a neighbour carry it, and a
    /// piece it carries falls with it: so one the rule reads as falling with everything laid
    /// (<see cref="ViaductSupport.Held.Falls"/>) is left out whatever it carries, the game would break it, and every other
    /// is left out where, with it and those already left out gone, no piece that stood falls. All of them at once first;
    /// where that fells one, each in its order, kept where leaving it out would. A piece the rule does not read (a wooden
    /// stair) is left out as buried. Removed from <paramref name="pieces"/>; returns them in their order.
    /// </summary>
    public static List<StructurePiece> Omit(List<StructurePiece> pieces, Func<int, int, float> vertex,
        Func<IReadOnlyList<StructurePiece>, List<ViaductSupport.Held>> support, Func<StructurePiece, bool>? candidate = null)
    {
        var buried = pieces.FindAll(p => (candidate == null || candidate(p)) && p.RollDegrees == 0f
            && Buried(p.Prefab, p.Position, p.YawDegrees, vertex, p.PitchDegrees));
        if (buried.Count == 0) return buried;
        var candidates = new HashSet<StructurePiece>(buried);
        var doomed = new HashSet<StructurePiece>();
        List<ViaductSupport.Held> all = support(pieces);
        foreach (ViaductSupport.Held h in all)
            if (h.Falls && candidates.Contains(h.Piece)) doomed.Add(h.Piece);
        List<StructurePiece> Without(HashSet<StructurePiece> left) => pieces.FindAll(p => !left.Contains(p));
        var stood = new HashSet<StructurePiece>();
        foreach (ViaductSupport.Held h in doomed.Count == 0 ? all : support(Without(doomed)))
            if (!h.Falls) stood.Add(h.Piece);
        bool Fells(HashSet<StructurePiece> left)
        {
            foreach (ViaductSupport.Held h in support(Without(left)))
                if (h.Falls && stood.Contains(h.Piece)) return true;
            return false;
        }
        var omitted = new HashSet<StructurePiece>(candidates);
        if (Fells(omitted))
        {
            omitted = new HashSet<StructurePiece>(doomed);
            foreach (StructurePiece p in buried)
            {
                if (!omitted.Add(p)) continue;
                if (Fells(omitted)) omitted.Remove(p);
            }
        }
        pieces.RemoveAll(omitted.Contains);
        return buried.FindAll(omitted.Contains);
    }
}
