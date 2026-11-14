using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A tooling probe of the terrain writer, not a suite test: it does nothing unless ROADS_WRITER_PROBE names an output
/// file. Then it writes every zone the released network fixture (Fixtures/released-network) reaches through the writer,
/// on two worlds: the suite's synthetic world, and rolling ground a few metres either side of the saved roads' median
/// height (the saved heights are another world's, so on the synthetic world most of them stand far off its ground). For each zone it records a hash of everything the write leaves in the compiler (level and smooth deltas,
/// modified flags, paint), so two builds' files say whether they write the same bytes; and the time of the writer's fit
/// over the zone's vertices (<see cref="RoadTerrainModifier.WrittenAt(RoadTerrainModifier.FitPoints, int[], int, Vector2, float)"/>,
/// the zone's points laid out once a round, which is the fit and the face noise) and of a whole zone write, best of ROADS_WRITER_PROBE_ROUNDS (default 20) rounds.
/// </summary>
public sealed class TerrainWriterProbeTests : IDisposable
{
    private const int Width = 64;

    public TerrainWriterProbeTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); RoadTerrainModifier.ResetForWorld(); RoadLocationProtection.Reset(); }
    public void Dispose() { RoadSpatialGrid.Clear(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); }

    private static byte[] Network()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "released-network", "global-data.bin.gz");
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Ground a few metres either side of the saved roads' median height, so they cut and fill as on a real slope.</summary>
    private sealed class RollingWorld : WorldGenerator
    {
        private readonly float m_level;
        public RollingWorld(float level) { m_level = level; }
        public override float GetHeight(float x, float z) => m_level + 3f * Mathf.Sin(x / 37f) + 2f * Mathf.Cos(z / 23f);
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }

    [Fact]
    public void WriteProbe()
    {
        string? output = Environment.GetEnvironmentVariable("ROADS_WRITER_PROBE");
        if (string.IsNullOrEmpty(output)) return;
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ROADS_WRITER_PROBE_ROUNDS"), out int r) && r > 0 ? r : 20;
        var report = new StringBuilder();
        float median;
        using (var scope = new ValheimWorldScope().WithWorld(new SyntheticWorld()))
        {
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Network()));
            var heights = RoadSpatialGrid.GetZonesWithRoadPoints().SelectMany(RoadSpatialGrid.GetRoadPointsInZone).Select(p => p.h).OrderBy(h => h).ToList();
            median = heights[heights.Count / 2];
            Probe("synthetic world", scope, WorldGenerator.instance!, rounds, report);
            RoadSpatialGrid.Clear(); RoadTerrainModifier.ResetForWorld();
        }
        using (var scope = new ValheimWorldScope().WithWorld(new RollingWorld(median)))
        {
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Network()));
            Probe("rolling ground", scope, WorldGenerator.instance!, rounds, report);
        }
        File.WriteAllText(output, report.ToString());
    }

    /// <summary>
    /// The paint the writer leaves over the released network fixture's zones (Fixtures/released-network) on the synthetic
    /// world, pinned to its bytes: with no Location footprint, and with footprints over some of its road points, where only a
    /// road into a Location paints. Taken from the paint before its hot loop ran the cheap tests first (a texel keeps the
    /// strongest paint any point gives it, so a point that cannot raise it skips the footprint and ground reads): the
    /// reordered loop must paint the same bytes.
    /// </summary>
    [Fact]
    public void TheWritersPaintOverTheReleasedNetworkIsUnchangedByTheOrderOfItsTests()
    {
        using var scope = new ValheimWorldScope().WithWorld(new SyntheticWorld());
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Network()));
        var zones = RoadSpatialGrid.GetZonesWithRoadPoints().OrderBy(z => z.x).ThenBy(z => z.y).ToList();
        var footprints = zones.SelectMany(RoadSpatialGrid.GetRoadPointsInZone).Where((_, i) => i % 23 == 0)
            .Select(p => new RoadLocationProtection.Footprint(p.p, 6f)).ToList();
        string Paint(bool withFootprints)
        {
            RoadLocationProtection.Set(withFootprints ? footprints : new List<RoadLocationProtection.Footprint>());
            using var all = SHA256.Create();
            foreach (Vector2s zone in zones)
            {
                Heightmap hm = scope.RegisterHeightmap(zone, Width);
                TerrainComp tc = hm.m_terrainComp!;
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                foreach (Color c in tc.m_paintMask) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
                foreach (bool b in tc.m_modifiedPaint) w.Write(b);
                w.Flush();
                byte[] bytes = ms.ToArray();
                all.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            all.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Hex(all.Hash!);
        }
        try
        {
            string plain = Paint(withFootprints: false), inFootprints = Paint(withFootprints: true);
            Assert.True(plain == "84ddbbf71b1269f86044a6dce68d39001e8c04654ad7e380bc90aa628362049d" && inFootprints == "671e28871faa0dbe3a81cdce9187a57f81110fe6560bc7bf3ef49939ac237c91", $"paint {plain}, with footprints {inFootprints}");
        }
        finally { RoadLocationProtection.Reset(); }
    }

    private static void Probe(string label, ValheimWorldScope scope, WorldGenerator world, int rounds, StringBuilder report)
    {
        var zones = RoadSpatialGrid.GetZonesWithRoadPoints().OrderBy(z => z.x).ThenBy(z => z.y).ToList();
        using var all = SHA256.Create();
        double fitTotal = 0, writeTotal = 0;
        int points = 0;
        foreach (Vector2s zone in zones)
        {
            List<RoadSpatialGrid.RoadPoint> zonePoints = RoadSpatialGrid.GetRoadPointsInZone(zone);
            points += zonePoints.Count;
            Heightmap hm = scope.RegisterHeightmap(zone, Width);
            TerrainComp tc = hm.m_terrainComp!;
            var fresh = (level: (float[])tc.m_levelDelta.Clone(), smooth: (float[])tc.m_smoothDelta.Clone(),
                height: (bool[])tc.m_modifiedHeight.Clone(), paint: (Color[])tc.m_paintMask.Clone(), painted: (bool[])tc.m_modifiedPaint.Clone());
            double bestWrite = double.MaxValue;
            for (int k = 0; k < rounds; k++)
            {
                Array.Copy(fresh.level, tc.m_levelDelta, fresh.level.Length);
                Array.Copy(fresh.smooth, tc.m_smoothDelta, fresh.smooth.Length);
                Array.Copy(fresh.height, tc.m_modifiedHeight, fresh.height.Length);
                Array.Copy(fresh.paint, tc.m_paintMask, fresh.paint.Length);
                Array.Copy(fresh.painted, tc.m_modifiedPaint, fresh.painted.Length);
                var clock = Stopwatch.StartNew();
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, zonePoints, hm, tc);
                bestWrite = Math.Min(bestWrite, clock.Elapsed.TotalMilliseconds);
            }
            byte[] bytes = Bytes(tc);
            string hash; using (var one = SHA256.Create()) hash = Hex(one.ComputeHash(bytes));
            all.TransformBlock(bytes, 0, bytes.Length, null, 0);

            Vector3 centre = ZoneSystem.GetZonePos(zone);
            var ground = new float[(Width + 1) * (Width + 1)];
            for (int vz = 0; vz <= Width; vz++)
                for (int vx = 0; vx <= Width; vx++)
                    ground[vz * (Width + 1) + vx] = BiomeBlendedHeight.GetBlendedHeight(centre.x + vx - Width / 2, centre.z + vz - Width / 2, world);
            double bestFit = double.MaxValue;
            float sum = 0f;
            int[] every = Enumerable.Range(0, zonePoints.Count).ToArray();
            for (int k = 0; k < rounds; k++)
            {
                var clock = Stopwatch.StartNew();
                // The zone's points laid out once a round, as the earthwork measure lays a piece's (RoadNetworkGenerator.EarthworkAcross).
                var fit = new RoadTerrainModifier.FitPoints().Set(zonePoints, null);
                for (int vz = 0; vz <= Width; vz++)
                    for (int vx = 0; vx <= Width; vx++)
                        sum += RoadTerrainModifier.WrittenAt(fit, every, every.Length, new Vector2(centre.x + vx - Width / 2, centre.z + vz - Width / 2), ground[vz * (Width + 1) + vx]);
                bestFit = Math.Min(bestFit, clock.Elapsed.TotalMilliseconds);
            }
            fitTotal += bestFit; writeTotal += bestWrite;
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0} zone {1},{2}: {3} points, bytes {4}, outcome {5}, fit {6:F2} ms (sum {7:R}), write {8:F2} ms",
                label, zone.x, zone.y, zonePoints.Count, hash, RoadTerrainModifier.LastWriteOutcome, bestFit, sum, bestWrite));
        }
        all.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        report.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0}: {1} zones, {2} points, all bytes {3}, fit {4:F1} ms, write {5:F1} ms (best of {6} rounds each)",
            label, zones.Count, points, Hex(all.Hash!), fitTotal, writeTotal, rounds));
    }

    /// <summary>Everything a write leaves in the compiler, as bytes.</summary>
    private static byte[] Bytes(TerrainComp tc)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (float f in tc.m_levelDelta) w.Write(f);
        foreach (float f in tc.m_smoothDelta) w.Write(f);
        foreach (bool b in tc.m_modifiedHeight) w.Write(b);
        foreach (Color c in tc.m_paintMask) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
        foreach (bool b in tc.m_modifiedPaint) w.Write(b);
        w.Flush();
        return ms.ToArray();
    }

    private static string Hex(byte[] hash) => string.Concat(hash.Select(b => b.ToString("x2")));
}
