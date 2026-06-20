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

Recurrence is stored as RFC 5545 RRULE strings (e.g.
`FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261231T000000Z`). The same format is
spoken natively by Google, Outlook (via Graph), Apple Calendar, and
CalDAV, so the future provider adapters become a field-mapping exercise
rather than a translation one.

### System model

Chronicle's recurrence is a **projection system with first-class
addressable derived entities**, not a pure functional expansion
pipeline.

- A recurring master row + its rule defines an *addressable projection
  space* of occurrences.
- The expander materializes the slice intersecting the current load
  range. Occurrences are transient `Event` instances; **no occurrence
  is ever persisted**.
- EXDATE and (Phase 2) `EventOverride` rows are persistent decorations
  keyed to addresses within that space.
- Cache fields are advisory and never influence semantics.

The addressing primitive is `EventKey = (SeriesId, Anchor?)`.
Recurrence defines the space; `EventKey` addresses points in it. The
boundary is non-negotiable: identity must not feed back into
expansion.

### Named invariants

These are load-bearing. Any change that violates them is a breaking
change to the projection space and requires data migration.

1. **Occurrence identity = (Event.Id, SeriesAnchorUtc).** An expanded
   occurrence carries its master's `Id`; `SeriesAnchorUtc` is the
   per-occurrence discriminator. `Event.Id` is unique among rows in the
   `Events` table but NOT among in-memory `Event` instances. Code that
   needs a key over a mixed collection must use `EventKey.For(evt)`.
2. **Identity stability is rule-version conditional.** `EventKey` is
   stable across loads, view switches, and cache lifecycle — but only
   within a single recurrence rule version. Semantic changes to RRULE
   handling, the anchor-walk algorithm, or the time-zone evaluation
   strategy are breaking; pre-existing EXDATEs / overrides become
   undefined.
3. **EXDATE is a semantic constraint over the space, not the
   algorithm.** Each EXDATE entry must equal a walker-emitted anchor
   bit-for-bit. The write path persists `SeriesAnchorUtc` verbatim — no
   normalization, no rounding. Today the space and algorithm coincide
   on UTC equality; Phase 2's tz-aware walk must preserve that
   coincidence intentionally.
4. **Cache fields are advisory.** `RecurrenceEndUtcCached` is used
   solely by `EventRepository.GetInRangeAsync` to prune ended finite
   series. The expander never reads it; it is not authoritative
   metadata and never participates in expansion or termination
   decisions. If a cached value disagrees with the rule, the rule
   wins and the cache is a bug to be repaired by the writer.
5. **`_eventsByDate` is a render-time projection cache, never an
   identity source.** UI state that needs to remember a selection
   across reloads must hold an `EventKey`, not a dictionary slot or
   list index.
6. **COUNT counts pre-EXDATE.** Per RFC 5545: COUNT counts all
   instances generated by the rule, before EXDATE filtering. Filter
   EXDATE after the count walk terminates.

### Two-phase rollout

**Phase 1 — recurrence engine + skip:**

- RRULE storage, parser, value object
- Expansion engine (Daily / Weekly / Monthly / Yearly, INTERVAL,
  COUNT / UNTIL, weekly BYDAY)
- `RecurrenceEndUtcCached` column + index
- EXDATE list ("skip this occurrence" — a *hole* in the series)
- Transient `SeriesAnchorUtc` on `Event`; `EventKey` primitive
- Create / edit / delete entire series; preset-pattern recurrence editor
- Banner on recurring-event edit: "Changes apply to all occurrences"

**Phase 2 — occurrence mutation and tz-aware anchoring:**

- `EventOverrides` table (a *divergence* — one occurrence with edited
  fields)
- "Edit this event" scope
- Scope picker (this / all / this-and-following)
- Override reconciliation when the master rule changes
- Ordinal BYDAY, multi-value BYMONTHDAY, RDATE, custom RRULE input
- Wall-clock anchoring (see below)

The split is built around a single insight: EXDATE creates a hole; an
override creates a divergence that must be tracked and merged forever.
Those are different complexity classes and don't belong in the same
milestone.

### UTC anchoring is a Phase 1 constraint, not a product position

Phase 1 anchors recurring events in UTC because the existing storage
carries only a UTC `StartTimeUtc`; a weekly meeting created at 9 AM
local will drift ±1 hour across DST boundaries. **This is an
implementation artifact, not the intended product behavior.** The user
intent for "weekly Monday 9 AM" is wall-clock semantics.

Phase 2 introduces a tz-aware evaluation strategy: an additional
`TimeZoneId` column on recurring masters, a tz-aware branch inside
`WalkAnchors` that walks in local time and projects each anchor to UTC
at emission. The pipeline shape is unchanged — same single semantic
model, strategy variation inside `WalkAnchors`.

Note the subtlety: a tz-aware master defines a **different equivalence
space** for its occurrences than a UTC-anchored one. EXDATEs and
overrides remain keyed to UTC anchors (because that is what the
expander emits in both modes), but the set of anchors a rule produces
is mode-dependent. Migration from UTC anchoring to tz anchoring on an
existing master is therefore a breaking change to its projection space
(invariant #2) and is treated as such.

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