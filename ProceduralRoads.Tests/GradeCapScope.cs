using System;

namespace ProceduralRoads.Tests;

/// <summary>
/// Sets the road grade cap to a positive value for the length of a using block and puts it back.
///
/// The suite runs sequentially (see TestAssemblyInfo), so setting the static
/// is safe; restoring it is not optional, because a leaked cap would change
/// every test that ran afterwards.
///
/// There is no scope with the cap off. A test of the height machinery -
/// smoothing, the terrain fit, the endpoint ramp - calls its owner directly
/// (RoadSpatialGrid.SmoothHeights, the terrain writer with the road the planner
/// leaves), and a whole road runs at the configured cap or another positive one.
/// A cap at or below zero (RoadGrade.Capped) is refused before anything changes.
/// The one zero cap left in the suite is EaseLengthTests' divisor-floor row,
/// which calls RoadGrade.EaseLength alone under its own restoring scope.
/// </summary>
internal sealed class GradeCap : IDisposable
{
    private readonly float m_previous;

    private GradeCap(float cap)
    {
        if (!RoadGrade.Capped(cap))
            throw new ArgumentOutOfRangeException(nameof(cap), cap, "a grade cap scope is a positive cap");
        m_previous = RoadGrade.Configured;
        RoadGrade.Configured = cap;
    }

    public static GradeCap At(float grade) => new GradeCap(grade);

    public void Dispose() => RoadGrade.Configured = m_previous;
}
