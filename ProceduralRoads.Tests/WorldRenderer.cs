using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>
/// Renders a synthetic world plus road paths to a 24-bit BMP for visual
/// inspection (zero image-library dependencies; convert with sips if needed).
/// </summary>
public static class WorldRenderer
{
    public static void Render(
        WorldGenerator world,
        List<(List<Vector2> path, byte r, byte g, byte b)> paths,
        List<(Vector2 pos, byte r, byte g, byte b)> markers,
        string outputPath,
        float worldMin = -700f,
        float worldMax = 700f,
        float metersPerPixel = 4f)
    {
        int size = (int)((worldMax - worldMin) / metersPerPixel);
        var pixels = new byte[size * size * 3]; // BGR rows, bottom-up

        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                float wx = worldMin + px * metersPerPixel;
                float wy = worldMin + py * metersPerPixel;

                float h = world.GetHeight(wx, wy);
                world.GetRiverWeight(wx, wy, out float riverWeight, out _);

                (byte r, byte g, byte b) c;
                if (h < RoadConstants.DeepWaterHeight)
                    c = (20, 40, 90);
                else if (h < RoadConstants.ShallowWaterHeight)
                    c = (60, 110, 170);
                else if (riverWeight > RoadConstants.RiverImpassableThreshold)
                    c = (80, 140, 200);
                else if (world.GetBiome(wx, wy) == Heightmap.Biome.Mountain)
                {
                    byte v = (byte)Mathf.Clamp(120 + (h - 40f) * 3f, 110, 235);
                    c = (v, v, v);
                }
                else
                {
                    float t = Mathf.Clamp01((h - 30f) / 30f);
                    c = ((byte)(70 + t * 120), (byte)(130 + t * 60), (byte)(60 + t * 30));
                }

                SetPixel(pixels, size, px, py, c.r, c.g, c.b);
            }
        }

        foreach (var (path, r, g, b) in paths)
        {
            if (path == null) continue;
            for (int i = 1; i < path.Count; i++)
                DrawLine(pixels, size, worldMin, metersPerPixel, path[i - 1], path[i], r, g, b);
        }

        foreach (var (pos, r, g, b) in markers)
            DrawDot(pixels, size, worldMin, metersPerPixel, pos, 3, r, g, b);

        WriteBmp(outputPath, pixels, size, size);
    }

    private static void DrawLine(byte[] px, int size, float worldMin, float mpp,
        Vector2 a, Vector2 b, byte r, byte g, byte bl)
    {
        float steps = Mathf.Max(Vector2.Distance(a, b) / mpp * 2f, 1f);
        for (int i = 0; i <= (int)steps; i++)
        {
            float t = i / steps;
            float wx = a.x + (b.x - a.x) * t;
            float wy = a.y + (b.y - a.y) * t;
            int ix = (int)((wx - worldMin) / mpp);
            int iy = (int)((wy - worldMin) / mpp);
            // 2px-thick stroke so roads stay readable at map scale
            SetPixel(px, size, ix, iy, r, g, bl);
            SetPixel(px, size, ix + 1, iy, r, g, bl);
            SetPixel(px, size, ix, iy + 1, r, g, bl);
        }
    }

    private static void DrawDot(byte[] px, int size, float worldMin, float mpp,
        Vector2 pos, int radius, byte r, byte g, byte b)
    {
        int cx = (int)((pos.x - worldMin) / mpp);
        int cy = (int)((pos.y - worldMin) / mpp);
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
            if (dx * dx + dy * dy <= radius * radius)
                SetPixel(px, size, cx + dx, cy + dy, r, g, b);
    }

    private static void SetPixel(byte[] px, int size, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || x >= size || y < 0 || y >= size) return;
        int i = (y * size + x) * 3;
        px[i] = b;
        px[i + 1] = g;
        px[i + 2] = r;
    }

    private static void WriteBmp(string path, byte[] bgr, int width, int height)
    {
        int rowBytes = width * 3;
        int padding = (4 - rowBytes % 4) % 4;
        int dataSize = (rowBytes + padding) * height;
        int fileSize = 54 + dataSize;

        using var w = new BinaryWriter(File.Create(path));
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(fileSize);
        w.Write(0);
        w.Write(54);
        w.Write(40);
        w.Write(width);
        w.Write(height);
        w.Write((short)1);
        w.Write((short)24);
        w.Write(0);
        w.Write(dataSize);
        w.Write(2835); w.Write(2835);
        w.Write(0); w.Write(0);

        var pad = new byte[padding];
        for (int y = 0; y < height; y++)
        {
            w.Write(bgr, y * rowBytes, rowBytes);
            w.Write(pad);
        }
    }
}
