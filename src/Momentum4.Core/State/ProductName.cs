// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Services;

namespace Momentum4.Core.State;

/// <summary>Anzeigename und Namensgrenze je Modell; die Modell-ID kommt vom Gerät (<c>0x1206</c>, z. B. „M4AEBT Black“).</summary>
public static class ProductName
{
    /// <summary>„MOMENTUM 4“, „MOMENTUM 5“ oder – unbekannt bzw. noch nicht verbunden – „MOMENTUM“.</summary>
    public static string For(string? modelId)
    {
        var gate = new FeatureGate(null, modelId);
        return gate.IsMomentum4 ? "MOMENTUM 4" : gate.IsMomentum5 ? "MOMENTUM 5" : "MOMENTUM";
    }

    /// <summary>Größte Länge des Gerätenamens in UTF-8-Bytes (localName, Feature 20): 16 am MOMENTUM 5, sonst 30.</summary>
    public static int MaxNameBytes(string? modelId) => new FeatureGate(null, modelId).IsMomentum5 ? 16 : 30;
}
