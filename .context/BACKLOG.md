# Backlog

## AI

- Copilot integration
- Natural language scheduling
- Event summarization

## Calendar Features

- Shared calendars
- Calendar subscriptions
- Holiday calendars
- Birthdays

## Reminders

Deferred beyond the Local Baseline reminder MVP (subsystem contract in
`architecture/REMINDERS.md`):

- Snooze / dismiss — a `ReminderState` table keyed on
  `(EventRef.Occurrence, ReminderId)`, interactive toast buttons,
  background activation. Likely its own branch. This state lives *outside*
  the `Reminder` entity, which stays pure domain (no notification state).
  Related open question recorded in REMINDERS.md "Reminder identity across
  saves": whether an offset change should preserve reminder identity
  becomes load-bearing once `ReminderState` keys on it.
- Multi-reminder editor UI — "Add reminder" collection rows. The domain,
  persistence, and projection already handle N reminders per event; this
  is editor work only.
- Per-occurrence reminder overrides.
- Default-reminder setting for new events.
- Non-toast notification channels.

## Integrations

- Apple Calendar
- CalDAV
- Calendly

## Visualization

- Timeline view
- Multi-day all-day events (data + visualization). The projection
  currently keys events by `StartTimeUtc`'s local date, so a multi-day
  all-day event would render only on its start day. Two entangled
  changes are needed and both are deferred to the design overhaul:
    - `EventProjection.GroupVisibleByDay` (or a peer) must fan a
      multi-day all-day event out onto every day it covers.
    - Month and Week must render the covered range as a single
      spanning bar rather than N per-day chips — a layout question
      (stacking, overlap with timed chips) more than a correctness
      one.
  As a Phase A guardrail, the editor constrains all-day events to
  a single day (start date == end date). Loosening that constraint
  depends on the two items above landing together.

(Agenda view and Year view were promoted to the Local Baseline
milestone in `EXECUTION_PLAN.md`.)

## Experimental

- Travel timeline
- Smart categorization

## Refactors / Tech Debt

- Year View render latency — first investigation done; findings below.
  The Year view reaches ~450 ms time-to-idle on a warm path (nearly
  empty database), a clear outlier: every other view lands in the
  ~50–200 ms range. A profiling pass (temporary instrumentation, since
  removed) established what is *not* responsible, which is the valuable
  part:
    - **Data and projection are ~0 ms.** `EventRepository` /
      SQLite and `EventProjection` contribute effectively nothing on
      the Year path — the range is cached and the grouping is trivial.
      The cost is entirely in the WinUI rendering layer.
    - **Control-template weight is NOT the cause.** Replacing the day
      cell's default `Button` template with a stripped
      `Border`+`ContentPresenter` template (focus / keyboard /
      automation all preserved) produced no meaningful change
      (~448 ms vs ~443 ms). The standard "give it a lighter template"
      fix does not apply here.
    - **The `Button` *object* is ~half the cost.** Swapping `Button`
      for a bare `Border`+`TextBlock`+`Tapped` roughly halved
      time-to-idle (~443 ms → ~231 ms) — but sacrificed keyboard
      navigation and accessibility (no focus, no automation peer),
      and was *still* materially slower than the other views. Not a
      shippable trade.
    - **Remaining hypothesis: raw framework-element count / layout.**
      Even the lightweight-`Border` variant (~231 ms) builds ~1,600
      elements (504 day cells × 2, plus twelve nested star-sized
      `Grid`s). The dominant residual cost is *how many elements
      exist*, not *what type they are*.
  Promising direction if revisited (design-overhaul phase, not now):
  reduce the framework-element count rather than micro-optimizing the
  per-cell control — e.g. custom rendering / composition-layer draw of
  the day grid with pointer hit-testing, so a mini-month is a handful
  of visuals instead of ~130 elements. This is the "504 interactive
  *cells*, not 504 interactive *controls*" reframe. Benchmark instinct:
  a native year view that feels instant is almost certainly not
  instantiating ~1,600 XAML elements. Deliberately deferred — the
  current `Button` implementation is correct and fully accessible, and
  the real fix is a rendering-model change that belongs with the
  design overhaul.

- Search backend upgrade (FTS5 or equivalent) — reach goal, not a
  roadmap item. The current `EventRepository.SearchCandidatesAsync`
  implementation uses SQLite `LIKE` on Title / Description, unioned
  in-SQL with `EventOverrides` matches, and hands the candidates to
  `EventProjection.SearchOccurrences` for expansion and re-filtering.
  This shape was chosen deliberately: the hard part of Chronicle
  search is recurrence projection, not text lookup, and FTS5 would
  add write-path invariants (content-table sync, rebuild bulk-writes)
  without solving the projection problem. Do NOT revisit this on
  performance or scale grounds — realistic single-user calendars
  stay well inside `LIKE`'s comfortable range. Revisit ONLY when
  user needs demand search *quality* the current shape can't deliver:
    - fuzzy matching ("meetng" → "meeting"),
    - typo tolerance,
    - relevance ranking across a searchable surface that has grown
      substantially beyond Title / Description / calendar name.
  When those triggers fire, evaluate FTS5 first (already-in-tree
  SQLite extension, no new NuGet), then a dedicated library — but
  only against a demonstrated user need, not an anticipated one.

- EventEditPopover → XAML UserControl with cached instance + Flyout
  (matching the EventPopover pattern). Recurrence Phase 1 roughly
  doubled the form's heavy-control count and pushed the
  programmatic-build-per-show pattern over its perception ceiling vs.
  the read-only popover. Structural fix: convert to a XAML UserControl
  with `x:Load`-deferred subtrees (recurrence picker, error block),
  single cached instance + Flyout constructed in `MainWindow`, and
  `SetForCreate(...)` / `SetForEdit(...)` reset methods so subsequent
  shows are value resets, not control allocations. Trigger: next time
  someone returns to this file for Phase 2 work or the design overhaul.