// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;

namespace Momentum4.Core.State;

/// <summary>
/// Das gemeinsame JSON-Schema eines Headset-Zustands: <c>momentumctl status --json</c> (Linux, über den CoreHost) und
/// <c>m4ctl status --json</c> (Windows) schreiben es identisch. Die Linux-Seite prüft die Schlüssel streng
/// (linux/momentum_control/models.py, <c>snapshot_from_dict</c>) – Änderungen hier sind Schemaänderungen.
/// </summary>
public static class HeadsetSnapshotJson
{
    public static string Serialize(HeadsetControlSnapshot state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, state);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Snapshot aus dem bestätigten Windows-Zustand, ohne Headset-Verkehr. <c>null</c>, solange kein Gerät verbunden und
    /// identifiziert ist. Hinweise und der Personalization-Status sind im Windows-Zustand nicht enthalten (leer bzw. null).
    /// </summary>
    public static HeadsetControlSnapshot? FromState(Momentum4State state)
    {
        if (state.Connection != ConnectionState.Connected || state.Device is not { } device)
        {
            return null;
        }

        var noise = state.NoiseControl;
        var behavior = state.Behavior;
        var eq = state.Equalizer;
        var settings = new HeadsetControlSettings
        {
            AncEnabled = noise?.AncEnabled,
            Transparency = noise?.TransparentHearing,
            AdaptiveAnc = noise?.Adaptive,
            AncStrength = noise?.Level,
            AntiWind = noise is null ? null : (int)noise.AntiWind,
            TouchControls = behavior?.TouchControl,
            SmartPause = behavior?.SmartPause,
            OnHeadDetection = behavior?.OnHeadDetection,
            AutoAnswer = behavior?.AutoAnswer,
            ComfortCall = behavior?.ComfortCall,
            BassBoost = eq?.BassBoost,
            SoundMode = eq?.SoundMode is { } soundMode ? (int)soundMode : null,
            PauseOnTransparency = state.AutoPauseOnTransparency,
            ImmersiveAudio = state.ImmersiveAudio,
            HeadTracking = state.HeadTracking,
            BatteryProtection = state.BatteryProtection,
            TransparencyLevel = state.TransparencyLevel,
            Sidetone = state.Sidetone,
            AutoPowerOff = state.AutoPowerOffSeconds,
            AudioPrompts = state.PromptMode is { } prompts ? (int)prompts : null,
        };
        var bandsKnown = eq is not null && eq.Bands.All(band => band.FrequencyHz is not null);
        var peers = state.Multipoint?.Devices.Select(peer => new HeadsetControlPeer(peer.Index, peer.Name, peer.Connected, peer.IsThisComputer, 0)).ToArray()
            ?? [];
        return new HeadsetControlSnapshot(device.ModelId, device.Firmware, state.BatteryPercent, settings,
            bandsKnown ? eq!.Bands.Select(band => band.GainDb).ToArray() : [],
            bandsKnown ? eq!.Bands.Select(band => band.FrequencyHz!.Value).ToArray() : [],
            bandsKnown ? eq!.MinGainDb : null, bandsKnown ? eq!.MaxGainDb : null,
            state.WritableControls ?? new HashSet<HeadsetControl>(),
            state.ControlChoices ?? new Dictionary<HeadsetIntegerControl, IReadOnlyList<int>>(),
            [], state.DeviceName, state.Charging, state.Wear is { } wear ? (int)wear : null, null,
            peers, state.Multipoint?.MaxConnections, state.Multipoint is not null, true);
    }

    public static string IntegerControlName(HeadsetIntegerControl control) => ControlName((HeadsetControl)control);

    public static void Write(Utf8JsonWriter writer, HeadsetControlSnapshot state)
    {
        writer.WriteStartObject();
        writer.WriteString("model", state.Model);
        writer.WriteString("firmware", state.Firmware.ToString());
        Optional(writer, "battery", state.Battery);
        writer.WritePropertyName("settings"); writer.WriteStartObject();
        var settings = state.Settings;
        Setting(writer, "anc_enabled", settings.AncEnabled);
        Setting(writer, "transparency", settings.Transparency);
        Setting(writer, "touch_controls", settings.TouchControls);
        Setting(writer, "smart_pause", settings.SmartPause);
        Setting(writer, "on_head_detection", settings.OnHeadDetection);
        Setting(writer, "auto_answer", settings.AutoAnswer);
        Setting(writer, "comfort_call", settings.ComfortCall);
        Setting(writer, "bass_boost", settings.BassBoost);
        Setting(writer, "pause_on_transparency", settings.PauseOnTransparency);
        Setting(writer, "adaptive_anc", settings.AdaptiveAnc);
        Setting(writer, "immersive_audio", settings.ImmersiveAudio);
        Setting(writer, "head_tracking", settings.HeadTracking);
        Setting(writer, "battery_protection", settings.BatteryProtection);
        Setting(writer, "anc_strength", settings.AncStrength);
        Setting(writer, "transparency_level", settings.TransparencyLevel);
        Setting(writer, "antiwind", settings.AntiWind);
        Setting(writer, "audio_prompts", settings.AudioPrompts);
        Setting(writer, "sidetone", settings.Sidetone);
        Setting(writer, "auto_power_off", settings.AutoPowerOff);
        Setting(writer, "sound_mode", settings.SoundMode);
        writer.WriteEndObject();
        writer.WritePropertyName("eq_bands"); writer.WriteStartArray();
        foreach (var gain in state.EqGainsDb) writer.WriteNumberValue(gain);
        writer.WriteEndArray();
        writer.WritePropertyName("eq_frequencies"); writer.WriteStartArray();
        foreach (var frequency in state.EqFrequenciesHz) writer.WriteNumberValue(frequency);
        writer.WriteEndArray();
        writer.WritePropertyName("eq_range");
        if (state.EqMinimumDb is { } minimum && state.EqMaximumDb is { } maximum)
        {
            writer.WriteStartArray(); writer.WriteNumberValue(minimum); writer.WriteNumberValue(maximum); writer.WriteEndArray();
        }
        else writer.WriteNullValue();
        writer.WritePropertyName("writable"); writer.WriteStartArray();
        foreach (var control in state.Writable.Order()) writer.WriteStringValue(ControlName(control));
        writer.WriteEndArray();
        writer.WritePropertyName("choices"); writer.WriteStartObject();
        foreach (var choice in state.Choices)
        {
            writer.WritePropertyName(IntegerControlName(choice.Key));
            writer.WriteStartArray(); foreach (var item in choice.Value) writer.WriteNumberValue(item); writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WritePropertyName("notes"); writer.WriteStartArray();
        foreach (var note in state.Notes) writer.WriteStringValue(note);
        writer.WriteEndArray();
        writer.WriteString("device_name", state.DeviceName);
        Optional(writer, "charging", state.Charging);
        Optional(writer, "wear_state", state.WearState);
        Optional(writer, "personalization_state", state.PersonalizationState);
        writer.WritePropertyName("peers"); writer.WriteStartArray();
        foreach (var peer in state.Peers)
        {
            writer.WriteStartObject(); writer.WriteNumber("index", peer.Index); writer.WriteString("name", peer.Name);
            Optional(writer, "connected", peer.Connected); writer.WriteBoolean("is_this_pc", peer.IsThisComputer);
            writer.WriteNumber("priority", peer.Priority); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Optional(writer, "max_connections", state.MaxConnections);
        writer.WriteBoolean("peers_available", state.PeersAvailable);
        writer.WriteBoolean("verified_session", state.VerifiedSession);
        writer.WriteEndObject();
    }

    public static string ControlName(HeadsetControl control) => control switch
    {
        HeadsetControl.Equalizer => "eq",
        HeadsetControl.Rename => "rename",
        HeadsetControl.ConnectPeer => "connect_peer",
        HeadsetControl.AncEnabled => "anc_enabled",
        HeadsetControl.Transparency => "transparency",
        HeadsetControl.TouchControls => "touch_controls",
        HeadsetControl.SmartPause => "smart_pause",
        HeadsetControl.OnHeadDetection => "on_head_detection",
        HeadsetControl.AutoAnswer => "auto_answer",
        HeadsetControl.ComfortCall => "comfort_call",
        HeadsetControl.BassBoost => "bass_boost",
        HeadsetControl.PauseOnTransparency => "pause_on_transparency",
        HeadsetControl.AdaptiveAnc => "adaptive_anc",
        HeadsetControl.ImmersiveAudio => "immersive_audio",
        HeadsetControl.HeadTracking => "head_tracking",
        HeadsetControl.BatteryProtection => "battery_protection",
        HeadsetControl.AncStrength => "anc_strength",
        HeadsetControl.TransparencyLevel => "transparency_level",
        HeadsetControl.AntiWind => "antiwind",
        HeadsetControl.AudioPrompts => "audio_prompts",
        HeadsetControl.Sidetone => "sidetone",
        HeadsetControl.AutoPowerOff => "auto_power_off",
        HeadsetControl.SoundMode => "sound_mode",
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    private static void Setting(Utf8JsonWriter writer, string name, bool? value) { if (value is { } observed) writer.WriteBoolean(name, observed); }
    private static void Setting(Utf8JsonWriter writer, string name, int? value) { if (value is { } observed) writer.WriteNumber(name, observed); }
    private static void Optional(Utf8JsonWriter writer, string name, bool? value) { if (value is { } observed) writer.WriteBoolean(name, observed); else writer.WriteNull(name); }
    private static void Optional(Utf8JsonWriter writer, string name, int? value) { if (value is { } observed) writer.WriteNumber(name, observed); else writer.WriteNull(name); }

}
