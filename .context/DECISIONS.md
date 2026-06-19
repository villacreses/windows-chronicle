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