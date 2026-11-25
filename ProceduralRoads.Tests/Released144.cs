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
    /// <summary>A 1.4.4 blob of the network this build's grid holds now (cells and points as this build stores them).</summary>
    internal static byte[] BlobOfCurrentGrid()
    {
        // This build's own v4 blob, read back cell by cell and written the way 1.4.4 wrote it.
        byte[] v4 = RoadSpatialGrid.SerializeAllRoadPoints()!;
        using var input = new BinaryReader(new MemoryStream(v4));
        Assert(input.ReadInt32() == 4, "envelope");
        int encoding = input.ReadInt32();
        Assert(encoding == 2, "a network without appends");
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
                input.ReadBoolean(); // paint-only: 1.4.4 had no such flag
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
