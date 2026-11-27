using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;

namespace Momentum4.CoreHost;

public sealed class HeadsetOperations : IAsyncDisposable
{
    private int _disposed;
    private readonly InheritedTransport _transport;
    private readonly Momentum4Service _service;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly IReadOnlyDictionary<string, HeadsetBooleanControl> BooleanControls = new Dictionary<string, HeadsetBooleanControl>(StringComparer.Ordinal)
    {
        ["anc_enabled"] = HeadsetBooleanControl.AncEnabled,
        ["transparency"] = HeadsetBooleanControl.Transparency,
        ["touch_controls"] = HeadsetBooleanControl.TouchControls,
        ["smart_pause"] = HeadsetBooleanControl.SmartPause,
        ["on_head_detection"] = HeadsetBooleanControl.OnHeadDetection,
        ["auto_answer"] = HeadsetBooleanControl.AutoAnswer,
        ["comfort_call"] = HeadsetBooleanControl.ComfortCall,
        ["bass_boost"] = HeadsetBooleanControl.BassBoost,
        ["pause_on_transparency"] = HeadsetBooleanControl.PauseOnTransparency,
        ["adaptive_anc"] = HeadsetBooleanControl.AdaptiveAnc,
        ["immersive_audio"] = HeadsetBooleanControl.ImmersiveAudio,
        ["head_tracking"] = HeadsetBooleanControl.HeadTracking,
        ["battery_protection"] = HeadsetBooleanControl.BatteryProtection,
    };

    private static readonly IReadOnlyDictionary<string, HeadsetIntegerControl> IntegerControls = new Dictionary<string, HeadsetIntegerControl>(StringComparer.Ordinal)
    {
        ["anc_strength"] = HeadsetIntegerControl.AncStrength,
        ["transparency_level"] = HeadsetIntegerControl.TransparencyLevel,
        ["antiwind"] = HeadsetIntegerControl.AntiWind,
        ["audio_prompts"] = HeadsetIntegerControl.AudioPrompts,
        ["sidetone"] = HeadsetIntegerControl.Sidetone,
        ["auto_power_off"] = HeadsetIntegerControl.AutoPowerOff,
        ["sound_mode"] = HeadsetIntegerControl.SoundMode,
    };

    public HeadsetOperations(int descriptor)
    {
        _transport = new(descriptor);
        _service = new(_transport, CommandPolicy.Production, new Momentum4ServiceOptions
        {
            ReadSerialNumber = false,
            EnablePolling = false,
            Reconnect = false,
            RegisterNotifications = false,
        }, NullLoggerFactory.Instance);
        _service.Store.StateChanged += StateChanged;
        _service.Start();
    }

    public Task Completion => _service.Completion;

    private void StateChanged(object? sender, StateChangedEventArgs state)
    {
        if (state.Current.Connection == ConnectionState.Connected) _ready.TrySetResult();
    }

    public async Task<HostValue> DispatchAsync(PrivateRequest request, CancellationToken cancellationToken)
    {
        var operation = Parse(request);
        await _ready.Task.WaitAsync(cancellationToken);
        var state = await operation(cancellationToken);
        return new(writer => WriteSnapshot(writer, state));
    }

    private Func<CancellationToken, Task<HeadsetControlSnapshot>> Parse(PrivateRequest request)
    {
        var args = request.Arguments;
        switch (request.Operation)
        {
            case "snapshot":
                PrivateProtocol.Fields(args);
                return _service.GetControlSnapshotAsync;
            case "set-boolean":
                PrivateProtocol.Fields(args, "control", "value");
                if (!BooleanControls.TryGetValue(PrivateProtocol.Text(args, "control", 32), out var boolean)) throw new PrivateProtocolException();
                var enabled = PrivateProtocol.Boolean(args, "value");
                return token => _service.SetBooleanControlAsync(boolean, enabled, token);
            case "set-integer":
                PrivateProtocol.Fields(args, "control", "value");
                if (!IntegerControls.TryGetValue(PrivateProtocol.Text(args, "control", 32), out var integer)) throw new PrivateProtocolException();
                var number = Int32(args, "value");
                return token => _service.SetIntegerControlAsync(integer, number, token);
            case "eq-band":
                PrivateProtocol.Fields(args, "band", "gain", "model", "frequencies");
                var band = Int32(args, "band");
                var gain = Decimal(args.GetProperty("gain"));
                var topology = Topology(args);
                return token => _service.SetControlEqBandAsync(band, gain, topology, token);
            case "eq-curve":
                PrivateProtocol.Fields(args, "gains", "model", "frequencies");
                var gainsValue = args.GetProperty("gains");
                if (gainsValue.ValueKind != JsonValueKind.Array || gainsValue.GetArrayLength() is < 1 or > 16) throw new PrivateProtocolException();
                var gains = gainsValue.EnumerateArray().Select(Decimal).ToArray();
                var expected = Topology(args);
                return token => _service.SetControlEqualizerAsync(gains, expected, token);
            case "rename":
                PrivateProtocol.Fields(args, "name");
                var name = PrivateProtocol.Text(args, "name", 128);
                return token => _service.RenameControlAsync(name, token);
            case "peer":
                PrivateProtocol.Fields(args, "index", "name", "priority", "is_this_pc");
                var peer = new HeadsetPeerSelection(Int32(args, "index"), PrivateProtocol.Text(args, "name", 128), Int32(args, "priority"), PrivateProtocol.Boolean(args, "is_this_pc"));
                return token => _service.ConnectControlPeerAsync(peer, token);
            case "noise-mode":
                PrivateProtocol.Fields(args, "mode");
                var mode = PrivateProtocol.Text(args, "mode", 16) switch
                {
                    "anc" => NoiseControlMode.Custom,
                    "adaptive" => NoiseControlMode.Adaptive,
                    "transparency" => NoiseControlMode.Transparency,
                    "off" => NoiseControlMode.Off,
                    _ => throw new PrivateProtocolException(),
                };
                return token => _service.SetControlNoiseModeAsync(mode, token);
            default: throw new PrivateProtocolException();
        }
    }

    private static int Int32(JsonElement args, string name)
    {
        var value = PrivateProtocol.Integer(args, name);
        if (value < int.MinValue || value > int.MaxValue) throw new PrivateProtocolException();
        return (int)value;
    }

    private static decimal Decimal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result)) throw new PrivateProtocolException();
        return result;
    }

    private static HeadsetEqTopology Topology(JsonElement args)
    {
        var value = args.GetProperty("frequencies");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 16) throw new PrivateProtocolException();
        var frequencies = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var frequency) ? frequency : throw new PrivateProtocolException()).ToArray();
        return new(PrivateProtocol.Text(args, "model", 128), frequencies);
    }

    public static void WriteSnapshot(Utf8JsonWriter writer, HeadsetControlSnapshot state) => HeadsetSnapshotJson.Write(writer, state);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _service.Store.StateChanged -= StateChanged;
        await _service.DisposeAsync();
        await _transport.DisposeAsync();
    }
}
