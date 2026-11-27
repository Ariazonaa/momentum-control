// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Services;
using Momentum4.Core.State;

namespace Momentum4.Core.Zones;

public enum ZoneOutcome
{
    /// <summary>Automatik aus, keine Zonen oder kein verbundenes Headset mit gelesenem EQ – kein Standort gelesen.</summary>
    Idle,

    /// <summary>Standort gelesen, aber kein neues, sicheres Betreten.</summary>
    NoEntry,

    /// <summary>Zone betreten, EQ-Kurve geschrieben und bestätigt.</summary>
    Applied,

    /// <summary>Zone betreten, aber Sound Check oder eine EQ-Aktion lief – Betreten verbraucht, nichts geschrieben.</summary>
    SkippedInteractive,

    /// <summary>Zone betreten, aber Verbindung, Gerät oder EQ haben sich während der Standortabfrage geändert.</summary>
    SkippedChanged,
}

public sealed record ZoneTickResult(ZoneOutcome Outcome, SoundZone? Zone = null);

/// <summary>
/// Ein Durchlauf der Sound-Zones-Automatik (Windows), mit den Schutzregeln der Linux-GUI: Ein Betreten wird genau
/// einmal verbraucht; geschrieben wird nur, wenn währenddessen kein Sound Check und keine EQ-Aktion läuft und sich
/// Verbindung, Gerät und EQ seit dem Start der Standortabfrage nicht geändert haben. Den Klangmodus wechselt sie nie.
/// </summary>
public sealed class ZoneAutomation(Func<CancellationToken, Task<LocationFix>> sample, Func<bool> eqInteractive)
{
    private readonly ZoneTracker _tracker = new();
    private readonly Lock _lock = new();

    public void Reset()
    {
        lock (_lock)
        {
            _tracker.Reset();
        }
    }

    public async Task<ZoneTickResult> TickAsync(ZoneData data, Momentum4Service service, string deviceAddress, CancellationToken cancellationToken)
    {
        var before = service.Store.Current;
        if (!data.Enabled || data.Zones.Count == 0
            || before is not { Connection: ConnectionState.Connected, Device: { } device, Equalizer: { } eq }
            || eq.Bands.Any(band => band.FrequencyHz is null))
        {
            return new ZoneTickResult(ZoneOutcome.Idle);
        }

        var fix = await sample(cancellationToken);
        SoundZone? zone;
        lock (_lock)
        {
            zone = _tracker.Update(fix, data.Zones, deviceAddress, device.ModelId, [.. eq.Bands.Select(band => band.FrequencyHz!.Value)], DateTimeOffset.Now);
        }

        if (zone is null)
        {
            return new ZoneTickResult(ZoneOutcome.NoEntry);
        }

        if (eqInteractive())
        {
            return new ZoneTickResult(ZoneOutcome.SkippedInteractive, zone);
        }

        var after = service.Store.Current;
        if (after.Connection != ConnectionState.Connected || after.Device != before.Device || !Equals(after.Equalizer, before.Equalizer))
        {
            return new ZoneTickResult(ZoneOutcome.SkippedChanged, zone);
        }

        await service.SetEqGainsAsync(zone.Preset.GainsDb, cancellationToken);
        return new ZoneTickResult(ZoneOutcome.Applied, zone);
    }
}
