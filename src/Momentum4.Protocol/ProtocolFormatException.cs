// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol;

/// <summary>Was an einer Antwort nicht gepasst hat.</summary>
public enum ProtocolFormatKind
{
    /// <summary>Struktur oder Wertebereich der Antwort stimmt nicht; die Session ist nicht mehr vertrauenswürdig.</summary>
    Frame,

    /// <summary>Der Frame ist intakt, nur ein vom Headset gemeldeter Gerätename ist nicht darstellbar.</summary>
    DeviceName,
}

/// <summary>Eine Antwort passt nicht zum dokumentierten Format. Es wird nie still ein Wert geraten.</summary>
public sealed class ProtocolFormatException : Exception
{
    public ProtocolFormatException(string message, ReadOnlySpan<byte> payload)
        : this(message, payload, ProtocolFormatKind.Frame)
    {
    }

    public ProtocolFormatException(string message, ReadOnlySpan<byte> payload, ProtocolFormatKind kind)
        : base($"{message} (Payload: [{Hex.Format(payload)}])")
    {
        Payload = payload.ToArray();
        Kind = kind;
    }

    public byte[] Payload { get; }

    public ProtocolFormatKind Kind { get; }
}
