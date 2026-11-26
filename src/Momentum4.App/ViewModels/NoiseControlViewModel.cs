// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Momentum4.App.Localization;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.ViewModels;

/// <summary>
/// Noise Control nach den Hardware-Befunden aus Phase E (docs/architecture.md §7): Moduswahl Transparenz / ANC /
/// Adaptive / Aus, im Modus ANC zusätzlich die ANC-Stärke (Pegel, 0 = am stärksten), dazu Anti-Wind.
/// </summary>
public sealed partial class NoiseControlViewModel : ObservableObject
{
    private static readonly NoiseControlMode[] ModeOrder =
        [NoiseControlMode.Transparency, NoiseControlMode.Custom, NoiseControlMode.Adaptive, NoiseControlMode.Off];

    private readonly IActionRunner _run;
    private readonly DispatcherQueueTimer _strengthDebounce;
    private readonly DispatcherQueueTimer _transparencyDebounce;
    private int[] _antiWindValues = [0, 1, 2];
    private int? _lastTransparencyLevel;

    /// <summary>Transparenzstufe, die der User gerade einstellt (nur MOMENTUM 5).</summary>
    private int? _pendingTransparency;
    private NoiseControlState? _last;
    private bool _applying;

    /// <summary>Pegel, den der User gerade einstellt – solange gesetzt, überschreiben Notifications den Regler nicht.</summary>
    private int? _pendingLevel;

    public NoiseControlViewModel(IActionRunner run, DispatcherQueue dispatcher)
    {
        _run = run;
        _strengthDebounce = dispatcher.CreateTimer();
        _strengthDebounce.Interval = TimeSpan.FromMilliseconds(300); // [MC]: nur den Endwert senden
        _strengthDebounce.IsRepeating = false;
        _strengthDebounce.Tick += async (_, _) => await SendStrengthAsync();
        _transparencyDebounce = dispatcher.CreateTimer();
        _transparencyDebounce.Interval = TimeSpan.FromMilliseconds(300);
        _transparencyDebounce.IsRepeating = false;
        _transparencyDebounce.Tick += async (_, _) => await SendTransparencyAsync();
    }

    // List<T> statt Collection-Expression: WinRT/AOT kann nur echte Listen als ItemsSource übergeben.
    public List<string> Modes { get; } = [L.Get("ModeTransparency"), L.Get("ModeAnc"), L.Get("ModeAdaptive"), L.Get("ModeOff")];

    /// <summary>Anti-Wind-Stufen, die der Core für das Gerät erlaubt (MOMENTUM 5: nur Aus und Maximal).</summary>
    [ObservableProperty]
    public partial List<string> AntiWindOptions { get; set; } = [L.Get("AntiWindOff"), L.Get("AntiWindMax"), L.Get("AntiWindAuto")];

    /// <summary>Transparenzstufe 0…100, nur MOMENTUM 5 mit FW 6.21.5 (Core-Freigabe).</summary>
    [ObservableProperty]
    public partial bool ShowTransparencyLevel { get; set; }

    /// <summary>Bei ANC aus gesperrt: Der Regler schaltet ANC nie selbst ein (wie unter Linux).</summary>
    [ObservableProperty]
    public partial bool TransparencyLevelEnabled { get; set; }

    [ObservableProperty]
    public partial double TransparencyLevel { get; set; }

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial int SelectedModeIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool ShowStrength { get; set; }

    /// <summary>0…100, 100 = stärkstes ANC (= Pegel 0).</summary>
    [ObservableProperty]
    public partial double Strength { get; set; }

    [ObservableProperty]
    public partial int AntiWindIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string ModeHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoPause { get; set; }

    private bool? _lastAutoPause;

    public void Apply(NoiseControlState? state, bool? autoPause, bool connected, int? transparencyLevel = null,
        bool canWriteTransparencyLevel = false, IReadOnlyList<int>? antiWindChoices = null)
    {
        _last = state;
        _lastAutoPause = autoPause;
        _lastTransparencyLevel = transparencyLevel;
        _applying = true;
        try
        {
            AutoPause = autoPause == true;
            IsAvailable = connected && state is not null;
            ApplyAntiWindChoices(antiWindChoices);
            ShowTransparencyLevel = canWriteTransparencyLevel && transparencyLevel is not null && state is not null;
            TransparencyLevelEnabled = ShowTransparencyLevel && state?.AncEnabled == true;
            if (_pendingTransparency is null && transparencyLevel is { } shownLevel)
            {
                TransparencyLevel = shownLevel;
            }

            if (state is null)
            {
                SelectedModeIndex = -1;
                AntiWindIndex = -1;
                ShowStrength = false;
                ModeHint = string.Empty;
                return;
            }

            SelectedModeIndex = Array.IndexOf(ModeOrder, state.Mode);
            ShowStrength = state.Mode == NoiseControlMode.Custom;
            if (_pendingLevel is null)
            {
                Strength = 100 - state.Level;
            }

            AntiWindIndex = Array.IndexOf(_antiWindValues, (int)state.AntiWind);
            ModeHint = state.Mode switch
            {
                NoiseControlMode.Transparency => L.Get("HintTransparency"),
                NoiseControlMode.Custom => L.Get("HintCustom"),
                NoiseControlMode.Adaptive => L.Get("HintAdaptive"),
                _ => L.Get("HintOff"),
            };
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnSelectedModeIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= ModeOrder.Length || _last?.Mode == ModeOrder[value])
        {
            return;
        }

        var mode = ModeOrder[value];
        _ = _run.RunAsync(L.Get("ActNoiseControl", Modes[value]), (service, ct) => service.SetNoiseControlModeAsync(mode, ct));
    }

    partial void OnStrengthChanged(double value)
    {
        if (_applying)
        {
            return;
        }

        _pendingLevel = 100 - (int)Math.Round(value);
        _strengthDebounce.Stop();
        _strengthDebounce.Start();
    }

    partial void OnAntiWindIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= _antiWindValues.Length || _last is { } last && (int)last.AntiWind == _antiWindValues[value])
        {
            return;
        }

        var antiWind = (AntiWindMode)_antiWindValues[value];
        _ = _run.RunAsync(L.Get("ActAntiWind", AntiWindOptions[value]), (service, ct) => service.SetAntiWindAsync(antiWind, ct));
    }

    partial void OnAutoPauseChanged(bool value)
    {
        if (_applying || _lastAutoPause == value)
        {
            return;
        }

        _ = _run.RunAsync(L.Get(value ? "ActAutoPauseOn" : "ActAutoPauseOff"), (service, ct) => service.SetAutoPauseAsync(value, ct));
    }

    partial void OnTransparencyLevelChanged(double value)
    {
        if (_applying)
        {
            return;
        }

        _pendingTransparency = (int)Math.Round(value);
        _transparencyDebounce.Stop();
        _transparencyDebounce.Start();
    }

    private void ApplyAntiWindChoices(IReadOnlyList<int>? choices)
    {
        int[] values = choices is { Count: > 0 } ? [.. choices.Where(v => v is >= 0 and <= 2)] : [0, 1, 2];
        if (values.SequenceEqual(_antiWindValues))
        {
            return;
        }

        string[] names = [L.Get("AntiWindOff"), L.Get("AntiWindMax"), L.Get("AntiWindAuto")];
        _antiWindValues = values;
        AntiWindOptions = values.Select(v => names[v]).ToList();
    }

    private async Task SendTransparencyAsync()
    {
        if (_pendingTransparency is not { } level)
        {
            return;
        }

        if (level == _lastTransparencyLevel)
        {
            _pendingTransparency = null;
            return;
        }

        await _run.RunAsync(L.Get("ActTransparencyLevel", level), async (service, ct) =>
        {
            try
            {
                await service.SetTransparencyLevelAsync(level, ct);
            }
            finally
            {
                if (_pendingTransparency == level)
                {
                    _pendingTransparency = null;
                }
            }
        });
    }

    private async Task SendStrengthAsync()
    {
        if (_pendingLevel is not { } level)
        {
            return;
        }

        await _run.RunAsync(L.Get("ActAncStrength", 100 - level), async (service, ct) =>
        {
            try
            {
                await service.SetNoiseControlLevelAsync(level, ct);
            }
            finally
            {
                // Hat der User inzwischen weitergezogen, gilt sein neuer Wert.
                if (_pendingLevel == level)
                {
                    _pendingLevel = null;
                }
            }
        });
    }
}
