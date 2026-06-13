# Architecture

## Core Philosophy

Chronicle owns the domain model.

External systems adapt into Chronicle.

Provider-specific concepts should never leak into UI code.

## Domain Model

Core entities:

- Calendar
- Event
- Recurrence

Future providers map into these entities.

## Storage

Local persistence is the source of truth.

Current implementation uses SQLite.

## Time Handling

Persist timestamps in UTC.

Convert at application boundaries.

Reason:

- provider interoperability
- synchronization correctness

## Provider Strategy

Future providers:

- Google Calendar
- Outlook
- Apple Calendar
- CalDAV

will be implemented as adapters.

Desired flow:

Provider
→ Adapter
→ Chronicle Domain Model
→ UI

Never:

Provider
→ UI

## UI Layer Organization

MainWindow is a coordinator: it owns application state, wires up event
handlers, and triggers refreshes. It does not build UI itself.

Rendering and dialog construction live in dedicated, single-purpose classes
under `Views/`:

- `Views/Rendering/CalendarGridRenderer` — main month grid and day cells
- `Views/Rendering/MiniMonthRenderer` — compact sidebar month navigator
- `Views/Rendering/SelectedDayRenderer` — selected-day detail panel
  (date, event count, event list, empty state)
- `Views/Rendering/SidebarRenderer` — calendar list, visibility toggles, and
  the add / edit / delete calendar affordances
- `Views/Dialogs/EventDialogService` — Create/Edit Event dialogs
- `Views/Dialogs/CalendarDialogService` — Create/Edit/Delete Calendar dialogs
- `Views/Popovers/EventPopover` — read-only event summary popover

Shared date/color conversions live in `Helpers/` (`DateHelpers`,
`ColorHelper`) so rendering classes don't duplicate them. Month-grid
geometry (week count + Sunday-aligned cell dates) is produced once by
`DateHelpers.BuildMonthGrid` and consumed by both the main grid and the
mini month, so the two never drift apart.

## Navigation State

MainWindow owns two distinct pieces of navigation state, kept separate on
purpose:

- `_displayMonth` — the month both grids render.
- `_selectedDate` — the user's focused day (defaults to today).

This separation is the foundation future Week/Day/Agenda views build on:
those views need a stable "focused date" independent of which month is
shown. Selection has several drivers, all funneling into `_selectedDate`:

- Mini month — click a day (advances `_displayMonth` if the day is outside
  the current month).
- Main grid — single tap selects a day; double tap selects it and opens the
  create-event dialog.
- Today button — selects today and shows its month.

`MainWindow.SelectDate` is the single in-month selection path: it updates
`_selectedDate` and refreshes only what depends on it (mini-month + grid
highlights and the selected-day panel) without reloading events. Cross-month
changes go through `RefreshMonthAsync`, which reloads events and re-renders
everything, including the selected-day panel. The selected-day panel reads
its events from the already-loaded `_eventsByDate`, so it never introduces a
competing query or date model.

These are plain classes instantiated directly by MainWindow — no DI
container, event bus, or MVVM framework. See "Avoid Premature MVVM" in
DECISIONS.md.

## Performance Philosophy

Calendar applications are read-heavy.

Optimize for:

- startup speed
- rendering speed
- low memory consumption

Avoid abstractions that materially harm responsiveness.