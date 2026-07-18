# Major Decisions

## WinUI 3

Reason:

Native Windows experience.

Alternatives considered:

- Electron
- Avalonia
- WPF

Decision:

WinUI 3.

---

## Provider-Neutral Architecture

Reason:

The product should not inherit UX decisions from Google or Microsoft.

Chronicle owns the experience.

Providers supply data.

---

## Build Provider-Agnostic Features First

Reason:

Most complexity lies in calendar UX.

Integrations should become mapping problems rather than product-design problems.

Current strategy:

1. Solve local calendar experience.
2. Solve calendar management.
3. Solve views and recurrence.
4. Add providers.

---

## UTC Storage

Reason:

Interoperability and synchronization.

Tradeoff:

Local-time conversion required at UI boundaries.

---

## Avoid Premature MVVM

Prohibited:

- MVVM frameworks (CommunityToolkit.Mvvm, Prism, ReactiveUI)
- DI containers
- Reactive / observable libraries (Rx)
- Event-bus libraries

Permitted:

- Plain interfaces, records, and shared callback objects passed at
  construction time.

The prohibition is on frameworks, not on ordinary C# composition. A single
host interface that bundles renderer→MainWindow callbacks is not MVVM, not
DI, and not an event bus — it is the seam that lets renderers stop taking
N `Action<...>` parameters per `Render()` call (which allocates a closure
per chip and violates the Idle Cost Budget).

Rapid iteration remains prioritized. This decision may be revisited later
if a framework's cost is justified against the Idle Cost Budget and the
No New Dependencies guardrail.

---

## MainWindow Decomposition into Helper Classes

Reason:

MainWindow.xaml.cs had grown into a dumping ground for state, calendar grid
rendering, sidebar rendering, and dialog construction, making it hard to
reason about.

Decision:

Extract cohesive rendering/dialog responsibilities into small focused
classes (`CalendarGridRenderer`, `SidebarRenderer`, `CalendarDialogService`,
plus `DateHelpers`/`ColorHelper`), instantiated directly by MainWindow.
MainWindow remains a coordinator: state ownership, event handlers, and
refresh orchestration.

No new architectural pattern (MVVM, DI, event bus) was introduced, in
keeping with "Avoid Premature MVVM." Behavior and repository usage are
unchanged.

---

## Deleting a Calendar Cascade-Deletes Its Events

Reason:

The `Events.CalendarId` foreign key references `Calendars.Id` with foreign
keys enforced (`PRAGMA foreign_keys = ON`). Deleting a calendar that still
has events would otherwise violate the constraint. Two options were weighed:
delete the events with the calendar, or reassign them to another calendar.

Decision:

Cascade-delete. It matches the user's mental model ("delete this calendar
and everything in it") and is the simplest behavior consistent with the
current architecture — reassignment would require a target-calendar picker
and special handling when no other calendar exists.

The cascade is performed in `CalendarRepository.DeleteAsync` inside a single
transaction (DELETE events, then the calendar), rather than via a schema
`ON DELETE CASCADE`. This keeps the operation in the repository layer, works
on existing databases without a migration (the schema is `CREATE TABLE IF
NOT EXISTS` only), and never trips the FK constraint. The delete dialog
surfaces the affected event count so the action is never silent.

---

## Idle Cost Budget

Chronicle is designed to be left open for the entire computer session.
Idle cost is therefore a first-class constraint, not a nice-to-have.

Rules:

- With the window open and no user interaction, Chronicle performs zero
  allocations per second and issues zero SQLite queries.
- No timers polling "now," no ambient background refresh loops, no
  speculative prefetching.
- The clock indicator (and any other time-derived visual) updates on a
  coalesced low-frequency tick (≤ 1/minute) and only when the view that
  consumes it is visible.
- Future provider sync (Google, Outlook) is opt-in and scheduled with
  user-visible state — never ambient "while app is open" work.

Regressions to this budget are bugs, not optimizations to defer.

---

## No New Dependencies Without Justification

Every NuGet package adds binary size, startup cost, and supply-chain
surface. None of these costs are recoverable once the dependency is
embedded.

Rule:

Any new NuGet reference requires a DECISIONS.md entry covering:

- what it provides that cannot reasonably be written by hand
- its impact on binary size and startup
- why a smaller alternative was rejected

Explicit ban list (consistent with "Avoid Premature MVVM"):

- MVVM toolkits
- DI containers
- Rx / reactive libraries
- Logging frameworks heavier than `System.Diagnostics`

---

## AOT / Trimming Compatibility

Chronicle code must remain compatible with .NET AOT and trimming, even
before AOT is shipped. The door stays open, and the patterns this rules
out tend to correlate with the performance properties Chronicle wants
anyway.

Avoid:

- heavy runtime reflection (`Type.GetMethod`, dynamic property lookup,
  `Activator.CreateInstance` over open generics)
- dynamic proxies / runtime code generation (`System.Reflection.Emit`,
  `DynamicMethod`, expression-tree compilation in hot paths)
- serializers and frameworks that require runtime type discovery without
  source generators
- any pattern flagged by AOT/trim warnings

Prefer:

- direct calls, source generators, hand-written mapping
- `System.Text.Json` source-generated contexts over reflection-based
  serialization, when serialization is needed at all

This guardrail composes with "No New Dependencies Without
Justification": a candidate package that emits trim warnings is a
stronger no.

---

## Bulk DB Writes Use an Explicit Transaction

SQLite auto-commits each statement when no explicit transaction is open,
which means one `fsync` per write. For small per-call writes (event
create/edit/delete from the UI) this is correct and the cost is
negligible. For bulk writes it is the difference between "fast" and
"unusable."

Rule:

- ≤ ~10 records: one repo-method call per record is acceptable.
- More than that: open a single `SqliteTransaction`, do all the writes,
  commit. Either inside a dedicated bulk repo method or at the call
  site — both are fine.

The constraint is being written down now because no caller exercises
it yet. The first real bulk-write workload — OAuth provider sync,
pulling N remote events down on initial connect or a refresh — will
otherwise default to the per-call shape and ship slow with no obvious
culprit. The lesson is much cheaper to read than to learn live.

Composes with the Idle Cost Budget: sync work is expected to be
batched and explicitly scheduled (see "Background work is opt-in,
not ambient" — rain-checked but on the radar), and unbatched writes
violate the spirit of that constraint even when fired inside an
opt-in window.

---

## PR Perf-Impact Line

Any PR that touches a renderer or a repository includes a one-line
allocation/query impact statement in its description.

Examples:

- "No new per-render allocations; no new queries."
- "Adds one SQLite query per month change; result cached in `_eventsByDate`."
- "Replaces per-chip closure with shared handler — eliminates O(events)
  allocations per render."

The line is cheap to write and forces the question to be asked while the
change is fresh. Reviewers should reject renderer/repository PRs that
omit it.

---

## Recurrence: RRULE Canonical Form, Two-Phase Rollout (2026-06-20)

Recurrence is stored as RFC 5545 RRULE strings. The same format is
spoken natively by Google, Outlook (via Graph), Apple Calendar, and
CalDAV, so the future provider adapters become a field-mapping
exercise rather than a translation one.

The current operational contract — RRULE surface, EXDATE / override
semantics, the 8 named invariants, expansion pipeline, DST handling,
and the anchor-zone-authoritative position — is documented in
`architecture/RECURRENCE.md`. The entries below capture only the
decisions and the rationale behind them.

### System model: projection over a functional pipeline

Chronicle's recurrence is a projection system with first-class
addressable derived entities, not a pure functional expansion
pipeline. A recurring master + rule defines an addressable projection
space; the expander materializes the slice intersecting the load
range; EXDATE and `EventOverride` are persistent decorations keyed to
addresses in that space.

The architectural alternative — stateless functional expansion that
treats overrides as a side input — was rejected because `EventKey` and
`EventRef` need stable, addressable points in the space to key writes
and UI state against. Identity is non-negotiable; the projection model
provides it.

Eight load-bearing invariants govern the space (occurrence identity,
rule-version-conditional stability, EXDATE as semantic constraint,
advisory cache fields, `_eventsByDate` as non-identity cache, COUNT
pre-EXDATE, no-throw on bad timezone, single walk dispatch). Any
violation requires data migration. The full texts are in
`architecture/RECURRENCE.md` and are referenced here by number where
relevant.

### Two-phase rollout

The split was built around a single insight: EXDATE creates a hole;
an override creates a divergence that must be tracked and merged
forever. Phase 2A (occurrence mutation) and 2B (wall-clock anchoring)
are independent concerns sharing no pipeline; both touch the expander
walk, so doing one at a time concentrated risk.

- **Phase 1** — recurrence engine + skip via EXDATE. Established the
  projection model, `EventKey`, and `RefuseOccurrence`. Anchored in
  UTC because the Phase 1 storage carried only `StartTimeUtc` (an
  implementation artifact, not a product position — see below).
- **Phase 2A** — occurrence mutation. Added `EventOverrides`,
  `EventRef`, and the scope picker. `EventRef` landed the
  wrapper-tripwire deal from Phase 1's tolerated ambiguity, at
  mutation boundaries only.
- **Phase 2B** — wall-clock anchoring. Added `TimeZoneId` to recurring
  masters, the tz-aware `WalkAnchorsForMaster` dispatch (invariant
  #8), DST handling via `ResolveLocalForDst` and `TzWalkPad`, and
  write-boundary IANA normalization. Resolved the Phase 1 UTC drift
  without breaking the projection space of legacy NULL-zone masters
  (invariant #2).

`EXECUTION_PLAN.md` lists the concrete deliverables for each phase.

### Deferred from the original Phase 2 list

Considered and explicitly deferred:

- **This-and-following scope** — introduces series fission with EXDATE
  and override migration rules; not built on evidence of user need.
- **Override reconciliation on master rule changes** — orphans are
  silently ignored at expansion per invariant #2; auto-reconciliation
  isn't worth the UX surface.
- **Ordinal BYDAY, multi-value BYMONTHDAY, RDATE, custom RRULE input**
  — RRULE expressiveness, separate from occurrence mutation.

### UTC anchoring was a Phase 1 constraint, not a product position

Phase 1 anchored recurring events in UTC because the existing storage
carried only `StartTimeUtc`; a weekly meeting created at 9 AM local
drifted ±1 hour across DST. The user intent for "weekly Monday 9 AM"
is wall-clock semantics — Phase 2B introduced the tz-aware evaluation
strategy that resolves this, without changing the pipeline shape
(same single semantic model, strategy variation inside
`WalkAnchorsForMaster`).

### Anchor zone is authoritative, not display zone

A tz-aware master records the zone it was created in; that zone is the
series's anchor for its entire lifetime. Edits never re-anchor — a
"9 AM NY meeting" edited from a PT machine remains anchored to NY.
This is a deliberate product position.

Two alternatives were weighed and rejected:

- **"Re-anchor to current system zone on edit"** would silently
  invalidate every EXDATE and override on the series (invariant #2)
  every time the user travelled and touched the event.
- **"Treat display zone as authoritative"** removes the concept of a
  fixed anchor entirely; the same calendar would render differently
  in two locations and the EXDATE / override identity contract would
  have nothing stable to key on.

The traveler case the chosen position handles imperfectly ("I moved
to LA permanently and now my NY meetings show at the wrong local
time") is recoverable by recreating the series — explicit user intent
for an explicit semantic change. The cases it handles correctly ("I'm
visiting LA for a week and my recurring NY meetings should still
happen at 9 AM NY") cover the common need.

The operational consequence — including how the projection space's
equivalence relation depends on the zone, and why migration between
anchoring modes is a breaking change — is in
`architecture/RECURRENCE.md`.

### Tolerated ambiguity: `Event` carries dual semantics in UI read paths

Phase 1 shipped with `Event` carrying two semantically distinct
shapes — persistent rows and expanded occurrences — in a single
type. The distinction is mediated by `IsOccurrence` / `EventKey` at
read sites and enforced at the persistence boundary by
`RefuseOccurrence`.

A wrapper type (`EventInstance` or similar) that encodes the
projection-vs-entity split in the type system was discussed and
deliberately deferred. The cost of a wrapper is paid in every
renderer and tap-target signature; the bug class it would
structurally prevent (occurrence handed to a persistence write) is
already caught by the repository guard. The cost of staying without
it is paid in discipline at branching mutation paths.

**Revisit triggers** (recorded so any future revisit lands on the
same evidence both sides agreed to):

- A second projection consumer appears that needs to distinguish
  occurrences from masters at read time (drag/drop reschedule,
  inline edit, multi-select copy/paste, provider sync
  reconciliation), OR
- `IsOccurrence` branching appears in more than one mutation path
  beyond the Phase 1 edit / delete handlers — a third site, not
  "drag/drop someday."

**Phase 2A landing (second trigger fired).** The override-save
handler was the third mutation site. The resolution was `EventRef` —
a discriminated identity primitive at mutation boundaries *only*.
Renderers and the projection cache stay on plain `Event`; the
wrapper does not propagate. The narrower form was chosen over the
original full-wrapper proposal because the edit-path benefit
(preserving occurrence-launch context across the popover boundary)
is concentrated at mutation entry, not in renderer signatures. The
broader read-time-wrapper question remains deferred under the same
triggers above.

### Tolerated ambiguity (Phase 2A): merge validity and cross-boundary overrides

Two corners we deliberately don't enforce in Phase 2A, with the
expectation that the write-path UX prevents normal user paths from
producing them:

- **Merge validity is not enforced post-merge.** A malformed override
  smuggled in via direct SQL (or a future writer that bypasses the
  editor) can produce an emitted occurrence with `End < Start` or
  other invalid shapes. Acceptable for MVP; revisit if a non-UI
  writer is introduced.
- **Master-loading gap for cross-boundary overrides.** An override on
  a master filtered out by natural-boundary pruning can be silently
  dropped from a visible window. The fix is a two-pass load (query
  candidate overrides first, then load masters including those
  referenced) and is deferred until a user hits the limitation.

Both are documented in detail in `architecture/RECURRENCE.md` under
"Tolerated Ambiguities."

---

## DEV-ONLY THEME OVERRIDE (2026-06-14)

A temporary hard-coded Dark Mode theme was introduced to reduce visual fatigue during development and enable sharing progress without UI distraction.

Rationale:
- UI polish is explicitly not in current roadmap phase.
- However, existing default UI quality was interfering with development motivation and communication needs.
- This change reduces cognitive friction without altering functional architecture.

Decision:
- Dark mode is hard-coded for development only.
- Theme system exists solely as a minimal infrastructure layer (`Theme.cs`).
- No expansion into full design system is allowed at this stage.
- Light mode support is deferred until formal “design overhaul” phase.

Constraints:
- No additional UI polish work until Day View + Recurrence are complete.
- Theme changes are restricted to bug fixes or rendering correctness issues.
- This does NOT shift project phase sequencing.

Status:
- Temporary, intentionally non-architectural UI deviation.
- To be revisited in design overhaul phase.

---

## Domain Extracted to Chronicle.Core (2026-06-28)

Reason:

The test suite could not reference the app project conventionally. The
single `Chronicle.csproj` was both the WinUI executable
(`OutputType=WinExe` + `UseWinUI=true`) and the home of all domain code,
so the WindowsAppSDK injected a bootstrap `<Module>` initializer into
`Chronicle.dll`. Any test touching any Chronicle type threw
`REGDB_E_CLASSNOTREG` in a non-packaged host. The first scaffold worked
around this by compiling pure source files into the test assembly
(`<Compile Include>`); that stopgap had a known drift risk and would not
extend cleanly to the `Data/` layer.

Decision:

Extract a `Chronicle.Core` class library (plain `net8.0`) holding the
pure domain — `Models/`, `Data/`, `Helpers/DateHelpers.cs`. The WinUI
app and the test project both reference it via `<ProjectReference>`. The
repo moved to the conventional `src/` + `tests/` layout
(`src/Chronicle`, `src/Chronicle.Core`, `tests/Chronicle.Tests`) so no
project's compile glob overlaps another's — eliminating the
`DefaultItemExcludes` workaround the root-level app project would
otherwise need.

This is not new architecture. The architecture docs already described
Chronicle as a domain the UI and persistence adapt into; the extraction
makes that an enforced assembly boundary rather than a prose convention.
None of the existing guardrails (no MVVM, no DI, AOT-friendly, idle
budget, no new deps) caused the bump or were touched by the fix.

Two couplings were severed to let the domain compile without WinUI:

- `AppDatabase` no longer resolves its own path via `Windows.Storage`;
  it takes `Initialize(string dbPath)` and the app supplies the
  `ApplicationData` location at the boundary. (This is the test seam
  `TESTING.md` already wanted, now mandatory.)
- `Calendar`'s default color constant moved into the domain
  (`Calendar.DefaultColorHex`); the UI-layer `ColorHelper` re-exposes
  it.

Repo conventions adopted in the same pass (build-time only, no runtime
dependencies, so consistent with "No New Dependencies"):

- `Directory.Build.props` — shared `Nullable` / `LangVersion` /
  `ImplicitUsings=disable`.
- `Directory.Packages.props` — Central Package Management; one source of
  truth for NuGet versions across the three projects.
- `global.json` — SDK pin for reproducible builds on a fresh machine.
- `<IsAotCompatible>true</IsAotCompatible>` on `Chronicle.Core` — makes
  the AOT/Trimming guardrail a compiler check instead of prose.

Operational detail (test project shape, Layer 3 parallelism caveat,
Schema.sql propagation) lives in `.context/TESTING.md`.

---

## Reminders: OS-Scheduled Toasts, Reminder as a Child Entity (2026-07-18)

Local Baseline Phase C delivers event reminders as Windows toast
notifications the OS fires on schedule — including while Chronicle is
closed. The operational contract — data model, the `ReminderSchedule`
projection, the reconciliation contract, activation, horizon policy, and the
scope boundaries — lives in `architecture/REMINDERS.md`. The entries below
capture only the two decisions that had real alternatives, and why each fork
was taken.

### OS-scheduled toasts over an app-owned scheduler

Chronicle registers concrete future toasts with the Windows notification
platform (`ScheduledToastNotification` / `ToastNotifier.AddToSchedule`) and
lets the OS deliver them. It runs no in-app timer, polling loop, or
background service.

The decision does **not** rest on idle-cost minimization. It rests on two
facts:

- The modern `AppNotificationManager` (Windows App SDK) is a show-*now* API
  with no scheduling equivalent — it cannot register a future toast.
- A reminder must fire while Chronicle is closed. That is a product
  requirement: an unreliable reminder erodes trust more than a missing
  feature does.

Together these reduce the real choice to *OS-owned scheduling vs. building a
scheduler ourselves*. The modern APIs were rejected because they do not
schedule, **not** because they are slower. The Idle Cost Budget permits
explicitly-scheduled future work; only continuous observation is banned.
Handing the schedule to the OS is the purest form of permitted scheduling —
Chronicle keeps no handle, thread, or timer.

The app-owned alternative was rejected because firing a reminder while
Chronicle is closed would require either a background process (a standing
idle cost, and a moving part that fails silently) or a scheduled relaunch of
Chronicle itself — both strictly worse than a schedule the OS already
maintains reliably. The spike also found that classic `ScheduledToastNotification`
scheduling and activation need **no** manifest COM-activator in a packaged
app, so the OS-owned path carried no offsetting complexity cost.

### `Reminder` as a child entity, not a scalar column

`Reminder` is a composed child of the `Event` aggregate — its own table,
cascade-owned, loaded as a side collection, like `EventOverride` — storing
the user's expressed offset as `(OffsetQuantity, OffsetUnit)`.

The subsystem was first designed with a scalar `Events.ReminderMinutesBefore`
column, on the theory that a single reminder is a derived scalar and a table
would be speculative structure. That was reversed **before the model
shipped** — still on the unmerged branch, at units 1–2 — for three reasons:

- **Preserving the user's representation already breaks the scalar.** Storing
  "2 weeks before" faithfully needs structured `(Quantity, Unit)` data;
  normalized minutes lose intent (is `10080` "1 week" or "7 days"?). Once the
  value is structured, a child collection is a small further step. This is
  the same principle Chronicle already applies by storing RRULE rather than a
  materialized date list, and by keeping the recurrence anchor zone
  authoritative rather than normalizing to UTC.
- **Single-reminder is an artificial constraint, not a faithful model.**
  Multiple reminders per event are table stakes for a real calendar. The
  scalar encodes "≤ 1 reminder" as a structural invariant the domain does not
  actually have (the editor exposing one reminder is a UI limitation, not a
  domain constraint).
- **The expensive migration is the API reshape, not the SQL.** Moving every
  read site from a scalar to a collection only gets costlier as more units
  build on it; making the correction at units 1–2 was the cheapest moment.

`Reminder` is a child entity, deliberately **not** an independent root:
cascade-owned, never referenced externally, keyed by `EventId`. This keeps
"first-class domain concept" from sprawling into an independent service
layer. It carries **no** notification state (no toast id, no last-fired, no
snooze) — that state belongs to the notification pipeline, and a future
snooze/dismiss `ReminderState` (see `BACKLOG.md` "Reminders") will live
*outside* `Reminder` so the entity stays pure. The operational contract is
in `architecture/REMINDERS.md` "Data model."

---

## Reminder → Notification → Toast Vocabulary (2026-07-18)

Early Phase C writing used "Notifications" and "Reminders" interchangeably
as the feature name. That conflated three concepts that vary independently,
and the vocabulary is now settled:

- **Reminder** — a domain entity tied to an event ("remind me 10 minutes
  before this event"). The cause.
- **Notification** — a user-facing message Chronicle surfaces. The effect a
  reminder triggers.
- **Toast** — one Windows mechanism for presenting a notification.

A reminder *causes* a notification, which today happens to be *delivered*
as a Windows toast. A reminder is not a notification, just as it is not a
toast.

The boundaries earn their keep independently. Reminder ≠ notification:
reminders are plausibly not the only notification producer Chronicle will
ever have (sync failures, import/backup completion, update available — all
notifications, none reminders). Notification ≠ toast: those future producers
carry different delivery semantics (show-now vs. reminder's scheduled
future delivery), and not every notification should even be a Windows toast
(a sync failure is plausibly an in-app status surface).

Naming consequences, decided deliberately:

- **Namespaces name responsibilities; architecture docs name subjects.**
- `src/Chronicle/Notifications/` (the delivery layer) keeps its generic
  name even while reminders are its only consumer — its responsibility is
  delivering notifications, and the first producer must not be baked into
  the infrastructure's name. A future proposal to rename it `Reminders/`
  "for consistency" would be un-making this decision.
- `architecture/REMINDERS.md` (formerly NOTIFICATIONS.md) names its
  subject: the reminder subsystem, which *uses* the notification
  infrastructure. The old name labeled the doc after the transport.
- `ScheduledToastReminderScheduler` is a correct name under the model — it
  schedules toasts for reminders, each tier in position.
- **No future documentation structure is reserved.** If a broader
  notification subsystem is ever built, whether it warrants its own
  architecture doc (or belongs inside another, or something else) is
  decided then, from the architecture actually built — not pre-allocated
  now. The architecture should earn its documents.

The working test for which word a sentence needs: would it survive swapping
the delivery mechanism? "The reminder fires at 9:50" — about intent →
*reminder*. "It appears in the Notification Center even when Chronicle is
closed" — about the message → *notification*. "The XML payload sets the
delivery time" — about the mechanism → *toast*.