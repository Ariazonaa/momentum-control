using System.Globalization;
using System.Text;

namespace Momentum4.Protocol.Features;

public static class SharedHeadsetCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string DecodeName(ReadOnlySpan<byte> payload, int limit)
    {
        while (payload.Length > 0 && payload[^1] == 0)
        {
            payload = payload[..^1];
        }

        if (payload.Length == 0 || payload.Length > limit)
        {
            throw new ProtocolFormatException("Invalid device name length", payload);
        }

        try
        {
            var name = StrictUtf8.GetString(payload);
            // Beim Lesen jede Leerzeichen-Art zulassen: iOS/watchOS benennen Geräte z. B. "Apple<U+00A0>Watch" (GitHub #10).
            if (name.EnumerateRunes().Any(IsUnprintable))
            {
                throw new ProtocolFormatException("Invalid device name", payload, ProtocolFormatKind.DeviceName);
            }

            return name;
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolFormatException("Invalid device name UTF-8", payload);
        }
    }

    public static byte[] EncodeName(string name, int limit)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            // Beim Umbenennen bleibt es streng: nur U+0020 als Leerzeichen.
            if (name.EnumerateRunes().Any(rune => IsUnprintable(rune) || IsNonAsciiSpace(rune)))
            {
                throw new ArgumentException("Device name must contain printable characters", nameof(name));
            }

            var payload = StrictUtf8.GetBytes(name.Trim());
            DecodeName(payload, limit);
            return payload;
        }
        catch (Exception exception) when (exception is EncoderFallbackException or ProtocolFormatException)
        {
            throw new ArgumentException("Invalid printable device name or UTF-8 byte length", nameof(name), exception);
        }
    }

    public static int DecodeByte(ReadOnlySpan<byte> payload, int maximum, string setting)
    {
        if (payload.Length != 1 || payload[0] > maximum)
        {
            throw new ProtocolFormatException($"Invalid {setting}", payload);
        }

        return payload[0];
    }

    public static bool DecodeCharging(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is not (1 or 2) || payload.ContainsAnyExcept((byte)0, (byte)1, (byte)2))
        {
            throw new ProtocolFormatException("Invalid charging response", payload);
        }

        return payload[0] == 1;
    }

    private static bool IsUnprintable(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
        UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static bool IsNonAsciiSpace(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator && rune.Value != 32;
}
