using System;

namespace Chronicle.Models.Recurrence;

// Write-shape for OverrideRepository.UpsertAsync — the per-field deltas
// the caller wants to persist for a single occurrence. Identity comes
// from EventRef.Occurrence; this record carries only the override
// fields themselves.
//
// Null on any field means "inherit from master at expansion time." The
// editor form decides which fields it knows about and which it leaves
// null; the popover today exposes Title / StartTimeUtc / EndTimeUtc, so
// Description and IsAllDay typically stay null and continue to track
// the master. See EventOverride.cs for the read-side contract this
// pairs with.
public sealed record OverrideFields(
    string? Title = null,
    string? Description = null,
    DateTime? StartTimeUtc = null,
    DateTime? EndTimeUtc = null,
    bool? IsAllDay = null);
