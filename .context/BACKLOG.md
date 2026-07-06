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

## Integrations

- Apple Calendar
- CalDAV
- Calendly

## Visualization

- Timeline view

(Agenda view and Year view were promoted to the Local Baseline
milestone in `EXECUTION_PLAN.md`.)

## Experimental

- Travel timeline
- Smart categorization

## Refactors / Tech Debt

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