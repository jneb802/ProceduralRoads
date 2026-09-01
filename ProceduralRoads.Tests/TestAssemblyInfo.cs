using Xunit;

// The mod under test is built on static state (WorldGenerator.instance,
// RoadSpatialGrid, RoadNetworkGenerator) — parallel test classes would race
// on it, so the suite runs sequentially. It completes in seconds regardless.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
