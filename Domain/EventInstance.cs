using System;
namespace Chronicle.Domain;

public sealed class EventInstance
{
    public Guid EventId { get; init; }

    public Guid CalendarId { get; init; }

    public string Title { get; init; } = "";

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public bool IsAllDay { get; init; }

    // Stable occurrence identity
    public DateTime InstanceStartUtc { get; init; }

    public Event SourceEvent { get; init; } = default!;

    public TimeSpan Duration =>
        EndTimeUtc - StartTimeUtc;
}
