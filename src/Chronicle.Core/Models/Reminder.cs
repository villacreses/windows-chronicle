using System;

namespace Chronicle.Models;

/// <summary>
/// The unit of a reminder's offset. Deliberately capped at
/// <see cref="Weeks"/>: minutes, hours, days, and weeks are fixed
/// durations, so a fire time is plain arithmetic on a UTC instant. A month
/// is not a fixed duration — "1 calendar month before" would be a
/// different semantic concept (calendar-relative), not another value here.
/// See NOTIFICATIONS.md "Offsets are fixed durations."
/// </summary>
public enum ReminderOffsetUnit
{
    Minutes,
    Hours,
    Days,
    Weeks,
}

/// <summary>
/// A reminder on an event — a composed child of the <see cref="Event"/>
/// aggregate, the same shape as <c>EventOverride</c>: it exists only in
/// the context of its event, is cascade-deleted with it, and is never
/// referenced from outside the aggregate.
///
/// The offset is stored as the user expressed it — <c>(2, Weeks)</c> stays
/// <c>(2, Weeks)</c>, never a normalized minute count — matching the
/// principle behind storing RRULE strings rather than materialized dates.
/// The scheduler derives minutes via <see cref="OffsetMinutes"/>.
///
/// Pure domain: this type carries NO notification state (no toast id, no
/// last-fired, no snooze). That state belongs to the notification
/// pipeline, never to the reminder. See NOTIFICATIONS.md.
/// </summary>
public sealed class Reminder
{
    public Guid Id { get; init; }

    /// <summary>The owning event's id (a master or standalone row —
    /// occurrences are projections and own nothing).</summary>
    public Guid EventId { get; init; }

    /// <summary>How many <see cref="OffsetUnit"/>s before the event start
    /// this reminder fires. Zero means "at start time".</summary>
    public int OffsetQuantity { get; set; }

    public ReminderOffsetUnit OffsetUnit { get; set; }

    /// <summary>
    /// The offset as minutes — derived, never stored. Fixed-duration
    /// arithmetic only (see <see cref="ReminderOffsetUnit"/>).
    /// </summary>
    public int OffsetMinutes => OffsetQuantity * MinutesPer(OffsetUnit);

    private static int MinutesPer(ReminderOffsetUnit unit) => unit switch
    {
        ReminderOffsetUnit.Minutes => 1,
        ReminderOffsetUnit.Hours => 60,
        ReminderOffsetUnit.Days => 1440,
        ReminderOffsetUnit.Weeks => 10080,
        _ => throw new InvalidOperationException(
            $"Unknown ReminderOffsetUnit '{unit}'."),
    };

    public void Validate()
    {
        if (OffsetQuantity < 0)
        {
            throw new InvalidOperationException(
                "Reminder offset quantity cannot be negative.");
        }
    }
}
