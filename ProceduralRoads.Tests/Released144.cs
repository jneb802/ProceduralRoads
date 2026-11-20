using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>
/// The road network format ProceduralRoads 1.4.4 (origin/master 50e88cd, format v1 since 1.4.0) writes and reads,
/// vendored from its RoadSpatialGrid.SerializeAllRoadPoints / DeserializeAllRoadPoints so a test can hold a world
/// saved by the released build and check that the released build can still read what this build leaves behind.
/// Format: [1][cellCount] then per cell [grid x][grid y][pointCount] and per point [px][py][w][h].
/// </summary>
internal static class Released144
{
    /// <summary>A 1.4.4 blob of one grid cell and the points [px, py, w, h] in it, written out by hand.</summary>
    internal static byte[] Blob(int cellX, int cellY, params (float x, float y, float w, float h)[] points)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(1);
        writer.Write(1);
        writer.Write(cellX); writer.Write(cellY);
        writer.Write(points.Length);
        foreach (var p in points) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.w); writer.Write(p.h); }
        return ms.ToArray();
    }

    /// <summary>A 1.4.4 blob of the network this build's grid holds now (cells and points as this build stores them).</summary>
    internal static byte[] BlobOfCurrentGrid()
    {
        // This build's own blob, read back cell by cell and written the way 1.4.4 wrote it.
        byte[] current = RoadSpatialGrid.SerializeAllRoadPoints()!;
        using var input = new BinaryReader(new MemoryStream(current));
        Assert(input.ReadInt32() == RoadSpatialGrid.SavedFormat, "format");
        Assert(input.ReadInt32() == RoadSpatialGrid.SavedEncoding, "encoding");
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(1);
        int cells = input.ReadInt32();
        writer.Write(cells);
        for (int c = 0; c < cells; c++)
        {
            writer.Write(input.ReadInt32());
            writer.Write(input.ReadInt32());
            int n = input.ReadInt32();
            writer.Write(n);
            for (int i = 0; i < n; i++)
            {
                writer.Write(input.ReadSingle());
                writer.Write(input.ReadSingle());
                writer.Write(input.ReadSingle());
                writer.Write(input.ReadSingle());
                // 1.4.4 had no flags and no additions: a plain road point only.
                bool paintOnly = input.ReadBoolean(), cutOnly = input.ReadBoolean(), intoLocation = input.ReadBoolean(), unpainted = input.ReadBoolean(), mound = input.ReadBoolean();
                Assert(!paintOnly && !cutOnly && !intoLocation && !unpainted && !mound && input.ReadInt32() == 0, "a plain road point");
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 1.4.4's reader: whether it accepts the blob, and how many points it reads. It refuses any version but 1 (and
    /// then regenerates the world's network), and otherwise reads cells until the count is reached.
    /// </summary>
    internal static bool Reads(byte[] data, out int points)
    {
        points = 0;
        if (data == null || data.Length == 0) return false;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            if (reader.ReadInt32() != 1) return false;
            int cellCount = reader.ReadInt32();
            if (cellCount < 0 || cellCount > 1000000) return false;
            var loaded = new Dictionary<Vector2i, int>(cellCount);
            for (int c = 0; c < cellCount; c++)
            {
                int gridX = reader.ReadInt32();
                int gridY = reader.ReadInt32();
                int pointCount = reader.ReadInt32();
                if (pointCount < 0 || pointCount > 100000) return false;
                for (int i = 0; i < pointCount; i++)
                {
                    reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
                }
                loaded[new Vector2i(gridX, gridY)] = pointCount;
                points += pointCount;
            }
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static void Assert(bool condition, string what)
    {
        if (!condition) throw new System.InvalidOperationException($"Released144 fixture: unexpected {what}");
    }
}
