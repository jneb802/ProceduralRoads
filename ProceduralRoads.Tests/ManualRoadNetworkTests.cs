using System;
using System.IO;
using Jotunn.Managers;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class ManualRoadNetworkTests : IDisposable
{
    public ManualRoadNetworkTests()
    {
        RoadNetworkGenerator.Reset();
        ZNet.instance = new ZNet { Server = true };
        ZRoutedRpc.instance = new ZRoutedRpc();
        ManualRoadNetworkSync.Register();
    }
    public void Dispose() { RoadNetworkGenerator.Reset(); ZNet.instance = new ZNet(); }

    private static void LoadLargeNetwork()
    {
        // More than one Steam message even before metadata is included.
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write(2); writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(40000);
        for (int i = 0; i < 40000; i++)
        { writer.Write((float)i); writer.Write(0f); writer.Write(4f); writer.Write(40f); writer.Write(false); }
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(data.ToArray()));
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    [Fact]
    public void LargeSnapshotsUseFragmentingTransportForJoinAndLiveAppend()
    {
        LoadLargeNetwork();
        ZNet.instance.Peers.Add(7, new ZNetPeer());
        var rpc = NetworkManager.Instance.Rpc;
        Assert.False(rpc.Server(7, new ZPackage()).MoveNext());
        ManualRoadNetworkSync.Publish();
        Assert.Equal(2, rpc.Sent.Count);
        Assert.All(rpc.Sent, p => { Assert.Equal(7, p.Peer); Assert.True(p.Package.Size() > 512 * 1024); });
    }

    [Fact]
    public void DisconnectedRequesterCannotSubscribeToSnapshots()
    {
        LoadLargeNetwork();
        var rpc = NetworkManager.Instance.Rpc;
        Assert.False(rpc.Server(123, new ZPackage()).MoveNext());
        ManualRoadNetworkSync.Publish();
        Assert.Empty(rpc.Sent);
    }

    [Fact]
    public void ClientOnlyAcceptsSnapshotsFromItsServer()
    {
        LoadLargeNetwork(); ZNet.instance.Peers.Add(7, new ZNetPeer());
        var rpc = NetworkManager.Instance.Rpc;
        Assert.False(rpc.Server(7, new ZPackage()).MoveNext());
        var package = Assert.Single(rpc.Sent).Package;
        RoadNetworkGenerator.Reset(); ZNet.instance.Server = false;
        package.SetPos(0);
        Assert.False(rpc.Client(999, package).MoveNext());
        Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
        package.SetPos(0);
        Assert.False(rpc.Client(42, package).MoveNext());
        Assert.Equal(40000, RoadSpatialGrid.TotalRoadPoints);
    }
}
