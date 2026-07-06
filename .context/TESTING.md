# Testing

Chronicle's test suite is a five-layer xUnit suite over `Chronicle.Core`. It
pins the provider-neutral domain — recurrence, storage, the projection/cache
pipeline, timeline layout, and the recurrence-picker model — so the contracts
those subsystems expose fail loudly when broken. It is the executable half of
the architecture docs: where `architecture/RECURRENCE.md`,
`architecture/DATA_MODEL.md`, `architecture/USER_INTERFACE.md`, and
`architecture/CORE_ENGINE.md` state an invariant, this suite enforces it.

This document is the subsystem map for that suite. It defines the layers,
the seams that make the domain testable, the boundaries the suite refuses to
cross, the invariants it pins, and the rule for which layer a change belongs
to. A change that contradicts an invariant here is either testing the wrong
thing or has surfaced real contract drift worth raising before it lands.

## Assembly Shape

The domain is a separate assembly, and the test project references it
conventionally:

- **`src/Chronicle.Core`** (plain `net8.0`, no WinUI) — `Models/`, `Data/`,
  `Helpers/`, `Projection/`, `Layout/`. The source of truth under test.
- **`src/Chronicle`** (WinUI app) — renderers, popovers, `MainWindow`. Calls
  into Core; carries no domain logic the suite needs to reach.
- **`tests/Chronicle.Tests`** (xUnit, plain `net8.0`) — references
  `Chronicle.Core` via `<ProjectReference>`.

Core exposes its internals to the app and the tests with
`InternalsVisibleTo("Chronicle")` / `InternalsVisibleTo("Chronicle.Tests")`.
The pure helpers the suite targets are `internal`; the test project reaches
them through that grant, not through a widened public surface.

Run the suite with `dotnet test tests/Chronicle.Tests/Chronicle.Tests.csproj`
— plain `net8.0`, no platform, because Core and the tests carry no Windows
surface. Changes that touch the WinUI app are additionally gated on the app
still compiling: `dotnet build src/Chronicle -p:Platform=x64` (needs the
Windows SDK / WinUI workload).

## Framework Constraints

- **xUnit.** `[Theory]` + `[InlineData]` carry the parameterized recurrence
  and validation cases the suite is built around.
- **Built-in `Assert.*` only.** No FluentAssertions, no snapshot framework.
- **No mocking framework.** The load-bearing dependencies are pure functions
  or real SQLite; a mock would add weight without protecting the behavior that
  matters.

These are absolute for the local suite. A new test package must justify itself
as test-only and stay out of the shipped app (`DECISIONS.md` "No New
Dependencies Without Justification").

## The Five Layers

### Layer 1 — Pure Domain

Cheapest, most stable contracts: the calendar's basic language.

Covers `RecurrenceRule` (RRULE parse / canonical round-trip / part
validation), `RecurrenceExpander` basic expansion, `EventKey`, `EventRef`,
`Event.Validate`, `EventOverride.Validate`, and `DateHelpers` (grid/week/day
geometry and the local↔UTC conversions).

Enforces: RRULE round-trips to canonical form; unsupported RRULE parts fail
loudly; `COUNT` and `UNTIL` are mutually exclusive; `BYDAY` is weekly-only,
`BYMONTHDAY` monthly-only; date-only `UNTIL` parses as UTC; all persisted
timestamps are UTC-kind; `EndTimeUtc >= StartTimeUtc`; an unresolvable
`TimeZoneId` is rejected at the model boundary; `EventKey.For` preserves
`(Id, SeriesAnchorUtc)`; `EventRef.From` returns `Master` for rows and
`Occurrence` for expanded instances.

### Layer 2 — Recurrence Invariants

The most important layer. Recurrence maps directly onto every provider, so
these tests are the definition of Chronicle's occurrence space.

Covers `RecurrenceExpander` (`Expand`, `ComputeEndUtc`, and the tz-aware
`WalkAnchorsForMaster` dispatch) and `EventOverride` merge semantics. The
eight named invariants live in `architecture/RECURRENCE.md`; this layer is
where they are executable.

Enforces:

- Daily / weekly / monthly / yearly expansion; weekly `BYDAY` emits in
  Sunday-aligned order and never before the master start; monthly skips
  missing month-days instead of clamping; Feb 29 yearly skips non-leap years.
- `COUNT` counts generated anchors **before** EXDATE filtering (invariant #6);
  EXDATE removes an occurrence only on an exact anchor match.
- Override merge: null fields inherit from the master; an override may move
  start/end/title/all-day; EXDATE wins over an override at the same anchor;
  an override that pulls a future anchor back into the window is still
  discovered; orphan overrides are ignored; `ComputeEndUtc` walks the same
  strategy as `Expand` (invariant #8).
- DST and time zones: a tz-anchored series preserves wall-clock time across
  spring-forward; a legacy UTC-anchored series keeps legacy UTC behavior;
  spring-forward invalid local times shift forward; fall-back ambiguous times
  do not crash; tz-aware `UNTIL` is not prematurely terminated at the DST
  boundary (`TzWalkPad`); a bad stored `TimeZoneId` degrades to the legacy UTC
  walk instead of throwing (invariant #7).

DST tests pin explicit zones and explicit transition dates (e.g.
`America/New_York`, 2026 spring-forward Mar 8 / fall-back Nov 1) and fail loudly
if a required zone does not resolve in the environment — silent skips would
hide timezone coverage gaps.

### Layer 3 — SQLite Repositories

Storage-contract bugs are the class Chronicle most needs to prevent before
provider data flows in. This layer runs against **real SQLite** on isolated
temp databases — never mocked SQL.

Covers `AppDatabase` (schema init + migration), `CalendarRepository`,
`EventRepository`, `OverrideRepository`, and `Schema.sql`.

Enforces: schema initializes on a fresh database and re-initializes
idempotently on a populated one; the recurrence migration is idempotent
(`RecurrenceRuleJson` → `RecurrenceRule` rename preserves data, recurrence
columns and the end index are added to legacy databases); calendar / event /
override CRUD round-trips; deleting a calendar cascade-deletes its events and
their overrides in one transaction, and deleting an event cascade-deletes its
overrides; `EventRepository.RefuseOccurrence` rejects any attempt to persist an
expanded occurrence; EXDATE lists and `TimeZoneId` round-trip with full UTC
precision; `GetByIdAsync` returns null for a missing row; `GetInRangeAsync`
includes overlapping standalones (inclusive at both bounds) and recurring
masters that may still project into the window, and prunes finite series whose
cached end precedes the range; override upsert preserves one row per
`(SeriesEventId, OccurrenceAnchorUtc)` with a stable `Id`, and bulk fetch
returns every override for the requested series and nothing for empty input;
`PRAGMA foreign_keys = ON` is enforced (orphan event and orphan override
inserts are rejected); the `OverrideRepository.UpsertAsync` guards reject
non-UTC anchor / start / end and end-before-start before any SQL runs.

### Layer 4 — Projection & Cache

The bridge between storage and UI. It protects the single-event-cache model
and keeps any one consumer from forking the pipeline.

Covers `EventProjection` (the pure projection helper) and the `DateHelpers`
view ranges, plus an end-to-end integration test that drives the real
repositories and `EventProjection` together against SQLite.

Enforces: standalone events pass through unchanged; recurring masters are
replaced by their expanded occurrences and never appear as themselves;
overrides are grouped by series and applied to the correct master; events group
under local day keys; calendar visibility filters without mutating the source
list; an empty visibility map treats every calendar as visible and an unlisted
calendar defaults to visible; `RangeCovered` is true only when the loaded range
contains the requested one (the invalidation sentinel never covers); Month,
Week, and Day load ranges match local calendar boundaries; `OrderForDay`
orders a day all-day-first, then by start instant, ties broken by title. The
integration test proves the links compose: persisted EXDATE and override rows
survive serialization and merge onto the masters they expand from, hidden
calendars drop after load, and series pruned by the range query never reach
the expander.

### Layer 5 — Thin UI Logic

Pure logic that once lived next to WinUI, now in Core so its decisions are
testable without a UI. The suite tests the decisions, not the pixels.

Covers `TimelinePacker` (overlap-packing geometry), `RecurrencePickerModel`
(recurrence rule ⇄ picker state), and `RecurrenceTimeZone` (write-boundary
zone normalization).

Enforces: non-overlapping and boundary-touching events pack full-width;
overlapping events split into equal side-by-side columns and a freed column is
reused within a cluster; vertical position/height derive from local time, with
a minimum-height floor, a half-hour fallback for zero-duration, and trimming at
the end of the day; `BuildRule` produces the correct rule per frequency with
the picker's inline validation (weekly requires a day, count is a positive
integer, the end date is on/after start, `UNTIL` is the inclusive end of the
chosen day), `SeedState` maps a rule back to picker state, and the two
round-trip within the picker's representable subset; `NormalizeToIana` converts
a Windows zone id to IANA, passes an IANA id through, and degrades an
unmappable id to UTC (never persisting an unmapped string).

## Seams

Four seams make the domain reachable without dragging WinUI or a live
filesystem into the test host. Each is `internal` in `Chronicle.Core`; the
WinUI host calls into it and owns nothing the seam owns.

- **`AppDatabase.Initialize(string dbPath)`** — the database-path seam. The
  app supplies the `ApplicationData` location at its boundary; tests supply an
  isolated temp path. `Windows.Storage` never appears in Core. `GetConnection`
  enables `PRAGMA foreign_keys = ON` on every connection.
- **`Chronicle.Projection.EventProjection`** — the event-pipeline seam:
  `GroupOverridesBySeries`, `ExpandRecurrences` (masters never survive),
  `GroupVisibleByDay` (visibility filter + day grouping + `OrderForDay`), and
  `RangeCovered`. `MainWindow` orchestrates the repository reads around it but
  holds none of this logic.
- **`Chronicle.Layout.TimelinePacker`** — the timeline-geometry seam: `Pack`
  returns column/position/height layout as plain data (`PackedEvent`).
  `TimelineRenderHelper` keeps only the `UIElement` building.
- **`Chronicle.Models.Recurrence.RecurrencePickerModel`** (with
  `RecurrenceTimeZone`) — the picker seam: rule construction, seed mapping, and
  validation over a plain `RecurrencePickerState`. `EventEditPopover` keeps
  only the WinUI control wiring.

## Boundaries

The suite does not cross these lines. Work that requires crossing one is out
of scope, not a gap to close.

- **No UI automation.** No WinUI rendering, visual-tree assertions,
  screenshots, or color/font/geometry-of-controls checks. Renderer correctness
  is verified by testing the pure inputs (`TimelinePacker`, `EventProjection`)
  and by the app-build gate, not by driving the UI.
- **No mocks for SQLite.** Storage is tested against real SQLite; foreign
  keys, transactions, `ON CONFLICT DO UPDATE`, ISO round-tripping, and range
  predicates are the point.
- **No MVVM / DI drift.** Tests respect `DECISIONS.md` "Avoid Premature MVVM":
  no MVVM toolkit, DI container, or reactive framework is introduced to make
  code testable. Testability comes from extracting small pure helpers into
  Core, not from restructuring the app.
- **No parallel DB stomping.** `AppDatabase` holds its path in static state, so
  every database-touching test class belongs to the single non-parallel
  `[Collection("Database")]`.

`ColorHelper`'s color math is intentionally not covered: it is typed on the
WinUI `Color` struct, and Core is plain `net8.0`; moving it would breach that
boundary for negligible gain.

## Test Infrastructure

- **`tests/Chronicle.Tests/Data/DatabaseTest.cs`** — the DB harness.
  `DatabaseCollection` is the non-parallel `[CollectionDefinition]`;
  `DatabaseTest` owns an isolated temp database and clears the connection pool
  on teardown so the file can be deleted; `InitializedDatabaseTest` runs
  `Schema.sql` + migration first. Repository tests derive from the latter and
  carry `[Collection(DatabaseCollection.Name)]`; migration tests derive from
  `DatabaseTest` to stage a pre-migration schema themselves.
- **`RepositoryTestData`** — builders for valid, UTC-kind `Calendar` / `Event`
  entities (`NewCalendar`, `StandaloneEvent`, `RecurringMaster`, `Utc`).
- **`RecurrenceTestSupport`** — builders for expander tests (`Utc`, `Master`,
  `Override`, and a list-materializing `Expand`).

Determinism rules the suite follows: timezone-dependent assertions round-trip
UTC bounds back through local time so they hold in any zone; packing tests pin
`TimeZoneInfo.Utc` and a clean pixels-per-hour height so geometry is exact;
DST tests pin named zones and explicit transition dates.

## Routing a Change to a Layer

Every change lands in the layer that owns the behavior it touches, and carries
its tests in the same change:

- **Recurrence rules or expansion** (`RecurrenceRule`, `RecurrenceExpander`,
  overrides, DST) → Layers 1–2. A semantic change to the RRULE surface, the
  anchor walk, or the tz-evaluation strategy is a breaking change to the
  projection space (`RECURRENCE.md` invariant #2) and must update the pinned
  invariants deliberately, not incidentally.
- **Persistence** (`AppDatabase`, repositories, `Schema.sql`, migrations) →
  Layer 3, against real SQLite. New schema or migration steps carry an
  idempotence test; new query shapes carry range/inclusion/pruning tests.
- **The event pipeline** (`EventProjection`, view ranges, cache coverage,
  day ordering) → Layer 4, with the integration test extended when a change
  alters how repository reads compose into the projection.
- **Extractable UI-adjacent logic** (timeline geometry, picker rule
  construction, zone normalization) → Layer 5. New pure logic pulled out of a
  renderer or popover lands in `Chronicle.Core` behind a seam and is tested
  there; the WinUI host retains only element building and control wiring.

A fixed bug lands with a test that fails without the fix, or a note explaining
why it cannot be tested at that layer. A change to a subsystem the suite
already covers is not complete until its layer's tests cover the new behavior;
this is what keeps the suite growing with the calendar instead of snapshotting
a frozen moment.

Performance contracts are behavioral and are pinned where they can regress
invisibly: view switches inside a loaded range issue no query (`RangeCovered`),
visibility toggles re-filter without a query (`GroupVisibleByDay`), override
loading is bulk not per-master, and occurrence writes are refused at the
repository boundary. Rules cheap to verify by reading code (e.g. "repositories
return concrete collections") stay in `DECISIONS.md` and review, not in tests.

## Provider-Era Direction

The one forward-facing part of the strategy. When provider integration begins,
provider tests layer on top of this local suite rather than redefining it:
adapter-mapping tests over representative provider payloads, sync-state tests
against local SQLite, conflict-resolution tests, token-storage tests with fake
secrets (never real accounts), and opt-in scheduled-sync tests that protect the
Idle Cost Budget. Those tests assert mapping *into* Chronicle's domain model;
they do not get to define it. The suite's standing message holds: providers are
inputs, and the local model is the product.
