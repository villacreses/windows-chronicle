# Data Model

Chronicle's persistence layer is the source of truth for all calendar
data. Providers, when introduced, will be downstream of it. This
document covers the identity, storage, and write-path rules that make
local persistence trustworthy.

## Identity Primitives

Two identity types coexist on purpose. Each addresses a distinct
problem and lives at distinct layers.

### `EventKey` — Read-Side Identity

`EventKey = (SeriesId, Anchor?)`.

Keys any code that holds `Event` instances across the mixed
standalone-plus-occurrence collection — renderers, the projection
cache, chip lookup, selection-across-reload state. Constructed via
`EventKey.For(evt)`.

`Event.Id` alone is not sufficient as a key over this collection.
Expanded occurrences carry their master's `Id`, so the same `Id` can
appear N times in a loaded range. `EventKey` disambiguates with the
per-occurrence anchor.

### `EventRef` — Mutation-Boundary Identity

`EventRef = Master(Guid) | Occurrence(Guid SeriesId, DateTime AnchorUtc)`.

A discriminated union that lives only at edit, delete, and upsert
entry points. Renderers stay on plain `Event`. Constructed via
`EventRef.From(evt)`.

The wrapper is narrow on purpose. Its job is to make
"master-vs-occurrence" a typed branch at the small set of sites that
actually need to act on the distinction. Repository signatures use it
to make incorrect calls compile errors —
`OverrideRepository.UpsertAsync(EventRef.Occurrence, …)` cannot be
invoked with a master.

### Occurrence Identity Contract

Occurrence identity is `(Event.Id, SeriesAnchorUtc)`:

- `Event.Id` is the master's id. (`occurrence.Id == master.Id`. This is
  why deleting an occurrence's series via
  `EventRepository.DeleteAsync(occurrence.Id)` works without a separate
  code path.)
- `SeriesAnchorUtc` is the per-occurrence discriminator emitted by the
  rule walker.
- `SeriesAnchorUtc` and `StartTimeUtc` may differ. An override can move
  the displayed wall-clock Start while identity stays on the rule-walk
  anchor. Any write keyed on "this occurrence" must use
  `SeriesAnchorUtc`, not `StartTimeUtc`. `EventRef.From(evt)`
  constructs the correct key.

## UTC Storage

All timestamps persist in UTC.

Conversion to local time happens at application boundaries:

- At the renderer, per-chip, via `ToLocalTime()` on a UTC `DateTime`.
- At the editor's write boundary, where user-entered local times are
  converted before storage.

The reason is provider interoperability and synchronization
correctness: providers exchange UTC, and storing it natively avoids
the class of bugs where a stored "local" time is reinterpreted in a
different runtime zone.

The tradeoff is that local-time conversion is required at UI
boundaries. That is accepted; the conversion is cheap and the storage
contract is the more important guarantee.

## Persistence Format

SQLite, accessed through repositories. Schema is managed by
`AppDatabase` — fresh installs run `Schema.sql`; existing databases
are upgraded by guarded `ALTER TABLE` statements (e.g.
`MigrateRecurrenceColumns`).

Core tables:

- `Calendars` — id, name, color. (Visibility is *not* a stored column;
  it is a runtime UI filter held in `MainWindow._calendarVisibility`,
  defaulted to visible on load. See `architecture/USER_INTERFACE.md`
  "Mutation Flows" for the rationale and zero-query contract.)
- `Events` — id, calendar id, title, description, `StartTimeUtc`,
  `EndTimeUtc`, all-day flag, optional `RecurrenceRule` (RFC 5545
  RRULE string), optional `RecurrenceEndUtcCached`, optional
  `TimeZoneId` (IANA), optional `ExDates` list.
- `EventOverrides` — foreign key to `Events`, unique
  `(SeriesEventId, OccurrenceAnchorUtc)`, per-field nullable columns.
  Null means "inherit from master at expansion time."
- `Reminders` — foreign key to `Events`, `(OffsetQuantity, OffsetUnit)`
  storing the user's expressed offset rather than a normalized minute
  count, index on `EventId`. A composed child of the `Event` aggregate
  (like `EventOverrides`), loaded as a side collection keyed by `EventId`,
  never referenced from outside the aggregate. New table via `Schema.sql`
  `CREATE TABLE IF NOT EXISTS` — no `ALTER TABLE` migration. Full contract
  in `architecture/REMINDERS.md`.

Repository return types are concrete collections (`List<T>` or
arrays), never `IEnumerable<T>`. This prevents hidden re-enumeration
and LINQ allocation in render paths, and makes the cost of each call
legible at the call site.

## Indexing Strategy

Indexes exist where range queries and identity lookups occur:

- `RecurrenceEndUtcCached` on `Events` — used solely by
  `EventRepository.GetInRangeAsync` to prune finite series whose end
  precedes the query range. The expander never reads it; it is
  advisory, not authoritative. If a cached value disagrees with the
  rule, the rule wins and the cache is a bug to be repaired by the
  writer.
- `EventOverrides` has indexes on both `SeriesEventId` and
  `OccurrenceAnchorUtc`, plus a unique constraint on
  `(SeriesEventId, OccurrenceAnchorUtc)` enforcing one override per
  anchor per series.
- `Reminders` has an index on `EventId` — the identity lookup behind the
  reconciler's bulk fetch (`GetForEventsAsync`) and the editor's per-event
  load.

## Cascade-Delete Semantics

Deleting a calendar cascade-deletes its events.

Implementation: `CalendarRepository.DeleteAsync` runs a single
transaction — delete events, then the calendar — rather than relying
on schema-level `ON DELETE CASCADE`. This keeps the operation in the
repository layer, works on existing databases without a schema
migration, and never trips the foreign-key constraint
(`PRAGMA foreign_keys = ON`).

The delete dialog surfaces the affected event count so the action is
never silent. The user's mental model is "delete this calendar and
everything in it"; reassignment was considered and rejected as adding
a target-calendar picker and an awkward edge case when no other
calendar exists.

`EventOverride` and `Reminder` rows cascade from event deletion via their
repositories' cascade helpers, invoked from `EventRepository.DeleteAsync`
and `CalendarRepository.DeleteAsync` — the same transactions, ahead of the
`Events` delete so the foreign-key constraint is never tripped.

## Bulk-Write Rules

SQLite auto-commits each statement when no explicit transaction is
open. One `fsync` per write. For small per-call writes this is correct
and the cost is negligible. For bulk writes it is the difference
between "fast" and "unusable."

The rule:

- **≤ ~10 records** — one repository-method call per record is fine.
- **More than that** — open a single `SqliteTransaction`, do all the
  writes, commit. Either inside a dedicated bulk repo method or at the
  call site.

The first real bulk-write workload will be provider sync (pulling N
remote events on initial connect or refresh). Defaulting to the
per-call shape would ship slow with no obvious culprit. The rule is
written down before any caller exercises it precisely because the
lesson is much cheaper to read than to learn live.

This composes with the engine-level Idle Cost Budget: sync work is
expected to be batched and explicitly scheduled, and unbatched writes
violate the spirit of that constraint even when fired inside an opt-in
window.

## Local-First Correctness

Local persistence is the source of truth. Providers, when added, will
be modeled as adapters that translate into this schema; the schema
does not bend to a provider.

The practical consequences for current development:

- The application remains useful without network connectivity.
- Reads are SQLite-only and synchronous on the load path.
- Identity is local — `Event.Id` is generated locally, not by a
  provider. Round-trip identity (mapping a local `Event.Id` to a
  remote provider id) belongs in adapter tables, not in `Events`.
- Writes commit locally first. Provider write-back, when added, is a
  separate scheduled step against the local source of truth.

The engine-level consequence is that the UI never waits on a network
to render the calendar.
