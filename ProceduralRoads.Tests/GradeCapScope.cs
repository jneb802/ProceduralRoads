using System;

namespace ProceduralRoads.Tests;

/// <summary>
/// Sets the road grade cap for the length of a using block and puts it back.
///
/// The suite runs sequentially (see TestAssemblyInfo), so setting the static
/// is safe; restoring it is not optional, because a leaked cap would change
/// every test that ran afterwards.
///
/// A test that sets the cap to zero is saying its subject is the height
/// machinery - smoothing, the terrain fit, the endpoint ramp - on a slope
/// steeper than any road the cap would allow. That is a real thing to test:
/// the reviewer's endpoint reproduction is a plane rising half a metre per
/// metre, and it has to keep reproducing on exactly that slope. It is not a
/// way of getting out from under the cap, and the cap has its own tests on
/// the same worlds.
/// </summary>
internal sealed class GradeCap : IDisposable
{
    private readonly float m_previous;

    private GradeCap(float cap)
    {
        m_previous = RoadGrade.Configured;
        RoadGrade.Configured = cap;
    }

    public static GradeCap Off() => new GradeCap(0f);
    public static GradeCap At(float grade) => new GradeCap(grade);

    public void Dispose() => RoadGrade.Configured = m_previous;
}
