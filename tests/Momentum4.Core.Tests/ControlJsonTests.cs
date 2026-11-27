// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Momentum4.Core.Control;
using Momentum4.Core.State;

namespace Momentum4.Core.Tests;

/// <summary>
/// <c>m4ctl status --json</c> und <c>m4ctl peers --json</c> geben dasselbe Schema aus wie <c>momentumctl</c> unter Linux
/// (linux/momentum_control/models.py <c>snapshot_from_dict</c> prüft genau diese Schlüssel), damit Skripte auf beiden
/// Systemen laufen.
/// </summary>
public sealed class ControlJsonTests
{
    private static readonly string[] SnapshotKeys =
    [
        "model", "firmware", "battery", "settings", "eq_bands", "eq_frequencies", "eq_range", "writable", "notes", "choices",
        "device_name", "charging", "wear_state", "personalization_state", "peers", "max_connections", "peers_available", "verified_session",
    ];

    [Fact(Timeout = 15000)]
    public async Task Status_json_has_the_linux_snapshot_schema()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartWindowsAsync();

        var result = await ControlCommands.ExecuteAsync(["status", "--json"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Assert.Equal(SnapshotKeys.Order(), root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("M5AEBT", root.GetProperty("model").GetString());
        Assert.Equal("6.21.5", root.GetProperty("firmware").GetString());
        Assert.False(root.GetProperty("settings").GetProperty("immersive_audio").GetBoolean());
        Assert.Equal(3, root.GetProperty("settings").GetProperty("sidetone").GetInt32());
        Assert.Contains("immersive_audio", root.GetProperty("writable").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { 0, 1 }, root.GetProperty("choices").GetProperty("antiwind").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(8, root.GetProperty("eq_bands").GetArrayLength());
        Assert.True(root.GetProperty("verified_session").GetBoolean());
    }

    [Fact(Timeout = 15000)]
    public async Task Status_json_matches_a_fresh_shared_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 38, 0, 3] };
        await using var service = await fixture.StartWindowsAsync();

        var fromCli = JsonDocument.Parse((await ControlCommands.ExecuteAsync(["status", "--json"], service, null, ct)).Output).RootElement;
        var fromCore = JsonDocument.Parse(HeadsetSnapshotJson.Serialize(await service.GetControlSnapshotAsync(ct))).RootElement;

        foreach (var key in new[] { "model", "firmware", "battery", "settings", "eq_bands", "eq_frequencies", "eq_range", "writable", "choices", "device_name", "max_connections" })
        {
            Assert.Equal(fromCore.GetProperty(key).GetRawText(), fromCli.GetProperty(key).GetRawText());
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Peers_json_lists_devices_like_momentumctl()
    {
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 38, 0, 3] };
        await using var service = await fixture.StartWindowsAsync();

        var result = await ControlCommands.ExecuteAsync(["peers", "--json"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var root = JsonDocument.Parse(result.Output).RootElement;
        Assert.Equal(new[] { "available", "max_connections", "peers", "writable" }, root.EnumerateObject().Select(p => p.Name).Order());
        var first = root.GetProperty("peers")[0];
        Assert.Equal(new[] { "connected", "index", "is_this_pc", "name", "priority" }, first.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(first.GetProperty("is_this_pc").GetBoolean());
        Assert.True(root.GetProperty("available").GetBoolean());
    }

    [Fact(Timeout = 15000)]
    public async Task Peers_without_json_prints_a_readable_list()
    {
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 38, 0, 3] };
        await using var service = await fixture.StartWindowsAsync();

        var result = await ControlCommands.ExecuteAsync(["peers"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("(dieser PC)", result.Output, StringComparison.Ordinal);
    }
}
