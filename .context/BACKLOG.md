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