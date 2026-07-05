using System;
using Chronicle.Helpers;
using Chronicle.Models.Recurrence;

namespace Chronicle.Tests.Models.Recurrence;

/// <summary>
/// Layer 5 — the recurrence-picker logic extracted from <c>EventEditPopover</c>
/// into <see cref="RecurrencePickerModel"/>. Covers rule construction from
/// picker state (with the inline validation the form surfaces), seeding state
/// back from a rule, and round-tripping rules within the picker's representable
/// subset (INTERVAL 1, monthly on the start day).
/// </summary>
public sealed class RecurrencePickerModelTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0)
        => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static RecurrencePickerState State(
        RecurrenceFrequencyChoice frequency,
        WeekdaySet weeklyDays = WeekdaySet.None,
        RecurrenceEndChoice end = RecurrenceEndChoice.Never,
        DateTime? untilLocalDate = null,
        string countText = "10")
        => new(frequency, weeklyDays, end, untilLocalDate ?? new DateTime(2026, 9, 1), countText);

    // ── BuildRule: frequency ──────────────────────────────────────────────

    [Fact]
    public void BuildRule_None_ReturnsNullRule()
    {
        var (rule, error, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.None), Utc(2026, 6, 1, 9, 0));

        Assert.True(ok);
        Assert.Null(rule);
        Assert.Null(error);
    }

    [Fact]
    public void BuildRule_Daily_ProducesDailyRule()
    {
        var (rule, _, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Daily), Utc(2026, 6, 1, 9, 0));

        Assert.True(ok);
        Assert.Equal(RecurrenceFrequency.Daily, rule!.Frequency);
        Assert.Null(rule.UntilUtc);
        Assert.Null(rule.Count);
    }

    [Fact]
    public void BuildRule_WeeklyWithDays_ProducesWeeklyByDay()
    {
        var days = RecurrenceRule.FromDayOfWeek(DayOfWeek.Monday)
                 | RecurrenceRule.FromDayOfWeek(DayOfWeek.Wednesday);

        var (rule, _, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Weekly, weeklyDays: days), Utc(2026, 6, 1, 9, 0));

        Assert.True(ok);
        Assert.Equal(RecurrenceFrequency.Weekly, rule!.Frequency);
        Assert.Equal(days, rule.ByDay);
    }

    [Fact]
    public void BuildRule_WeeklyWithNoDays_ReturnsError()
    {
        var (rule, error, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Weekly, weeklyDays: WeekdaySet.None),
            Utc(2026, 6, 1, 9, 0));

        Assert.False(ok);
        Assert.Null(rule);
        Assert.Contains("day", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRule_Monthly_UsesStartLocalDayOfMonth()
    {
        // Noon UTC → same local calendar day in any real zone.
        var startUtc = Utc(2026, 6, 17, 12, 0);

        var (rule, _, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Monthly), startUtc);

        Assert.True(ok);
        Assert.Equal(RecurrenceFrequency.Monthly, rule!.Frequency);
        Assert.Equal(startUtc.ToLocalTime().Day, rule.ByMonthDay);
    }

    [Fact]
    public void BuildRule_Yearly_ProducesYearlyRule()
    {
        var (rule, _, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Yearly), Utc(2026, 6, 1, 9, 0));

        Assert.True(ok);
        Assert.Equal(RecurrenceFrequency.Yearly, rule!.Frequency);
    }

    // ── BuildRule: ends ───────────────────────────────────────────────────

    [Fact]
    public void BuildRule_EndsOnDate_SetsInclusiveEndOfDayUntil()
    {
        var startUtc = Utc(2026, 6, 1, 9, 0);
        var untilLocalDate = new DateTime(2026, 6, 30);

        var (rule, _, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Daily,
                end: RecurrenceEndChoice.OnDate, untilLocalDate: untilLocalDate),
            startUtc);

        Assert.True(ok);
        // UNTIL is the last tick of the chosen local day (inclusive).
        var expected = DateHelpers
            .CombineLocalDateAndTimeAsUtc(untilLocalDate, TimeSpan.Zero)
            .AddDays(1).AddTicks(-1);
        Assert.Equal(expected, rule!.UntilUtc);
        Assert.Equal(DateTimeKind.Utc, rule.UntilUtc!.Value.Kind);
    }

    [Fact]
    public void BuildRule_EndsOnDateBeforeStart_ReturnsError()
    {
        var (rule, error, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Daily,
                end: RecurrenceEndChoice.OnDate, untilLocalDate: new DateTime(2026, 6, 1)),
            Utc(2026, 6, 15, 9, 0));

        Assert.False(ok);
        Assert.Null(rule);
        Assert.Contains("on or after", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRule_EndsAfterN_SetsCount()
    {
        var (rule, _, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Daily,
                end: RecurrenceEndChoice.AfterN, countText: "5"),
            Utc(2026, 6, 1, 9, 0));

        Assert.True(ok);
        Assert.Equal(5, rule!.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("")]
    public void BuildRule_EndsAfterN_InvalidCount_ReturnsError(string countText)
    {
        var (rule, error, ok) = RecurrencePickerModel.BuildRule(
            State(RecurrenceFrequencyChoice.Daily,
                end: RecurrenceEndChoice.AfterN, countText: countText),
            Utc(2026, 6, 1, 9, 0));

        Assert.False(ok);
        Assert.Null(rule);
        Assert.Contains("positive integer", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── SeedState ─────────────────────────────────────────────────────────

    [Fact]
    public void SeedState_NullRule_DefaultsToNoneWithStartWeekday()
    {
        var startLocal = new DateTime(2026, 6, 3, 9, 0, 0);

        var state = RecurrencePickerModel.SeedState(null, startLocal);

        Assert.Equal(RecurrenceFrequencyChoice.None, state.Frequency);
        Assert.Equal(RecurrenceEndChoice.Never, state.End);
        Assert.Equal(RecurrenceRule.FromDayOfWeek(startLocal.DayOfWeek), state.WeeklyDays);
        Assert.Equal("10", state.CountText);
        Assert.Equal(startLocal.AddMonths(3).Date, state.UntilLocalDate);
    }

    [Fact]
    public void SeedState_WeeklyByDayRule_MapsFrequencyAndDays()
    {
        var days = RecurrenceRule.FromDayOfWeek(DayOfWeek.Monday)
                 | RecurrenceRule.FromDayOfWeek(DayOfWeek.Friday);
        var rule = RecurrenceRule.Weekly(days);

        var state = RecurrencePickerModel.SeedState(rule, new DateTime(2026, 6, 3, 9, 0, 0));

        Assert.Equal(RecurrenceFrequencyChoice.Weekly, state.Frequency);
        Assert.Equal(days, state.WeeklyDays);
        Assert.Equal(RecurrenceEndChoice.Never, state.End);
    }

    [Fact]
    public void SeedState_UntilRule_MapsToOnDate()
    {
        var until = Utc(2026, 12, 31, 0, 0);
        var rule = RecurrenceRule.Daily().WithUntil(until);

        var state = RecurrencePickerModel.SeedState(rule, new DateTime(2026, 6, 1));

        Assert.Equal(RecurrenceEndChoice.OnDate, state.End);
        Assert.Equal(until.ToLocalTime().Date, state.UntilLocalDate);
    }

    [Fact]
    public void SeedState_CountRule_MapsToAfterN()
    {
        var rule = RecurrenceRule.Daily().WithCount(7);

        var state = RecurrencePickerModel.SeedState(rule, new DateTime(2026, 6, 1));

        Assert.Equal(RecurrenceEndChoice.AfterN, state.End);
        Assert.Equal("7", state.CountText);
    }

    // ── Round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_RepresentableRules_RuleToStateToRule()
    {
        var startLocal = new DateTime(2026, 6, 17, 9, 0, 0);
        var startUtc = Utc(2026, 6, 17, 12, 0);
        var startDay = startUtc.ToLocalTime().Day;

        var mon = RecurrenceRule.FromDayOfWeek(DayOfWeek.Monday);
        var wed = RecurrenceRule.FromDayOfWeek(DayOfWeek.Wednesday);

        var rules = new[]
        {
            RecurrenceRule.Daily(),
            RecurrenceRule.Daily().WithCount(5),
            RecurrenceRule.Weekly(mon | wed),
            RecurrenceRule.Yearly(),
            RecurrenceRule.Monthly(byMonthDay: startDay),
        };

        foreach (var rule in rules)
        {
            var state = RecurrencePickerModel.SeedState(rule, startLocal);
            var (rebuilt, error, ok) = RecurrencePickerModel.BuildRule(state, startUtc);

            Assert.True(ok, $"BuildRule failed for {rule.ToRruleString()}: {error}");
            Assert.Equal(rule, rebuilt); // record equality over all rule fields
        }
    }
}
