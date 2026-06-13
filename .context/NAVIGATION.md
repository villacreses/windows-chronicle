# Navigation

This document describes the current Chronicle navigation model before Week
View work begins. It documents existing behavior; it is not a redesign.

## State

`MainWindow` owns the navigation state.

- `_displayMonth` is the month currently rendered by the main month grid and
  the mini month.
- `_selectedDate` is the user's focused calendar day. It defaults to today
  and is intentionally separate from `_displayMonth`.

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

Toolbar month navigation:

- `PrevMonthButton_Click` subtracts one month from `_displayMonth`, then calls
  `RefreshMonthAsync`.
- `NextMonthButton_Click` adds one month to `_displayMonth`, then calls
  `RefreshMonthAsync`.
- These paths do not change `_selectedDate`.

Today navigation:

- `TodayButton_Click` sets `_displayMonth` to the first day of today's month.
- It sets `_selectedDate` to today's local day key.
- It then calls `RefreshMonthAsync`.

Mini-month date selection:

- If the chosen date is outside `_displayMonth`, `OnMiniMonthDateSelected`
  sets `_selectedDate` to that date, sets `_displayMonth` to that date's month,
  and calls `RefreshMonthAsync`.
- If the chosen date is inside `_displayMonth`, it calls `SelectDate`.

Mini-month header arrows:

- `OnMiniMonthPrevMonth` subtracts one month from `_displayMonth`, then calls
  `RefreshMonthAsync`.
- `OnMiniMonthNextMonth` adds one month to `_displayMonth`, then calls
  `RefreshMonthAsync`.
- These paths do not change `_selectedDate`.

Main grid day selection:

- A single tap on an in-month day calls `SelectDate`.
- A double tap on an in-month day calls `OnDayActivated`, which calls
  `SelectDate` and opens the create-event dialog for that day.

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
path. It updates both navigation fields and calls `RefreshMonthAsync`, because
the event range changes.

The selected-day panel reads from `_eventsByDate`, which contains events for
the currently displayed month after calendar visibility filtering.

## Month Navigation Behavior Rules

`RefreshMonthAsync` is the full month refresh path.

- It loads events for `_displayMonth`.
- It renders day headers, the main grid, the mini month, and the selected-day
  panel.
- It updates the month/year header.

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
