using Xunit;
namespace ProceduralRoads.Tests;
public class SharedTerrainParityTests
{
    [Theory]
    [InlineData(true, true)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public void ExtractedModelPreservesExistingSyntheticInputs(bool river, bool mountain)
    {
        var old = new LegacySyntheticWorldFixture { HasRiver = river, HasMountain = mountain };
        var current = new SyntheticWorld { HasRiver = river, HasMountain = mountain };
        for (int x = -700; x <= 700; x += 17)
        for (int z = -650; z <= 650; z += 23)
        {
            Assert.Equal(old.GetHeight(x, z), current.GetHeight(x, z));
            Assert.Equal(old.GetBiome(x, z), current.GetBiome(x, z));
            old.GetRiverWeight(x, z, out float ow, out float os); current.GetRiverWeight(x, z, out float nw, out float ns);
            Assert.Equal(ow, nw); Assert.Equal(os, ns);
        }
    }
}
