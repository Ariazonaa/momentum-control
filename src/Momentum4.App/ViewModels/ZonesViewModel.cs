// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momentum4.App.Localization;
using Momentum4.App.Zones;
using Momentum4.Core.State;
using Momentum4.Core.Zones;

namespace Momentum4.App.ViewModels;

public sealed partial class ZoneItemViewModel(SoundZone zone, Action<ZoneItemViewModel> remove) : ObservableObject
{
    public SoundZone Zone { get; } = zone;

    public string Title => Zone.Name;

    public string Detail => L.Get("ZoneDetail", Zone.RadiusM.ToString(CultureInfo.CurrentCulture), Zone.Preset.Name);

    [RelayCommand]
    private void Remove() => remove(this);
}

/// <summary>
/// Einstellungen der lokalen Sound Zones: Automatik an/aus, Zonen auflisten und löschen, „Zone hier anlegen“ mit
/// Name, Radius und einem gespeicherten EQ-Preset. Die Zone gilt für das verbundene Headset (Adresse, Modell und
/// Bandfrequenzen); die Koordinaten bleiben lokal in <c>zones.json</c>.
/// </summary>
public sealed partial class ZonesViewModel : ObservableObject, IDisposable
{
    private readonly HeadsetHost _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly EventHandler _changed;
    private Momentum4State? _state;
    private bool _applying;

    public ZonesViewModel(HeadsetHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _changed = (_, _) => _dispatcher.TryEnqueue(Reload);
        _host.Zones.Changed += _changed;
        Reload();
    }

    public ObservableCollection<ZoneItemViewModel> Zones { get; } = [];

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool CanEdit { get; set; }

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double NewRadius { get; set; } = 300;

    [ObservableProperty]
    public partial List<string> PresetNames { get; set; } = [];

    [ObservableProperty]
    public partial int PresetIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void Apply(Momentum4State state)
    {
        _state = state;
        if (!_host.Presets.All.Select(preset => preset.Name).SequenceEqual(PresetNames))
        {
            Reload(); // neue Presets aus dem EQ oder dem Sound Check
        }
    }

    public void Dispose() => _host.Zones.Changed -= _changed;

    partial void OnEnabledChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        try
        {
            _host.Zones.SetEnabled(value);
            Hint = value ? L.Get("ZonesEnabledHint") : string.Empty;
        }
        catch (ZoneException ex)
        {
            Hint = ex.Message;
            Reload();
        }
    }

    [RelayCommand]
    private async Task AddHereAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_state is not { Connection: ConnectionState.Connected, Device: { } device, Equalizer: { } eq } || _host.Address is not { } address
            || eq.Bands.Any(band => band.FrequencyHz is null))
        {
            Hint = L.Get("ZoneNeedsHeadset");
            return;
        }

        if (PresetIndex < 0 || PresetIndex >= PresetNames.Count || _host.Presets.Find(PresetNames[PresetIndex]) is not { } preset
            || preset.GainsDb.Count != eq.Bands.Count)
        {
            Hint = L.Get("ZoneNeedsPreset");
            return;
        }

        IsBusy = true;
        Hint = L.Get("ZoneLocating");
        try
        {
            var fix = await ZoneController.SampleAsync(CancellationToken.None);
            var radius = double.IsFinite(NewRadius) ? (int)Math.Clamp(Math.Round(NewRadius), SoundZones.MinRadiusM, SoundZones.MaxRadiusM) : 300;
            var zone = new SoundZone(NewName.Trim(), address.ToString(), fix.Latitude, fix.Longitude, radius,
                new ZonePreset(preset.Name, device.ModelId, [.. eq.Bands.Select(band => band.FrequencyHz!.Value)], [.. preset.GainsDb]));
            _host.Zones.Add(zone);
            NewName = string.Empty;
            Hint = L.Get("ZoneSaved", zone.Name, Math.Round(fix.AccuracyM).ToString(CultureInfo.CurrentCulture));
        }
        catch (Exception ex) when (ex is ZoneException or ZoneLocationException)
        {
            Hint = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveZone(ZoneItemViewModel item)
    {
        try
        {
            _host.Zones.Remove(item.Zone.Name);
        }
        catch (ZoneException ex)
        {
            Hint = ex.Message;
        }
    }

    private void Reload()
    {
        _applying = true;
        try
        {
            var zones = _host.Zones;
            Enabled = zones.Data.Enabled;
            CanEdit = zones.LoadError is null;
            Zones.Clear();
            foreach (var zone in zones.Data.Zones)
            {
                Zones.Add(new ZoneItemViewModel(zone, RemoveZone));
            }

            var selected = PresetIndex >= 0 && PresetIndex < PresetNames.Count ? PresetNames[PresetIndex] : null;
            PresetNames = [.. _host.Presets.All.Select(preset => preset.Name)];
            PresetIndex = selected is null ? (PresetNames.Count > 0 ? 0 : -1) : PresetNames.IndexOf(selected);
            if (zones.LoadError is { } error)
            {
                Hint = L.Get("ZonesFileInvalid", error);
            }
            else if (zones.LastEvent.Length > 0)
            {
                Hint = zones.LastEvent;
            }
        }
        finally
        {
            _applying = false;
        }
    }
}
