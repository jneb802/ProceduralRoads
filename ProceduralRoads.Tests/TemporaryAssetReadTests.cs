using System;
using Xunit;

namespace ProceduralRoads.Tests;

public class TemporaryAssetReadTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("before-acquire")]
    [InlineData("after-acquire")]
    [InlineData("read")]
    public void ReturnsOnlyItsOwnReferenceOnEveryExit(string failure)
    {
        uint references = 3;
        int releases = 0;
        Func<int> run = () => TemporaryAssetRead.Read(
            () => references,
            () =>
            {
                if (failure == "before-acquire") throw new InvalidOperationException();
                references++;
                if (failure == "after-acquire") throw new InvalidOperationException();
            },
            () => { references--; releases++; },
            () => failure == "read" ? throw new InvalidOperationException() : 42);
        if (failure == "success") Assert.Equal(42, run());
        else Assert.Throws<InvalidOperationException>(() => run());
        Assert.Equal(3u, references);
        Assert.Equal(failure == "before-acquire" ? 0 : 1, releases);
    }
}
