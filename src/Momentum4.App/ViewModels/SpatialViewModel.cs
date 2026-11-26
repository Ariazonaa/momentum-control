// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using Momentum4.App.Localization;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.ViewModels;

/// <summary>
/// Raumklang des MOMENTUM 5 (FW 6.21.5): Immersive Audio und Head Tracking. Regeln wie im Core und im Linux-Frontend:
/// Head Tracking nur bei eingeschaltetem Immersive Audio, beides nicht im Klangmodus Sound Personalization. Sichtbar
/// nur, wenn der Core Immersive Audio für dieses Gerät freigibt.
/// </summary>
public sealed partial class SpatialViewModel(IActionRunner run) : ObservableObject
{
    private bool? _lastImmersive;
    private bool? _lastTracking;
    private bool _applying;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial bool ImmersiveAudio { get; set; }

    [ObservableProperty]
    public partial bool HeadTracking { get; set; }

    [ObservableProperty]
    public partial bool ImmersiveEnabled { get; set; }

    [ObservableProperty]
    public partial bool HeadTrackingEnabled { get; set; }

    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    public void Apply(Momentum4State state, bool connected)
    {
        _lastImmersive = state.ImmersiveAudio;
        _lastTracking = state.HeadTracking;
        _applying = true;
        try
        {
            var personalization = state.Equalizer?.SoundMode == SoundMode.SoundPersonalization;
            IsVisible = state.CanWrite(HeadsetControl.ImmersiveAudio) && state.ImmersiveAudio is not null;
            IsAvailable = connected && IsVisible;
            ImmersiveAudio = state.ImmersiveAudio == true;
            HeadTracking = state.HeadTracking == true;
            ImmersiveEnabled = !personalization;
            HeadTrackingEnabled = !personalization && state.ImmersiveAudio == true && state.CanWrite(HeadsetControl.HeadTracking);
            Hint = personalization ? L.Get("SpatialPersonalizationHint") : string.Empty;
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnImmersiveAudioChanged(bool value)
    {
        if (_applying || _lastImmersive == value)
        {
            return;
        }

        _ = run.RunAsync(L.Get("ActImmersive", L.Get(value ? "StateOn" : "StateOff")), (service, ct) => service.SetImmersiveAudioAsync(value, ct));
    }

    partial void OnHeadTrackingChanged(bool value)
    {
        if (_applying || _lastTracking == value)
        {
            return;
        }

        _ = run.RunAsync(L.Get("ActHeadTracking", L.Get(value ? "StateOn" : "StateOff")), (service, ct) => service.SetHeadTrackingAsync(value, ct));
    }
}
