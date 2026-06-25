using System;

namespace Chronicle.Models.Recurrence;

// A divergence over a single occurrence in a recurring series — Phase 2A
// model per DECISIONS.md "Recurrence ... Phase 2 — occurrence mutation."
//
// Identity: (SeriesEventId, OccurrenceAnchorUtc). The anchor MUST equal
// a walker-emitted anchor of the master's rule bit-for-bit, by the same
// precision invariant that governs EXDATE (DECISIONS.md "Named
// invariants" #3). The Phase 2A write path will persist the anchor as
// the occurrence's `SeriesAnchorUtc` verbatim — no normalization.
//
// Field semantics:
//   - Override fields are nullable; null means "inherit from master."
//     This is the canonical iCalendar / CalDAV pattern and maps directly
//     to Google/Outlook override semantics. Storing the full edited
//     Event would force every consumer to choose between "stale carries
//     forward" and "diverge on every master change" — both are wrong.
//   - There is no IsCancelled field. EXDATE handles cancellation
//     atomically on the master; having two cancellation mechanisms
//     invites bugs and forces every consumer to check both.
//
// Lifecycle: orphaned overrides (anchor no longer in the rule's output
// after a master rule edit) are silently ignored at expansion time, not
// auto-deleted. This is consistent with EXDATE under the same
// circumstances and with DECISIONS.md invariant #2 (rule changes are
// breaking to the projection space).
public sealed class EventOverride
{
    public Guid Id { get; init; }

    public Guid SeriesEventId { get; init; }

    // The canonical UTC anchor of the occurrence being overridden,
    // verbatim from the master's rule walk.
    public DateTime OccurrenceAnchorUtc { get; init; }

    // Override fields — null = inherit from master.
    public string? Title { get; set; }

    public string? Description { get; set; }

    public DateTime? StartTimeUtc { get; set; }

    public DateTime? EndTimeUtc { get; set; }

    public bool? IsAllDay { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public void Validate()
    {
        ValidateUtcKind(OccurrenceAnchorUtc, nameof(OccurrenceAnchorUtc));
        ValidateUtcKind(UpdatedAtUtc, nameof(UpdatedAtUtc));

        if (StartTimeUtc is DateTime start)
            ValidateUtcKind(start, nameof(StartTimeUtc));

        if (EndTimeUtc is DateTime end)
            ValidateUtcKind(end, nameof(EndTimeUtc));

        if (StartTimeUtc is DateTime s && EndTimeUtc is DateTime e && e < s)
        {
            throw new InvalidOperationException(
                "Override end time cannot be before override start time.");
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
