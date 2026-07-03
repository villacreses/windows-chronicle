using System;
using System.Collections.Generic;
using System.Linq;
using Chronicle.Models;
using Chronicle.Models.Recurrence;

namespace Chronicle.Tests.Models.Recurrence;

/// <summary>
/// Shared builders for recurrence-expander tests: UTC literals, recurring
/// master events, override rows, and a list-materializing Expand wrapper.
/// </summary>
internal static class RecurrenceTestSupport
{
    public static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0)
        => new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    public static Event Master(
        string rrule,
        DateTime startUtc,
        TimeSpan? duration = null,
        IReadOnlyList<DateTime>? exDates = null,
        string? timeZoneId = null)
    {
        var dur = duration ?? TimeSpan.FromHours(1);
        return new Event
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Title = "Series",
            StartTimeUtc = startUtc,
            EndTimeUtc = startUtc + dur,
            RecurrenceRule = rrule,
            RecurrenceExDatesUtc = exDates ?? Array.Empty<DateTime>(),
            TimeZoneId = timeZoneId,
            CreatedAtUtc = startUtc,
            UpdatedAtUtc = startUtc,
        };
    }

    public static List<Event> Expand(
        Event master,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyList<EventOverride>? overrides = null)
        => RecurrenceExpander.Expand(master, rangeStartUtc, rangeEndUtc, overrides).ToList();

    public static EventOverride Override(
        Guid seriesId,
        DateTime anchorUtc,
        string? title = null,
        string? description = null,
        DateTime? startUtc = null,
        DateTime? endUtc = null,
        bool? isAllDay = null)
        => new EventOverride
        {
            Id = Guid.NewGuid(),
            SeriesEventId = seriesId,
            OccurrenceAnchorUtc = anchorUtc,
            Title = title,
            Description = description,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            IsAllDay = isAllDay,
            UpdatedAtUtc = anchorUtc,
        };
}
