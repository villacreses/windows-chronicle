using System;

namespace Chronicle.Models.Recurrence;

// Addressing primitive over the projection space defined by recurrence.
//
// Role boundary (explicit, do not invert):
//   - Recurrence defines the space: (master + rule + walk strategy)
//     generates an addressable set of occurrences.
//   - EventKey addresses points in that space. It does not define the
//     space, does not influence the walk, and never participates in
//     expansion decisions.
//
// Cases:
//   - Standalone event:      SeriesId = Event.Id,  Anchor = null
//   - Recurring master row:  SeriesId = Event.Id,  Anchor = null
//                            (masters do not enter `_eventsByDate`;
//                             only their expansions do.)
//   - Expanded occurrence:   SeriesId = master.Id, Anchor = SeriesAnchorUtc
//
// Stability contract (load-bearing — see DECISIONS.md "Recurrence:
// RRULE Canonical Form, Two-Phase Rollout"):
//   An EventKey identifies the same logical occurrence across loads,
//   the visibility filter, view switches, and the cache lifecycle —
//   but ONLY within a single recurrence rule version. A semantic change
//   to RRULE handling, the anchor-walk algorithm, or the time-zone
//   evaluation strategy is a breaking change to the projection space;
//   pre-existing EXDATEs and overrides become undefined and require
//   migration.
//
// `_eventsByDate` is a render-time projection cache, never an identity
// source. UI state that needs to remember a selection across reloads
// must hold an `EventKey`, not a dictionary slot or a list index.
public readonly record struct EventKey(Guid SeriesId, DateTime? Anchor)
{
    public static EventKey For(Event evt) =>
        new(evt.Id, evt.SeriesAnchorUtc);

    public bool IsOccurrence => Anchor is not null;
}
