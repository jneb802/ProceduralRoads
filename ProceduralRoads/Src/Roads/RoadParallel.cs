namespace ProceduralRoads;

/// <summary>Bounded island workers. One worker provides the serial comparison.</summary>
public static class RoadParallel
{
    public static int Configured = 0;
    public static int IslandWorkers =>
        Configured > 0 ? Configured : System.Math.Max(1, System.Environment.ProcessorCount - 1);
}
