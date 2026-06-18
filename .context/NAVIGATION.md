# Navigation

This document describes the Chronicle navigation model. It documents existing
behavior; it is not a redesign.

## State

`MainWindow` owns the navigation state.

- `_displayMonth` is the month currently rendered by the main month grid and
  the mini month.
- `_selectedDate` is the user's focused calendar day. It defaults to today
  and is intentionally separate from `_displayMonth`.
- `_currentView` (`Month` | `Week` | `Day`) is the active main view. It is a
  display mode, not date state: Week View *derives* its visible week from
  `_selectedDate` via `DateHelpers.BuildWeek`, and Day View *is* `_selectedDate`.
  There is no separate "displayed week" or "displayed day" field — moving the
  week or day means moving `_selectedDate`.

There remains exactly one source of truth for date focus: `_selectedDate`
(with `_displayMonth` as the month anchor for the month grid and mini month).

Renderer classes may keep local copies of these values so they can update
visual state incrementally, but those copies are not authoritative.

## Ownership

`MainWindow` is the only owner of navigation mutations.

- `CalendarGridRenderer` reports day selection and day activation back to
  `MainWindow`.
- `MiniMonthRenderer` reports date selection and month-arrow requests back to
  `MainWindow`.
- `SelectedDayRenderer` displays the current selected day and its already
  loaded events; it does not own navigation state.

## Mutation Paths

Initial window construction:

- `_displayMonth` is set to the first day of the current month.
- `_selectedDate` is set to today's local day key.

Toolbar Previous/Next navigation (view-aware via `StepPeriod`):

- In Month view, Previous/Next subtract / add one month to `_displayMonth`,
  then call `RefreshActiveViewAsync`. They do not change `_selectedDate`.
- In Week view, they call `StepWeek(±1)`, which moves `_selectedDate` by seven
  days and re-anchors `_displayMonth` to the selected date's month. Because the
  week is derived from `_selectedDate`, paging the week and moving the selected
  day are the same operation.
- In Day view, they call `StepDay(±1)`, which moves `_selectedDate` by one day
  and re-anchors `_displayMonth`. Same principle as Week — Day View *is* the
  selected day, so paging it is moving `_selectedDate`.

View switching:

- `SwitchView` sets `_currentView`, toggles `MonthViewRoot` / `WeekViewRoot` /
  `DayViewRoot` visibility, syncs the toggle buttons, and calls
  `RefreshActiveViewAsync` so the newly active view loads its range and renders.
  It introduces no new date state.

Today navigation:

- `TodayButton_Click` sets `_displayMonth` to the first day of today's month.
- It sets `_selectedDate` to today's local day key.
- It then calls `RefreshActiveViewAsync`.

Mini-month date selection:

- If the chosen date is outside `_displayMonth`, `OnMiniMonthDateSelected`
  sets `_selectedDate` to that date, sets `_displayMonth` to that date's month,
  and calls `RefreshActiveViewAsync`.
- If the chosen date is inside `_displayMonth`, it calls `SelectDate`.

Mini-month header arrows:

- `OnMiniMonthPrevMonth` subtracts one month from `_displayMonth`, then calls
  `RefreshActiveViewAsync`.
- `OnMiniMonthNextMonth` adds one month to `_displayMonth`, then calls
  `RefreshActiveViewAsync`.
- These paths do not change `_selectedDate`.

Main grid day selection (Month view):

- A tap on an in-month day's number badge calls `SelectDate` (the tap is
  marked handled so it does not bubble to the cell).
- A tap on the rest of an in-month cell's empty space calls `OnDayCreateRequested`,
  which calls `SelectDate` and opens `EventEditPopover` for that day (defaulting
  to a 9am start). Tap-to-create is now the uniform gesture across Month, Week,
  and Day views — there is no double-tap path anywhere.

Week view day selection and time-slot interaction:

- Tapping a day header calls `SelectDate` — Week View is a consumer of the
  same selection model as Month View.
- Tapping an empty timeline slot calls `OnTimeSlotCreateRequested(day, hour)`, which
  selects the day and opens `EventEditPopover` pre-filled with that day and
  start hour. The same callback is shared with Day View.
- Selecting a day already in the visible week uses the incremental path:
  `WeekViewRenderer.UpdateSelectedDate` mutates the previous and new
  selected-day header highlights in place. Columns, gridlines, chips, and
  scroll state are not touched.
- Selecting a day in a different week (e.g. from the mini month) re-anchors
  `_displayMonth` and calls `RefreshActiveViewAsync`, because the visible seven
  days and the loaded range change.

Day view:

- Day View renders `_selectedDate`: an optional all-day band over a scrollable
  24-hour timeline, with timed events positioned by start/end (overlapping
  events packed into side-by-side columns) and a current-time indicator on
  today. It reads the day's events from the shared `_eventsByDate`.
- Clicking an event opens the read-only popover (`OnEventClicked`); the
  popover's Edit button opens `EventEditPopover`. Tapping an empty time slot
  calls the shared `OnTimeSlotCreateRequested(_selectedDate, hour)`, which opens
  `EventEditPopover` pre-filled with the selected day and the slot's start hour.
- Because Day View's loaded range is a single day, any `SelectDate` to a
  different day re-anchors `_displayMonth` and calls `RefreshActiveViewAsync`
  (the single-day cache never covers another day).

Event editing:

- Tapping an event chip (any view) or a selected-day panel row opens
  `OnEventClicked` (read-only). Its Edit button forwards to `EventEditPopover`
  via the host, which on save updates the event and refreshes the active view.
- All event create/edit flows go through `EventEditPopover` (light-dismiss
  flyout). Calendars still use modal dialogs via `CalendarDialogService`.

Calendar/sidebar changes:

- Calendar visibility changes, calendar create/edit/delete, event delete, and
  event dialog completion refresh the current month data.
- They do not directly change `_selectedDate` or `_displayMonth`.

## Selection Behavior Rules

`SelectDate` is the in-month selection path.

- It normalizes the target to a local day key.
- It updates `_selectedDate`.
- It updates mini-month and main-grid selection visuals incrementally.
- It re-renders the selected-day panel.
- It does not reload events, because in-month events are already loaded.

Cross-month date selection from the mini month does not use the incremental
path. It updates both navigation fields and calls `RefreshActiveViewAsync`, because
the event range changes. The same is true of cross-week selection in Week View
and any day change in Day View.

The selected-day panel reads from `_eventsByDate`, which contains events for
the currently loaded range (the displayed month in Month view, or the visible
week in Week view) after calendar visibility filtering.

## Month Navigation Behavior Rules

`RefreshActiveViewAsync` is the full refresh path for whichever view is active.

- It loads events via `LoadEventsAsync` for the active view's range: the
  `_displayMonth` month range in Month view, the `_selectedDate` week range in
  Week view, or the `_selectedDate` day range in Day view (one repository call,
  one filter, one `_eventsByDate` store — no separate event pipeline per view).
- It renders the active main view (month grid + day headers, the week columns,
  or the day timeline), then the mini month and the selected-day panel.
- It updates the period header (month name, week range, or full day date).

Toolbar and mini-month arrow navigation change `_displayMonth` only. They do
not implicitly move `_selectedDate` into the newly displayed month.

As a result, after month-arrow navigation, `_selectedDate` may refer to a day
outside `_displayMonth`. In that state:

- The main grid and mini month render the new `_displayMonth`.
- No selected-day highlight appears unless `_selectedDate` is visible in that
  rendered month grid.
- The selected-day panel still labels `_selectedDate`, but its event list is
  sourced from the currently loaded month data.

That behavior is existing behavior and should be revisited deliberately when
Week/Day navigation semantics are designed.
