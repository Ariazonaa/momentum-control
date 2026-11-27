// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.Presets;

public enum SoundCheckStage
{
    Bass = 0,
    Midrange = 1,
    Treble = 2,
    Finished = 3,
}

/// <summary>
/// Lokaler Sound Check, wie im Linux-Frontend (linux/momentum_control/sound_check.py): drei Schritte (Bass ≤ 500 Hz,
/// Mitten, Höhen ≥ 3 kHz), je ein Vergleich +2 dB gegen −2 dB auf den Bändern des Schritts, begrenzt auf ±6 dB und den
/// Bereich des Geräts. Kein Herstellerverfahren und kein Sound Personalization – nur EQ-Kurven, die der User selbst
/// beim Hören eigener Musik vergleicht. Die Klasse hält nur den Ablauf; geschrieben wird über den Service.
/// </summary>
public sealed class SoundCheck
{
    public const decimal ChangeDb = 2.0m;
    public const decimal LimitDb = 6.0m;

    private readonly int[] _frequencies;
    private readonly decimal _minimum;
    private readonly decimal _maximum;

    public SoundCheck(IReadOnlyList<decimal> gainsDb, IReadOnlyList<int> frequenciesHz, decimal minimumDb, decimal maximumDb)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        if (gainsDb.Count is < 1 or > 16 || gainsDb.Count != frequenciesHz.Count || minimumDb > maximumDb)
        {
            throw new ArgumentException("Sound check needs one gain per known band frequency.", nameof(gainsDb));
        }

        _frequencies = [.. frequenciesHz];
        _minimum = minimumDb;
        _maximum = maximumDb;
        Initial = Array.AsReadOnly(gainsDb.ToArray());
        Confirmed = Initial;
        Prepare();
    }

    public SoundCheckStage Stage { get; private set; }

    /// <summary>Kurve beim Start; „Zurücksetzen“ schreibt sie wieder.</summary>
    public IReadOnlyList<decimal> Initial { get; }

    /// <summary>Zuletzt vom Headset bestätigte Kurve – Grundlage des nächsten Schritts und des gespeicherten Presets.</summary>
    public IReadOnlyList<decimal> Confirmed { get; private set; }

    public IReadOnlyList<decimal> CandidateA { get; private set; } = [];

    public IReadOnlyList<decimal> CandidateB { get; private set; } = [];

    /// <summary>Kurve, die das Headset nach dem Schreiben zurückgelesen hat.</summary>
    public void Confirm(IReadOnlyList<decimal> appliedDb)
    {
        ArgumentNullException.ThrowIfNull(appliedDb);
        if (appliedDb.Count != Initial.Count)
        {
            throw new ArgumentException("Applied curve does not match the band count.", nameof(appliedDb));
        }

        // Die Kandidaten bleiben innerhalb eines Schritts fest (relativ zur Kurve beim Schrittbeginn), sonst würde jedes
        // Anhören die Kurve um weitere 2 dB verschieben.
        Confirmed = Array.AsReadOnly(appliedDb.ToArray());
    }

    /// <summary>Aktuell bestätigte Kurve behalten und zum nächsten Schritt mit Bändern gehen.</summary>
    public void Next()
    {
        if (Stage == SoundCheckStage.Finished)
        {
            return;
        }

        Stage++;
        Prepare();
    }

    /// <summary>
    /// Beide Vergleichskurven eines Schritts: die Bänder des Schritts +2 dB (A) bzw. −2 dB (B), auf ±6 dB und den
    /// Gerätebereich begrenzt, auf 0,1 dB gerundet; die übrigen Bänder bleiben.
    /// </summary>
    public static (IReadOnlyList<decimal> A, IReadOnlyList<decimal> B) ComparisonCurves(
        IReadOnlyList<decimal> gainsDb, IReadOnlyList<int> frequenciesHz, int stage, decimal minimumDb, decimal maximumDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stage, 2);
        var low = Math.Max(-LimitDb, minimumDb);
        var high = Math.Min(LimitDb, maximumDb);
        decimal[] Shift(decimal change) => gainsDb
            .Select((gain, band) => InStage(frequenciesHz[band], stage) ? Math.Round(Math.Clamp(gain + change, low, high), 1) : gain)
            .ToArray();
        return (Shift(ChangeDb), Shift(-ChangeDb));
    }

    private static bool InStage(int frequency, int stage) => stage switch
    {
        0 => frequency <= 500,
        1 => frequency is > 500 and < 3000,
        _ => frequency >= 3000,
    };

    private void Prepare()
    {
        while (Stage != SoundCheckStage.Finished)
        {
            var (a, b) = ComparisonCurves(Confirmed, _frequencies, (int)Stage, _minimum, _maximum);
            if (!a.SequenceEqual(b))
            {
                CandidateA = a;
                CandidateB = b;
                return;
            }

            Stage++;
        }

        CandidateA = [];
        CandidateB = [];
    }
}
