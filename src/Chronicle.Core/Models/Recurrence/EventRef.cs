using System;

namespace Chronicle.Models.Recurrence;

// Discriminated identity primitive for mutations against the recurrence
// projection space. See DECISIONS.md "Recurrence ... Phase 2A" for the
// rationale and the wrapper-tripwire deal it satisfies.
//
// Scope: bounded to mutation entry points by design. Does not propagate
// into renderers, the projection cache, or read-only flows — those stay
// on plain `Event` (with `EventKey` as the read-side identity).
//
// Cases:
//   - Master:     the persisted `Events` row. Either a standalone event
//                 or a recurring series master.
//   - Occurrence: an expanded instance of a recurring series. `SeriesId`
//                 equals the master's `Event.Id` by the identity
//                 contract (Phase 1 named invariants); `AnchorUtc` is
//                 the rule-walk anchor that addresses the occurrence
//                 within the projection space.
//
// Constructed at mutation entry points via `EventRef.From(evt)` from the
// chip's `Event`. The variant a method requires is named in its
// signature (e.g. `Upsert(EventRef.Occurrence, ...)`), so calling it
// with the wrong shape is a compile error rather than a runtime branch.
public abstract record EventRef
{
    public sealed record Master(Guid Id) : EventRef;

    public sealed record Occurrence(Guid SeriesId, DateTime AnchorUtc) : EventRef;

    public static EventRef From(Event evt) =>
        evt.SeriesAnchorUtc is DateTime anchor
            ? new Occurrence(evt.Id, anchor)
            : new Master(evt.Id);
}
