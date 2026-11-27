// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.Core.State;
using Momentum4.Core.Zones;
using Windows.Devices.Geolocation;

namespace Momentum4.App.Zones;

/// <summary>Standort nicht verfügbar (Datenschutzeinstellung, kein Dienst, Zeitüberschreitung) – mit lesbarem Grund.</summary>
public sealed class ZoneLocationException(string message) : Exception(message);

/// <summary>
/// Lokale Sound Zones unter Windows, wie im Linux-Frontend: Ist die Automatik an, fragt die App höchstens einmal pro
/// Minute den Windows-Standort ab und wendet beim sicheren Betreten einer Zone einmal deren EQ-Kurve an – nur für das
/// Headset, Modell und die Bandfrequenzen, für die die Zone gespeichert wurde, und nie mit Wechsel des Klangmodus.
/// Verlassen ändert nichts. Lebt im <see cref="HeadsetHost"/>, damit es auch im Tray-Betrieb läuft.
/// </summary>
public sealed class ZoneController : IAsyncDisposable
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(60);

    private readonly HeadsetHost _host;
    private readonly ILogger _logger;
    private readonly ZoneAutomation _automation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    public ZoneController(HeadsetHost host, string path, ILogger logger)
    {
        _host = host;
        _logger = logger;
        _automation = new ZoneAutomation(SampleAsync, () => host.EqInteractive);
        Store = new ZoneStore(path);
        try
        {
            Data = Store.Load();
        }
        catch (ZoneException ex)
        {
            Data = new ZoneData(false, []);
            LoadError = ex.Message;
            _logger.LogWarning("Sound Zones nicht geladen: {Message}", ex.Message);
        }

        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Zonen geändert, angewendet oder Standortfehler – die Oberfläche liest dann <see cref="Data"/> und <see cref="LastEvent"/>.</summary>
    public event EventHandler? Changed;

    public ZoneStore Store { get; }

    public ZoneData Data { get; private set; }

    /// <summary>Ein ungültiges <c>zones.json</c> wird nicht überschrieben; die Automatik bleibt dann aus.</summary>
    public string? LoadError { get; }

    /// <summary>Letzte Meldung für die Oberfläche (Zone angewendet, Standort nicht verfügbar …).</summary>
    public string LastEvent { get; private set; } = string.Empty;

    public void SetEnabled(bool enabled) => Save(Data with { Enabled = enabled }, reset: true);

    public void Add(SoundZone zone) => Save(Data with { Zones = [.. Data.Zones, zone] }, reset: false);

    public void Remove(string name) => Save(Data with { Zones = [.. Data.Zones.Where(zone => zone.Name != name)] }, reset: true);

    /// <summary>Einmal den Windows-Standort lesen (für „Aktuellen Ort verwenden“ und die Automatik).</summary>
    public static async Task<LocationFix> SampleAsync(CancellationToken cancellationToken)
    {
        GeolocationAccessStatus access;
        try
        {
            access = await Geolocator.RequestAccessAsync().AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ZoneLocationException(Localization.L.Get("ZoneLocationUnavailable"));
        }

        if (access != GeolocationAccessStatus.Allowed)
        {
            throw new ZoneLocationException(Localization.L.Get("ZoneLocationDenied"));
        }

        try
        {
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)).AsTask(cancellationToken);
            var coordinate = position.Coordinate;
            return new LocationFix(coordinate.Point.Position.Latitude, coordinate.Point.Position.Longitude, coordinate.Accuracy, coordinate.Timestamp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ZoneLocationException(Localization.L.Get("ZoneLocationUnavailable"));
        }
    }

    private void Save(ZoneData data, bool reset)
    {
        Store.Save(data);
        Data = data;
        if (reset)
        {
            _automation.Reset();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                await TickAsync(_stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_host.Service is not { } service || _host.Address is not { } address || !await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            // Zonennamen sind private Ortsangaben: ins Log (und damit in die Diagnose) kommt nur das Ergebnis.
            var result = await _automation.TickAsync(Data, service, address.ToString(), cancellationToken);
            switch (result)
            {
                case { Outcome: ZoneOutcome.Applied, Zone: { } applied }:
                    _logger.LogInformation("Sound Zone betreten – EQ-Kurve angewendet.");
                    Report(Localization.L.Get("ZoneApplied", applied.Name, applied.Preset.Name));
                    break;
                case { Outcome: ZoneOutcome.SkippedInteractive, Zone: { } busy }:
                    _logger.LogInformation("Sound Zone betreten – nicht angewendet (Sound Check oder EQ-Aktion lief).");
                    Report(Localization.L.Get("ZoneSkippedInteractive", busy.Name));
                    break;
                case { Outcome: ZoneOutcome.SkippedChanged, Zone: { } changed }:
                    _logger.LogInformation("Sound Zone betreten – nicht angewendet (EQ oder Verbindung hat sich geändert).");
                    Report(Localization.L.Get("ZoneSkippedChanged", changed.Name));
                    break;
            }
        }
        catch (ZoneLocationException ex)
        {
            if (LastEvent != ex.Message)
            {
                Report(ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Sound Zone nicht angewendet: {Message}", ex.Message);
            Report(Localization.L.Get("ZoneNotApplied", ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Report(string message)
    {
        LastEvent = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
        _gate.Dispose();
    }
}
