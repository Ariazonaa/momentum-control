// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Momentum4.Core.Zones;

/// <summary>
/// Lokale Sound Zones wie im Linux-Frontend (linux/momentum_control/sound_zones.py): Orte mit Radius, an denen die App
/// beim Betreten einmal ein EQ-Preset anwendet. Rein lokal, ohne Konto oder Cloud; Koordinaten sind private Daten und
/// gehören nicht in die Diagnose.
/// </summary>
public sealed class ZoneException(string message) : Exception(message);

/// <summary>Position mit Genauigkeit (Radius in Metern) und dem Zeitpunkt, zu dem die Quelle sie ermittelt hat.</summary>
public sealed record LocationFix(double Latitude, double Longitude, double AccuracyM, DateTimeOffset ReceivedAt);

/// <summary>EQ-Kurve einer Zone, gebunden an Modell und Bandfrequenzen des Headsets, für das sie gespeichert wurde.</summary>
public sealed record ZonePreset(string Name, string Model, IReadOnlyList<int> FrequenciesHz, IReadOnlyList<decimal> GainsDb)
{
    public bool Matches(string model, IReadOnlyList<int> frequenciesHz) => Model == model && FrequenciesHz.SequenceEqual(frequenciesHz);

    public bool Equals(ZonePreset? other) =>
        other is not null && Name == other.Name && Model == other.Model
        && FrequenciesHz.SequenceEqual(other.FrequenciesHz) && GainsDb.SequenceEqual(other.GainsDb);

    public override int GetHashCode() => HashCode.Combine(Name, Model, FrequenciesHz.Count);
}

public sealed record SoundZone(string Name, string DeviceAddress, double Latitude, double Longitude, int RadiusM, ZonePreset Preset);

public sealed record ZoneData(bool Enabled, IReadOnlyList<SoundZone> Zones)
{
    public bool Equals(ZoneData? other) => other is not null && Enabled == other.Enabled && Zones.SequenceEqual(other.Zones);

    public override int GetHashCode() => HashCode.Combine(Enabled, Zones.Count);
}

public static partial class SoundZones
{
    public const int MaxZones = 16;
    public const int MinRadiusM = 100;
    public const int MaxRadiusM = 50000;
    public const int MaxNameLength = 40;

    /// <summary>Großkreisentfernung in Metern (Haversine, Erddurchmesser 12 742 km wie unter Linux).</summary>
    public static double Distance(double latitude, double longitude, SoundZone zone)
    {
        var latDelta = Radians(zone.Latitude - latitude);
        var lonDelta = Radians(zone.Longitude - longitude);
        var haversine = Math.Pow(Math.Sin(latDelta / 2), 2)
            + (Math.Cos(Radians(latitude)) * Math.Cos(Radians(zone.Latitude)) * Math.Pow(Math.Sin(lonDelta / 2), 2));
        return 12742000 * Math.Asin(Math.Sqrt(Math.Clamp(haversine, 0, 1)));
    }

    public static void Validate(ZoneData data)
    {
        if (data?.Zones is not { } zones)
        {
            throw new ZoneException("Invalid zone settings");
        }

        Require(zones.Count <= MaxZones, "Invalid zone count");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < zones.Count; index++)
        {
            var zone = zones[index];
            Require(zone is not null && Printable(zone.Name, MaxNameLength), "Invalid zone name");
            Require(names.Add(zone!.Name.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant()), "Duplicate zone name");
            Require(zone.DeviceAddress is not null && AddressPattern().IsMatch(zone.DeviceAddress), "Invalid zone device");
            Require(ValidCoordinates(zone.Latitude, zone.Longitude), "Invalid zone coordinates");
            Require(zone.RadiusM is >= MinRadiusM and <= MaxRadiusM, "Invalid zone radius");
            ValidatePreset(zone.Preset);
            for (var previous = 0; previous < index; previous++)
            {
                var other = zones[previous];
                if (other.DeviceAddress == zone.DeviceAddress)
                {
                    Require(Distance(zone.Latitude, zone.Longitude, other) >= zone.RadiusM + other.RadiusM, "Zones for the same device overlap");
                }
            }
        }
    }

    internal static bool ValidCoordinates(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    private static void ValidatePreset(ZonePreset? preset)
    {
        if (preset is not { FrequenciesHz: { Count: >= 1 and <= 16 } frequencies, GainsDb: { } gains } || gains.Count != frequencies.Count
            || !Printable(preset.Name, MaxNameLength) || !Printable(preset.Model, 128))
        {
            throw new ZoneException("Invalid zone preset");
        }

        Require(frequencies.All(frequency => frequency is >= 1 and <= 48000), "Invalid zone preset");
        Require(gains.All(gain => gain is >= -6m and <= 6m && gain * 10 == decimal.Truncate(gain * 10)), "Invalid zone preset");
    }

    private static bool Printable(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ZoneException(message);
        }
    }

    [GeneratedRegex("^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$")]
    private static partial Regex AddressPattern();
}

/// <summary>
/// Meldet eine Zone einmal beim sicheren Betreten (Position samt Genauigkeit ganz im Radius). Verlassen gilt erst, wenn
/// die Position mit Genauigkeit sicher außerhalb liegt (50 m Hysterese); unsichere, alte (&gt; 120 s), zukünftige oder
/// mehrdeutige Positionen ändern nichts.
/// </summary>
public sealed class ZoneTracker
{
    private (string Name, string Device)? _active;

    public void Reset() => _active = null;

    public SoundZone? Update(LocationFix fix, IReadOnlyList<SoundZone> zones, string deviceAddress, string model,
        IReadOnlyList<int> frequenciesHz, DateTimeOffset now)
    {
        var age = (now - fix.ReceivedAt).TotalSeconds;
        if (!SoundZones.ValidCoordinates(fix.Latitude, fix.Longitude) || !double.IsFinite(fix.AccuracyM)
            || fix.AccuracyM is < 0 or > 1000000 || age is < 0 or > 120)
        {
            return null;
        }

        try
        {
            SoundZones.Validate(new ZoneData(true, zones));
        }
        catch (ZoneException)
        {
            return null;
        }

        var matching = zones.Where(zone => zone.DeviceAddress == deviceAddress && zone.Preset.Matches(model, frequenciesHz)).ToList();
        var active = matching.FirstOrDefault(zone => _active == (zone.Name, zone.DeviceAddress));
        if (active is not null && SoundZones.Distance(fix.Latitude, fix.Longitude, active) - fix.AccuracyM <= active.RadiusM + 50)
        {
            return null;
        }

        var candidates = matching.Where(zone => SoundZones.Distance(fix.Latitude, fix.Longitude, zone) + fix.AccuracyM <= zone.RadiusM).ToList();
        if (candidates.Count > 1)
        {
            return null;
        }

        if (candidates.Count == 0)
        {
            if (!matching.Any(zone => SoundZones.Distance(fix.Latitude, fix.Longitude, zone) - fix.AccuracyM <= zone.RadiusM))
            {
                _active = null;
            }

            return null;
        }

        _active = (candidates[0].Name, candidates[0].DeviceAddress);
        return candidates[0];
    }
}

/// <summary>
/// <c>zones.json</c> im Datenverzeichnis der App (Schema-Version 1, wie unter Linux). Ein unlesbares oder ungültiges
/// Dokument wird gemeldet und nie überschrieben.
/// </summary>
public sealed class ZoneStore(string path)
{
    private const int MaxBytes = 256 * 1024;

    public string Path { get; } = path;

    public ZoneData Load()
    {
        byte[] content;
        try
        {
            if (!File.Exists(Path))
            {
                return new ZoneData(false, []);
            }

            content = File.ReadAllBytes(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ZoneException("Cannot read sound zones");
        }

        return Decode(content);
    }

    public void Save(ZoneData data)
    {
        SoundZones.Validate(data);
        var content = Encode(data);
        if (content.Length > MaxBytes)
        {
            throw new ZoneException("Zone data exceeds size limit");
        }

        try
        {
            if (File.Exists(Path))
            {
                Decode(File.ReadAllBytes(Path)); // ein kaputtes Dokument bleibt liegen, bis der User es ansieht
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ZoneException("Cannot save sound zones");
        }
    }

    private static byte[] Encode(ZoneData data)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteBoolean("enabled", data.Enabled);
            writer.WriteStartArray("zones");
            foreach (var zone in data.Zones)
            {
                writer.WriteStartObject();
                writer.WriteString("name", zone.Name);
                writer.WriteString("device_address", zone.DeviceAddress);
                writer.WriteNumber("latitude", zone.Latitude);
                writer.WriteNumber("longitude", zone.Longitude);
                writer.WriteNumber("radius_m", zone.RadiusM);
                writer.WriteStartObject("preset");
                writer.WriteString("name", zone.Preset.Name);
                writer.WriteString("model", zone.Preset.Model);
                writer.WriteStartArray("frequencies");
                foreach (var frequency in zone.Preset.FrequenciesHz)
                {
                    writer.WriteNumberValue(frequency);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("gains");
                foreach (var gain in zone.Preset.GainsDb)
                {
                    writer.WriteNumberValue(gain);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static ZoneData Decode(byte[] content)
    {
        try
        {
            if (content.Length > MaxBytes)
            {
                throw new ZoneException("Zone data exceeds size limit");
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            Expect(root, "version", "enabled", "zones");
            if (root.GetProperty("version").GetInt32() != 1)
            {
                throw new ZoneException("Unsupported zone version");
            }

            var zones = new List<SoundZone>();
            foreach (var raw in root.GetProperty("zones").EnumerateArray())
            {
                Expect(raw, "name", "device_address", "latitude", "longitude", "radius_m", "preset");
                var preset = raw.GetProperty("preset");
                Expect(preset, "name", "model", "frequencies", "gains");
                zones.Add(new SoundZone(raw.GetProperty("name").GetString()!, raw.GetProperty("device_address").GetString()!,
                    raw.GetProperty("latitude").GetDouble(), raw.GetProperty("longitude").GetDouble(), raw.GetProperty("radius_m").GetInt32(),
                    new ZonePreset(preset.GetProperty("name").GetString()!, preset.GetProperty("model").GetString()!,
                        [.. preset.GetProperty("frequencies").EnumerateArray().Select(item => item.GetInt32())],
                        [.. preset.GetProperty("gains").EnumerateArray().Select(item => item.GetDecimal())])));
            }

            var data = new ZoneData(root.GetProperty("enabled").GetBoolean(), zones);
            SoundZones.Validate(data);
            return data;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new ZoneException("Invalid sound zones document");
        }
    }

    private static void Expect(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
        {
            throw new ZoneException("Invalid zone schema");
        }
    }
}
