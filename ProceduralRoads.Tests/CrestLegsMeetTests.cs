using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// Ways onto a placed location's landing over a crest whose second leg, laid as it falls (not head-on), met the first beside it:
/// both legs climbed past where they pass each other to two tops side by side, turned the same way, and the walk went up one
/// and back down the other, the crest's ground standing over both exits (in game, in blackmarble: two flights in line over a
/// spur, their tops 2.6 m apart turned 45 degrees from each other; and a one-stair climb off a road's end whose second leg
/// came back down beside it, 3.7 m apart, then switched back on its own pad). The legs below are the pieces as they were laid,
/// and the ground the finished ground saved round them (whole-metre pins).
/// </summary>
public class CrestLegsMeetTests
{
    private readonly ITestOutputHelper _out;
    public CrestLegsMeetTests(ITestOutputHelper output) => _out = output;

    private const char S = 'S', L = 'L', F = 'F';

    /// <summary>Up from the road's end at 38.22 to a top at 45.22, then the second leg from the landing at 39.26 up to its own
    /// top at 45.26 beside it (the second's pieces from the landing up, as laid).</summary>
    private static readonly (char kind, string prefab, float x, float y, float z, float yaw)[] InLine =
    {
        (S, "blackmarble_stair", -8450.435f, 37.216f, 3308.982f, 337.50f),
        (S, "blackmarble_stair", -8449.669f, 38.216f, 3307.134f, 337.50f),
        (L, "blackmarble_floor", -8448.903f, 38.716f, 3305.287f, 157.50f),
        (F, "blackmarble_2x1x1", -8448.329f, 38.716f, 3303.901f, 337.50f),
        (S, "blackmarble_stair", -8447.597f, 39.216f, 3303.331f, 315.00f),
        (F, "blackmarble_2x1x1", -8446.536f, 39.716f, 3302.271f, 315.00f),
        (F, "blackmarble_1x1", -8446.097f, 39.716f, 3302.417f, 0.00f),
        (S, "blackmarble_stair", -8446.597f, 40.216f, 3300.917f, 0.00f),
        (F, "blackmarble_2x1x1", -8446.597f, 40.716f, 3299.417f, 0.00f),
        (S, "blackmarble_stair", -8446.138f, 41.216f, 3298.611f, 337.50f),
        (L, "blackmarble_floor", -8445.372f, 41.716f, 3296.763f, 157.50f),
        (S, "blackmarble_stair", -8444.606f, 42.216f, 3294.915f, 337.50f),
        (S, "blackmarble_stair", -8443.841f, 43.216f, 3293.068f, 337.50f),
        (F, "blackmarble_2x1x1", -8443.267f, 43.716f, 3291.682f, 337.50f),
        (F, "blackmarble_1x1", -8443.570f, 43.716f, 3291.332f, 292.50f),
        (S, "blackmarble_stair", -8441.993f, 44.216f, 3291.220f, 292.50f),
        (L, "blackmarble_floor", -8428.926f, 38.760f, 3264.193f, 0.00f),
        (L, "blackmarble_floor", -8429.232f, 38.760f, 3265.734f, 337.50f),
        (L, "blackmarble_floor", -8430.105f, 38.760f, 3267.041f, 315.00f),
        (L, "blackmarble_floor", -8431.520f, 38.760f, 3268.455f, 315.00f),
        (L, "blackmarble_floor", -8432.934f, 38.760f, 3269.869f, 315.00f),
        (L, "blackmarble_floor", -8434.348f, 38.760f, 3271.284f, 315.00f),
        (L, "blackmarble_floor", -8435.762f, 38.760f, 3272.698f, 315.00f),
        (S, "blackmarble_stair", -8437.176f, 39.260f, 3274.112f, 135.00f),
        (S, "blackmarble_stair", -8438.590f, 40.260f, 3275.527f, 135.00f),
        (L, "blackmarble_floor", -8440.004f, 40.760f, 3276.941f, 315.00f),
        (L, "blackmarble_floor", -8440.877f, 40.760f, 3278.248f, 337.50f),
        (L, "blackmarble_floor", -8441.643f, 40.760f, 3280.095f, 337.50f),
        (F, "blackmarble_2x1x1", -8442.217f, 40.760f, 3281.481f, 157.50f),
        (S, "blackmarble_stair", -8442.949f, 41.260f, 3282.051f, 135.00f),
        (F, "blackmarble_2x1x1", -8444.010f, 41.760f, 3283.111f, 135.00f),
        (F, "blackmarble_1x1", -8444.449f, 41.760f, 3282.965f, 180.00f),
        (S, "blackmarble_stair", -8443.949f, 42.260f, 3284.465f, 180.00f),
        (F, "blackmarble_2x1x1", -8443.949f, 42.760f, 3285.965f, 180.00f),
        (S, "blackmarble_stair", -8443.490f, 43.260f, 3286.771f, 202.50f),
        (F, "blackmarble_2x1x1", -8442.916f, 43.760f, 3288.157f, 202.50f),
        (F, "blackmarble_1x1", -8443.220f, 43.760f, 3288.507f, 247.50f),
        (S, "blackmarble_stair", -8441.643f, 44.260f, 3288.619f, 247.50f),
    };

    /// <summary>The finished ground round <see cref="InLine"/>: x,z,height.</summary>
    private const string InLineGround = ""
        + "-8452,3307,38.09 -8452,3308,38.21 -8452,3309,38.22 -8452,3310,38.23 -8451,3307,38.19 -8451,3308,38.19 -8451,3309,38.22 -8451,3310,38.23 "
        + "-8450,3307,38.22 -8450,3308,38.19 -8450,3309,38.22 -8450,3310,38.22 -8449,3307,38.22 -8449,3308,38.22 -8449,3309,38.22 -8449,3310,38.21 "
        + "-8451,3306,38.16 -8450,3306,38.22 -8449,3306,38.21 -8448,3306,38.21 -8448,3307,38.29 -8448,3308,38.37 -8448,3309,38.33 -8450,3304,38.45 "
        + "-8450,3305,38.20 -8449,3304,38.65 -8449,3305,38.26 -8448,3304,39.05 -8448,3305,38.39 -8447,3304,39.05 -8447,3305,38.84 -8447,3306,38.79 "
        + "-8447,3307,38.84 -8450,3302,38.27 -8450,3303,38.38 -8449,3302,38.50 -8449,3303,38.60 -8448,3302,39.09 -8448,3303,39.10 -8447,3302,39.46 "
        + "-8447,3303,39.36 -8446,3302,39.48 -8446,3303,39.30 -8446,3304,38.97 -8446,3305,38.76 -8448,3301,39.50 -8447,3301,39.98 -8446,3301,40.46 "
        + "-8445,3301,40.52 -8445,3302,39.95 -8445,3303,39.86 -8445,3304,39.51 -8448,3299,39.99 -8448,3300,39.85 -8447,3299,40.28 -8447,3300,39.98 "
        + "-8446,3299,40.54 -8446,3300,40.55 -8445,3299,41.59 -8445,3300,41.02 -8448,3298,40.42 -8447,3298,40.86 -8446,3298,40.95 -8445,3298,41.55 "
        + "-8448,3297,40.35 -8447,3297,40.93 -8446,3297,41.01 -8445,3297,41.46 -8447,3295,40.89 -8447,3296,40.52 -8446,3295,41.08 -8446,3296,41.04 "
        + "-8445,3295,41.44 -8445,3296,41.48 -8444,3295,42.47 -8444,3296,42.18 -8444,3297,42.03 -8444,3298,42.32 -8446,3293,40.46 -8446,3294,41.00 "
        + "-8445,3293,40.97 -8445,3294,41.04 -8444,3293,42.01 -8444,3294,42.06 -8443,3293,42.94 -8443,3294,43.00 -8443,3295,42.76 -8443,3296,42.65 "
        + "-8445,3292,40.96 -8444,3292,41.94 -8443,3292,42.44 -8442,3292,43.48 -8442,3293,43.99 -8442,3294,43.88 -8442,3295,43.75 -8445,3290,41.05 "
        + "-8445,3291,41.03 -8444,3290,41.47 -8444,3291,41.48 -8443,3290,42.49 -8443,3291,42.45 -8442,3290,44.00 -8442,3291,43.52 -8441,3290,44.90 "
        + "-8441,3291,44.70 -8441,3292,44.63 -8441,3293,45.00 -8440,3290,45.53 -8440,3291,45.38 -8440,3292,45.27 -8440,3293,45.83 -8430,3263,39.26 "
        + "-8430,3264,39.26 -8430,3265,39.26 -8430,3266,39.26 -8429,3263,39.26 -8429,3264,39.26 -8429,3265,39.26 -8429,3266,39.26 -8428,3263,39.26 "
        + "-8428,3264,39.26 -8428,3265,39.26 -8428,3266,39.26 -8427,3263,38.98 -8427,3264,38.93 -8427,3265,38.86 -8427,3266,38.72 -8431,3264,39.26 "
        + "-8431,3265,39.26 -8431,3266,39.26 -8431,3267,39.04 -8430,3267,38.96 -8429,3267,38.86 -8428,3267,38.72 -8432,3266,39.26 -8432,3267,39.08 "
        + "-8432,3268,38.94 -8432,3269,38.76 -8431,3268,38.88 -8431,3269,38.70 -8430,3268,38.76 -8430,3269,38.56 -8429,3268,38.66 -8429,3269,38.47 "
        + "-8433,3267,39.11 -8433,3268,38.98 -8433,3269,38.80 -8433,3270,38.59 -8432,3270,38.53 -8431,3270,38.45 -8430,3270,38.32 -8434,3268,39.01 "
        + "-8434,3269,38.85 -8434,3270,38.63 -8434,3271,38.33 -8433,3271,38.28 -8432,3271,38.22 -8431,3271,38.28 -8436,3270,38.59 -8436,3271,38.32 "
        + "-8436,3272,38.29 -8436,3273,38.56 -8435,3270,38.64 -8435,3271,38.34 -8435,3272,38.25 -8435,3273,38.60 -8434,3272,38.13 -8434,3273,38.52 "
        + "-8433,3272,38.03 -8433,3273,38.69 -8437,3271,39.23 -8437,3272,39.09 -8437,3273,38.92 -8437,3274,38.86 -8436,3274,39.06 -8435,3274,39.16 "
        + "-8434,3274,39.36 -8439,3273,39.77 -8439,3274,39.34 -8439,3275,39.08 -8439,3276,39.77 -8438,3273,39.48 -8438,3274,39.11 -8438,3275,39.03 "
        + "-8438,3276,39.82 -8437,3275,39.37 -8437,3276,40.57 -8436,3275,39.77 -8436,3276,41.05 -8440,3274,39.46 -8440,3275,39.35 -8440,3276,39.82 "
        + "-8440,3277,40.70 -8439,3277,40.81 -8438,3277,41.33 -8437,3277,41.84 -8442,3275,39.37 -8442,3276,39.83 -8442,3277,40.28 -8442,3278,40.42 "
        + "-8441,3275,39.12 -8441,3276,39.76 -8441,3277,40.25 -8441,3278,40.41 -8440,3278,40.98 -8439,3278,41.53 -8442,3279,40.02 -8442,3280,40.02 "
        + "-8441,3279,40.42 -8441,3280,40.48 -8440,3279,41.41 -8440,3280,41.55 -8439,3279,41.99 -8439,3280,42.52 -8443,3279,40.04 -8443,3280,40.44 "
        + "-8443,3281,40.58 -8443,3282,40.49 -8442,3281,40.52 -8442,3282,40.95 -8441,3281,40.93 -8441,3282,41.38 -8440,3281,41.49 -8440,3282,41.52 "
        + "-8444,3280,39.97 -8444,3281,40.48 -8444,3282,40.44 -8444,3283,40.55 -8443,3283,40.49 -8442,3283,40.96 -8441,3283,41.79 -8444,3284,41.52 "
        + "-8443,3284,41.05 -8442,3284,41.48 -8441,3284,42.50 -8446,3282,40.46 -8446,3283,40.48 -8446,3284,41.01 -8446,3285,41.55 -8445,3282,39.98 "
        + "-8445,3283,40.46 -8445,3284,41.45 -8445,3285,41.50 -8444,3285,41.50 -8443,3285,41.50 -8446,3281,39.96 -8445,3281,39.98 -8445,3286,41.03 "
        + "-8444,3286,41.03 -8443,3286,41.45 -8442,3285,41.96 -8442,3286,41.95 -8445,3287,41.41 -8444,3287,41.01 -8443,3287,41.44 -8442,3287,42.45 "
        + "-8445,3288,41.47 -8444,3288,41.44 -8443,3288,41.50 -8442,3288,42.99 -8444,3289,41.45 -8443,3289,42.01 -8442,3289,43.52 -8441,3287,43.50 "
        + "-8441,3288,44.51 -8441,3289,44.98 -8445,3289,41.44 -8440,3287,44.48 -8440,3288,44.98 -8440,3289,45.15 "
        ;

    /// <summary>Off a road's end at 49.09 over two floors and one stair up to 50.09, then the second leg from the landing at 43.68
    /// up to 49.68 beside it, switched back on its own pad on the way.</summary>
    private static readonly (char kind, string prefab, float x, float y, float z, float yaw)[] Beside =
    {
        (L, "blackmarble_floor", 6004.704f, 48.595f, 5355.549f, 67.50f),
        (L, "blackmarble_floor", 6005.786f, 48.595f, 5355.549f, 112.50f),
        (F, "blackmarble_floor", 6007.634f, 48.595f, 5354.784f, 112.50f),
        (S, "blackmarble_stair", 6006.868f, 49.095f, 5352.936f, 22.50f),
        (L, "blackmarble_floor", 6028.199f, 43.178f, 5335.141f, 315.00f),
        (L, "blackmarble_floor", 6026.892f, 43.178f, 5336.014f, 292.50f),
        (L, "blackmarble_floor", 6025.585f, 43.178f, 5336.886f, 315.00f),
        (L, "blackmarble_floor", 6024.585f, 43.178f, 5337.300f, 270.00f),
        (L, "blackmarble_floor", 6023.585f, 43.178f, 5337.714f, 315.00f),
        (L, "blackmarble_floor", 6022.171f, 43.178f, 5339.128f, 315.00f),
        (L, "blackmarble_floor", 6021.298f, 43.178f, 5340.435f, 337.50f),
        (F, "blackmarble_2x1x1", 6020.725f, 43.178f, 5341.821f, 157.50f),
        (F, "blackmarble_2x1x1", 6020.492f, 43.178f, 5341.976f, 90.00f),
        (S, "blackmarble_stair", 6018.992f, 43.678f, 5341.976f, 90.00f),
        (F, "blackmarble_2x1x1", 6017.492f, 44.178f, 5341.976f, 90.00f),
        (S, "blackmarble_stair", 6016.686f, 44.678f, 5342.435f, 112.50f),
        (F, "blackmarble_2x1x1", 6015.300f, 45.178f, 5343.009f, 112.50f),
        (S, "blackmarble_stair", 6014.730f, 45.678f, 5343.741f, 135.00f),
        (S, "blackmarble_stair", 6013.316f, 46.678f, 5345.155f, 135.00f),
        (L, "blackmarble_floor", 6011.902f, 47.178f, 5346.569f, 315.00f),
        (F, "blackmarble_2x1x1", 6010.842f, 47.178f, 5347.630f, 135.00f),
        (F, "blackmarble_2x1x1", 6010.787f, 47.178f, 5347.904f, 202.50f),
        (S, "blackmarble_stair", 6011.361f, 47.678f, 5349.290f, 202.50f),
        (L, "blackmarble_floor", 6012.126f, 48.178f, 5351.138f, 22.50f),
        (L, "blackmarble_floor", 6010.279f, 48.178f, 5351.903f, 22.50f),
        (F, "blackmarble_2x1x1", 6009.705f, 48.178f, 5350.517f, 22.50f),
        (S, "blackmarble_stair", 6008.972f, 48.678f, 5349.948f, 45.00f),
    };

    /// <summary>The finished ground round <see cref="Beside"/>: x,z,height.</summary>
    private const string BesideGround = ""
        + "6003,5354,49.09 6003,5355,49.08 6003,5356,48.95 6003,5357,49.07 6004,5354,48.95 6004,5355,48.95 6004,5356,48.95 6004,5357,48.93 6005,5354,48.99 "
        + "6005,5355,48.95 6005,5356,48.90 6005,5357,47.38 6006,5354,48.95 6006,5355,47.84 6006,5356,46.85 6007,5354,48.50 6007,5355,46.80 6006,5353,49.09 "
        + "6007,5353,49.09 6008,5353,48.77 6008,5354,46.47 6009,5353,47.16 6005,5351,50.38 6005,5352,49.62 6005,5353,49.26 6006,5351,50.06 6006,5352,49.09 "
        + "6007,5351,49.15 6007,5352,49.09 6008,5351,48.88 6008,5352,49.04 6027,5334,43.68 6027,5335,43.68 6027,5336,43.21 6027,5337,42.70 6028,5334,43.68 "
        + "6028,5335,43.68 6028,5336,43.21 6028,5337,42.70 6029,5334,43.68 6029,5335,43.68 6029,5336,43.16 6029,5337,42.61 6030,5334,43.68 6030,5335,43.68 "
        + "6030,5336,43.01 6030,5337,42.33 6025,5335,43.68 6025,5336,43.08 6025,5337,42.50 6025,5338,41.69 6026,5335,43.68 6026,5336,43.17 6026,5337,42.67 "
        + "6026,5338,41.83 6027,5338,41.82 6028,5338,41.72 6024,5335,43.68 6024,5336,42.94 6024,5337,42.39 6024,5338,41.49 6023,5336,42.80 6023,5337,42.33 "
        + "6023,5338,41.48 6025,5339,40.30 6026,5339,40.29 6022,5336,42.72 6022,5337,42.17 6022,5338,41.34 6021,5338,41.23 6021,5339,40.99 6021,5340,40.51 "
        + "6020,5339,41.90 6020,5340,41.19 6020,5341,40.77 6019,5340,42.31 6019,5341,41.61 6019,5342,41.00 6019,5343,41.00 6017,5340,44.87 6017,5341,44.00 "
        + "6017,5342,43.00 6017,5343,43.00 6018,5340,43.53 6018,5341,43.01 6018,5342,42.00 6018,5343,42.50 6016,5340,45.90 6016,5341,45.50 6016,5342,44.50 "
        + "6016,5343,44.00 6015,5341,46.00 6015,5342,45.50 6015,5343,45.50 6015,5344,45.50 6016,5344,44.50 6017,5344,43.00 6018,5344,42.50 6014,5342,46.50 "
        + "6014,5343,46.00 6014,5344,46.00 6014,5345,46.00 6015,5345,45.00 6016,5345,43.50 6017,5345,43.00 6013,5342,47.50 6013,5343,47.00 6013,5344,46.65 "
        + "6013,5345,46.50 6012,5344,47.22 6012,5345,47.15 6012,5346,46.50 6012,5347,46.50 6013,5346,45.50 6013,5347,45.50 6014,5346,45.00 6014,5347,45.00 "
        + "6015,5346,44.50 6015,5347,44.50 6010,5345,48.48 6010,5346,47.79 6010,5347,47.50 6010,5348,47.00 6011,5345,47.80 6011,5346,47.50 6011,5347,47.00 "
        + "6011,5348,46.50 6012,5348,46.00 6013,5348,45.00 6009,5346,48.71 6009,5347,48.50 6009,5348,48.50 6009,5349,48.00 6010,5349,47.00 6011,5349,46.50 "
        + "6012,5349,45.00 6010,5350,46.50 6010,5351,45.50 6011,5350,46.00 6011,5351,45.00 6012,5350,45.00 6009,5350,47.50 6009,5351,47.83 6009,5352,47.82 "
        + "6010,5352,45.50 6010,5353,45.50 6008,5349,49.29 6008,5350,49.02 6007,5348,50.40 6007,5349,49.97 6007,5350,49.57 6008,5348,49.44 "
        ;

    /// <summary>Where the second leg starts in <see cref="InLine"/> and <see cref="Beside"/>: its floor on the landing.</summary>
    private const int InLineSecond = 16, BesideSecond = 4;

    private static StairKit Kit => StairKit.Of("blackmarble");

    private static StairPiece Piece((char kind, string prefab, float x, float y, float z, float yaw) p) => new()
    {
        Kind = p.kind == S ? StairPieceKind.Stair : p.kind == L ? StairPieceKind.Landing : StairPieceKind.Filler,
        Prefab = p.prefab, Position = new Vector3(p.x, p.y, p.z), YawDegrees = p.yaw,
    };

    /// <summary>A leg as laid: its pieces, reached, its end its last stair's exit edge at its walking height, climbing on its
    /// heading (a stair faces back down its climb); the second laid from the landing up (Reversed).</summary>
    private static StairPlan Leg(IEnumerable<(char, string, float, float, float, float)> pieces, bool second)
    {
        var plan = new StairPlan { Kit = Kit.Name, Reached = true, Reversed = second };
        plan.Pieces.AddRange(pieces.Select(Piece));
        StairPiece top = plan.Pieces.Last(p => p.Kind == StairPieceKind.Stair);
        float climb = StairLayout.Wrap(top.YawDegrees + 180f);
        Vector2 edge = new Vector2(top.Position.x, top.Position.z) + StairLayout.Forward(climb) * (StairLayout.PieceRun / 2f);
        plan.End = new Vector3(edge.x, top.Position.y + StairLayout.PieceRise, edge.y);
        plan.EndHeading = climb;
        plan.LastRowKind = StairPieceKind.Stair;
        StairPiece foot = plan.Pieces[0];
        plan.StartHeading = foot.Kind == StairPieceKind.Stair ? StairLayout.Wrap(foot.YawDegrees + 180f) : foot.YawDegrees;
        plan.Stairs = plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair);
        plan.Landings = plan.Pieces.Count(p => p.Kind == StairPieceKind.Landing);
        return plan;
    }

    private static (StairPlan up, StairPlan down, Vector3 landing) Legs((char, string, float, float, float, float)[] laid, int second)
    {
        StairPlan down = Leg(laid.Skip(second), second: true);
        StairPiece floor = down.Pieces[0];
        return (Leg(laid.Take(second), second: false), down, new Vector3(floor.Position.x, floor.Position.y + Kit.FloorTop, floor.Position.z));
    }

    /// <summary>The finished ground from its whole-metre pins: between four pins, bilinear; else the nearest pin.</summary>
    private static Func<float, float, float> Ground(string pins)
    {
        var at = new Dictionary<(int, int), float>();
        foreach (string pin in pins.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] v = pin.Split(',');
            at[(int.Parse(v[0], CultureInfo.InvariantCulture), int.Parse(v[1], CultureInfo.InvariantCulture))] = float.Parse(v[2], CultureInfo.InvariantCulture);
        }
        return (x, z) =>
        {
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            if (at.TryGetValue((x0, z0), out float a) && at.TryGetValue((x0 + 1, z0), out float b)
                && at.TryGetValue((x0, z0 + 1), out float c) && at.TryGetValue((x0 + 1, z0 + 1), out float d))
            {
                float tx = x - x0, tz = z - z0;
                return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
            }
            return at.OrderBy(kv => (kv.Key.Item1 - x) * (kv.Key.Item1 - x) + (kv.Key.Item2 - z) * (kv.Key.Item2 - z)).First().Value;
        };
    }

    /// <summary>
    /// Two tops side by side: two stairs of the walk, each a top (no walked piece at its walking height just past its exit edge),
    /// their tops within a rise of each other, within <see cref="StairChain.PassClose"/>, climbing within
    /// <see cref="StairChain.BackTrackYaw"/> of the same way: up one, and back down the other beside it.
    /// </summary>
    internal static List<string> TopsSideBySide(StairPlan plan, StairKit kit)
    {
        static Vector2 At(StairPiece p) => new(p.Position.x, p.Position.z);
        float Walk(StairPiece p) => p.Kind == StairPieceKind.Stair ? p.Position.y : p.Position.y + kit.FloorTop;
        var walked = plan.Pieces.Where(p => p.Kind != StairPieceKind.Support).ToList();
        var tops = new List<(StairPiece p, float climb, float top)>();
        foreach (StairPiece s in walked.Where(p => p.Kind == StairPieceKind.Stair))
        {
            float climb = StairLayout.Wrap(s.YawDegrees + 180f), top = s.Position.y + StairLayout.PieceRise;
            Vector2 past = At(s) + StairLayout.Forward(climb) * (StairLayout.PieceRun / 2f + 0.5f);
            if (!walked.Any(q => q != s && Mathf.Abs(Walk(q) - top) < 0.3f && Vector2.Distance(At(q), past) < 1.2f)) tops.Add((s, climb, top));
        }
        var found = new List<string>();
        for (int i = 0; i < tops.Count; i++)
            for (int j = i + 1; j < tops.Count; j++)
            {
                var (a, ca, ta) = tops[i];
                var (b, cb, tb) = tops[j];
                float apart = Vector2.Distance(At(a), At(b));
                if (Mathf.Abs(ta - tb) <= StairLayout.PieceRise + 0.01f && apart <= StairChain.PassClose
                    && StairChain.YawApart(ca, cb) <= StairChain.BackTrackYaw + 0.1f)
                    found.Add(FormattableString.Invariant($"tops {ta:F2} and {tb:F2} at ({a.Position.x:F1}, {a.Position.z:F1}) and ({b.Position.x:F1}, {b.Position.z:F1}), {apart:F1} m apart, climbing {ca:F1} and {cb:F1}"));
            }
        return found;
    }

    [Fact]
    public void AsLaidBothWaysClimbPastWhereTheirLegsPassToTwoTopsSideBySide()
    {
        foreach (var (laid, second) in new[] { (InLine, InLineSecond), (Beside, BesideSecond) })
        {
            var (up, down, landing) = Legs(laid, second);
            StairPlan joined = RoadStairRuns.Join(up, down, landing);
            Assert.True(joined.Reached, joined.Reason);
            List<string> side = TopsSideBySide(joined, Kit);
            foreach (string line in side) _out.WriteLine(line);
            Assert.Single(side);
        }
    }

    /// <summary>
    /// The two legs in line over the spur, joined where they pass by the crest's owner (RoadStairRuns.JoinedWhereTheyPass, JoinLow's
    /// flat run): both cut back under their tops and joined level, the walk climbs no higher than where they pass, no two tops
    /// stand side by side, nothing lies over another piece nor doubles back, the run stands within the walkway cap, and both ends
    /// are where they were: the first leg's foot on the road's end and the second's on the landing, piece for piece.
    /// </summary>
    [Fact]
    public void LegsInLineOverASpurAreJoinedWhereTheyPassTheirEndsKept()
    {
        var (up, down, landing) = Legs(InLine, InLineSecond);
        Func<float, float, float> ground = Ground(InLineGround);
        StairPlan? joined = RoadStairRuns.JoinedWhereTheyPass(up, down, landing, Kit, ground, _ => true);
        Assert.NotNull(joined);
        foreach (StairPiece p in joined!.Pieces) _out.WriteLine(FormattableString.Invariant($"{p.Kind} ({p.Position.x:F2}, {p.Position.y:F2}, {p.Position.z:F2}) {p.YawDegrees}"));
        Assert.Empty(TopsSideBySide(joined, Kit));
        Assert.Empty(StairChain.BackTracks(joined.Pieces, null, Kit));
        // The second leg (from its floor on the landing) lies over none of the first, seen from above (RoadStairRuns.Join's test).
        int second = joined.Pieces.FindIndex(p => p.Kind == StairPieceKind.Landing && Vector3.Distance(p.Position, down.Pieces[0].Position) < 0.001f);
        Assert.True(second > 0 && joined.Reached && !StairLayout.OverlapsItself(joined, second), joined.Reason);
        float highest = joined.Pieces.Where(p => p.Kind == StairPieceKind.Stair).Max(p => p.Position.y + StairLayout.PieceRise);
        Assert.True(highest < up.End.y - StairLayout.PieceRise + 0.01f, FormattableString.Invariant($"climbs to {highest:F2}, the tops were at {up.End.y:F2}"));
        foreach (StairPiece f in joined.Pieces.Where(p => p.Kind == StairPieceKind.Landing))
            Assert.True(f.Position.y + Kit.FloorTop - ground(f.Position.x, f.Position.z) <= StairKit.WalkwayCap + 0.01f, $"floor at {f.Position} over the cap");
        // Both ends as laid: the road's end's first pieces and the landing's, piece for piece.
        static string Key(StairPiece p) => FormattableString.Invariant($"{p.Kind} {p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3} {p.YawDegrees:F2}");
        var keys = joined.Pieces.Select(Key).ToHashSet();
        foreach (StairPiece p in up.Pieces.Take(3).Concat(down.Pieces.Take(9))) Assert.Contains(Key(p), keys);
    }

    /// <summary>The one-stair climb whose second leg came back down beside it: no rows of the two pass a rise under both tops (the
    /// first is two floors and one stair), so no join where they pass: the approach's straight layout is tried first anyway, and where it does not
    /// reach the way is refused (its legs go on the same way, BothWaysAsTheyFellGoOnTheSameWayOffTheRidgeAndAreRefused).</summary>
    [Fact]
    public void AOneStairClimbBesideTheSecondLegHasNoJoinWhereTheyPass()
    {
        var (up, down, landing) = Legs(Beside, BesideSecond);
        Assert.Null(RoadStairRuns.JoinedWhereTheyPass(up, down, landing, Kit, Ground(BesideGround), _ => true));
    }

    /// <summary>
    /// Both ways as they fell go on the same way off the ridge (the road's end to the first's top, the second's top to the
    /// landing, under StairChain.BackTrackYaw apart): RoadStairRuns.LegsCarryOnTheSameWay, and a way laid as it falls so is refused
    /// (the owner's, 18 Nov 2026: "the stairs are going the same direction outside of the ridge - so should probably be rejected").
    /// </summary>
    [Fact]
    public void BothWaysAsTheyFellGoOnTheSameWayOffTheRidgeAndAreRefused()
    {
        foreach (var (laid, second, road, onto) in new[]
        {
            (InLine, InLineSecond, new Vector2(-8450.72f, 3309.40f), new Vector2(-8428.93f, 3263.19f)),
            (Beside, BesideSecond, new Vector2(6002.28f, 5355.09f), new Vector2(6028.91f, 5334.43f)),
        })
        {
            var (up, down, _) = Legs(laid, second);
            Vector2 firstTop = new(up.End.x, up.End.z), secondTop = new(down.End.x, down.End.z);
            float apart = StairChain.YawApart(StairLayout.Bearing(road, firstTop), StairLayout.Bearing(secondTop, onto));
            _out.WriteLine(FormattableString.Invariant($"legs {apart:F1} degrees apart"));
            Assert.True(RoadStairRuns.LegsCarryOnTheSameWay(road, firstTop, secondTop, onto), $"{apart} degrees apart");
        }
    }

    /// <summary>A ridge crossed with its legs going off it opposite ways (up one face, the second coming back down the same side
    /// beside it: a turn of 180), or turned 135 over a sharp ridge (StairChain's own example, up on 135 and down on 0), is kept:
    /// "there are some cases it makes sense such as when there is a ridge and the stairs do go opposite directions".</summary>
    [Theory]
    [InlineData(0f, 0f, 0f, 20f, 3f, 20f, 3f, 0f)]
    [InlineData(0f, 0f, 14f, -14f, 16f, -14f, 16f, 6f)]
    public void LegsGoingOffTheRidgeOppositeWaysAreKept(float rx, float rz, float ax, float az, float bx, float bz, float lx, float lz)
    {
        Assert.False(RoadStairRuns.LegsCarryOnTheSameWay(new Vector2(rx, rz), new Vector2(ax, az), new Vector2(bx, bz), new Vector2(lx, lz)));
    }
}
