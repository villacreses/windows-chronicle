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

## Reminders — UX Parity

A single coherent capability area, not scattered deferrals. Product
direction: DECISIONS.md "Reminders: Calendar Parity, Not Notification
Platform" — the admission test for this area is:

> Does Chronicle provide the reminder experience users would reasonably
> expect from a modern Windows calendar app?

The engine (entity → projection → OS scheduling → toast → deep link) is
shipped; this area completes the expected *experience* on top of it.
Subsystem contract: `architecture/REMINDERS.md`. The two audit
*correctness* items (editor preservation, bounded offsets) are not here —
they extend the Local Baseline; see EXECUTION_PLAN.md "Local Baseline
Addendum." Sequencing of this area is in EXECUTION_PLAN "Next Milestones."

### Setting reminders (editor parity)

- Multi-reminder editing — "Add reminder" collection rows. The domain,
  persistence, and projection already handle N reminders per event; this
  is editor work only. Validation policy (duplicate offsets, max count)
  is decided with this feature.
- Custom reminder offsets — arbitrary `(quantity, unit)` entry rather
  than presets only.
- All-day reminder fire time — today "1 day before" an all-day event
  fires at local midnight (all-day events are midnight-bounded). The
  model already represents "6 PM the day before" as `(30, Hours)`; this
  is an editor-preset and default-setting question, not a schema one.
- Default-reminder setting for new events — revisits the recorded
  "silent unless the user opts in" position; needs settings storage
  (which the provider phase introduces anyway).

### Responding to reminders (notification parity)

- Snooze / dismiss — a `ReminderState` table keyed on
  `(EventRef.Occurrence, ReminderId)`, interactive toast buttons. Likely
  its own branch. This state lives *outside* the `Reminder` entity, which
  stays pure domain (no notification state). Design notes carried from
  the audit: (a) snooze-without-opening-the-window requires background
  activation, which requires the manifest COM activator the classic path
  avoided — the alternative is foreground activation (window opens); the
  spike's "no manifest change needed" finding covers fire-and-click only,
  not this. (b) Offset edits mint new `ReminderId`s, so `ReminderState`
  needs orphan cleanup for ids that no longer exist. (c) The desired set
  becomes projection ⊖ dismissed ⊕ snooze-retimes — state joins at the
  compute site; the scheduler contract is untouched.

### Reliability the user can feel (trust parity)

- Notification-disabled awareness — reminders are silently suppressed
  when Windows notifications are off for Chronicle (`ToastNotifier
  .Setting` is readable; surface a one-time notice).
- Schedule staleness trigger — reconciliation runs only on launch and
  data mutation; a session left open without edits ages its coverage
  window toward the horizon edge (~2 mutation-free months before a miss;
  REMINDERS.md "Horizon policy"). A date-gated check on window activation
  (or a daily-coalesced equivalent) closes it within the Idle Cost
  Budget's event-driven rules.
- Reconcile diagnostics — failures are visible only in the debug log;
  persist a minimal last-reconcile timestamp/count/result for a future
  status surface.

### Supporting robustness (nice-to-have)

- Per-toast failure isolation in the scheduler (one bad `AddToSchedule`
  currently abandons the remaining adds until the next reconcile).
- Off-UI-thread reconcile — measure first; the scheduler's OS calls run
  synchronously on the UI thread today and are cheap at realistic scale.
- Manual-verification registry additions: reboot persistence of the OS
  schedule; behavior with notifications disabled.

### Wait for real user feedback

- Per-occurrence reminder overrides (an occurrence inherits series
  reminders; the occurrence editor deliberately omits the picker).
- Series-reminder hint in the occurrence editor.
- Deleted-event messaging on toast activation (currently: land on the
  day, nothing opens).
- Per-calendar reminder defaults; per-calendar notification mute.

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