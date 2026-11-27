// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Zones;

namespace Momentum4.Core.Tests;

/// <summary>Lokale Sound Zones wie im Linux-Frontend (linux/tests/test_sound_zones.py, TrackerTests und StoreTests).</summary>
public sealed class SoundZoneTests : IDisposable
{
    private const string Address = "AA:BB:CC:DD:EE:FF";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(100);
    private static readonly ZonePreset Preset = new("Calm", "M4", [100, 100, 1000], [1.0m, 2.0m, -1.0m]);
    private static readonly SoundZone Zone = new("Home", Address, 0, 0, 100, Preset);
    private readonly ZoneTracker _tracker = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "m4-zones-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static LocationFix Fix(double latitude = 0, double longitude = 0, double accuracy = 0, double receivedSeconds = 100) =>
        new(latitude, longitude, accuracy, DateTimeOffset.UnixEpoch.AddSeconds(receivedSeconds));

    private SoundZone? Update(LocationFix? fix = null, IReadOnlyList<SoundZone>? zones = null, string address = Address,
        string model = "M4", int[]? frequencies = null) =>
        _tracker.Update(fix ?? Fix(), zones ?? [Zone], address, model, frequencies ?? [100, 100, 1000], Now);

    [Fact]
    public void Enters_once_and_leaves_only_when_confident()
    {
        Assert.Equal(Zone, Update());
        Assert.Null(Update());
        Assert.Null(Update(Fix(longitude: 0.0012)));
        Assert.Null(Update());
        Assert.Null(Update(Fix(longitude: 0.002, accuracy: 100)));
        Assert.Null(Update());
        Assert.Null(Update(Fix(longitude: 0.002)));
        Assert.Equal(Zone, Update());
        _tracker.Reset();
        Assert.Equal(Zone, Update());
    }

    [Fact]
    public void Ignores_invalid_stale_uncertain_and_foreign_targets()
    {
        LocationFix[] invalid =
        [
            Fix(accuracy: 101), Fix(accuracy: -1), Fix(accuracy: 1000001), Fix(latitude: 91), Fix(longitude: 181),
            Fix(latitude: double.NaN), Fix(receivedSeconds: -21), Fix(receivedSeconds: 101),
        ];
        foreach (var fix in invalid)
        {
            Assert.Null(Update(fix));
        }

        Assert.Null(Update(address: "00:11:22:33:44:55"));
        Assert.Null(Update(model: "M5"));
        Assert.Null(Update(frequencies: [100, 1000, 100]));
        Assert.Null(Update(frequencies: [100, 1000]));
        Assert.Equal(Zone, Update(Fix(accuracy: 100, receivedSeconds: -20)));
    }

    [Fact]
    public void Active_zone_wins_over_a_touching_one()
    {
        var offset = 200 / 6371000.0 * 180 / Math.PI;
        var second = Zone with { Name = "Next", Longitude = offset };
        SoundZone[] zones = [Zone, second];
        Assert.Equal(Zone, Update(zones: zones));
        Assert.Null(Update(Fix(longitude: offset * 0.6), zones));
        Assert.Null(Update(Fix(longitude: offset, receivedSeconds: -30), zones));
        Assert.Null(Update(zones: zones));
        Assert.Equal(second, Update(Fix(longitude: offset), zones));
        Assert.Null(Update(Fix(longitude: offset), zones));
    }

    [Fact]
    public void Ambiguous_overlap_is_denied()
    {
        Assert.Null(Update(zones: [Zone, Zone with { Name = "Other" }]));
        Assert.Equal(Zone, Update());
    }

    [Fact]
    public void Store_roundtrips_and_defaults_to_disabled()
    {
        var store = new ZoneStore(Path.Combine(_directory, "zones.json"));
        Assert.Equal(new ZoneData(false, []), store.Load());

        var data = new ZoneData(true, [Zone]);
        store.Save(data);

        var loaded = store.Load();
        Assert.True(loaded.Enabled);
        var zone = Assert.Single(loaded.Zones);
        Assert.Equal(Zone, zone);
    }

    [Fact]
    public void Store_rejects_invalid_zones_and_keeps_the_file()
    {
        var path = Path.Combine(_directory, "zones.json");
        var store = new ZoneStore(path);
        store.Save(new ZoneData(true, [Zone]));
        var before = File.ReadAllText(path);
        SoundZone[][] invalid =
        [
            [Zone with { RadiusM = 99 }],
            [Zone with { RadiusM = 50001 }],
            [Zone with { Latitude = 90.5 }],
            [Zone with { Name = "" }],
            [Zone with { Name = new string('x', 41) }],
            [Zone with { DeviceAddress = "aa:bb:cc:dd:ee:ff" }],
            [Zone, Zone with { Name = "home " }],
            [Zone, Zone with { Name = "Close", Longitude = 0.001 }],
            [Zone with { Preset = Preset with { GainsDb = [1.0m] } }],
            [.. Enumerable.Range(0, 17).Select(i => Zone with { Name = $"Z{i}", Latitude = i })],
        ];
        foreach (var zones in invalid)
        {
            Assert.Throws<ZoneException>(() => store.Save(new ZoneData(true, zones)));
        }

        Assert.Equal(before, File.ReadAllText(path));
        store.Save(new ZoneData(true, [Zone, Zone with { Name = "Elsewhere", DeviceAddress = "00:11:22:33:44:55" }]));
    }

    [Fact]
    public void Corrupt_file_is_reported_and_preserved()
    {
        var path = Path.Combine(_directory, "zones.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"version\":2}");
        var store = new ZoneStore(path);

        Assert.Throws<ZoneException>(() => store.Load());
        Assert.Throws<ZoneException>(() => store.Save(new ZoneData(false, [])));
        Assert.Equal("{\"version\":2}", File.ReadAllText(path));
    }
}
