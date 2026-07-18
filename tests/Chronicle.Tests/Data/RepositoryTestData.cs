using System;
using System.Collections.Generic;
using Chronicle.Models;

namespace Chronicle.Tests.Data;

/// <summary>
/// Builders for repository tests: valid <see cref="Calendar"/> and
/// <see cref="Event"/> instances with UTC-kind timestamps that pass
/// <see cref="Event.Validate"/>. Every field has a sensible default so a
/// test names only what it cares about.
/// </summary>
internal static class RepositoryTestData
{
    public static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0)
        => new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    public static Calendar NewCalendar(string name = "Test", string? color = null)
        => new Calendar
        {
            Id = Guid.NewGuid(),
            Name = name,
            Color = color ?? Calendar.DefaultColorHex,
        };

    public static Event StandaloneEvent(
        Guid calendarId,
        DateTime? startUtc = null,
        TimeSpan? duration = null,
        string title = "Event",
        string? description = null,
        bool isAllDay = false)
    {
        var start = startUtc ?? Utc(2026, 6, 1, 9, 0);
        var created = Utc(2026, 1, 1);
        return new Event
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Title = title,
            Description = description,
            StartTimeUtc = start,
            EndTimeUtc = start + (duration ?? TimeSpan.FromHours(1)),
            IsAllDay = isAllDay,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        };
    }

    public static Reminder NewReminder(
        Guid eventId,
        int quantity = 10,
        ReminderOffsetUnit unit = ReminderOffsetUnit.Minutes)
        => new Reminder
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            OffsetQuantity = quantity,
            OffsetUnit = unit,
        };

    public static Event RecurringMaster(
        Guid calendarId,
        string rrule = "FREQ=WEEKLY",
        DateTime? startUtc = null,
        TimeSpan? duration = null,
        IReadOnlyList<DateTime>? exDates = null,
        DateTime? endUtcCached = null,
        string? timeZoneId = null,
        string title = "Series")
    {
        var start = startUtc ?? Utc(2026, 6, 1, 9, 0);
        var created = Utc(2026, 1, 1);
        return new Event
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Title = title,
            StartTimeUtc = start,
            EndTimeUtc = start + (duration ?? TimeSpan.FromHours(1)),
            RecurrenceRule = rrule,
            RecurrenceExDatesUtc = exDates ?? Array.Empty<DateTime>(),
            RecurrenceEndUtcCached = endUtcCached,
            TimeZoneId = timeZoneId,
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
        };
    }
}
