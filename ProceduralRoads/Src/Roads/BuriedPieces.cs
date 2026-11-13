using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Pieces the game breaks as soon as it weighs them, because they are wholly under the ground; none is placed
/// (fully buried supports pop, leaving rocks behind).
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
/// A piece is dropped here where the ground over its whole footprint grown by <see cref="SupportGraph.Reach"/> stands at or
/// above its top: wholly under the surface, so out of sight, and the game's own test with a margin of the growth
/// for ground that comes out lower than it was modelled. Nothing it held is lost: a piece standing on it stands
/// on its top, which is in the ground, so it is grounded itself.
/// </summary>
public static class BuriedPieces
{
    /// <summary>Each stair kit piece's collider bounds about its origin (cli_piece_geometry, Valheim 1.0.x).</summary>
    private static readonly Dictionary<string, (Vector3 min, Vector3 max)> s_bounds = new()
    {
        ["stone_stair"] = (new Vector3(-1.021f, 0f, -1f), new Vector3(1.021f, 1f, 1f)),
        ["stone_floor_2x2"] = (new Vector3(-1f, -0.5f, -1f), new Vector3(1f, 0.5f, 1f)),
        ["stone_wall_2x1"] = (new Vector3(-1f, -0.5f, -0.5f), new Vector3(1f, 0.5f, 0.5f)),
        ["stone_wall_1x1"] = (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f)),
        ["stone_arch"] = (new Vector3(-1f, -0.5f, -0.5f), new Vector3(1f, 0.5f, 0.5f)),
        ["stone_pillar"] = (new Vector3(-0.5f, -1f, -0.5f), new Vector3(0.5f, 1f, 0.5f)),
        ["blackmarble_stair"] = (new Vector3(-1f, 0f, -1f), new Vector3(1.001f, 1f, 1f)),
        ["blackmarble_floor"] = (new Vector3(-1f, -0.5f, -1f), new Vector3(1f, 0.5f, 1f)),
        ["blackmarble_2x1x1"] = (new Vector3(-1f, -0.5f, -0.5f), new Vector3(1f, 0.5f, 0.5f)),
        ["blackmarble_1x1"] = (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f)),
        // Rip: its convex collider 2 x 2 x 1, its top 1 m deep at local -x and its face sloping to the bottom at +x.
        ["blackmarble_base_1"] = (new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f)),
        ["blackmarble_column_1"] = (new Vector3(-0.5f, -0.501f, -0.5f), new Vector3(0.5f, 0.501f, 0.5f)),
        ["blackmarble_arch"] = (new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f)),
        ["blackmarble_out_1"] = (new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f)),
        // Its 2 x 2 top over a 1 m block in one quarter (the rip's snap points): the box round both.
        ["blackmarble_outcorner"] = (new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f)),
        ["wood_stair"] = (new Vector3(-1f, -0.073f, -1.024f), new Vector3(1f, 1.048f, 1.005f)),
        ["wood_floor"] = (new Vector3(-1.01f, -0.033f, -1.015f), new Vector3(0.99f, 0.097f, 0.985f)),
        ["wood_floor_1x1"] = (new Vector3(-0.51f, -0.033f, -0.515f), new Vector3(0.49f, 0.097f, 0.485f)),
        ["wood_pole2"] = (new Vector3(-0.2f, -1f, -0.2f), new Vector3(0.2f, 1f, 0.2f)),
    };

    /// <summary>Whether the piece's bounds are known (an unknown piece is never dropped).</summary>
    public static bool Known(string prefab) => s_bounds.ContainsKey(prefab);

    /// <summary>A piece's bounds about its origin, unturned; false for a piece whose bounds are not known.</summary>
    public static bool BoundsOf(string prefab, out Vector3 min, out Vector3 max)
    {
        bool known = s_bounds.TryGetValue(prefab, out var b);
        min = b.min; max = b.max;
        return known;
    }

    /// <summary>
    /// Whether the piece, turned <paramref name="yawDegrees"/> about its origin at <paramref name="position"/>, lies
    /// wholly under the ground: the lowest of the game's terrain mesh over its footprint grown by
    /// <see cref="SupportGraph.Reach"/> at or above its top. <paramref name="vertex"/>: the ground at a whole-metre vertex
    /// (NaN where not known: then never). A piece tipped off level or of unknown bounds is never buried.
    /// The one owner of Supports, foundations and burial (docs/OWNERS.md).
    /// </summary>
    public static bool Buried(string prefab, Vector3 position, float yawDegrees, Func<int, int, float> vertex, float pitchDegrees = 0f)
    {
        if (pitchDegrees != 0f || !s_bounds.TryGetValue(prefab, out var b)) return false;
        float top = position.y + b.max.y;
        Vector2 at = new Vector2(position.x, position.z), right = StairLayout.Right(yawDegrees), forward = StairLayout.Forward(yawDegrees);
        // Its middle first: most pieces stand out of the ground there.
        Vector2 middle = at + right * ((b.min.x + b.max.x) / 2f) + forward * ((b.min.z + b.max.z) / 2f);
        float h = MeshHeight(middle.x, middle.y, vertex);
        if (!(h >= top)) return false;
        float x0 = b.min.x - SupportGraph.Reach, x1 = b.max.x + SupportGraph.Reach, z0 = b.min.z - SupportGraph.Reach, z1 = b.max.z + SupportGraph.Reach;
        var corners = new[]
        {
            at + right * x0 + forward * z0, at + right * x1 + forward * z0,
            at + right * x1 + forward * z1, at + right * x0 + forward * z1,
        };
        float low = LowestOver(corners, vertex);
        return low >= top;
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
    /// The lowest of the terrain mesh over a convex polygon, exactly: a surface flat over each triangle is lowest at a
    /// corner of the polygon, at a mesh vertex inside it, or where its edges cross the mesh's edges (lines x = n,
    /// z = n and x + z = n). NaN where any of those reads NaN.
    /// </summary>
    public static float LowestOver(IReadOnlyList<Vector2> polygon, Func<int, int, float> vertex)
    {
        float low = float.PositiveInfinity;
        void Take(float h) { if (float.IsNaN(h) || float.IsNaN(low)) low = float.NaN; else if (h < low) low = h; }
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
        return low;
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
    /// Drop the run's pieces wholly under <paramref name="vertex"/>'s ground; returns how many, adding each to
    /// <paramref name="byPrefab"/> when given.
    /// </summary>
    public static int Drop(StairRun run, Func<int, int, float> vertex, IDictionary<string, int>? byPrefab = null)
    {
        int dropped = 0;
        for (int i = run.Pieces.Count - 1; i >= 0; i--)
        {
            StructurePiece p = run.Pieces[i];
            if (p.RollDegrees != 0f || !Buried(p.Prefab, p.Position, p.YawDegrees, vertex, p.PitchDegrees)) continue;
            run.Pieces.RemoveAt(i);
            dropped++;
            if (byPrefab != null) byPrefab[p.Prefab] = byPrefab.TryGetValue(p.Prefab, out int n) ? n + 1 : 1;
        }
        return dropped;
    }
}
