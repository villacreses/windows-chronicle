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
}
