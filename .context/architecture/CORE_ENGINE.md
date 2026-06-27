# Core Engine

Chronicle is a local-first calendar application. The engine owns the
calendar domain end to end: external systems adapt into it, never the
reverse. Provider-specific concepts must not leak past the adapter
boundary into the domain model or UI.

## Core Philosophy

Chronicle owns the domain model.

External systems adapt into Chronicle.

Provider-specific concepts must never leak into the UI or persistence
layers.

## Domain Model Overview

Three entities form the calendar:

- `Calendar` — a top-level grouping.
- `Event` — the unit of scheduling. A standalone event is a single row;
  a recurring event is a *master* row plus a rule.
- `EventOverride` — a per-occurrence divergence: one occurrence with
  edited fields, keyed by `(SeriesEventId, OccurrenceAnchorUtc)`.
  EXDATE handles cancellation; overrides handle modification.

Identity over these entities is supplied by two primitives:

- `EventKey` — read-side identity, used by code that holds `Event`
  instances across the mixed standalone-plus-occurrence collection.
- `EventRef` — mutation-boundary identity, a discriminated union of
  `Master` and `Occurrence`.

Their substantive treatment lives in DATA_MODEL.md.

## Mutation Semantics

All writes go through repositories. `EventRepository.RefuseOccurrence`
is the persistence-boundary chokepoint that guarantees no expanded
occurrence is ever persisted as a row — occurrences exist only as
in-memory projections.

Writes against recurring series split three ways: master edit (updates
the row), occurrence edit (writes an `EventOverride`), and occurrence
skip (appends to the master's EXDATE list). The detailed contract —
including the typed compile-time guard on
`OverrideRepository.UpsertAsync(EventRef.Occurrence, …)` — is in
RECURRENCE.md. The user-gesture-to-write mapping is in
USER_INTERFACE.md.

## Time Zone Anchoring

Recurring events optionally carry an IANA `TimeZoneId` as the anchor
frame for the rule walk. A single dispatch,
`RecurrenceExpander.WalkAnchorsForMaster`, handles both legacy UTC
anchoring (`TimeZoneId IS NULL`) and wall-clock anchoring
(`TimeZoneId` set), caching one `TimeZoneInfo` per `Expand` call. That
cache is the documented exception to the
no-`TimeZoneInfo`-lookups-in-loops rule below.

The two modes, DST resolution, no-auto-migration policy, and the
"anchor zone is authoritative" product position are detailed in
RECURRENCE.md.

## Recurrence Expansion Entry Point

The recurrence engine plugs into the load pipeline at exactly one
point: `RecurrenceExpander.Expand` runs after the repository returns
recurring masters and before `_eventsByDate` is built. Renderers never
see the expander; they read a flat slice of `Event` instances from the
projection cache.

This is the seam the rest of the engine treats as a black box. RRULE
parsing, expansion logic, EXDATE handling, overrides, DST resolution,
and the named invariants are covered in RECURRENCE.md.

## Provider Strategy

Future providers — Google Calendar, Outlook, Apple Calendar, CalDAV —
will be implemented as adapters that translate provider entities into
the domain model above.

Desired flow:

`Provider → Adapter → Chronicle Domain Model → UI`

Never:

`Provider → UI`

The adapter boundary is where provider-specific shapes (Google's
`iCalUID`, Outlook's response status, CalDAV's ETags) are normalized
or discarded. Once an event is in the domain model, its provenance
must be irrelevant to renderers and to the recurrence engine.

## Performance Constraints

Calendar applications are read-heavy. Chronicle is also designed to be
left open for an entire computer session, so steady-state cost matters
as much as peak cost.

### Idle Cost Budget

With the window open and no user interaction:

- Zero allocations per second.
- Zero SQLite queries per second.
- No timers polling "now."
- No ambient background refresh loops.
- No speculative prefetching.

Time-derived visuals (e.g. the now-line) update on a coalesced
low-frequency tick (≤ 1/minute) and only when the view that consumes
them is visible.

Future provider sync is opt-in and explicitly scheduled with
user-visible state — never ambient "while app is open" work.

Regressions to this budget are bugs, not optimizations to defer.

### No Parallel Caches of Local Time

`evt.StartTimeUtc.ToLocalTime()` is fine at the point of use in
renderers — the runtime caches the system zone after the first call,
and the per-conversion cost is microseconds.

What is banned:

- **Parallel caches of local times alongside `_eventsByDate`.** Doubles
  event memory, creates an invalidation problem when the user crosses a
  DST boundary with the app open, buys nothing measurable.
- **`TimeZoneInfo` lookups or DST math inside per-event loops** —
  `TimeZoneInfo.FindSystemTimeZoneById`, manual offset rule
  construction, etc. `ToLocalTime()` on a UTC `DateTime` is not such a
  lookup; it reuses the cached system zone.

The recurrence expander's one-cache-per-`Expand`-call pattern is the
documented exception (see "Time Zone Anchoring" above).

### No Per-Render Allocations on the Hot Path

The renderer hot paths — selection changes, view switches, frame
updates — must not allocate proportional to event count. The
cross-cutting principle is that `_eventsByDate` is the single source
of truth for displayed events and renderers do not maintain parallel
state. The operational rules that enforce this (bounded-visual reuse,
view-switch zero-query, concrete collection return types) are
detailed in USER_INTERFACE.md and DATA_MODEL.md.

### No Ambient Background Work

Chronicle does not run background threads. Time-derived UI updates are
event-driven (resume, view-switch, the coalesced minute tick) and
self-cancel when their view is hidden. The engine never owns a thread
pool, a timer chain, or a polling loop. Provider sync, when
introduced, will be scheduled work with explicit user-visible state —
not an ambient process.
