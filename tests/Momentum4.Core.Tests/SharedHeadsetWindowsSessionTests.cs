using System.Globalization;
using System.Text.Json;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

/// <summary>
/// Die Windows-App startet den Service mit Notifications, Seriennummer und Polling. Ein MOMENTUM 5 darf dabei nur
/// senden, was der am Gerät geprüfte Linux-Ablauf sendet.
/// </summary>
public sealed class SharedHeadsetWindowsSessionTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 15000)]
    public async Task M5_windows_session_sends_only_commands_seen_in_m5_captures()
    {
        var allowed = M5CaptureCommands();
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();
        await RunPollsAsync(fixture, 2, TestContext.Current.CancellationToken);

        var unexpected = fixture.Requests.Select(Key).Where(key => !allowed.Contains(key)).Distinct().ToList();
        Assert.Empty(unexpected);
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value == 0x0007);
        Assert.DoesNotContain(fixture.Requests, frame => frame.Vendor == 0x001D && frame.Command.Value == 0x0003);
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value is 0x0800 or 0x0807);
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task M5_other_firmware_windows_session_reads_only_shared_snapshot()
    {
        var fixture = new SharedHeadsetFixture { Firmware = [0, 6, 0, 21, 0, 6] };
        await using var service = await fixture.StartWindowsAsync();
        await RunPollsAsync(fixture, 1, TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value is 0x0601 or 0x1801);
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task M5_windows_session_polls_at_most_every_30_seconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();
        await RunPollsAsync(fixture, 0, ct);
        var afterConnect = fixture.Requests.Count;

        await RunPollsAsync(fixture, 2, ct); // 20 s: wie der Linux-Standard noch kein neuer Snapshot
        Assert.Equal(afterConnect, fixture.Requests.Count);

        await RunPollsAsync(fixture, 1, ct); // 30 s
        Assert.True(fixture.Requests.Count > afterConnect);
    }

    [Fact(Timeout = 15000)]
    public async Task M5_session_ignores_unsolicited_frames()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();
        Assert.False(service.Store.Current.Charging);

        fixture.Transport.Receive(GaiaFrameCodec.Encode(0x0495, new CommandWord(0x0682), new byte[] { 1 }));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(service.Store.Current.Charging);
    }

    [Fact(Timeout = 15000)]
    public async Task M4_windows_session_still_registers_notifications()
    {
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 38, 0, 3] };
        await using var service = await fixture.StartWindowsAsync();
        await RunPollsAsync(fixture, 0, TestContext.Current.CancellationToken);

        Assert.Contains(fixture.Requests, frame => frame.Command.Value == 0x0007);
    }

    [Fact(Timeout = 15000)]
    public async Task M5_windows_session_exposes_extended_values()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();
        await RunPollsAsync(fixture, 0, TestContext.Current.CancellationToken);
        var state = service.Store.Current;

        Assert.False(state.ImmersiveAudio);
        Assert.False(state.HeadTracking);
        Assert.True(state.BatteryProtection);
        Assert.Equal(100, state.TransparencyLevel);
        Assert.Equal(3, state.Sidetone);
        Assert.True(state.CanWrite(HeadsetControl.ImmersiveAudio));
        Assert.True(state.CanWrite(HeadsetControl.Sidetone));
        Assert.Equal(new[] { 0, 1 }, state.ControlChoices![HeadsetIntegerControl.AntiWind]);
    }

    [Fact(Timeout = 15000)]
    public async Task M4_windows_session_has_sidetone_value_but_no_m5_values()
    {
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 38, 0, 3] };
        await using var service = await fixture.StartWindowsAsync();
        await RunPollsAsync(fixture, 0, TestContext.Current.CancellationToken);
        var state = service.Store.Current;

        Assert.Equal(3, state.Sidetone);
        Assert.Null(state.ImmersiveAudio);
        Assert.Null(state.HeadTracking);
        Assert.Null(state.BatteryProtection);
        Assert.Null(state.TransparencyLevel);
        Assert.False(state.CanWrite(HeadsetControl.Sidetone));
        Assert.False(state.CanWrite(HeadsetControl.ImmersiveAudio));
        Assert.True(state.CanWrite(HeadsetControl.SmartPause));
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value is 0x060b or 0x3603 or 0x3605 or 0x1803);
    }

    [Fact(Timeout = 15000)]
    public async Task Typed_setters_round_trip_on_m5()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();

        Assert.True(await service.SetImmersiveAudioAsync(true, ct));
        Assert.True(await service.SetHeadTrackingAsync(true, ct));
        Assert.False(await service.SetBatteryProtectionAsync(false, ct));
        Assert.Equal(1, await service.SetSidetoneAsync(1, ct));
        Assert.Equal(50, await service.SetTransparencyLevelAsync(50, ct));

        var state = service.Store.Current;
        Assert.True(state.ImmersiveAudio);
        Assert.True(state.HeadTracking);
        Assert.False(state.BatteryProtection);
        Assert.Equal(1, state.Sidetone);
        Assert.Equal(50, state.TransparencyLevel);
    }

    [Fact(Timeout = 15000)]
    public async Task Typed_setter_reports_rejection_with_confirmed_value()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();
        fixture.Overrides[0x060b] = [1];

        var failure = await Assert.ThrowsAsync<SettingNotAppliedException<bool>>(() => service.SetBatteryProtectionAsync(false, TestContext.Current.CancellationToken));
        Assert.True(failure.Actual);
    }

    [Fact(Timeout = 15000)]
    public async Task Device_name_limit_is_counted_in_utf8_bytes()
    {
        Assert.Equal(16, ProductName.MaxNameBytes("M5AEBT Black"));
        Assert.Equal(30, ProductName.MaxNameBytes("M4AEBT Black"));
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();

        Assert.Equal("Ääääääää", await service.SetDeviceNameAsync("Ääääääää", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetDeviceNameAsync("Äääääääää", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("M4AEBT Black", "MOMENTUM 4")]
    [InlineData("M5AEBT", "MOMENTUM 5")]
    [InlineData("Foreign", "MOMENTUM")]
    [InlineData(null, "MOMENTUM")]
    public void Product_name_follows_the_model(string? model, string expected) => Assert.Equal(expected, ProductName.For(model));

    /// <summary>Dreht die Fake-Uhr <paramref name="polls"/> Poll-Intervalle weiter und wartet, bis keine Requests mehr kommen.</summary>
    private static async Task RunPollsAsync(SharedHeadsetFixture fixture, int polls, CancellationToken ct)
    {
        for (var poll = 0; poll < polls; poll++)
        {
            fixture.Time!.Advance(PollInterval);
            var seen = -1;
            while (seen != fixture.Requests.Count)
            {
                seen = fixture.Requests.Count;
                await Task.Delay(50, ct);
            }
        }
    }

    private static (ushort Vendor, ushort Command) Key(GaiaFrame frame) => (frame.Vendor, frame.Command.Value);

    /// <summary>Alle (Vendor, Command) aus den Requests der M5-Mitschnitte unter tests/CapturedPackets/M5.</summary>
    private static HashSet<(ushort Vendor, ushort Command)> M5CaptureCommands()
    {
        var allowed = new HashSet<(ushort, ushort)>();
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "CapturedPackets", "M5"), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in document.RootElement.GetProperty("exchanges").EnumerateArray())
            {
                if (row.TryGetProperty("tx", out var tx))
                {
                    allowed.Add(Key(new GaiaFrameReader().Push(Hex.Parse(tx.GetString()!)).Frames.Single()));
                }
                else if (row.TryGetProperty("command", out var command))
                {
                    allowed.Add((0x0495, ushort.Parse(command.GetString()!.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                }
            }
        }

        return allowed;
    }
}
