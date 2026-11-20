using System;
using System.IO;

namespace ProceduralRoads;

/// <summary>
/// A fingerprint of one zone's saved terrain, so two runs can be compared by
/// their data instead of by eye.
///
/// The payload is the game's own: TerrainComp.Save compresses a ZPackage of
///   [int version][int operations][Vector3 lastOpPoint][float lastOpRadius]
///   [int n] then per vertex [bool modified] and, if modified,
///   [float levelDelta][float smoothDelta],
///   [int m] then per texel [bool modified] and, if modified, [float r,g,b,a].
/// ZPackage writes through a BinaryWriter, so a BinaryReader reads it back.
///
/// The hashes cover only what the roads write -- which vertices and texels
/// changed and to what -- and leave out the operation count and last operation
/// point, which record how the terrain was edited rather than how it ended up.
/// </summary>
public static class TerrainFingerprint
{
    public readonly struct Summary
    {
        public readonly int Operations;
        public readonly int HeightVertices;
        public readonly int ModifiedHeights;
        public readonly double LevelSum;
        public readonly double SmoothSum;
        public readonly int PaintTexels;
        public readonly int ModifiedPaint;
        public readonly uint HeightHash;
        public readonly uint PaintHash;

        public Summary(int operations, int heightVertices, int modifiedHeights, double levelSum, double smoothSum,
            int paintTexels, int modifiedPaint, uint heightHash, uint paintHash)
        {
            Operations = operations;
            HeightVertices = heightVertices;
            ModifiedHeights = modifiedHeights;
            LevelSum = levelSum;
            SmoothSum = smoothSum;
            PaintTexels = paintTexels;
            ModifiedPaint = modifiedPaint;
            HeightHash = heightHash;
            PaintHash = paintHash;
        }

        public override string ToString() =>
            $"ops {Operations} heights {ModifiedHeights}/{HeightVertices} sumLevel {LevelSum:F3} " +
            $"sumSmooth {SmoothSum:F3} hHash {HeightHash:x8} paint {ModifiedPaint}/{PaintTexels} pHash {PaintHash:x8}";
    }

    public static bool TryRead(byte[] payload, out Summary summary, out string error)
    {
        summary = default;
        error = "";
        try
        {
            using var stream = new MemoryStream(payload);
            using var reader = new BinaryReader(stream);
            reader.ReadInt32(); // format version
            int operations = reader.ReadInt32();
            reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); // last operation point
            reader.ReadSingle(); // last operation radius

            int heightVertices = reader.ReadInt32();
            if (heightVertices < 0 || heightVertices > 1_000_000)
            {
                error = $"height array length {heightVertices}";
                return false;
            }
            uint heightHash = 2166136261u;
            int modifiedHeights = 0;
            double levelSum = 0, smoothSum = 0;
            for (int i = 0; i < heightVertices; i++)
            {
                if (!reader.ReadBoolean())
                    continue;
                float level = reader.ReadSingle();
                float smooth = reader.ReadSingle();
                modifiedHeights++;
                levelSum += level;
                smoothSum += smooth;
                Mix(ref heightHash, i);
                Mix(ref heightHash, Bits(level));
                Mix(ref heightHash, Bits(smooth));
            }

            int paintTexels = reader.ReadInt32();
            if (paintTexels < 0 || paintTexels > 1_000_000)
            {
                error = $"paint array length {paintTexels}";
                return false;
            }
            uint paintHash = 2166136261u;
            int modifiedPaint = 0;
            for (int i = 0; i < paintTexels; i++)
            {
                if (!reader.ReadBoolean())
                    continue;
                modifiedPaint++;
                Mix(ref paintHash, i);
                for (int channel = 0; channel < 4; channel++)
                    Mix(ref paintHash, Bits(reader.ReadSingle()));
            }

            summary = new Summary(operations, heightVertices, modifiedHeights, levelSum, smoothSum,
                paintTexels, modifiedPaint, heightHash, paintHash);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>A float's bits, the same on every runtime (no unsafe, no GetHashCode).</summary>
    private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

    private static void Mix(ref uint hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 16777619u;
        }
    }
}
