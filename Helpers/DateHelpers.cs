using System;

namespace Chronicle.Helpers;

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
}
