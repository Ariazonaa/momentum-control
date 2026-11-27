// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Presets;

namespace Momentum4.Core.Tests;

/// <summary>Lokaler Sound Check wie im Linux-Frontend (linux/momentum_control/sound_check.py).</summary>
public sealed class SoundCheckTests
{
    private static readonly int[] M4Frequencies = [90, 325, 1500, 6500, 6500];

    [Fact]
    public void Comparison_curves_raise_and_lower_only_the_stage_bands()
    {
        var (a, b) = SoundCheck.ComparisonCurves([1.0m, 0m, 0m, 0m, 0m], M4Frequencies, 0, -6m, 6m);

        Assert.Equal(new[] { 3.0m, 2.0m, 0m, 0m, 0m }, a);
        Assert.Equal(new[] { -1.0m, -2.0m, 0m, 0m, 0m }, b);

        var (midA, midB) = SoundCheck.ComparisonCurves([0m, 0m, 0m, 0m, 0m], M4Frequencies, 1, -6m, 6m);
        Assert.Equal(new[] { 0m, 0m, 2.0m, 0m, 0m }, midA);
        Assert.Equal(new[] { 0m, 0m, -2.0m, 0m, 0m }, midB);

        var (highA, _) = SoundCheck.ComparisonCurves([0m, 0m, 0m, 0m, 0m], M4Frequencies, 2, -6m, 6m);
        Assert.Equal(new[] { 0m, 0m, 0m, 2.0m, 2.0m }, highA);
    }

    [Fact]
    public void Curves_clamp_to_six_db_and_to_the_device_range()
    {
        var (a, b) = SoundCheck.ComparisonCurves([5.0m, -5.0m, 0m, 0m, 0m], M4Frequencies, 0, -6m, 6m);
        Assert.Equal(new[] { 6.0m, -3.0m, 0m, 0m, 0m }, a);
        Assert.Equal(new[] { 3.0m, -6.0m, 0m, 0m, 0m }, b);

        var (narrowA, _) = SoundCheck.ComparisonCurves([4.0m, 0m, 0m, 0m, 0m], M4Frequencies, 0, -4m, 4.5m);
        Assert.Equal(new[] { 4.5m, 2.0m, 0m, 0m, 0m }, narrowA);
    }

    [Fact]
    public void Starts_at_bass_and_offers_both_candidates()
    {
        var check = new SoundCheck([0m, 0m, 0m, 0m, 0m], M4Frequencies, -6m, 6m);

        Assert.Equal(SoundCheckStage.Bass, check.Stage);
        Assert.Equal(new[] { 2.0m, 2.0m, 0m, 0m, 0m }, check.CandidateA);
        Assert.Equal(new[] { -2.0m, -2.0m, 0m, 0m, 0m }, check.CandidateB);
    }

    [Fact]
    public void Confirmed_curve_is_the_baseline_of_the_next_stage()
    {
        var check = new SoundCheck([0m, 0m, 0m, 0m, 0m], M4Frequencies, -6m, 6m);
        check.Confirm(check.CandidateA);
        check.Next();

        Assert.Equal(SoundCheckStage.Midrange, check.Stage);
        Assert.Equal(new[] { 2.0m, 2.0m, 2.0m, 0m, 0m }, check.CandidateA);
        Assert.Equal(new[] { 2.0m, 2.0m, -2.0m, 0m, 0m }, check.CandidateB);
    }

    [Fact]
    public void Stages_without_bands_are_skipped_and_the_end_is_finished()
    {
        var check = new SoundCheck([0m, 0m], [60, 150], -6m, 6m);
        Assert.Equal(SoundCheckStage.Bass, check.Stage);

        check.Next();

        Assert.Equal(SoundCheckStage.Finished, check.Stage);
        Assert.Equal(new[] { 0m, 0m }, check.Confirmed);
    }

    [Fact]
    public void Restore_returns_to_the_starting_curve()
    {
        var check = new SoundCheck([1.0m, 0m, 0m, 0m, 0m], M4Frequencies, -6m, 6m);
        check.Confirm(check.CandidateB);

        Assert.Equal(new[] { 1.0m, 0m, 0m, 0m, 0m }, check.Initial);
        Assert.NotEqual(check.Initial, check.Confirmed);
    }

    [Fact]
    public void Rejects_a_topology_that_does_not_match()
    {
        Assert.Throws<ArgumentException>(() => new SoundCheck([0m, 0m], [90], -6m, 6m));
        var check = new SoundCheck([0m, 0m, 0m, 0m, 0m], M4Frequencies, -6m, 6m);
        Assert.Throws<ArgumentException>(() => check.Confirm([0m, 0m]));
    }
}
