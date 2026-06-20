using System;
using System.Collections.Generic;

namespace Chronicle.Models;

// Identity invariants — see Models/Recurrence/EventKey.cs for the full
// contract. Summary:
//   - Id is unique among ROWS in the Events table.
//   - Id is NOT unique among in-memory Event instances: an expanded
//     occurrence carries its master's Id so series-scoped mutations
//     route by Id alone. Keying a Dictionary<Guid, ...> over a mixed
//     standalone+occurrence collection collapses occurrences silently.
//     Use EventKey.For(evt) instead.
//   - Stability of identity is conditional on the recurrence rule
//     version: changes to RRULE semantics or the walk algorithm are a
//     breaking change to the projection space.
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

    // RFC 5545 RRULE string. Null for non-recurring events.
    public string? RecurrenceRule { get; set; }

    // EXDATE — semantic exclusion over the occurrence space ("hole" in
    // the series). Each entry MUST equal a walker-emitted anchor for
    // this master bit-for-bit. The Phase 2 "skip this occurrence"
    // write path persists `SeriesAnchorUtc` verbatim — do not normalize,
    // round, or recompute. EXDATE matches the space, not the algorithm:
    // any change to the walk that alters which anchors a rule produces
    // is a breaking change to the projection space and existing EXDATEs
    // become undefined. Empty for non-recurring events.
    public IReadOnlyList<DateTime> RecurrenceExDatesUtc { get; set; }
        = Array.Empty<DateTime>();

    // ADVISORY cache field. Pre-computed UTC end of the last occurrence
    // for finite series, or null for infinite / non-recurring. Used
    // SOLELY as a query-pruning hint by EventRepository.GetInRangeAsync
    // to skip ended finite series. The expander never reads this field;
    // it is not authoritative metadata and never participates in
    // expansion or termination decisions. Maintained by the writer.
    public DateTime? RecurrenceEndUtcCached { get; set; }

    // Transient (NEVER persisted). Set only on expanded occurrences as
    // the canonical UTC anchor of this occurrence within its series;
    // null for standalone events and master rows. Combined with `Id`
    // (which equals the master's Id on an occurrence) it forms the
    // EventKey addressing the occurrence in the projection space.
    public DateTime? SeriesAnchorUtc { get; set; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime UpdatedAtUtc { get; set; }

    public bool IsRecurring => RecurrenceRule is not null;

    public bool IsOccurrence => SeriesAnchorUtc is not null;

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

        if (RecurrenceEndUtcCached is DateTime cached)
        {
            ValidateUtcKind(cached, nameof(RecurrenceEndUtcCached));
        }

        foreach (var ex in RecurrenceExDatesUtc)
        {
            ValidateUtcKind(ex, nameof(RecurrenceExDatesUtc));
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
