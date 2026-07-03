using System;
using Chronicle.Models.Recurrence;
using static Chronicle.Tests.Models.Recurrence.RecurrenceTestSupport;

namespace Chronicle.Tests.Models.Recurrence;

public class RecurrenceExpanderComputeEndUtcTests
{
    [Fact]
    public void ComputeEndUtc_InfiniteSeries_ReturnsNull()
    {
        var rule = RecurrenceRule.Daily();
        Assert.Null(RecurrenceExpander.ComputeEndUtc(
            Utc(2026, 1, 1, 9, 0), TimeSpan.FromHours(1), rule, null));
    }

    [Fact]
    public void ComputeEndUtc_Count_ReturnsLastOccurrenceEnd()
    {
        var rule = RecurrenceRule.Daily().WithCount(5);
        var end = RecurrenceExpander.ComputeEndUtc(
            Utc(2026, 1, 1, 9, 0), TimeSpan.FromHours(1), rule, null);
        Assert.Equal(Utc(2026, 1, 5, 10, 0), end); // Jan 5 09:00 + 1h
    }

    [Fact]
    public void ComputeEndUtc_Until_ReturnsLastOccurrenceEnd()
    {
        var rule = RecurrenceRule.Daily().WithUntil(Utc(2026, 1, 3, 9, 0));
        var end = RecurrenceExpander.ComputeEndUtc(
            Utc(2026, 1, 1, 9, 0), TimeSpan.FromHours(1), rule, null);
        Assert.Equal(Utc(2026, 1, 3, 10, 0), end);
    }

    [Fact]
    public void ComputeEndUtc_MatchesLastExpandedOccurrence_ForCount()
    {
        var start = Utc(2026, 1, 1, 9, 0);
        var dur = TimeSpan.FromHours(1);
        var rule = RecurrenceRule.Daily().WithCount(5);
        var master = Master(rule.ToRruleString(), start, dur);

        var expanded = Expand(master, Utc(2026, 1, 1), Utc(2027, 1, 1));
        var computed = RecurrenceExpander.ComputeEndUtc(start, dur, rule, null);

        Assert.Equal(expanded[^1].EndTimeUtc, computed);
    }

    [Fact]
    public void ComputeEndUtc_MatchesLastExpandedOccurrence_ForTzAwareCount()
    {
        // Exercises the shared WalkAnchorsForMaster dispatch across DST:
        // the cached end must equal Expand's last occurrence end
        // (DECISIONS.md / RECURRENCE.md invariant #8).
        var start = Utc(2026, 2, 27, 14, 0); // 09:00 EST
        var dur = TimeSpan.FromHours(1);
        var rule = RecurrenceRule.Weekly(WeekdaySet.None).WithCount(4);
        var master = Master(rule.ToRruleString(), start, dur, timeZoneId: "America/New_York");

        var expanded = Expand(master, Utc(2026, 1, 1), Utc(2027, 1, 1));
        var computed = RecurrenceExpander.ComputeEndUtc(start, dur, rule, "America/New_York");

        Assert.Equal(4, expanded.Count);
        Assert.Equal(expanded[^1].EndTimeUtc, computed);
    }
}
