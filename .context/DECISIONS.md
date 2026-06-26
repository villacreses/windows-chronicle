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
   *Post-Phase-2A corollary:* `SeriesAnchorUtc` and `StartTimeUtc` may
   differ on the same occurrence — an override can move the displayed
   wall-clock Start while identity stays on the rule-walk anchor. Any
   write keyed on "this occurrence" (EXDATE append, override upsert,
   future scope-picker routes) must use `SeriesAnchorUtc`, not
   `StartTimeUtc`. `EventRef.From(evt)` constructs the correct key.
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
7. **The expander never throws on bad timezone data.** A
   `TimeZoneId` that fails to resolve (`TimeZoneNotFoundException` /
   `InvalidTimeZoneException`) degrades to the legacy UTC walk for
   that one series with a `Debug.WriteLine`. One malformed row
   cannot take down the load. The write boundary
   (`Event.Validate` + `EventEditPopover.GetDefaultRecurringTimeZoneId`)
   prevents Chronicle itself from writing bad data; the expander
   fallback handles bad data from other paths (manual edits, future
   imports, restored backups). Both ends of the defense matter.
8. **Single walk dispatch.** `RecurrenceExpander.Expand` and
   `RecurrenceExpander.ComputeEndUtc` both route through one
   `WalkAnchorsForMaster(start, rule, timeZoneId)` helper. The
   tz-aware branch (and any future walk-strategy variation) lives
   inside that one helper. Drift between the rendering walk and the
   cached-end walk is structurally impossible — there is no parallel
   implementation to fall out of sync.

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

**Phase 2A — occurrence mutation:**

- `EventOverrides` table (a *divergence* — one occurrence with edited
  fields). Per-field nullable, null = inherit from master at expansion
  time. No `IsCancelled` column; EXDATE handles cancellation
  atomically on the master.
- `EventRef` discriminated identity primitive (`Master(Guid)` /
  `Occurrence(Guid SeriesId, DateTime AnchorUtc)`) at mutation
  boundaries only. Lands the wrapper-tripwire deal from the section
  below — see "Phase 2A landing" there.
- Scope picker on edit (This event / All events). "This event" writes
  an override via `OverrideRepository.UpsertAsync(EventRef.Occurrence,
  OverrideFields)`; "All events" updates the master.
- Expander gains an override-merge step joined to the existing
  per-anchor pipeline (after EXDATE filter, before merged-range gate).
  Walk termination is extended by `maxPastDisplacement` so overrides
  that pull future anchors back into the visible window are reached.

**Phase 2B — wall-clock anchoring (DST drift fix):**

- `TimeZoneId` column on `Events`. NULL = legacy UTC anchoring;
  non-null = local-time anchoring in the named IANA zone.
- Single tz-aware dispatch (`WalkAnchorsForMaster`) shared by
  `Expand` and `ComputeEndUtc`. The tz-aware branch walks in local
  time and projects each anchor to UTC at emission. Legacy NULL
  path unchanged.
- New recurring events default `TimeZoneId` to the system's current
  zone via `EventEditPopover.GetDefaultRecurringTimeZoneId`, which
  normalizes Windows IDs → IANA at the write boundary. The "always
  IANA" invariant is enforced at this single point of origin;
  `Event.Validate` rejects strings that don't resolve via
  `TimeZoneInfo` at the persistence boundary as defense in depth.
- Existing recurring rows stay NULL. No auto-migration — changing a
  master's anchoring strategy is breaking per invariant #2; users
  who want the fix recreate the series.
- DST edge cases (matching Google / Apple / libical convention):
  - **Invalid local times** (spring-forward gap): shift forward by
    the DST adjustment delta so the event still occurs on its
    scheduled day. `ResolveLocalForDst` handles this before
    `ConvertTimeToUtc`.
  - **Ambiguous local times** (fall-back overlap):
    `TimeZoneInfo.ConvertTimeToUtc` defaults to standard time (the
    earlier interpretation). We don't override.
- **`TzWalkPad` (1 hour)** is added to walk-termination gates
  (`walkEndUtc`, `UNTIL`) when a tz-aware walk is active. The
  shift-forward fix produces a one-time UTC blip at DST transitions
  that would otherwise cause premature termination. Anchors
  temporarily past unpadded UNTIL are skipped (continue) without
  counting toward COUNT or updating `lastAnchor`, preserving rule
  semantics.

**Deferred from the original Phase 2 list:**

- This-and-following scope (introduces series fission with EXDATE /
  override migration rules; not built on evidence of user need).
- Override reconciliation on master rule changes (orphans are silently
  ignored at expansion per invariant #2; auto-reconciliation isn't
  worth the UX surface).
- Ordinal BYDAY, multi-value BYMONTHDAY, RDATE, custom RRULE input
  (RRULE expressiveness, separate from occurrence mutation).

The split is built around a single insight: EXDATE creates a hole; an
override creates a divergence that must be tracked and merged forever.
Phase 2A and 2B are independent concerns sharing no pipeline; both
touch the expander walk, so doing one at a time concentrates risk.

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

**Anchor zone is authoritative, not the display zone (product
decision).** A tz-aware master records the zone it was created in;
that zone is the series's anchor for its entire lifetime. Display
always converts to `TimeZoneInfo.Local`, but edits never
*re-anchor* — saving an edit from a different OS zone preserves the
master's existing `TimeZoneId`. A "9 AM NY meeting" edited from a PT
machine remains anchored to NY and renders as 6 AM PT to the editor.
This is a deliberate UX position, not an implementation accident:

- The alternative ("re-anchor to current system zone on edit") would
  silently invalidate every EXDATE and override on the series
  (invariant #2) every time the user travelled and touched the event.
- The other alternative ("treat display zone as authoritative")
  removes the concept of a fixed anchor entirely; the same calendar
  would render differently in two locations and the EXDATE / override
  identity contract would have nothing stable to key on.
- The traveler case the chosen position handles imperfectly ("I moved
  to LA permanently and now my NY meetings show at the wrong local
  time") is recoverable by recreating the series — explicit user
  intent for an explicit semantic change. The traveler cases it
  handles correctly ("I'm visiting LA for a week and my recurring NY
  meetings should still happen at 9 AM NY") cover the common need.

A maintainer reading the edit-path code and seeing
`TimeZoneId = eventToEdit.TimeZoneId` preserved across saves is
looking at this decision, not an oversight.

Note the subtlety: a tz-aware master defines a **different equivalence
space** for its occurrences than a UTC-anchored one. EXDATEs and
overrides remain keyed to UTC anchors (because that is what the
expander emits in both modes), but the set of anchors a rule produces
is mode-dependent. Migration from UTC anchoring to tz anchoring on an
existing master is therefore a breaking change to its projection space
(invariant #2) and is treated as such.

### Tolerated ambiguity: `Event` carries dual semantics in UI read paths

Phase 1 ships with `Event` carrying two semantically distinct shapes —
persistent rows and expanded occurrences — in a single type. The
distinction is mediated by `Event.IsOccurrence` / `EventKey` at read
sites and enforced at the persistence boundary by
`EventRepository.RefuseOccurrence`.

A wrapper type (`EventInstance` or similar) that encodes the
projection-vs-entity split in the type system was discussed and
deliberately deferred. The cost of a wrapper is paid in every renderer
and tap-target signature; the bug class it would structurally prevent
(occurrence handed to a persistence write) is already caught by the
repository guard. The cost of staying without it is paid in discipline
at branching mutation paths.

**Revisit trigger.** Introduce the wrapper when either:

- a second projection consumer appears that needs to distinguish
  occurrences from masters at read time (drag/drop reschedule, inline
  edit, multi-select copy/paste, provider sync reconciliation), OR
- `IsOccurrence` branching appears in more than one mutation path
  beyond the Phase 1 edit / delete handlers — i.e. a third place,
  not "drag/drop someday."

Until one of those triggers fires, the chokepoint + discriminator
approach is the accepted answer. The deal is recorded here so any
future revisit lands on the same evidence both sides agreed to.

**Phase 2A landing (trigger fired).** The override-save handler in
Phase 2A is the third mutation site the second trigger named. The
resolution is `EventRef` — a discriminated identity primitive
(`Master(Guid)` / `Occurrence(Guid SeriesId, DateTime AnchorUtc)`)
introduced at mutation boundaries *only*. Renderers and the projection
cache stay on plain `Event`; the wrapper does not propagate. The
existing two `IsOccurrence` mutation branches (edit, delete) and the
new third one (override save) are now `EventRef`-typed pattern
dispatches; `OverrideRepository.UpsertAsync(EventRef.Occurrence,
OverrideFields)` makes the master-variant call a compile error. The
narrower form was chosen over the original full-wrapper proposal
because the edit-path benefit (preserving occurrence-launch context
across the popover boundary) is concentrated at mutation entry, not
in renderer signatures.

### Tolerated ambiguity (Phase 2A)

Two corners we deliberately don't enforce in Phase 2A, with the
expectation that the write-path UX prevents normal user paths from
producing them.

**Merge validity is not enforced post-merge.** `EventOverride.Validate`
catches `End < Start` only when both override times are set. The
expander does not re-validate the merged Event before emitting. A
malformed override smuggled in via direct SQL (or a future writer that
bypasses the editor's validation) can produce an emitted occurrence
with `End < Start`, or `IsAllDay = true` paired with non-zero Start/End
times. Renderer behavior on such malformed merges is undefined. The
editor form's own validation prevents normal user paths from producing
these. Acceptable for MVP; revisit if a non-UI writer is introduced.

**Master-loading gap for cross-boundary overrides.** Override
completeness within the loaded master set is explicit (see invariant
#3 / "Override loading shape" in the sub-step 2 commit). But masters
themselves may be filtered out by `EventRepository.GetInRangeAsync`
based on the master's natural boundaries (`StartTimeUtc > rangeEnd` or
`RecurrenceEndUtcCached < rangeStart`). If an override on such a
filtered-out master would pull an occurrence into the visible window,
that occurrence is silently dropped — the master isn't loaded, so its
overrides aren't queried, so the displacement isn't computed. Both
ends of the gap require the user to have moved an occurrence across
the master's *natural* boundary, which is unusual. The fix is a
two-pass load (query candidate overrides first, then load masters
including those referenced) and is deferred until a user actually hits
the limitation.

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