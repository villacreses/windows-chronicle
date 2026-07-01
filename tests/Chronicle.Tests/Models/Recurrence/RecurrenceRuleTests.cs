using System;
using Chronicle.Models.Recurrence;

namespace Chronicle.Tests.Models.Recurrence;

public class RecurrenceRuleTests
{
    // ── Round-trips ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=DAILY;INTERVAL=3")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=SU,SA")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15")]
    [InlineData("FREQ=YEARLY")]
    [InlineData("FREQ=DAILY;COUNT=10")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231T000000Z")]
    public void Parse_ThenToRruleString_RoundTripsCanonicalForm(string canonical)
    {
        var parsed = RecurrenceRule.Parse(canonical);
        Assert.Equal(canonical, parsed.ToRruleString());
    }

    [Fact]
    public void FactoryRules_RoundTripThroughStringAndBack()
    {
        AssertRoundTrips(RecurrenceRule.Daily());
        AssertRoundTrips(RecurrenceRule.Daily(interval: 4));
        AssertRoundTrips(RecurrenceRule.Weekly(WeekdaySet.Monday | WeekdaySet.Wednesday));
        AssertRoundTrips(RecurrenceRule.Monthly(byMonthDay: 1));
        AssertRoundTrips(RecurrenceRule.Yearly());
        AssertRoundTrips(RecurrenceRule.Daily().WithCount(5));
        AssertRoundTrips(RecurrenceRule.Daily().WithUntil(
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        static void AssertRoundTrips(RecurrenceRule rule)
            => Assert.Equal(rule, RecurrenceRule.Parse(rule.ToRruleString()));
    }

    [Fact]
    public void ByDay_SerializesInSundayAlignedOrder_RegardlessOfInputOrder()
    {
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=FR,MO,WE");
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,WE,FR", rule.ToRruleString());
    }

    [Fact]
    public void Parse_WkstIsAcceptedButNotSerialized()
    {
        // Chronicle is Sunday-aligned everywhere; WKST is tolerated on input
        // but never round-trips out (purely cosmetic).
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=MO;WKST=MO");
        Assert.Equal(RecurrenceFrequency.Weekly, rule.Frequency);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", rule.ToRruleString());
    }

    // ── COUNT / UNTIL are mutually exclusive ─────────────────────────────

    [Fact]
    public void WithUntil_ClearsCount()
    {
        var rule = RecurrenceRule.Daily().WithCount(5).WithUntil(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Null(rule.Count);
        Assert.NotNull(rule.UntilUtc);
    }

    [Fact]
    public void WithCount_ClearsUntil()
    {
        var rule = RecurrenceRule.Daily()
            .WithUntil(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))
            .WithCount(5);
        Assert.Null(rule.UntilUtc);
        Assert.Equal(5, rule.Count);
    }

    [Fact]
    public void WithUntil_RejectsNonUtc()
    {
        var local = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Local);
        Assert.Throws<ArgumentException>(() => RecurrenceRule.Daily().WithUntil(local));
    }

    // ── Parse rejects malformed / unsupported input loudly ───────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_Throws(string input)
        => Assert.Throws<FormatException>(() => RecurrenceRule.Parse(input));

    [Theory]
    [InlineData("FREQ=DAILY;GARBAGE")]
    [InlineData("FREQ")]
    public void Parse_MalformedPart_Throws(string input)
        => Assert.Throws<FormatException>(() => RecurrenceRule.Parse(input));

    [Fact]
    public void Parse_MissingFreq_Throws()
        => Assert.Throws<FormatException>(() => RecurrenceRule.Parse("INTERVAL=2"));

    [Fact]
    public void Parse_UnknownFreq_Throws()
        => Assert.Throws<FormatException>(() => RecurrenceRule.Parse("FREQ=HOURLY"));

    [Fact]
    public void Parse_UnsupportedPart_Throws()
        => Assert.Throws<FormatException>(
            () => RecurrenceRule.Parse("FREQ=DAILY;BYSETPOS=1"));

    [Fact]
    public void Parse_CountAndUntilTogether_Throws()
        => Assert.Throws<FormatException>(
            () => RecurrenceRule.Parse("FREQ=DAILY;COUNT=5;UNTIL=20261231T000000Z"));

    [Fact]
    public void Parse_ByDayOnNonWeekly_Throws()
        => Assert.Throws<FormatException>(
            () => RecurrenceRule.Parse("FREQ=DAILY;BYDAY=MO"));

    [Fact]
    public void Parse_ByMonthDayOnNonMonthly_Throws()
        => Assert.Throws<FormatException>(
            () => RecurrenceRule.Parse("FREQ=WEEKLY;BYMONTHDAY=15"));

    [Theory]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=0")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=32")]
    public void Parse_ByMonthDayOutOfRange_Throws(string input)
        => Assert.Throws<FormatException>(() => RecurrenceRule.Parse(input));

    [Fact]
    public void Parse_OrdinalByDay_Throws()
        => Assert.Throws<FormatException>(
            () => RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=2MO"));

    [Fact]
    public void Parse_IntervalBelowOne_Throws()
        => Assert.Throws<FormatException>(
            () => RecurrenceRule.Parse("FREQ=DAILY;INTERVAL=0"));

    [Fact]
    public void Parse_CountBelowOne_Throws()
        => Assert.Throws<FormatException>(
            () => RecurrenceRule.Parse("FREQ=DAILY;COUNT=0"));

    // ── UNTIL parsing ────────────────────────────────────────────────────

    [Fact]
    public void Parse_DateOnlyUntil_IsUtcMidnight()
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20261231");
        Assert.NotNull(rule.UntilUtc);
        Assert.Equal(DateTimeKind.Utc, rule.UntilUtc!.Value.Kind);
        Assert.Equal(
            new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            rule.UntilUtc);
    }

    [Fact]
    public void Parse_DateTimeUntil_IsUtc()
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20261231T235959Z");
        Assert.Equal(DateTimeKind.Utc, rule.UntilUtc!.Value.Kind);
        Assert.Equal(
            new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            rule.UntilUtc);
    }
}
