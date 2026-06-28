# Testing Strategy

Chronicle should build a local-core test suite before the next wave of local
features lands and well before any provider integration.

`EXECUTION_PLAN.md` now inserts a six-feature Local Baseline — all-day
polish, notes / description, search, agenda view, year view, notifications —
between the current state and Google Calendar work. All six sit downstream of
the recurrence engine, the projection cache, and the repository contracts that
are currently only manually verified. Without tests, an untested core compounds
risk across those features before the first provider line of code is written;
with tests, each baseline feature is built against contracts that fail loudly
when broken.

The provider-era case still holds: recurrence, occurrence overrides, EXDATE
skips, timezone anchoring, navigation ranges, SQLite persistence, and
visibility filtering all define behavior that providers must later preserve.
If those contracts are not executable, Google integration will turn local bugs
into sync bugs, which are harder to diagnose and more expensive to unwind. The
test suite protects both stretches: the six features ahead, and the providers
beyond them.

The goal is not "coverage for coverage's sake." The goal is antifragility:
tests that make the most important Chronicle invariants difficult to break by
accident.

## Where the Contracts Under Test Live

The architecture docs are the source of truth for the invariants and contracts
that this suite encodes. When writing a test, fact-check the behavior against:

- `architecture/RECURRENCE.md` — the eight numbered invariants, expansion
  pipeline, EXDATE / override semantics, DST handling, anchor-zone position.
- `architecture/DATA_MODEL.md` — identity primitives (`EventKey`, `EventRef`),
  UTC storage, repository contracts, cascade-delete semantics, bulk-write
  rules.
- `architecture/USER_INTERFACE.md` — view-switching zero-query rule,
  `_eventsByDate` as the single event cache, selection vs. range paths.
- `architecture/CORE_ENGINE.md` — Idle Cost Budget, no-parallel-local-time
  rule, no-ambient-background-work rule.
- `DECISIONS.md` — rationale for why each contract is what it is (alternatives
  weighed, tradeoffs). Useful when a test seems to demand a behavior the
  contract doesn't, or vice versa.

A test that contradicts an invariant in these docs is either testing the wrong
thing or has surfaced a real contract drift worth raising before the test
lands.

## Principles

### Test the Local Core First

Provider integrations should adapt into a stable Chronicle domain model. The
test suite should therefore start with the provider-agnostic core:

- recurrence rule parsing and expansion
- occurrence identity
- model validation
- date range math
- SQLite repository behavior
- event projection and visibility filtering

This prevents a future failure mode where Google behavior is blamed for bugs
that were already present in the local calendar model.

### Prefer Pure Tests Where Possible

Most of the recurrence and date behavior can be tested without WinUI, SQLite,
or filesystem state. Those tests should be the first layer because they are
fast, deterministic, and cheap to run after every change.

Pure tests also protect against subtle future edits. For example, a developer
changing recurrence expansion for a Google edge case should immediately know if
they broke COUNT-before-EXDATE semantics or occurrence anchor identity.

### Use SQLite Integration Tests for Storage

Repository behavior should be tested against real SQLite, not mocked SQL calls.
Chronicle depends on SQLite details that mocks would fail to exercise:

- foreign key enforcement
- transactions
- `ON CONFLICT DO UPDATE`
- ISO timestamp round-tripping
- nullable columns
- range query predicates
- schema migration idempotence

A fake repository would make the tests easier to write but less useful. The
bugs Chronicle most needs to prevent here are storage-contract bugs.

### Keep WinUI Tests Thin

The first test suite should not try to automate the whole WinUI surface.
Chronicle's renderers are programmatic WinUI builders, and full UI automation
would be slow and brittle at this stage.

Instead, extract and test pure logic currently trapped near UI code when that
logic becomes important enough:

- event projection from repository rows to rendered occurrences
- visibility filtering
- loaded-range coverage decisions
- timeline overlap packing
- recurrence picker rule construction

This keeps the test suite useful without forcing MVVM, dependency injection, or
a broad UI rewrite.

### Protect Performance Contracts

Chronicle's performance rules are behavioral contracts, not optional tuning.
Tests should encode the most important ones where practical:

- view switches inside an already loaded range do not query SQLite
- calendar visibility toggles do not query SQLite
- recurrence override loading is bulk, not per master
- occurrence writes are rejected at the repository boundary

These tests prevent slow regressions from arriving disguised as harmless
feature work.

Not every performance rule needs a test. For example, "repositories return
concrete collections" is cheap for reviewers to verify and expensive to test
well without reflection or analyzers. Keep that kind of rule in `DECISIONS.md`
and code review; spend test effort on behavior that can regress invisibly, such
as query counts and cache invalidation.

### Treat Fixed Bugs As Test Seeds

Every bug fixed after the suite exists should land with a failing test first,
or with a clear note explaining why the bug cannot reasonably be tested at that
layer.

This habit matters more than any single test list in this document. A young
suite becomes valuable fastest when real regressions leave executable fossils
behind.

### Grow the Suite with the Local Baseline

The six Local Baseline features are not "add later" coverage — each lands with
tests in the layers it touches, the same PR that ships the feature:

- **All-day polish** — Layer 1 model coverage for whatever validation rules
  Phase A defines (e.g. `IsAllDay`-vs-time-field constraints, if the audit
  makes them explicit — `EXECUTION_PLAN.md` Phase A opens with an audit, so the
  rules are not yet decided), Layer 4 (all-day events flow through the
  projection cache and visibility filter correctly).
- **Notes / description** — Layer 1 (model round-trip), Layer 3 (column
  persistence), Layer 5 if any logic moves out of the editor.
- **Search** — Layer 3 (query shape, predicates, recurring-master inclusion),
  Layer 4 (result projection, recurring instances expanded under the same
  pipeline as views).
- **Agenda view** — Layer 4 (new range model, `_eventsByDate` reuse, zero-query
  on already-loaded data per `architecture/USER_INTERFACE.md`).
- **Year view** — Layer 4 (range coverage), Layer 5 if density rendering grows
  testable logic.
- **Notifications / reminders** — Layer 1 (reminder model validation), Layer 3
  (persistence, cascade with `Event` deletion, override interaction), plus an
  Idle Cost Budget assertion that the scheduler does not poll: no in-app timer
  loop, no recurring DB wakeup. The exact test mechanism (SQLite query count,
  timer-hook absence, platform-registration check) is decided in
  `NOTIFICATIONS.md` when Phase C begins — the contract is "no polling," not
  any specific instrumentation.

Without this rule, the suite snapshots pre-baseline behavior and silently
fails to grow with the calendar. The point is not exhaustive coverage of every
new feature — it is that the feature does not ship without exercising the
contracts it touches.

## Proposed Test Project

Add a separate test project, likely `Chronicle.Tests`.

Preferred shape:

- target `net8.0-windows10.0.19041.0`
- reference `Chronicle.csproj`
- use one mainstream .NET test framework
- keep tests organized by production area

Start by referencing `Chronicle.csproj` directly, with
`InternalsVisibleTo("Chronicle.Tests")` if internal helpers need to be tested.
This keeps tests pointed at the same compiled code the app ships. It may carry
WinUI build overhead, but that is acceptable until it proves painful.

Avoid compiling selected production files into the test project unless the
direct project reference is blocked. Duplicating `Models/**/*.cs` into tests is
lighter, but it creates a second compilation shape that can drift from the app.

Do not extract a `Chronicle.Core` library just to make the first tests easy.
That may become the right architecture later, but doing it now would be a
speculative restructure.

MSTest is a reasonable first choice because it fits the Microsoft/.NET/Windows
stack and avoids adding a more opinionated testing ecosystem. xUnit is also
acceptable if the project already prefers it later. The important constraint is
that the framework should stay boring: no mocking framework, no snapshot
framework, no UI automation framework in the first wave.

Any new test package is still a dependency. It should be justified as test-only
and excluded from the shipped app. Production dependencies remain subject to
the `DECISIONS.md` "No New Dependencies Without Justification" rule.

The suite should run locally with `dotnet test` and on every pull request. A
test suite that only runs when a developer remembers to invoke it catches too
little too late.

## Testability Seams Needed

The current codebase is close to testable, but a few small seams would pay off.

### Database Path Override

`AppDatabase` currently stores `chronicle.db` under
`ApplicationData.Current.LocalFolder.Path`. That is correct for the app, but
repository tests need isolated temporary databases.

There is an important static-initialization trap: `DbPath` is currently a
static field initialized from `ApplicationData.Current.LocalFolder.Path`.
Unpackaged test processes can throw when that lookup happens, and they can do
so as soon as any `AppDatabase` member is touched. A test seam must therefore
move the `ApplicationData.Current` lookup behind a production-only lazy path,
not merely add a new `InitializeForPath(...)` method next to the existing field.

Add a narrow internal test seam, for example:

- `AppDatabase.InitializeForPath(string dbPath, string schemaPath)`
- or an internal connection factory override
- or an internal `AppDatabaseOptions` used only by tests

The seam should preserve the production default and avoid introducing a DI
container. Its purpose is only to let tests run repositories against temporary
SQLite files.

Why this prevents later difficulty:

Provider sync will rely on bulk inserts, updates, deletes, and conflict
resolution. Without real isolated database tests, sync bugs will be discovered
only through manual app use, after data has already been mutated.

### Projection Helper Extraction

`MainWindow` currently owns event loading, recurrence expansion, loaded-range
coverage, and visibility filtering. Some of that is pure calendar logic mixed
with UI coordination.

Extract only the pure pieces when tests need them, likely into an internal
helper such as `EventProjectionService` or `EventProjectionHelper`:

- group overrides by series
- expand recurring master rows
- apply calendar visibility
- decide whether a loaded UTC range covers a requested UTC range

Do not extract the whole `MainWindow`. Do not introduce MVVM. This is a real
refactor and should be treated as multi-step work, not a quick seam. The goal
is a small testable helper around behavior that already exists.

This extraction should happen **before Phase B of the Local Baseline** (search,
agenda view, year view) — not after. Phase B introduces three new consumers of
the projection pipeline: search adds a new query shape with recurring-instance
expansion; agenda adds a new range model that needs `_eventsByDate` coverage
decisions; year view adds another renderer reading the projection cache. If
those features are built before the helper is extracted, they will stamp their
shape into `MainWindow` directly, and the helper that gets pulled out later
will be co-designed around their needs rather than the provider-neutral domain
model. The same logic applies further out: extracting before Google integration
prevents the Google adapter from accidentally shaping the helper. Phase B is
the closer threat.

Why this prevents later difficulty:

Both Phase B features and provider integration will add more event sources, and
the projection pipeline must remain provider-neutral. A small tested projection
layer makes it much harder for any single consumer — local feature or external
provider — to leak its shape into UI code.

### Timeline Packing Extraction

`TimelineRenderHelper` has overlap-packing logic that is currently private.
If day/week layout regressions appear, extract the packing algorithm into an
internal pure helper and test it directly.

Why this prevents later difficulty:

Rendering overlap bugs are visual, fiddly, and easy to reintroduce. Testing the
packing result is cheaper than repeatedly verifying dense calendars by eye.

## Test Layers

### Layer 1: Pure Domain Tests

These should be added first.

Files:

- `Models/Recurrence/RecurrenceRule.cs`
- `Models/Recurrence/RecurrenceExpander.cs`
- `Models/Recurrence/EventKey.cs`
- `Models/Recurrence/EventRef.cs`
- `Models/Event.cs`
- `Models/Recurrence/EventOverride.cs`
- `Models/Recurrence/OverrideFields.cs`
- `Helpers/DateHelpers.cs`

Core cases:

- RRULE round-trips for daily, weekly, monthly, yearly rules
- unsupported RRULE parts fail loudly
- COUNT and UNTIL cannot both be specified
- BYDAY is accepted only for weekly rules
- BYMONTHDAY is accepted only for monthly rules
- date-only UNTIL parses as UTC
- all persisted event timestamps must be UTC
- `EndTimeUtc < StartTimeUtc` is rejected
- invalid `TimeZoneId` is rejected at the model boundary
- `EventKey.For` preserves `(Id, SeriesAnchorUtc)`
- `EventRef.From` returns `Master` for rows and `Occurrence` for expanded
  occurrences

Why this layer matters:

These are the cheapest tests and the most stable contracts. They give immediate
feedback when a change alters Chronicle's basic calendar language.

### Layer 2: Recurrence Invariant Tests

These are the most important tests in the suite.

Files:

- `Models/Recurrence/RecurrenceExpander.cs`
- `Models/Recurrence/EventOverride.cs`

Core cases:

- daily, weekly, monthly, and yearly expansion
- weekly BYDAY emits days in Sunday-aligned order
- weekly BYDAY does not emit anchors before the master start
- monthly recurrence skips missing month days instead of clamping
- Feb 29 yearly recurrence skips non-leap years
- COUNT counts generated anchors before EXDATE filtering
- EXDATE removes an occurrence only when the anchor matches exactly
- an override can change title/start/end/all-day fields
- null override fields inherit from the master
- EXDATE wins over an override for the same anchor
- overridden start can differ from `SeriesAnchorUtc`
- override moved backward across a range boundary is still discovered
- orphan override anchors are ignored
- bad stored timezone falls back to legacy UTC expansion instead of throwing
- `ComputeEndUtc` matches the same walk strategy used by `Expand`

Timezone and DST cases:

- timezone-anchored weekly event preserves wall-clock time across DST
- legacy UTC-anchored recurring event keeps legacy UTC behavior
- spring-forward invalid local time shifts forward
- fall-back ambiguous local time does not crash
- tz-aware UNTIL handling does not prematurely terminate on the DST boundary

DST tests must pin explicit zones and transition dates, for example a known
`America/New_York` spring-forward or fall-back transition in a named year, or
compute the transition dynamically from the zone's adjustment rules. Do not use
vague "around March" fixtures. The test should also fail loudly if a known zone
such as `America/New_York` does not resolve in the test environment; silent
skips would hide timezone coverage gaps.

Why this layer matters:

Recurrence is the hardest local feature to reason about manually. It also maps
directly to Google, Outlook, Apple Calendar, and CalDAV. If these tests exist,
provider adapters can be judged by whether they preserve Chronicle's recurrence
space instead of redefining it.

### Layer 3: SQLite Repository Tests

Files:

- `Data/AppDatabase.cs`
- `Data/Repositories/CalendarRepository.cs`
- `Data/Repositories/EventRepository.cs`
- `Data/Repositories/OverrideRepository.cs`
- `Data/Schema.sql`

Use temporary database files and real SQLite connections.

Core cases:

- schema initializes on a fresh database
- recurrence migration is idempotent
- old `RecurrenceRuleJson` column is renamed to `RecurrenceRule`
- recurrence columns are added to older databases
- recurrence end index exists after initialization
- calendar insert/update/delete works
- deleting a calendar deletes its events and overrides in one transaction
- event insert/update/delete works
- deleting an event deletes its overrides
- repository refuses to insert or update expanded occurrences
- EXDATE list round-trips with UTC precision
- `TimeZoneId` round-trips
- `GetByIdAsync` returns null for missing rows
- `GetInRangeAsync` includes overlapping standalone events
- `GetInRangeAsync` includes recurring masters that might produce visible
  occurrences
- `GetInRangeAsync` prunes finite recurring series whose cached end is before
  the requested range
- override upsert preserves one row per `(SeriesEventId, OccurrenceAnchorUtc)`
- bulk override fetch returns all overrides for requested series and none for
  empty input

Why this layer matters:

Provider sync will stress storage more than local manual use does. These tests
make sure Chronicle can trust its local database before external data starts
flowing into it.

### Layer 4: Projection And Cache Tests

Files after small extraction:

- event projection helper extracted from `Views/MainWindow.xaml.cs`
- `Helpers/DateHelpers.cs`

Core cases:

- standalone events pass through unchanged
- recurring masters are replaced by expanded occurrences
- recurring masters themselves do not enter the projected event list
- overrides are grouped by series and applied to the right master
- events group under local day keys
- hidden calendars are filtered without changing the projected source list
- empty visibility map treats all calendars as visible
- requested range inside loaded range does not require a reload
- requested range outside loaded range requires a reload
- Month, Week, and Day view ranges match local calendar boundaries

Why this layer matters:

This is the bridge between storage and UI. It protects the "single event
cache" model and prevents future provider work from creating a parallel event
pipeline.

### Layer 5: Thin UI Logic Tests

This layer should come after the local core is covered.

Possible targets:

- recurrence picker rule construction, if extracted from `EventEditPopover`
- timeline overlap packing, if extracted from `TimelineRenderHelper`
- selected-day event sorting, if the ordering contract becomes explicit

Avoid in the first wave:

- full WinUI automation
- screenshot testing
- testing exact colors or visual tree shapes
- testing private methods through reflection

Why this layer matters:

Some UI-adjacent logic is important, but the first suite should not become
fragile by depending on visual implementation details. Test the decisions, not
the pixels.

## Suggested First Milestone

The first milestone should produce a small, fast suite that runs from the
command line and proves the core recurrence model.

Minimum useful set:

- create `Chronicle.Tests`
- add tests for `RecurrenceRule`
- add tests for `RecurrenceExpander`
- add tests for `Event`, `EventKey`, and `EventRef`
- add tests for `DateHelpers`
- verify the app still builds
- verify the tests run locally
- verify the tests run on pull requests

This gives immediate protection to the highest-risk local behavior without
touching repositories or WinUI.

## Suggested Second Milestone

The second milestone should make repositories testable against isolated
SQLite databases.

Work:

- add a narrow `AppDatabase` test seam
- test schema initialization and migrations
- test calendar/event/override CRUD
- test cascade delete behavior
- test range query behavior

This is the point where Chronicle starts gaining confidence against data-loss
classes of bugs.

## Suggested Third Milestone

The third milestone should harden the app-level event pipeline.

Work:

- extract projection/filter/range-coverage helper logic from `MainWindow`
- test recurrence expansion through that helper
- test calendar visibility filtering
- test cache coverage decisions
- test Month/Week/Day range selection

This should land before Phase B of the Local Baseline (search / agenda / year
view) begins, and well before Google integration. It prevents local navigation
and view switching from regressing while new event-pipeline consumers — first
Phase B features, then providers — are added, and it keeps the
provider-neutral event pipeline from being designed around any single
consumer's needs.

## What Not To Do Yet

Do not begin with full UI automation. It will be slow, brittle, and will not
cover the recurrence/storage contracts where Chronicle carries the most risk.

Do not introduce a mocking framework in the first wave. The codebase is small,
and the important dependencies are either pure functions or real SQLite. Mocks
would add dependency weight without protecting the right behavior.

Do not refactor toward MVVM or DI for testing. The existing architecture
intentionally avoids those frameworks. Testing should respect that decision and
extract only small pure helpers where the current code already contains
test-worthy logic.

Do not test provider behavior before the local recurrence and storage contracts
are executable. Provider integration tests should eventually assert mapping
into Chronicle's domain model, not define the domain model by accident.

## Long-Term Direction

After Google integration begins, provider tests should be layered on top of the
local suite:

- adapter mapping tests using representative Google payloads
- sync-state tests using local SQLite
- conflict-resolution tests
- token-storage tests with fake secrets, never real accounts
- opt-in background/scheduled sync tests that protect the Idle Cost Budget

Those tests will only be meaningful if the local suite already defines what
Chronicle considers a correct calendar.

The test suite should make one thing clear: providers are inputs. Chronicle's
local model is the product.
