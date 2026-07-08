using System;
using System.Linq;
using Chronicle.Helpers;

namespace Chronicle.Tests.Helpers;

public class DateHelpersTests
{
    // ── Month grid geometry ──────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 1)]
    [InlineData(2026, 2)]   // 28-day non-leap February
    [InlineData(2024, 2)]   // 29-day leap February
    [InlineData(2026, 6)]
    [InlineData(2026, 8)]
    [InlineData(2027, 12)]
    public void BuildMonthGrid_IsSundayAlignedAndContainsWholeMonth(int year, int month)
    {
        var displayMonth = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Local);
        var grid = DateHelpers.BuildMonthGrid(displayMonth);

        // First cell is the Sunday on or before the 1st, as a local day key.
        Assert.Equal(DayOfWeek.Sunday, grid.FirstCellDate.DayOfWeek);
        Assert.Equal(DateTimeKind.Local, grid.FirstCellDate.Kind);
        Assert.True(grid.FirstCellDate <= displayMonth);
        Assert.Equal((int)displayMonth.DayOfWeek, (displayMonth - grid.FirstCellDate).Days);

        // Whole weeks (4–6), cell count a multiple of 7.
        Assert.InRange(grid.Weeks, 4, 6);
        Assert.Equal(grid.Weeks * 7, grid.CellCount);

        // Every day of the target month appears in the grid, and the grid
        // fully contains the month.
        var days = grid.Days().ToList();
        Assert.Equal(grid.CellCount, days.Count);
        int daysInMonth = DateTime.DaysInMonth(year, month);
        for (int d = 1; d <= daysInMonth; d++)
        {
            var target = new DateTime(year, month, d).Date;
            Assert.Contains(days, x => x.Date == target);
        }
        Assert.True(days[^1].Date >= new DateTime(year, month, daysInMonth).Date);
    }

    [Fact]
    public void BuildMonthGrid_January2024_HasKnownLayout()
    {
        // 2024-01-01 is a Monday, so the grid starts on Sun 2023-12-31 and
        // spans 5 weeks (35 cells) to cover the 31-day month.
        var grid = DateHelpers.BuildMonthGrid(
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local));

        Assert.Equal(new DateTime(2023, 12, 31).Date, grid.FirstCellDate.Date);
        Assert.Equal(5, grid.Weeks);
        Assert.Equal(35, grid.CellCount);
    }

    // ── Week geometry (Sunday-aligned) ───────────────────────────────────

    [Fact]
    public void GetWeekStart_ReturnsSundayOnOrBeforeDate()
    {
        // 2024-01-03 is a Wednesday; its week starts Sun 2023-12-31.
        var wed = new DateTime(2024, 1, 3, 15, 0, 0, DateTimeKind.Local);
        var start = DateHelpers.GetWeekStart(wed);

        Assert.Equal(DayOfWeek.Sunday, start.DayOfWeek);
        Assert.Equal(new DateTime(2023, 12, 31).Date, start.Date);
        Assert.Equal(DateTimeKind.Local, start.Kind);
    }

    [Fact]
    public void GetWeekStart_OnSunday_ReturnsSameDay()
    {
        var sunday = new DateTime(2023, 12, 31, 9, 0, 0, DateTimeKind.Local);
        Assert.Equal(sunday.Date, DateHelpers.GetWeekStart(sunday).Date);
    }

    [Fact]
    public void BuildWeek_ReturnsSevenConsecutiveDaysSundayToSaturday()
    {
        var anyDay = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Local);
        var week = DateHelpers.BuildWeek(anyDay);

        Assert.Equal(7, week.Count);
        Assert.Equal(DayOfWeek.Sunday, week[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Saturday, week[6].DayOfWeek);
        for (int i = 1; i < 7; i++)
            Assert.Equal(week[i - 1].AddDays(1).Date, week[i].Date);
        Assert.Equal(DateHelpers.GetWeekStart(anyDay).Date, week[0].Date);
    }

    [Fact]
    public void IsSameWeek_TrueWithinWeek_FalseAcrossBoundary()
    {
        var sun = new DateTime(2023, 12, 31, 0, 0, 0, DateTimeKind.Local);
        var sat = new DateTime(2024, 1, 6, 0, 0, 0, DateTimeKind.Local);      // same week
        var nextSun = new DateTime(2024, 1, 7, 0, 0, 0, DateTimeKind.Local);  // next week

        Assert.True(DateHelpers.IsSameWeek(sun, sat));
        Assert.False(DateHelpers.IsSameWeek(sun, nextSun));
    }

    // ── Day-key and calendar predicates ──────────────────────────────────

    [Fact]
    public void GetLocalDayKey_StripsTimeAndSetsLocalKind()
    {
        var key = DateHelpers.GetLocalDayKey(new DateTime(2026, 5, 10, 14, 30, 0));
        Assert.Equal(new DateTime(2026, 5, 10).Date, key.Date);
        Assert.Equal(TimeSpan.Zero, key.TimeOfDay);
        Assert.Equal(DateTimeKind.Local, key.Kind);
    }

    [Fact]
    public void IsSameDay_IgnoresTime()
    {
        var morning = new DateTime(2026, 5, 10, 1, 0, 0);
        var night = new DateTime(2026, 5, 10, 23, 0, 0);
        Assert.True(DateHelpers.IsSameDay(morning, night));
        Assert.False(DateHelpers.IsSameDay(morning, morning.AddDays(1)));
    }

    [Fact]
    public void IsInMonth_ChecksMonthAndYear()
    {
        var month = new DateTime(2026, 5, 1);
        Assert.True(DateHelpers.IsInMonth(new DateTime(2026, 5, 31), month));
        Assert.False(DateHelpers.IsInMonth(new DateTime(2026, 6, 1), month));
        Assert.False(DateHelpers.IsInMonth(new DateTime(2025, 5, 15), month));
    }

    // ── View load ranges (Layer 4: match local calendar boundaries) ──────
    //
    // Each range is [local-boundary-midnight → last tick before the next
    // local-boundary-midnight], expressed in UTC. Assertions round-trip the
    // UTC bounds back to local so they hold regardless of the machine's zone.
    // June 2026 is used throughout to stay clear of DST transitions.

    [Fact]
    public void GetMonthRangeUtc_SpansTheWholeLocalMonth()
    {
        var (startUtc, endUtc) = DateHelpers.GetMonthRangeUtc(new DateTime(2026, 6, 1));

        Assert.Equal(DateTimeKind.Utc, startUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, endUtc.Kind);

        var startLocal = startUtc.ToLocalTime();
        Assert.Equal(new DateTime(2026, 6, 1), startLocal.Date);
        Assert.Equal(TimeSpan.Zero, startLocal.TimeOfDay);

        // One tick past the end is local midnight on the 1st of next month.
        var afterEndLocal = endUtc.AddTicks(1).ToLocalTime();
        Assert.Equal(new DateTime(2026, 7, 1), afterEndLocal.Date);
        Assert.Equal(TimeSpan.Zero, afterEndLocal.TimeOfDay);
    }

    [Fact]
    public void GetWeekRangeUtc_SpansSundayToSaturdayLocalWeek()
    {
        var wednesday = new DateTime(2026, 6, 3);
        var (startUtc, endUtc) = DateHelpers.GetWeekRangeUtc(wednesday);

        Assert.Equal(DateTimeKind.Utc, startUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, endUtc.Kind);

        var startLocal = startUtc.ToLocalTime();
        Assert.Equal(DayOfWeek.Sunday, startLocal.DayOfWeek);
        Assert.Equal(DateHelpers.GetWeekStart(wednesday).Date, startLocal.Date);
        Assert.Equal(TimeSpan.Zero, startLocal.TimeOfDay);

        // One tick past the end is local midnight of the following Sunday.
        var afterEndLocal = endUtc.AddTicks(1).ToLocalTime();
        Assert.Equal(DateHelpers.GetWeekStart(wednesday).AddDays(7).Date, afterEndLocal.Date);
        Assert.Equal(TimeSpan.Zero, afterEndLocal.TimeOfDay);
    }

    [Fact]
    public void GetDayRangeUtc_SpansTheSingleLocalDay()
    {
        var afternoon = new DateTime(2026, 6, 3, 15, 0, 0);
        var (startUtc, endUtc) = DateHelpers.GetDayRangeUtc(afternoon);

        Assert.Equal(DateTimeKind.Utc, startUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, endUtc.Kind);

        var startLocal = startUtc.ToLocalTime();
        Assert.Equal(new DateTime(2026, 6, 3), startLocal.Date);
        Assert.Equal(TimeSpan.Zero, startLocal.TimeOfDay);

        // One tick past the end is local midnight of the next day.
        var afterEndLocal = endUtc.AddTicks(1).ToLocalTime();
        Assert.Equal(new DateTime(2026, 6, 4), afterEndLocal.Date);
        Assert.Equal(TimeSpan.Zero, afterEndLocal.TimeOfDay);
    }

    [Fact]
    public void GetAgendaRangeUtc_StartsAtTodayMidnight_EndsAtLastDayOfFollowingMonth()
    {
        // Mid-month anchor: today = Jun 12 → range covers Jun 12 through Jul 31.
        var today = new DateTime(2026, 6, 12, 15, 0, 0);
        var (startUtc, endUtc) = DateHelpers.GetAgendaRangeUtc(today);

        Assert.Equal(DateTimeKind.Utc, startUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, endUtc.Kind);

        var startLocal = startUtc.ToLocalTime();
        Assert.Equal(new DateTime(2026, 6, 12), startLocal.Date);
        Assert.Equal(TimeSpan.Zero, startLocal.TimeOfDay);

        // One tick past the end is local midnight of Aug 1 — so the range's
        // last-covered local day is Jul 31.
        var afterEndLocal = endUtc.AddTicks(1).ToLocalTime();
        Assert.Equal(new DateTime(2026, 8, 1), afterEndLocal.Date);
        Assert.Equal(TimeSpan.Zero, afterEndLocal.TimeOfDay);
    }

    [Fact]
    public void GetAgendaRangeUtc_CrossesYearBoundary()
    {
        // Dec 20 → range covers Dec 20 through Jan 31 of the following year.
        var today = new DateTime(2026, 12, 20, 9, 0, 0);
        var (startUtc, endUtc) = DateHelpers.GetAgendaRangeUtc(today);

        var startLocal = startUtc.ToLocalTime();
        Assert.Equal(new DateTime(2026, 12, 20), startLocal.Date);

        var afterEndLocal = endUtc.AddTicks(1).ToLocalTime();
        Assert.Equal(new DateTime(2027, 2, 1), afterEndLocal.Date);
    }

    [Fact]
    public void GetAgendaRangeUtc_FirstOfMonth_CoversWholeMonthPlusNextMonth()
    {
        // Jun 1 → Jun 1 through Jul 31, the full month plus the next.
        var today = new DateTime(2026, 6, 1, 0, 0, 0);
        var (startUtc, endUtc) = DateHelpers.GetAgendaRangeUtc(today);

        Assert.Equal(new DateTime(2026, 6, 1), startUtc.ToLocalTime().Date);
        Assert.Equal(new DateTime(2026, 8, 1), endUtc.AddTicks(1).ToLocalTime().Date);
    }

    // ── Local↔UTC conversions (previously only exercised indirectly) ──────

    [Fact]
    public void CombineLocalDateAndTimeAsUtc_TreatsInputAsLocalWallClock()
    {
        // The date's own time-of-day is dropped; `time` supplies the clock.
        var date = new DateTime(2026, 6, 15, 9, 30, 0);
        var time = new TimeSpan(14, 30, 0);

        var result = DateHelpers.CombineLocalDateAndTimeAsUtc(date, time);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        // Round-trips back to 14:30 local on the given date, in any zone.
        Assert.Equal(new DateTime(2026, 6, 15, 14, 30, 0), result.ToLocalTime());
    }

    [Fact]
    public void GetMonthStartLocal_ReturnsFirstOfMonthAtLocalMidnight()
    {
        var start = DateHelpers.GetMonthStartLocal(new DateTime(2026, 6, 17, 8, 0, 0));

        Assert.Equal(new DateTime(2026, 6, 1), start.Date);
        Assert.Equal(TimeSpan.Zero, start.TimeOfDay);
        Assert.Equal(DateTimeKind.Local, start.Kind);
    }

    [Fact]
    public void GetEventDayKey_ConvertsUtcInstantToLocalDayKey()
    {
        var utc = new DateTime(2026, 6, 15, 18, 0, 0, DateTimeKind.Utc);

        var key = DateHelpers.GetEventDayKey(utc);

        // The key is the local calendar day of the instant, time stripped.
        Assert.Equal(utc.ToLocalTime().Date, key.Date);
        Assert.Equal(TimeSpan.Zero, key.TimeOfDay);
        Assert.Equal(DateTimeKind.Local, key.Kind);
    }
}
