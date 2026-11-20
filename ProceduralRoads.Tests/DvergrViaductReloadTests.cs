using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A network saved with a valley viaduct gives the viaduct back on a reload: crossings are
/// restored, so <see cref="RoadCrossing.ValleyJump"/> is saved with them, and the reload plans the same pieces and
/// puts the viaduct's keep-out back. Once nothing came back at all: the reader refused every network with a
/// staircase's cuts, and ValleyJump was not saved either.
/// </summary>
public sealed class DvergrViaductReloadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public DvergrViaductReloadTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }


}
