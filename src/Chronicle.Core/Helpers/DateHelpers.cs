using System;
using System.Collections.Generic;

namespace Chronicle.Helpers;

/// <summary>
/// The geometry of a traditional month grid: a Sunday-aligned block of
/// whole weeks that fully contains the given month. Shared by the main
/// calendar grid and the mini-month navigator so date math lives in one place.
/// </summary>
internal readonly record struct MonthGrid(DateTime FirstCellDate, int Weeks)
{
    /// <summary>Total cells in the grid (always a multiple of 7).</summary>
    public int CellCount => Weeks * 7;

    /// <summary>
    /// Enumerates every cell's date, left-to-right then top-to-bottom,
    /// starting from <see cref="FirstCellDate"/>. Dates outside the target
    /// month are included (leading/trailing days); callers decide how to
    /// render them via <see cref="DateHelpers.IsInMonth"/>.
    /// </summary>
    public IEnumerable<DateTime> Days()
    {
        for (int i = 0; i < CellCount; i++)
            yield return FirstCellDate.AddDays(i);
    }
}

/// <summary>
/// Shared date/time conversions between UTC event storage and the
/// local wall-clock month grid.
/// </summary>
internal static class DateHelpers
{
    /// <summary>
    /// Gets the local calendar date key for event grouping and lookup.
    /// Event storage remains UTC; the month grid is a local wall-clock calendar.
    /// </summary>
    public static DateTime GetEventDayKey(DateTime utcDateTime)
    {
        return GetLocalDayKey(utcDateTime.ToLocalTime());
    }

    public static DateTime GetLocalDayKey(DateTime localDateTime)
    {
        return DateTime.SpecifyKind(localDateTime.Date, DateTimeKind.Local);
    }

    /// <summary>
    /// Gets the start (local, first day of month) and end (UTC, inclusive)
    /// boundaries of the given display month.
    /// </summary>
    public static (DateTime startUtc, DateTime endUtc) GetMonthRangeUtc(DateTime displayMonth)
    {
        var monthStartLocal = GetMonthStartLocal(displayMonth);
        var monthEndUtc = monthStartLocal.AddMonths(1).ToUniversalTime().AddTicks(-1);
        return (monthStartLocal.ToUniversalTime(), monthEndUtc);
    }

    public static DateTime GetMonthStartLocal(DateTime displayMonth)
    {
        return new DateTime(
            displayMonth.Year, displayMonth.Month, 1, 0, 0, 0, DateTimeKind.Local);
    }

    public static DateTime CombineLocalDateAndTimeAsUtc(DateTime date, TimeSpan time)
    {
        var localDateTime =
            DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Local);

        return localDateTime.ToUniversalTime();
    }

    /// <summary>
    /// Builds the month-grid geometry for <paramref name="displayMonth"/>:
    /// a Sunday-aligned block of whole weeks containing the entire month.
    /// <see cref="MonthGrid.FirstCellDate"/> is a local day key, so cell
    /// dates can be used directly for event lookups.
    /// </summary>
    public static MonthGrid BuildMonthGrid(DateTime displayMonth)
    {
        var monthStart = GetMonthStartLocal(displayMonth);
        var firstDayOfWeek = (int)monthStart.DayOfWeek;
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var weeks = (int)Math.Ceiling((firstDayOfWeek + daysInMonth) / 7.0);
        var firstCell = GetLocalDayKey(monthStart.AddDays(-firstDayOfWeek));
        return new MonthGrid(firstCell, weeks);
    }

    /// <summary>True if <paramref name="date"/> falls in the same calendar month/year as <paramref name="month"/>.</summary>
    public static bool IsInMonth(DateTime date, DateTime month)
        => date.Year == month.Year && date.Month == month.Month;

    /// <summary>True if both values fall on the same calendar day (ignores time and kind).</summary>
    public static bool IsSameDay(DateTime a, DateTime b)
        => a.Date == b.Date;

    // ── Week geometry (Sunday-aligned, matching the month grid) ───────────

    /// <summary>
    /// Gets the Sunday that begins the week containing <paramref name="date"/>,
    /// as a local day key. Uses the same Sunday-first convention as
    /// <see cref="BuildMonthGrid"/>.
    /// </summary>
    public static DateTime GetWeekStart(DateTime date)
    {
        var key = GetLocalDayKey(date);
        return key.AddDays(-(int)key.DayOfWeek);
    }

    /// <summary>
    /// Returns the seven local day keys (Sunday→Saturday) of the week
    /// containing <paramref name="date"/>.
    /// </summary>
    public static IReadOnlyList<DateTime> BuildWeek(DateTime date)
    {
        var start = GetWeekStart(date);
        var days = new DateTime[7];
        for (int i = 0; i < 7; i++)
            days[i] = start.AddDays(i);
        return days;
    }

    /// <summary>
    /// Gets the start (UTC) and end (UTC, inclusive) boundaries of the week
    /// containing <paramref name="date"/>. Mirrors <see cref="GetMonthRangeUtc"/>.
    /// </summary>
    public static (DateTime startUtc, DateTime endUtc) GetWeekRangeUtc(DateTime date)
    {
        var weekStartLocal = GetWeekStart(date);
        var weekEndUtc = weekStartLocal.AddDays(7).ToUniversalTime().AddTicks(-1);
        return (weekStartLocal.ToUniversalTime(), weekEndUtc);
    }

    /// <summary>True if both dates fall in the same Sunday-aligned week.</summary>
    public static bool IsSameWeek(DateTime a, DateTime b)
        => GetWeekStart(a) == GetWeekStart(b);

    /// <summary>
    /// Gets the start (UTC) and end (UTC, inclusive) boundaries of the single
    /// local day containing <paramref name="date"/>. Mirrors
    /// <see cref="GetMonthRangeUtc"/> / <see cref="GetWeekRangeUtc"/> so Day
    /// View reuses the same event-loading pipeline.
    /// </summary>
    public static (DateTime startUtc, DateTime endUtc) GetDayRangeUtc(DateTime date)
    {
        var dayStartLocal = GetLocalDayKey(date);
        var dayEndUtc = dayStartLocal.AddDays(1).ToUniversalTime().AddTicks(-1);
        return (dayStartLocal.ToUniversalTime(), dayEndUtc);
    }
}
