// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Zones;

namespace Momentum4.Core.Tests;

/// <summary>
/// Automatik der Sound Zones unter Windows: einmal beim Betreten anwenden, sonst nichts schreiben – nicht während
/// eines Sound Checks oder einer EQ-Aktion und nicht, wenn sich EQ oder Gerät während der Standortabfrage geändert haben
/// (wie die Linux-GUI, gui.py <c>apply_zone</c>).
/// </summary>
public sealed class ZoneAutomationTests
{
    private const string Address = "AA:BB:CC:DD:EE:FF";

    private static async Task<(SharedHeadsetFixture Fixture, Momentum4Service Service, ZoneData Data)> StartAsync()
    {
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 38, 0, 3] };
        var service = await fixture.StartWindowsAsync();
        var eq = service.Store.Current.Equalizer!;
        var gains = eq.Bands.Select((_, band) => band == 0 ? 3.0m : 0m).ToArray();
        var preset = new ZonePreset("Office", "M4AEBT", [.. eq.Bands.Select(b => b.FrequencyHz!.Value)], gains);
        return (fixture, service, new ZoneData(true, [new SoundZone("Office", Address, 50, 8, 200, preset)]));
    }

    private static Func<CancellationToken, Task<LocationFix>> At(double latitude, double longitude, Action? during = null) => _ =>
    {
        during?.Invoke();
        return Task.FromResult(new LocationFix(latitude, longitude, 10, DateTimeOffset.Now));
    };

    [Fact(Timeout = 15000)]
    public async Task Applies_the_zone_preset_once_on_entry()
    {
        var (fixture, service, data) = await StartAsync();
        await using var _ = service;
        var automation = new ZoneAutomation(At(50, 8), () => false);

        var first = await automation.TickAsync(data, service, Address, TestContext.Current.CancellationToken);
        var writes = fixture.Writes.Count;
        var second = await automation.TickAsync(data, service, Address, TestContext.Current.CancellationToken);

        Assert.Equal(ZoneOutcome.Applied, first.Outcome);
        Assert.Equal(3.0m, service.Store.Current.Equalizer!.Bands[0].GainDb);
        Assert.Equal(ZoneOutcome.NoEntry, second.Outcome);
        Assert.Equal(writes, fixture.Writes.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task Skips_and_consumes_the_entry_while_eq_is_interactive()
    {
        var (fixture, service, data) = await StartAsync();
        await using var _ = service;
        var writes = fixture.Writes.Count;
        var interactive = true;
        var automation = new ZoneAutomation(At(50, 8), () => interactive);

        var skipped = await automation.TickAsync(data, service, Address, TestContext.Current.CancellationToken);
        interactive = false;
        var later = await automation.TickAsync(data, service, Address, TestContext.Current.CancellationToken);

        Assert.Equal(ZoneOutcome.SkippedInteractive, skipped.Outcome);
        Assert.Equal(ZoneOutcome.NoEntry, later.Outcome);
        Assert.Equal(writes, fixture.Writes.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task Skips_when_the_eq_changed_while_locating()
    {
        var (fixture, service, data) = await StartAsync();
        await using var _ = service;
        var writes = fixture.Writes.Count;
        var eq = service.Store.Current.Equalizer!;
        var changed = eq with { Bands = [.. eq.Bands.Select(b => b with { GainDb = 1.0m })] };
        var automation = new ZoneAutomation(At(50, 8, () => service.Store.Apply(new EqualizerChanged(changed))), () => false);

        var result = await automation.TickAsync(data, service, Address, TestContext.Current.CancellationToken);

        Assert.Equal(ZoneOutcome.SkippedChanged, result.Outcome);
        Assert.Equal(writes, fixture.Writes.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task Does_nothing_when_disabled_or_outside()
    {
        var (fixture, service, data) = await StartAsync();
        await using var _ = service;
        var writes = fixture.Writes.Count;
        var located = 0;
        var automation = new ZoneAutomation(_ =>
        {
            located++;
            return Task.FromResult(new LocationFix(10, 10, 10, DateTimeOffset.Now));
        }, () => false);

        var disabled = await automation.TickAsync(data with { Enabled = false }, service, Address, TestContext.Current.CancellationToken);
        var outside = await automation.TickAsync(data, service, Address, TestContext.Current.CancellationToken);

        Assert.Equal(ZoneOutcome.Idle, disabled.Outcome);
        Assert.Equal(1, located);
        Assert.Equal(ZoneOutcome.NoEntry, outside.Outcome);
        Assert.Equal(writes, fixture.Writes.Count);
    }
}
