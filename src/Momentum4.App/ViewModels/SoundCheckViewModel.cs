// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum4.App.Localization;
using Momentum4.Core.Presets;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.ViewModels;

/// <summary>
/// Lokaler Sound Check (wie im Linux-Frontend): Bass, Mitten, Höhen je als A/B-Vergleich (+2 / −2 dB) beim Hören eigener
/// Musik; am Ende als Preset speichern. Schreibt nur ganze EQ-Kurven über den Service (Write → Verify); den Klangmodus
/// wechselt er nie. Schließen behält die zuletzt bestätigte Kurve.
/// </summary>
public sealed partial class SoundCheckViewModel(IActionRunner run, EqPresetStore presets, Action presetsChanged, Action<bool> interactive) : ObservableObject
{
    private EqualizerState? _eq;
    private bool _connected;
    private bool _writable;
    private SoundCheck? _check;

    [ObservableProperty]
    public partial bool CanStart { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool ShowCompare { get; set; }

    [ObservableProperty]
    public partial bool ShowSave { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PresetName { get; set; } = string.Empty;

    public void Apply(Momentum4State state, bool connected)
    {
        var eq = state.Equalizer;
        _eq = eq;
        _connected = connected;
        _writable = state.CanWrite(HeadsetControl.Equalizer);
        CanStart = Startable(eq, connected, _writable);
        if (!CanStart && IsRunning)
        {
            Stop(L.Get("SoundCheckInterrupted"));
        }
    }

    /// <summary>Beim Öffnen des Flyouts: mit der aktuellen, bestätigten Kurve neu beginnen.</summary>
    [RelayCommand]
    public void Start()
    {
        if (!Startable(_eq, _connected, _writable) || _eq is not { } eq)
        {
            Stop(L.Get("SoundCheckNeedsEq"));
            return;
        }

        _check = new SoundCheck([.. eq.Bands.Select(b => b.GainDb)], [.. eq.Bands.Select(b => b.FrequencyHz!.Value)], eq.MinGainDb, eq.MaxGainDb);
        IsRunning = true;
        interactive(true);
        PresetName = string.Empty;
        Message = string.Empty;
        Render();
    }

    /// <summary>Flyout geschlossen: die zuletzt bestätigte Kurve bleibt; Zonen dürfen nach kurzem Nachlauf wieder schreiben.</summary>
    public void Close()
    {
        if (IsRunning)
        {
            _check = null;
            IsRunning = false;
            Render();
        }

        interactive(false);
    }

    [RelayCommand]
    private Task ListenAAsync() => _check is { } check ? WriteAsync(check.CandidateA, "A") : Task.CompletedTask;

    [RelayCommand]
    private Task ListenBAsync() => _check is { } check ? WriteAsync(check.CandidateB, "B") : Task.CompletedTask;

    [RelayCommand]
    private Task RestoreAsync() => _check is { } check ? WriteAsync(check.Initial, L.Get("SoundCheckStart")) : Task.CompletedTask;

    [RelayCommand]
    private void Continue()
    {
        if (_check is null || IsBusy)
        {
            return;
        }

        _check.Next();
        Message = string.Empty;
        Render();
    }

    [RelayCommand]
    private void Save()
    {
        if (_check is not { Stage: SoundCheckStage.Finished } check || IsBusy)
        {
            return;
        }

        try
        {
            var saved = presets.Save(PresetName, check.Confirmed);
            presetsChanged();
            Message = L.Get("SoundCheckSaved", saved.Name);
            PresetName = string.Empty;
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
    }

    private async Task WriteAsync(IReadOnlyList<decimal> gains, string label)
    {
        if (_check is not { } check || IsBusy || !CanStart)
        {
            return;
        }

        IsBusy = true;
        Message = L.Get("SoundCheckApplying");
        try
        {
            var ok = await run.RunAsync(L.Get("ActSoundCheck", label), async (service, ct) =>
            {
                var eq = await service.SetEqGainsAsync(gains, ct);
                check.Confirm([.. eq.Bands.Select(b => b.GainDb)]);
            });
            Message = ok && check.Confirmed.SequenceEqual(gains) ? L.Get("SoundCheckConfirmed", label) : L.Get("SoundCheckFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Render()
    {
        if (_check is not { } check)
        {
            ShowCompare = ShowSave = false;
            StageText = string.Empty;
            return;
        }

        ShowCompare = check.Stage != SoundCheckStage.Finished;
        ShowSave = check.Stage == SoundCheckStage.Finished;
        StageText = check.Stage switch
        {
            SoundCheckStage.Bass => L.Get("SoundCheckStep", 1, L.Get("SoundCheckBass")),
            SoundCheckStage.Midrange => L.Get("SoundCheckStep", 2, L.Get("SoundCheckMids")),
            SoundCheckStage.Treble => L.Get("SoundCheckStep", 3, L.Get("SoundCheckTreble")),
            _ => L.Get("SoundCheckDone"),
        };
    }

    private void Stop(string message)
    {
        if (IsRunning)
        {
            interactive(false);
        }

        _check = null;
        IsRunning = false;
        Message = message;
        Render();
    }

    private static bool Startable(EqualizerState? eq, bool connected, bool writable) =>
        connected && writable && eq is { SoundMode: SoundMode.Equalizer } && eq.Bands.Count > 0 && eq.Bands.All(b => b.FrequencyHz is not null);
}
