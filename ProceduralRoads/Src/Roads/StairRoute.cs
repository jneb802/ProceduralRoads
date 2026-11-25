using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The coarse route a long staircase follows: the shortest way over the
/// ground, on a grid, that never climbs steeper than a stair flight can and
/// never drops more than a little (a staircase only goes up). On a face too
/// steep to climb straight, the shortest such route zigzags, which is where
/// the switchbacks come from. Pure logic.
///
/// <see cref="StairLayout"/> places pieces along it through waypoints, so its
/// own search only ever solves short legs. Searching a whole valley-to-summit
/// climb piece by piece wandered on the valley floor and ran out of steps
/// (a valley bottom to mountain top climb with some flex).
/// </summary>
public static class StairRoute
{
    public const float Cell = 4f;
    /// <summary>Steepest a route edge may climb: a stair flight's grade.</summary>
    public const float MaxClimb = 0.5f;
    /// <summary>Steepest it may drop: a staircase only goes up, but the ground under a landing may dip a little.</summary>
    public const float MaxDrop = 0.25f;
    /// <summary>How far round the straight line the route may wander, at least.</summary>
    public const float MinMargin = 48f;
    /// <summary>Waypoints handed to the stair search are about this far apart along the route.</summary>
    public const float WaypointSpacing = 12f;

    // Sixteen directions: the eight neighbours and the eight knight's moves, so a
    // route can run at about 22.5 degree steps like the stairs.
    private static readonly (int dx, int dz)[] Moves =
    {
        (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1),
        (2, 1), (1, 2), (-1, 2), (-2, 1), (-2, -1), (-1, -2), (1, -2), (2, -1),
    };

    /// <summary>
    /// Waypoints from just after <paramref name="from"/> to <paramref name="to"/>
    /// (the last one is <paramref name="to"/> itself), or null when no route
    /// within the grade limits exists in the searched area.
    /// </summary>
    public static List<Vector2>? Waypoints(Vector2 from, Vector2 to, Func<float, float, float> ground)
    {
        float margin = Mathf.Max(MinMargin, Vector2.Distance(from, to) * 0.5f);
        float minX = Mathf.Min(from.x, to.x) - margin, minZ = Mathf.Min(from.y, to.y) - margin;
        int nx = Mathf.CeilToInt((Mathf.Max(from.x, to.x) + margin - minX) / Cell) + 1;
        int nz = Mathf.CeilToInt((Mathf.Max(from.y, to.y) + margin - minZ) / Cell) + 1;
        Vector2 At(int i, int k) => new Vector2(minX + i * Cell, minZ + k * Cell);
        (int, int) CellOf(Vector2 p) => (Mathf.RoundToInt((p.x - minX) / Cell), Mathf.RoundToInt((p.y - minZ) / Cell));

        var heights = new float[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int k = 0; k < nz; k++)
            {
                Vector2 p = At(i, k);
                heights[i, k] = ground(p.x, p.y);
            }

        var (si, sk) = CellOf(from);
        var (gi, gk) = CellOf(to);
        var cost = new float[nx, nz];
        var parent = new (int, int)[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int k = 0; k < nz; k++) cost[i, k] = float.MaxValue;
        cost[si, sk] = 0f;
        parent[si, sk] = (-1, -1);
        var open = new SortedSet<(float f, int i, int k)>();
        open.Add((Vector2.Distance(At(si, sk), At(gi, gk)), si, sk));
        bool found = false;
        while (open.Count > 0)
        {
            var (_, i, k) = open.Min;
            open.Remove(open.Min);
            if (i == gi && k == gk) { found = true; break; }
            foreach (var (dx, dz) in Moves)
            {
                int ni = i + dx, nk = k + dz;
                if (ni < 0 || nk < 0 || ni >= nx || nk >= nz) continue;
                float run = Cell * Mathf.Sqrt(dx * dx + dz * dz);
                float rise = heights[ni, nk] - heights[i, k];
                if (rise > MaxClimb * run || rise < -MaxDrop * run) continue;
                float c = cost[i, k] + run;
                if (c >= cost[ni, nk]) continue;
                if (cost[ni, nk] != float.MaxValue) open.Remove((cost[ni, nk] + Vector2.Distance(At(ni, nk), At(gi, gk)), ni, nk));
                cost[ni, nk] = c;
                parent[ni, nk] = (i, k);
                open.Add((c + Vector2.Distance(At(ni, nk), At(gi, gk)), ni, nk));
            }
        }
        if (!found) return null;

        var path = new List<Vector2>();
        for (var (i, k) = (gi, gk); i >= 0; (i, k) = parent[i, k]) path.Add(At(i, k));
        path.Reverse();

        // Thin to about WaypointSpacing along the route; always end on the goal itself.
        var waypoints = new List<Vector2>();
        float walked = 0f;
        for (int n = 1; n < path.Count - 1; n++)
        {
            walked += Vector2.Distance(path[n - 1], path[n]);
            if (walked < WaypointSpacing) continue;
            waypoints.Add(path[n]);
            walked = 0f;
        }
        waypoints.Add(to);
        return waypoints;
    }
}
