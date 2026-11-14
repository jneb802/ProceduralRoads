using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Switchbacks (one owner): two floors abreast, a flight of at least MinFlightBetweenSwitchbacks stairs between two and of
/// FirstFlightStairs from the foot before the first, never at a staircase's end, and dearer on steep ground than on a bench
/// (the tree-well rule).
/// </summary>
public static partial class StairLayout
{
    private const float SwitchbackWeight = 1f;

    /// <summary>Whether a switchback is laid after the last stair: the staircase would end turned back.</summary>
    private static bool EndsTurnedBack(Node node)
    {
        for (Node? n = node; n != null && n.Parent != null; n = n.Parent)
        {
            if (n.Switchback != 0) return true;
            if (n.Kind == StairPieceKind.Stair) return false;
        }
        return false;
    }

    /// <summary>
    /// Whether a switchback has been laid since the last stair. Two switchbacks
    /// with only floors between them turn a landing into a pad that shuffles the
    /// stair 4 m sideways. Switchbacks may come in spurts, but a flight climbs between.
    /// The foot counts as one: a switchback a stair up from it stood its pad over the
    /// road the staircase leaves (the bottom of the stairs was blocked off by the lower switchback).
    /// </summary>
    private static bool SwitchbackSinceLastStair(Node node)
    {
        int stairs = 0;
        Node? root = node;
        for (Node? n = node; n != null && n.Parent != null; n = n.Parent)
        {
            if (n.Switchback != 0) return stairs < MinFlightBetweenSwitchbacks;
            if (n.Kind == StairPieceKind.Stair) stairs++;
            root = n.Parent;
        }
        // From the foot itself (not carried on from a row already laid): the first flight is longer.
        return stairs < (root != null && !root.Placed ? FirstFlightStairs : MinFlightBetweenSwitchbacks);
    }

    /// <summary>
    /// Stairs a staircase climbs from its foot before its first switchback (at the foot of Hildir_cave 1664,768: no
    /// switchbacks at the start of a stair run for at least 5 steps up). Bends on the way
    /// are allowed; a switchback is the turn back on a floor beside the lane.
    /// </summary>
    public const int FirstFlightStairs = 5;

    /// <summary>
    /// Stairs a flight climbs between two switchbacks. One let a narrow
    /// corridor fill a face with mini switchbacks (a stretch of mini switchbacks looks unintended);
    /// switchbacks come in spurts, with flights between.
    /// </summary>
    public const int MinFlightBetweenSwitchbacks = 3;

    /// <summary>
    /// Like a back country skier aiming a turn at a tree well but cutting across a steep powder slope between,
    /// a switchback, the staircase's turn, is
    /// priced by how steep the ground is round its pad. Up to <see cref="BenchGrade"/> (a bench,
    /// ledge or shoulder) it costs what it always did; steeper, <see cref="SwitchbackSteepWeight"/>
    /// more for each unit of grade over it, up to <see cref="SteepCap"/>. The flight's own small
    /// bends (22.5 and 45 degrees) are not: priced too, every bend on Hildir's cave's 2:1 face
    /// cost more and the road's staircase allowance stalled short of the shelf.
    /// </summary>
    public const float BenchGrade = 1f, SteepCap = 3f, SwitchbackSteepWeight = 4f;

    /// <summary>How far round a turn the ground is read: a switchback's pad reaches 2 m either side of
    /// its middle, and a metre more is the room round it.</summary>
    public const float TurnRoom = 3f;

    /// <summary>The ground's grade round <paramref name="p"/>: the steepest of the four ways out to
    /// <see cref="TurnRoom"/>, so a drop just past a turn counts as a slope under it does.</summary>
    internal static float Steepness(Vector2 p, Func<float, float, float> ground)
    {
        float h = ground(p.x, p.y), steepest = 0f;
        steepest = Mathf.Max(steepest, Mathf.Abs(ground(p.x + TurnRoom, p.y) - h));
        steepest = Mathf.Max(steepest, Mathf.Abs(ground(p.x - TurnRoom, p.y) - h));
        steepest = Mathf.Max(steepest, Mathf.Abs(ground(p.x, p.y + TurnRoom) - h));
        steepest = Mathf.Max(steepest, Mathf.Abs(ground(p.x, p.y - TurnRoom) - h));
        return steepest / TurnRoom;
    }

    private static float Excess(float steep) => Mathf.Clamp(steep - BenchGrade, 0f, SteepCap - BenchGrade);

    /// <summary>Added to a switchback whose pad lies on ground this steep.</summary>
    public static float SwitchbackSteepPrice(float steep) => SwitchbackSteepWeight * Excess(steep);
}
