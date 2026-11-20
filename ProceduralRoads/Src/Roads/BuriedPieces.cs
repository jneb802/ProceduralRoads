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
/// A piece is dropped here where the ground over its whole footprint grown by <see cref="Growth"/> stands at or
/// above its top: wholly under the surface, so out of sight, and the game's own test with a margin of the growth
/// for ground that comes out lower than it was modelled. Nothing it held is lost: a piece standing on it stands
/// on its top, which is in the ground, so it is grounded itself.
/// </summary>
public static class BuriedPieces
{
    /// <summary>How far WearNTear grows each collider box on every side for its overlap.</summary>
    public const float Growth = 0.15f;

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
        ["blackmarble_column_1"] = (new Vector3(-0.5f, -0.501f, -0.5f), new Vector3(0.5f, 0.501f, 0.5f)),
        ["blackmarble_arch"] = (new Vector3(-1f, -1f, -0.5f), new Vector3(1f, 1f, 0.5f)),
        ["wood_stair"] = (new Vector3(-1f, -0.073f, -1.024f), new Vector3(1f, 1.048f, 1.005f)),
        ["wood_floor"] = (new Vector3(-1.01f, -0.033f, -1.015f), new Vector3(0.99f, 0.097f, 0.985f)),
        ["wood_floor_1x1"] = (new Vector3(-0.51f, -0.033f, -0.515f), new Vector3(0.49f, 0.097f, 0.485f)),
        ["wood_pole2"] = (new Vector3(-0.2f, -1f, -0.2f), new Vector3(0.2f, 1f, 0.2f)),
    };

    /// <summary>Whether the piece's bounds are known (an unknown piece is never dropped).</summary>
    public static bool Known(string prefab) => s_bounds.ContainsKey(prefab);

    /// <summary>
    /// Whether the piece, turned <paramref name="yawDegrees"/> about its origin at <paramref name="position"/>, lies
    /// wholly under the ground: the lowest of the game's terrain mesh over its footprint grown by
    /// <see cref="Growth"/> at or above its top. <paramref name="vertex"/>: the ground at a whole-metre vertex
    /// (NaN where not known: then never). A piece tipped off level or of unknown bounds is never buried.
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
        float x0 = b.min.x - Growth, x1 = b.max.x + Growth, z0 = b.min.z - Growth, z1 = b.max.z + Growth;
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
        float fx = x - i, fz = z - k;
        if (fx + fz <= 1f)
        {
            float h00 = vertex(i, k);
            return h00 + fx * (vertex(i + 1, k) - h00) + fz * (vertex(i, k + 1) - h00);
        }
        float h11 = vertex(i + 1, k + 1);
        return h11 + (1f - fx) * (vertex(i, k + 1) - h11) + (1f - fz) * (vertex(i + 1, k) - h11);
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
    /// The one column rule, for every column whatever ground it is read on (the layout's, its own cuts', the finished
    /// ground the network leaves): a column is carried on down a pillar while its base, <paramref name="bottom"/>, stands
    /// less than StairLayout.SupportBury under the lowest ground at its corners, <paramref name="lowestCorner"/> (the whole
    /// bottom should clip into the ground), but never by a pillar that would lie wholly under
    /// the ground (<see cref="Buried"/>), which the game breaks: there the base is in the ground under all of the next one's
    /// grown footprint already. <paramref name="at"/>, <paramref name="yaw"/>: the column's middle and turn.
    /// </summary>
    public static bool ColumnGoesOn(float bottom, float lowestCorner, string prefab, float height, Vector2 at, float yaw, Func<int, int, float> vertex) =>
        bottom > lowestCorner - StairLayout.SupportBury && !Buried(prefab, new Vector3(at.x, bottom - height / 2f, at.y), yaw, vertex);

    /// <summary>The deepest <see cref="CarryColumns"/> carries one column on down: the most the terrain writer lowers ground
    /// (<see cref="RoadConstants.TerrainDeltaMin"/>, 8 m), in whatever pillars the kit stacks. It was 4 pillars: 8 m of stone
    /// but 4 m of black marble's 1 m columns, so a Mistlands column over a deeper road cut was left hanging and broke (the
    /// whole column collapsed, on a staircase at -3850,-5020).</summary>
    public const float MostCarriedDepth = -RoadConstants.TerrainDeltaMin;

    /// <summary>
    /// Each of the run's columns (its kit's support pieces stacked at one spot, not a cantilever's clipped into a face)
    /// carried on down by <see cref="ColumnGoesOn"/> over <paramref name="vertex"/>'s ground: where the network's earthwork
    /// (a road's cut beside a staircase's end, another staircase's cut) lowered the ground under a column after the layout
    /// sized it, its base hung in the air, lost its support and broke (leaving stray
    /// stones, at a staircase foot at 6497,-1497). Returns the pillars added.
    /// </summary>
    public static int CarryColumns(StairRun run, Func<int, int, float> vertex)
    {
        var bottoms = new Dictionary<(int, int), StructurePiece>();
        foreach (StructurePiece p in run.Pieces)
        {
            if (p.Kind != StructurePieceKind.StairSupport || p.Cantilevered || ColumnHeight(p.Prefab) is not float) continue;
            var key = (Mathf.RoundToInt(p.Position.x * 100f), Mathf.RoundToInt(p.Position.z * 100f));
            if (!bottoms.TryGetValue(key, out StructurePiece? lowest) || p.Position.y < lowest.Position.y) bottoms[key] = p;
        }
        int added = 0;
        foreach (StructurePiece bottom in bottoms.Values)
        {
            float height = ColumnHeight(bottom.Prefab)!.Value;
            var at = new Vector2(bottom.Position.x, bottom.Position.z);
            // A corner on ground not known (NaN) leaves the column as it is, whichever corner it is: Mathf.Min drops a NaN
            // read before a number, so one read first was ignored and the column carried down by the corners known alone.
            float lowest = float.PositiveInfinity;
            for (int cx = -1; cx <= 1; cx += 2)
                for (int cz = -1; cz <= 1; cz += 2)
                {
                    float corner = MeshHeight(at.x + cx * StairLayout.SupportHalfWidth, at.y + cz * StairLayout.SupportHalfWidth, vertex);
                    lowest = float.IsNaN(corner) || float.IsNaN(lowest) ? float.NaN : Mathf.Min(lowest, corner);
                }
            if (float.IsNaN(lowest)) continue;
            float base_ = bottom.Position.y - height / 2f;
            for (int n = 0, most = Mathf.CeilToInt(MostCarriedDepth / height - 1e-3f); n < most && ColumnGoesOn(base_, lowest, bottom.Prefab, height, at, bottom.YawDegrees, vertex); n++, base_ -= height)
            {
                run.Pieces.Add(new StructurePiece
                {
                    Kind = StructurePieceKind.StairSupport, Prefab = bottom.Prefab,
                    Position = new Vector3(at.x, base_ - height / 2f, at.y), YawDegrees = bottom.YawDegrees,
                });
                added++;
            }
        }
        return added;
    }

    /// <summary>The height of a kit's column piece, null for any other piece.</summary>
    private static float? ColumnHeight(string prefab)
    {
        foreach (StairKit kit in StairKit.All)
            if (kit.Support == prefab) return kit.SupportHeight;
        return null;
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
