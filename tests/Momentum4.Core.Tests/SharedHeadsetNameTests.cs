using Momentum4.Core.Services;
using Momentum4.Core.State;

namespace Momentum4.Core.Tests;

/// <summary>GitHub #10: Gerätenamen dürfen den Snapshot nicht kippen.</summary>
public sealed class SharedHeadsetNameTests
{
    [Fact(Timeout = 15000)]
    public async Task Peer_name_with_no_break_space_is_read_verbatim()
    {
        var fixture = M4();
        fixture.Headset.PeerNames[1] = "Apple Watch de X";
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Apple Watch de X", snapshot.Peers[1].Name);
        Assert.True(snapshot.PeersAvailable);
    }

    [Fact(Timeout = 15000)]
    public async Task Unreadable_peer_name_skips_only_that_entry_and_keeps_session()
    {
        var fixture = M4();
        fixture.Headset.PeerNames[2] = "bad\u0007name";
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "PC-A", "Laptop-B", "PC-D" }, snapshot.Peers.Select(peer => peer.Name));
        Assert.False(snapshot.PeersAvailable);
        Assert.DoesNotContain(HeadsetControl.ConnectPeer, snapshot.Writable);
        Assert.Contains(snapshot.Notes, note => note.Contains("unreadable device name", StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Unreadable_own_name_disables_rename_and_keeps_session()
    {
        var fixture = M4();
        fixture.Overrides[0x2802] = [.. "bad\nname"u8];
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Null(snapshot.DeviceName);
        Assert.DoesNotContain(HeadsetControl.Rename, snapshot.Writable);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
    }

    [Fact(Timeout = 15000)]
    public async Task Malformed_peer_frame_still_revokes_session()
    {
        var fixture = M4();
        fixture.Overrides[0x1401] = [7, 0, 1, 80, 67];
        await using var service = await fixture.StartAsync();
        await Assert.ThrowsAsync<Momentum4.Protocol.ProtocolFormatException>(() => service.GetControlSnapshotAsync(TestContext.Current.CancellationToken));
    }

    private static SharedHeadsetFixture M4() => new() { Model = "M4AEBT", Firmware = [0, 3, 0, 37, 0, 3] };
}
