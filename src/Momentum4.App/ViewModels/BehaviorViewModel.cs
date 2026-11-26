// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using Momentum4.App.Localization;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.ViewModels;

/// <summary>
/// Verhalten: On-Head-Erkennung, Smart Pause, Touch-Steuerung, Anrufe automatisch annehmen, Comfort Call, Töne &amp;
/// Sprachansagen und automatisch ausschalten (Hardware-Tests 2026-09-19). Nur Touch meldet sich selbst (<c>0x1687</c>); der Rest kommt beim
/// vollständigen Refresh und nach jedem Setzen. Unbekannt (noch nicht gelesen) heißt: Karte ausblenden statt „aus“ anzeigen.
/// </summary>
public sealed partial class BehaviorViewModel(IActionRunner run) : ObservableObject
{
    private BehaviorState? _last;
    private int? _lastAutoOff;
    private bool _applying;

    private static readonly AudioPromptMode[] PromptOrder = [AudioPromptMode.TonesAndVoice, AudioPromptMode.TonesOnly, AudioPromptMode.Off];
    private AudioPromptMode? _lastPrompts;

    // List<T> statt Collection-Expression: WinRT/AOT kann nur echte Listen als ItemsSource übergeben.
    public List<string> AutoOffOptions { get; } = [L.Get("AutoOffNever"), L.Get("AutoOff15"), L.Get("AutoOff30"), L.Get("AutoOff60")];

    public List<string> PromptOptions { get; } = [L.Get("PromptTonesVoice"), L.Get("PromptTonesOnly"), L.Get("PromptOff")];

    /// <summary>Transparenz bei Anrufen (Sidetone) 0…5; Eintrag 0 heißt „Aus“.</summary>
    public List<string> SidetoneOptions { get; } = [L.Get("CallTransparencyOff"), "1", "2", "3", "4", "5"];

    private int? _lastSidetone;
    private bool? _lastBatteryProtection;

    [ObservableProperty]
    public partial bool ShowSidetone { get; set; }

    [ObservableProperty]
    public partial int SidetoneIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool ShowBatteryProtection { get; set; }

    [ObservableProperty]
    public partial bool BatteryProtection { get; set; }

    [ObservableProperty]
    public partial int PromptIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int AutoOffIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string AutoOffHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsKnown { get; set; }

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial bool OnHeadDetection { get; set; }

    [ObservableProperty]
    public partial bool SmartPause { get; set; }

    [ObservableProperty]
    public partial bool AutoAnswer { get; set; }

    [ObservableProperty]
    public partial bool ComfortCall { get; set; }

    [ObservableProperty]
    public partial bool TouchControl { get; set; }

    public void Apply(BehaviorState? state, int? autoOffSeconds, AudioPromptMode? prompts, bool connected, Momentum4State? full = null)
    {
        _last = state;
        _lastAutoOff = autoOffSeconds;
        _lastPrompts = prompts;
        _lastSidetone = full?.Sidetone;
        _lastBatteryProtection = full?.BatteryProtection;
        _applying = true;
        try
        {
            // Nur zeigen, was der Core für dieses Gerät schreiben würde (am M4 Sidetone erst nach einem Hardware-Test).
            ShowSidetone = full is not null && full.CanWrite(HeadsetControl.Sidetone) && full.Sidetone is not null;
            SidetoneIndex = ShowSidetone ? full!.Sidetone!.Value : -1;
            ShowBatteryProtection = full is not null && full.CanWrite(HeadsetControl.BatteryProtection) && full.BatteryProtection is not null;
            BatteryProtection = full?.BatteryProtection == true;
            PromptIndex = prompts is { } mode ? Array.IndexOf(PromptOrder, mode) : -1;
            AutoOffIndex = autoOffSeconds is { } seconds ? IndexOf(seconds) : -1;
            AutoOffHint = autoOffSeconds is { } other && IndexOf(other) < 0 ? L.Get("AutoOffOther", other % 60 == 0 ? $"{other / 60} min" : $"{other} s") : string.Empty;
            IsKnown = state is not null;
            IsAvailable = connected && state is not null;
            OnHeadDetection = state?.OnHeadDetection == true;
            SmartPause = state?.SmartPause == true;
            AutoAnswer = state?.AutoAnswer == true;
            ComfortCall = state?.ComfortCall == true;
            TouchControl = state?.TouchControl == true;
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnOnHeadDetectionChanged(bool value) => Set(BehaviorSetting.OnHeadDetection, value);

    partial void OnSmartPauseChanged(bool value) => Set(BehaviorSetting.SmartPause, value);

    partial void OnAutoAnswerChanged(bool value) => Set(BehaviorSetting.AutoAnswer, value);

    partial void OnComfortCallChanged(bool value) => Set(BehaviorSetting.ComfortCall, value);

    partial void OnTouchControlChanged(bool value) => Set(BehaviorSetting.TouchControl, value);

    partial void OnPromptIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= PromptOrder.Length || _lastPrompts == PromptOrder[value])
        {
            return;
        }

        var mode = PromptOrder[value];
        _ = run.RunAsync(L.Get("ActPrompts", PromptOptions[value]), (service, ct) => service.SetAudioPromptModeAsync(mode, ct));
    }

    partial void OnAutoOffIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= Momentum4State.AutoPowerOffChoices.Count || _lastAutoOff == Momentum4State.AutoPowerOffChoices[value])
        {
            return;
        }

        var seconds = Momentum4State.AutoPowerOffChoices[value];
        _ = run.RunAsync(L.Get("ActAutoOff", AutoOffOptions[value]), (service, ct) => service.SetAutoPowerOffAsync(seconds, ct));
    }

    partial void OnSidetoneIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= SidetoneOptions.Count || _lastSidetone == value)
        {
            return;
        }

        _ = run.RunAsync(L.Get("ActSidetone", SidetoneOptions[value]), (service, ct) => service.SetSidetoneAsync(value, ct));
    }

    partial void OnBatteryProtectionChanged(bool value)
    {
        if (_applying || _lastBatteryProtection is null || _lastBatteryProtection == value)
        {
            return;
        }

        _ = run.RunAsync(L.Get("ActBatteryProtection", L.Get(value ? "StateOn" : "StateOff")), (service, ct) => service.SetBatteryProtectionAsync(value, ct));
    }

    private static int IndexOf(int seconds)
    {
        for (var i = 0; i < Momentum4State.AutoPowerOffChoices.Count; i++)
        {
            if (Momentum4State.AutoPowerOffChoices[i] == seconds)
            {
                return i;
            }
        }

        return -1;
    }

    private void Set(BehaviorSetting setting, bool value)
    {
        if (_applying || _last is null || _last.Get(setting) == value)
        {
            return;
        }

        _ = run.RunAsync($"{BehaviorState.NameOf(setting)} {(value ? "an" : "aus")}", (service, ct) => service.SetBehaviorAsync(setting, value, ct));
    }
}
