using Momentum4.Protocol.Features;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Tests;

public sealed class SharedHeadsetCodecTests
{
    [Theory]
    [InlineData("FF 03 02 10 01 04 95 13 06")]
    [InlineData("FF 02 00 00")]
    [InlineData("FF 03 04 00")]
    [InlineData("6A 75 6E 6B")]
    public void Strict_reader_rejects_bad_headers_and_lengths_before_body(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => new GaiaFrameReader(strict: true).Push(Hex.Parse(hex)));
        var valid = Hex.Parse("FF 03 02 10 00 04 95 13 06").Concat(new byte[4096]).ToArray();
        Assert.Equal(4096, Assert.Single(new GaiaFrameReader(strict: true).Push(valid).Frames).Payload.Length);
    }

    [Fact]
    public void Reader_bounds_buffer_before_allocating_and_retains_valid_fragments()
    {
        var reader = new GaiaFrameReader(strict: true);
        Assert.Equal(8192, GaiaFrameReader.MaxBufferedBytes);
        var exception = Assert.Throws<ProtocolFormatException>(() => reader.Push(new byte[8193]));
        Assert.StartsWith("GAIA buffer limit exceeded", exception.Message);
        Assert.Equal(0, reader.PendingByteCount);
        var bytes = Hex.Parse("FF 03 00 01 04 95 07 03 50");
        var frames = bytes.SelectMany(value => reader.Push(new byte[] { value }).Frames).ToArray();
        Assert.Equal(new byte[] { 80 }, Assert.Single(frames).Payload);
        Assert.Equal(2, new GaiaFrameReader(strict: true).Push(bytes.Concat(bytes).ToArray()).Frames.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0100")]
    [InlineData("02")]
    public void New_switches_are_strict_bool(string hex) =>
        Assert.Throws<ProtocolFormatException>(() => SwitchCodec.Decode(Hex.Parse(hex), "captured setting"));

    [Theory]
    [InlineData("c3")]
    [InlineData("626164006e616d65")]
    [InlineData("6261640a")]
    [InlineData("")]
    public void Names_reject_invalid_utf8_controls_and_empty_payload(string hex) =>
        Assert.Throws<ProtocolFormatException>(() => SharedHeadsetCodec.DecodeName(Hex.Parse(hex), 128));

    [Theory]
    [InlineData("Apple Watch de X")]   // iOS/watchOS-Standardname (GitHub #10)
    [InlineData("Mac Book")]
    [InlineData("Ideographic　Space")]
    public void Names_read_from_headset_accept_non_ascii_space_separators(string name) =>
        Assert.Equal(name, SharedHeadsetCodec.DecodeName([.. System.Text.Encoding.UTF8.GetBytes(name), 0], 128));

    [Fact]
    public void Name_read_failures_are_typed_as_name_format_errors()
    {
        var exception = Assert.Throws<ProtocolFormatException>(() => SharedHeadsetCodec.DecodeName(Hex.Parse("6261640a"), 128));
        Assert.Equal(ProtocolFormatKind.DeviceName, exception.Kind);
        // Leer oder kaputtes UTF-8 bleibt ein Frame-Fehler (protocol.md §9: leeres OK kann eine Aktion sein).
        Assert.Equal(ProtocolFormatKind.Frame, Assert.Throws<ProtocolFormatException>(() => SharedHeadsetCodec.DecodeName(Hex.Parse("c3"), 128)).Kind);
        Assert.Equal(ProtocolFormatKind.Frame, Assert.Throws<ProtocolFormatException>(() => SharedHeadsetCodec.DecodeName([], 128)).Kind);
    }

    [Fact]
    public void Rename_keeps_rejecting_non_ascii_space_separators() =>
        Assert.Throws<ArgumentException>(() => SharedHeadsetCodec.EncodeName("Apple Watch", 30));

    [Fact]
    public void Names_separate_read_and_model_write_byte_limits()
    {
        var name = new string('ü', 60);
        Assert.Equal(name, SharedHeadsetCodec.DecodeName(System.Text.Encoding.UTF8.GetBytes(name), 128));
        Assert.Throws<ArgumentException>(() => SharedHeadsetCodec.EncodeName(new string('ü', 9), 16));
        Assert.Equal(16, SharedHeadsetCodec.EncodeName(new string('ü', 8), 16).Length);
        Assert.Equal(30, SharedHeadsetCodec.EncodeName(new string('x', 30), 30).Length);
        Assert.Throws<ProtocolFormatException>(() => SharedHeadsetCodec.DecodeName(new byte[129].Select(_ => (byte)65).ToArray(), 128));
    }

    [Theory]
    [InlineData("0001")]
    [InlineData("010100")]
    [InlineData("0102")]
    public void Peer_status_requires_exact_echo_bool_and_length(string hex) =>
        Assert.Throws<ProtocolFormatException>(() => DeviceManagementCodec.DecodeConnectionStatus(Hex.Parse(hex), 1));

    [Theory]
    [InlineData("01000101")]
    [InlineData("03020300")]
    public void Anc_duplicate_modes_are_rejected(string hex) =>
        Assert.Throws<ProtocolFormatException>(() => AncCodec.DecodeModes(Hex.Parse(hex)));
    [Fact]
    public void Equalizer_band_domain_accepts_16_and_rejects_17_before_gain_reads()
    {
        Assert.Equal(16, UserEqCodec.DecodeConfig(new byte[] { 16, 196, 60 }).BandCount);
        Assert.Throws<ProtocolFormatException>(() => UserEqCodec.DecodeConfig(new byte[] { 17, 196, 60 }));
    }

}
