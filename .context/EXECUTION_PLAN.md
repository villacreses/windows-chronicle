# Execution Plan

## Current Objective

Build the complete provider-agnostic calendar experience before beginning account integrations.

## Completed

- Local persistence
- Calendar model
- Event model
- Month view
- Month navigation
- Event CRUD
- Calendar visibility sidebar
- MainWindow decomposition into rendering/dialog helper classes
- Event popover (read-only summary before edit)
- Mini Month Navigator
- Calendar Management (create / edit / delete / color)
- Date Selection experience (selected-day panel + click/double-click model)
- Week View (first additional view, built on the selection model)
- Day View (single-day 24h timeline, built on the same selection model)

### UI CONSTRAINT (TEMPORARY)

A dev-only dark theme override is active to reduce visual fatigue.

This does NOT change execution priority:
1. Recurrence
2. Provider integrations
3. Design overhaul

No UI polish work should be introduced outside of Theme infrastructure stability.

## Current Milestone

Recurrence (see Next Milestones)

## Recently Completed: Day View

Delivered:

- Day View as a third view in the Month/Week/Day segmented switcher, derived
  entirely from `_selectedDate` (no new navigation state)
- `DayViewRenderer`: optional all-day band over a scrollable 24-hour timeline
  (00:00–23:00), solid hour + dashed half-hour lines, current-time indicator on
  today, timed events positioned by start/end with overlapping events packed
  into side-by-side columns, auto-scroll to ~7am/first event
- Previous/Next step one day in Day View (`StepDay` via `StepPeriod`); event
  click opens the popover; double-click an empty slot creates pre-filled with
  the slot's hour
- Reuses the existing event pipeline (`LoadEventsAsync` day range +
  `_eventsByDate`), `CalendarRenderHelper`, `ColorHelper`, and `Theme`

## Recently Completed: Week View

Delivered:

- Month/Week view switcher in the header (two toggle buttons)
- Week View (`WeekViewRenderer`): seven Sunday→Saturday day columns derived
  from `_selectedDate`, each with a header (today/selected highlight) and a
  scrollable list of event chips
- Previous/Next are view-aware: by month in Month view, by week in Week view
  (`StepWeek`, which moves `_selectedDate` ±7 days)
- Reuses `_selectedDate`/`_displayMonth`, `SelectDate`, the repositories,
  `ColorHelper`, and the `_eventsByDate` store; no parallel state or pipeline
- `DateHelpers` gained week geometry (`GetWeekStart`, `BuildWeek`,
  `GetWeekRangeUtc`, `IsSameWeek`); `LoadEventsAsync` queries the week range
  in Week view via the same single pipeline

## Recently Completed: Date Selection

Delivered:

- New day-interaction model in the main grid: single tap selects a day,
  double tap creates an event for it (replacing click-to-create)
- Selected Day sidebar panel (`SelectedDayRenderer`): selected date, event
  count, clickable event list, and a "No events scheduled." empty state
- Clicking an event in the panel opens the existing edit dialog
- In-month selection updates incrementally via `SelectDate` (no event
  reload); cross-month selection still goes through `RefreshActiveViewAsync`
- Reuses the existing `_selectedDate` model; no competing selection state

## Recently Completed: Calendar Management

Delivered:

- Create calendar (name + preset color palette) from the sidebar header
- Edit calendar (rename + recolor) via per-row overflow menu
- Delete calendar with confirmation; cascade-deletes its events
  (see DECISIONS.md) and surfaces the affected event count
- Sidebar is now the management surface: "+" to add, "⋯" per row to
  edit/delete
- New `CalendarRepository.UpdateAsync`/`DeleteAsync` (transactional) and
  `EventRepository.CountByCalendarAsync`; preset palette added to
  `ColorHelper`
- Consolidated startup + post-mutation reload into a single
  `ReloadCalendarsAndRefreshAsync`

## Next Milestones

### Recurrence

- Recurrence editing
- Recurrence rendering

## Provider Integration Phase

After local UX is mature:

- Google Calendar
- Outlook Calendar

## Definition of Ready for Integrations

Before Google integration begins:

- Month view complete
- Week view complete
- Day view complete
- Calendar management complete
- Recurrence complete