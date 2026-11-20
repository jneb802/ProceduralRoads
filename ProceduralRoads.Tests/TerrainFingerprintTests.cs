using System.IO;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The fingerprint of a zone's saved terrain, which is how two runs of the same
/// world (one written by a modded client, one by the server) are compared. The
/// payloads here are written exactly as the game's TerrainComp.Save writes them.
/// </summary>
public class TerrainFingerprintTests
{
    /// <summary>A payload in the game's format: level deltas at the given vertices, paint at the given texels.</summary>
    private static byte[] Payload(int vertices, (int index, float level, float smooth)[] heights,
        (int index, float r, float g, float b, float a)[] paint, int operations = 0)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(1);
        writer.Write(operations);
        writer.Write(0f); writer.Write(0f); writer.Write(0f); // last operation point
        writer.Write(0f);                                      // last operation radius
        writer.Write(vertices);
        for (int i = 0; i < vertices; i++)
        {
            bool modified = false;
            foreach ((int index, float level, float smooth) in heights)
            {
                if (index != i) continue;
                writer.Write(true);
                writer.Write(level);
                writer.Write(smooth);
                modified = true;
                break;
            }
            if (!modified)
                writer.Write(false);
        }
        writer.Write(vertices);
        for (int i = 0; i < vertices; i++)
        {
            bool modified = false;
            foreach ((int index, float r, float g, float b, float a) in paint)
            {
                if (index != i) continue;
                writer.Write(true);
                writer.Write(r); writer.Write(g); writer.Write(b); writer.Write(a);
                modified = true;
                break;
            }
            if (!modified)
                writer.Write(false);
        }
        writer.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void ItCountsAndSumsWhatTheTerrainHolds()
    {
        byte[] payload = Payload(9,
            new[] { (1, 0.5f, 0f), (4, -0.25f, 0.125f) },
            new[] { (4, 0f, 0f, 1f, 1f) },
            operations: 3);

        Assert.True(TerrainFingerprint.TryRead(payload, out TerrainFingerprint.Summary summary, out string error), error);
        Assert.Equal(3, summary.Operations);
        Assert.Equal(9, summary.HeightVertices);
        Assert.Equal(2, summary.ModifiedHeights);
        Assert.Equal(0.25, summary.LevelSum, 5);
        Assert.Equal(0.125, summary.SmoothSum, 5);
        Assert.Equal(9, summary.PaintTexels);
        Assert.Equal(1, summary.ModifiedPaint);
    }

    [Fact]
    public void TheSameTerrainFingerprintsTheSameAndDifferentTerrainDoesNot()
    {
        byte[] one = Payload(9, new[] { (1, 0.5f, 0f) }, new[] { (4, 1f, 0f, 0f, 1f) });
        byte[] same = Payload(9, new[] { (1, 0.5f, 0f) }, new[] { (4, 1f, 0f, 0f, 1f) });
        // Same heights and paint, but the terrain was reached by a different
        // number of edits: that is history, not the result, and is not hashed.
        byte[] sameByAnotherRoute = Payload(9, new[] { (1, 0.5f, 0f) }, new[] { (4, 1f, 0f, 0f, 1f) }, operations: 7);
        byte[] otherHeight = Payload(9, new[] { (1, 0.5001f, 0f) }, new[] { (4, 1f, 0f, 0f, 1f) });
        byte[] otherVertex = Payload(9, new[] { (2, 0.5f, 0f) }, new[] { (4, 1f, 0f, 0f, 1f) });
        byte[] otherPaint = Payload(9, new[] { (1, 0.5f, 0f) }, new[] { (4, 0f, 0f, 1f, 1f) });

        TerrainFingerprint.Summary a = Read(one), b = Read(same), c = Read(sameByAnotherRoute);
        Assert.Equal(a.HeightHash, b.HeightHash);
        Assert.Equal(a.PaintHash, b.PaintHash);
        Assert.Equal(a.HeightHash, c.HeightHash);
        Assert.Equal(a.PaintHash, c.PaintHash);

        Assert.NotEqual(a.HeightHash, Read(otherHeight).HeightHash);
        Assert.NotEqual(a.HeightHash, Read(otherVertex).HeightHash);
        Assert.NotEqual(a.PaintHash, Read(otherPaint).PaintHash);
    }

    [Fact]
    public void ATruncatedPayloadIsRefusedInsteadOfGuessed()
    {
        byte[] payload = Payload(9, new[] { (1, 0.5f, 0f) }, new[] { (4, 1f, 0f, 0f, 1f) });
        var cut = new byte[payload.Length - 6];
        System.Array.Copy(payload, cut, cut.Length);
        Assert.False(TerrainFingerprint.TryRead(cut, out _, out string error));
        Assert.NotEmpty(error);
    }

    private static TerrainFingerprint.Summary Read(byte[] payload)
    {
        Assert.True(TerrainFingerprint.TryRead(payload, out TerrainFingerprint.Summary summary, out string error), error);
        return summary;
    }
}
