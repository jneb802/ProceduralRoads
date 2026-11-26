using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>What this peer does by role: only a server (or the hosting player) holds a road network, and every location
/// that asks reads RoadLifecycleManager.HoldsTheNetwork.</summary>
public sealed class ServerRoleTests : System.IDisposable
{
    private readonly ValheimWorldScope _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void OnlyAServerHoldsTheNetwork()
    {
        _world.WithNetwork(server: true);
        Assert.True(RoadLifecycleManager.HoldsTheNetwork);
        _world.WithNetwork(server: false);
        Assert.False(RoadLifecycleManager.HoldsTheNetwork);
    }

    [Fact]
    public void AClientIsRefusedANetworkOperationInTheOneWording()
    {
        _world.WithNetwork(server: false);
        Assert.Equal("Roads are generated only on the server; this peer holds no road network",
            RoadLifecycleManager.BeginForOperator(null, _ => { }));
    }
}
