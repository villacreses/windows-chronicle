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
- `Views/Rendering/WeekViewRenderer` — week strip of seven day columns,
  derived from `_selectedDate`
- `Views/Rendering/MiniMonthRenderer` — compact sidebar month navigator
- `Views/Rendering/SelectedDayRenderer` — selected-day detail panel
  (date, event count, event list, empty state)
- `Views/Rendering/SidebarRenderer` — calendar list, visibility toggles, and
  the add / edit / delete calendar affordances
- `Views/Rendering/CalendarRenderHelper` — shared rendering primitives (event
  chip, day-container and day-number visuals, common colors) used by the month
  grid and week view; renderers still own their own layout
- `Views/Dialogs/EventDialogService` — Create/Edit Event dialogs
- `Views/Dialogs/CalendarDialogService` — Create/Edit/Delete Calendar dialogs
- `Views/Popovers/EventPopover` — read-only event summary popover

Shared date/color conversions live in `Helpers/` (`DateHelpers`,
`ColorHelper`) so rendering classes don't duplicate them.

## Visual Design

Chronicle uses a dark "Fluent" visual language (teal-green accent), ported
from the Claude Design handoff. It is applied in two coordinated layers:

- **Standard WinUI controls** (dialogs, buttons, the event popover, toggles)
  pick it up from `App.xaml`: `RequestedTheme="Dark"` plus `SystemAccentColor`
  overrides set to the teal ramp. The window keeps the Mica backdrop.
- **Code-built renderers** read explicit tokens from `Helpers/Theme.cs` — the
  single source of truth for surface, hairline, text-ramp, and accent colors.
  `ColorHelper` exposes the accent and per-calendar color helpers
  (`Soften`/`LightenForText` derive the filled event-pill background and text
  from a calendar's stored hex). `CalendarRenderHelper` applies the shared
  day-cell, day-number badge, and event-pill visuals so Month and Week stay
  consistent.

Renderer color constants were removed in favor of `Theme`; to retune the
palette, edit `Theme.cs` (renderers) and the `App.xaml` accent colors
(standard controls). Month-grid
geometry (week count + Sunday-aligned cell dates) is produced once by
`DateHelpers.BuildMonthGrid` and consumed by both the main grid and the
mini month, so the two never drift apart. Shared *visual* construction
(event chips, selectable day-cell styling) lives in
`Views/Rendering/CalendarRenderHelper`, so the month and week renderers share
appearance and interaction without copy-pasting cell/chip code.

### Theme System

The current Theme layer is a minimal, development-only abstraction used to centralize renderer colors.

Constraints:
- Hard-coded dark mode is used during development.
- No full theming system (light/dark switching, user preferences) exists.
- Theme is NOT part of product architecture at this stage.
- It exists only to stabilize visual consistency during feature development.

Future work:
- Replace with proper system-aware theme support during design overhaul phase.

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

`MainWindow.SelectDate` is the single incremental selection path: it updates
`_selectedDate` and refreshes only what depends on it (mini-month, main grid,
and week view highlights, plus the selected-day panel) without reloading
events. Cross-month changes (and, in Week View, cross-week changes) go through
`RefreshActiveViewAsync`, which reloads events and re-renders the active view. The
selected-day panel reads its events from the already-loaded `_eventsByDate`,
so it never introduces a competing query or date model.

Week View is a second consumer of this same model: `_currentView` selects
which main view renders, the visible week is derived from `_selectedDate` via
`DateHelpers.BuildWeek` (no stored week), and its events come from the shared
`_eventsByDate` (loaded for the week's range by the same `LoadEventsAsync`).
Day selection, activation, and event clicks reuse the same callbacks as the
month grid.

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