using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One actor's disposable waypoint list. Built roads live in world persistence.</summary>
public sealed class ManualRoadDraft
{
    public const int MaxAppends = 4096;
    public static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    public static bool ValidPoint(Vector2 p) => IsFinite(p.x) && IsFinite(p.y) && p.sqrMagnitude < 10000f * 10000f;
}
