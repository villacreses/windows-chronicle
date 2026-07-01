using System;
using System.Linq;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using static Chronicle.Tests.Models.Recurrence.RecurrenceTestSupport;

namespace Chronicle.Tests.Models.Recurrence;

public class RecurrenceExpanderTests
{
    [Fact]
    public void NonRecurringMaster_YieldsNothing()
    {
        var master = new Event
        {
            Id = Guid.NewGuid(),
            StartTimeUtc = Utc(2026, 1, 1, 9, 0),
            EndTimeUtc = Utc(2026, 1, 1, 10, 0),
            CreatedAtUtc = Utc(2026, 1, 1),
            UpdatedAtUtc = Utc(2026, 1, 1),
        };
        Assert.Empty(RecurrenceExpander.Expand(master, Utc(2026, 1, 1), Utc(2026, 12, 31)));
    }

    [Fact]
    public void Daily_EmitsConsecutiveDays_WithinRange()
    {
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 5, 23, 59));

        Assert.Equal(
            new[]
            {
                Utc(2026,1,1,9,0), Utc(2026,1,2,9,0), Utc(2026,1,3,9,0),
                Utc(2026,1,4,9,0), Utc(2026,1,5,9,0),
            },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Occurrences_CarryMasterIdentityAndAnchor()
    {
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 2, 23, 59));

        Assert.All(occ, o =>
        {
            Assert.Equal(master.Id, o.Id);
            Assert.Null(o.RecurrenceRule);
            Assert.True(o.IsOccurrence);
            Assert.Equal(o.StartTimeUtc, o.SeriesAnchorUtc); // no override applied
        });
    }

    [Fact]
    public void Daily_RangeFiltersLeadingOccurrences()
    {
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 3), Utc(2026, 1, 5, 23, 59));

        Assert.Equal(
            new[] { Utc(2026,1,3,9,0), Utc(2026,1,4,9,0), Utc(2026,1,5,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Daily_Interval_SkipsDays()
    {
        var master = Master("FREQ=DAILY;INTERVAL=2", Utc(2026, 1, 1, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 10, 23, 59));

        Assert.Equal(
            new[]
            {
                Utc(2026,1,1,9,0), Utc(2026,1,3,9,0), Utc(2026,1,5,9,0),
                Utc(2026,1,7,9,0), Utc(2026,1,9,9,0),
            },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Weekly_NoByDay_RecursSameWeekday()
    {
        var master = Master("FREQ=WEEKLY", Utc(2026, 1, 1, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 29, 23, 59));

        Assert.Equal(
            new[]
            {
                Utc(2026,1,1,9,0), Utc(2026,1,8,9,0), Utc(2026,1,15,9,0),
                Utc(2026,1,22,9,0), Utc(2026,1,29,9,0),
            },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Weekly_ByDay_EmitsSundayAlignedOrder()
    {
        // Start Monday 2026-01-05; BYDAY=MO,WE across two weeks.
        var master = Master("FREQ=WEEKLY;BYDAY=MO,WE", Utc(2026, 1, 5, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 5), Utc(2026, 1, 15, 23, 59));

        Assert.Equal(
            new[] { Utc(2026,1,5,9,0), Utc(2026,1,7,9,0), Utc(2026,1,12,9,0), Utc(2026,1,14,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Weekly_ByDay_DoesNotEmitBeforeStart()
    {
        // Start Wednesday 2026-01-07; the Monday of that first week (Jan 5)
        // precedes the start and must not be emitted.
        var master = Master("FREQ=WEEKLY;BYDAY=MO,WE", Utc(2026, 1, 7, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 15, 23, 59));

        Assert.Equal(
            new[] { Utc(2026,1,7,9,0), Utc(2026,1,12,9,0), Utc(2026,1,14,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Monthly_SameDayEachMonth()
    {
        var master = Master("FREQ=MONTHLY", Utc(2026, 1, 15, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 4, 30, 23, 59));

        Assert.Equal(
            new[] { Utc(2026,1,15,9,0), Utc(2026,2,15,9,0), Utc(2026,3,15,9,0), Utc(2026,4,15,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Monthly_ByMonthDay31_SkipsShortMonthsInsteadOfClamping()
    {
        var master = Master("FREQ=MONTHLY;BYMONTHDAY=31", Utc(2026, 1, 31, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 6, 30, 23, 59));

        // Feb (28), Apr (30), Jun (30) have no 31st and are skipped, not
        // clamped to month-end.
        Assert.Equal(
            new[] { Utc(2026,1,31,9,0), Utc(2026,3,31,9,0), Utc(2026,5,31,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Yearly_EmitsSameDateEachYear()
    {
        var master = Master("FREQ=YEARLY", Utc(2026, 3, 10, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2029, 12, 31, 23, 59));

        Assert.Equal(
            new[] { Utc(2026,3,10,9,0), Utc(2027,3,10,9,0), Utc(2028,3,10,9,0), Utc(2029,3,10,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Yearly_Feb29_SkipsNonLeapYears()
    {
        var master = Master("FREQ=YEARLY", Utc(2024, 2, 29, 9, 0));
        var occ = Expand(master, Utc(2024, 1, 1), Utc(2032, 12, 31, 23, 59));

        // 2025/26/27/29/30/31 are non-leap and skipped.
        Assert.Equal(
            new[] { Utc(2024,2,29,9,0), Utc(2028,2,29,9,0), Utc(2032,2,29,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Count_ProducesExactlyCountOccurrences()
    {
        var master = Master("FREQ=DAILY;COUNT=3", Utc(2026, 1, 1, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 12, 31)); // wide range

        Assert.Equal(
            new[] { Utc(2026,1,1,9,0), Utc(2026,1,2,9,0), Utc(2026,1,3,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Count_CountsAnchorsBeforeExDateFiltering()
    {
        // COUNT=5 with the 3rd anchor EXDATE'd. The EXDATE'd anchor still
        // consumes a count, so output is 4 (Jan 1,2,4,5) and the series
        // stops at the 5th generated anchor — Jan 6 is never emitted.
        var master = Master(
            "FREQ=DAILY;COUNT=5",
            Utc(2026, 1, 1, 9, 0),
            exDates: new[] { Utc(2026, 1, 3, 9, 0) });
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 12, 31));

        Assert.Equal(
            new[] { Utc(2026,1,1,9,0), Utc(2026,1,2,9,0), Utc(2026,1,4,9,0), Utc(2026,1,5,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void Until_StopsAtUntilInclusive()
    {
        var master = Master("FREQ=DAILY;UNTIL=20260103T090000Z", Utc(2026, 1, 1, 9, 0));
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 12, 31));

        Assert.Equal(
            new[] { Utc(2026,1,1,9,0), Utc(2026,1,2,9,0), Utc(2026,1,3,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void ExDate_RemovesOnlyExactAnchorMatch()
    {
        var master = Master(
            "FREQ=DAILY",
            Utc(2026, 1, 1, 9, 0),
            exDates: new[] { Utc(2026, 1, 2, 9, 0) });
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 3, 23, 59));

        Assert.Equal(
            new[] { Utc(2026,1,1,9,0), Utc(2026,1,3,9,0) },
            occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void ExDate_WithWrongTime_DoesNotRemoveOccurrence()
    {
        // EXDATE must equal a walker-emitted anchor bit-for-bit; a 10:00
        // entry does not match the 09:00 anchor.
        var master = Master(
            "FREQ=DAILY",
            Utc(2026, 1, 1, 9, 0),
            exDates: new[] { Utc(2026, 1, 2, 10, 0) });
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 3, 23, 59));

        Assert.Equal(3, occ.Count);
    }
}
