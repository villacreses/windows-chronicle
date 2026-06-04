using System;

namespace Chronicle.Models;

public sealed class Event
{
    public Guid Id { get; init; }

    public Guid CalendarId { get; set; }

    public string Title { get; set; } = "";

    public string? Description { get; set; }

    // Always UTC internally
    public DateTime StartTimeUtc { get; set; }

    public DateTime EndTimeUtc { get; set; }

    public bool IsAllDay { get; set; }

    public string? RecurrenceRuleJson { get; set; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime UpdatedAtUtc { get; set; }

    public void Validate()
    {
        ValidateUtcKind(StartTimeUtc, nameof(StartTimeUtc));
        ValidateUtcKind(EndTimeUtc, nameof(EndTimeUtc));
        ValidateUtcKind(CreatedAtUtc, nameof(CreatedAtUtc));
        ValidateUtcKind(UpdatedAtUtc, nameof(UpdatedAtUtc));

        if (EndTimeUtc < StartTimeUtc)
        {
            throw new InvalidOperationException(
                "Event end time cannot be before start time.");
        }
    }

    private static void ValidateUtcKind(DateTime value, string propertyName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException(
                $"{propertyName} must be UTC.");
        }
    }
}
