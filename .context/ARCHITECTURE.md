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

Local-time conversion happens at point of use in the renderer.
`evt.StartTimeUtc.ToLocalTime()` per event chip is fine — the runtime
caches the system `TimeZoneInfo` after the first call, and the per-call
cost is microseconds (dwarfed by the surrounding `Border` / `TextBlock`
allocations the chip already pays for).

What is banned:

- **Parallel caches of local times** alongside `_eventsByDate`. Doubles
  event memory, introduces an invalidation problem when the user crosses
  a DST boundary with the app open, and buys nothing measurable.
- **`TimeZoneInfo` lookups or DST math inside per-event loops** — e.g.
  `TimeZoneInfo.FindSystemTimeZoneById`, manual offset rule construction.
  `ToLocalTime()` on a UTC `DateTime` is not such a lookup; it reuses
  the cached system zone.

The earlier "once per render at the view boundary" framing was
theoretically clean but practically over-strict: the conversion cost
isn't on any hot path, and forcing a pre-converted projection would
add per-event allocations or a parallel array for no measurable win,
working against the small-footprint goal.

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
- `Views/Rendering/WeekViewRenderer` — 7-column 24-hour timeline grid for the
  week (Sun→Sat) containing `_selectedDate`: clickable day headers, an optional
  all-day events band (shown only when all-day events exist), and seven day
  content columns built by `TimelineRenderHelper.BuildDayColumnContent` behind a
  single shared gutter from `TimelineRenderHelper.BuildSharedGutter`. Reports
  day-header taps, empty time-slot taps, and event taps back to `MainWindow`.
  Retains per-day day-number visuals from the last `Render()` so
  `UpdateSelectedDate` can mutate the previous and new selected-day highlights
  in place without rebuilding columns, gridlines, chips, or scroll state. Full
  `Render()` is reserved for range changes (cross-week navigation).
- `Views/Rendering/DayViewRenderer` — single-day all-day band + scrollable
  24-hour timeline, derived from `_selectedDate`
- `Views/Rendering/MiniMonthRenderer` — compact sidebar month navigator
- `Views/Rendering/SelectedDayRenderer` — selected-day detail panel
  (date, event count, event list, empty state)
- `Views/Rendering/SidebarRenderer` — calendar list, visibility toggles, and
  the add / edit / delete calendar affordances
- `Views/Rendering/TimelineRenderHelper` — stateless helper that builds a
  single day's timed-event timeline (gutter, gridlines, now-line, overlap-packed
  event blocks). Extracted from `DayViewRenderer` so `WeekViewRenderer` can
  reuse it to render seven timelines side by side; each call returns a
  self-contained `UIElement` with no shared mutable state.
- `Views/Rendering/CalendarRenderHelper` — shared rendering primitives (event
  chip, day-container and day-number visuals, common colors) used by the month
  grid and week view; renderers still own their own layout
- `Views/Dialogs/CalendarDialogService` — Create/Edit/Delete Calendar dialogs
- `Views/Popovers/EventPopover` — read-only event summary popover; its Edit
  button forwards to `EventEditPopover` via the host
- `Views/Popovers/EventEditPopover` — light-dismiss create/edit event editor: a
  static helper that shows a programmatic form (name, calendar, start/end
  date+time) in a `Flyout` anchored to a window point, returning the resulting
  `Event` on save or `null` on cancel/dismiss. The sole event-editing UI — it
  builds and returns the `Event` only; MainWindow persists via
  `EventRepository`. Used by Month (double-tap a day), Week and Day (tap an
  empty time slot), the selected-day panel, and the read-only event popover's
  Edit button.

Renderers may retain UI bookkeeping needed for incremental updates —
references to per-day visuals so a selection change can mutate two
highlights in place, dictionaries keyed by date, etc. They must not
retain event data: `_eventsByDate` on `MainWindow` is the single
source of truth for events (see "Single Event Cache" in Performance
Philosophy). The distinction is what "stateless renderer" means in
this codebase: no parallel event cache, not no state at all.

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

Week View and Day View are further consumers of this same model: `_currentView`
selects which main view renders, the visible week/day is derived from
`_selectedDate` (no stored week or day — `DateHelpers.BuildWeek` for the week,
`_selectedDate` itself for the day), and their events come from the shared
`_eventsByDate` (loaded for the active view's range by the same
`LoadEventsAsync`). Day selection, activation, and event clicks reuse the same
callbacks as the month grid. Week and Day View share a `onTimeSlotActivated`
callback — tapping an empty timeline slot opens `EventEditPopover` pre-filled
with that day and hour.

These are plain classes instantiated directly by MainWindow — no DI
container, event bus, or MVVM framework. See "Avoid Premature MVVM" in
DECISIONS.md.

## Performance Philosophy

Calendar applications are read-heavy. Chronicle is also designed to be
left open for an entire computer session, so steady-state cost matters as
much as peak cost.

Optimize for:

- startup speed
- rendering speed
- low memory consumption
- low idle cost (see "Idle Cost Budget" in DECISIONS.md)

Avoid abstractions that materially harm responsiveness.

### Bounded Visuals Are Reused, Not Rebuilt

Several visual element counts are fixed by the calendar model:

- 24 hour rows in a timeline
- 7 day columns in Week View
- 42 cells in a month grid (6 weeks × 7 days)

These are built once and updated in place on subsequent renders (text,
brushes, highlight state). Event chips, whose count is unbounded, may be
rebuilt — preferably from a pool — but the surrounding scaffolding is
not.

Selection-only changes (`SelectDate` to a day already in the loaded
range) must not reallocate cell, gridline, gutter, or column visuals.
Reserve full rebuild for range changes (month / week / day moved).

### View Switching Does Not Query

Toggling Month ↔ Week ↔ Day inside the same already-loaded date range
issues zero SQLite queries. `_eventsByDate` is the shared source of
truth and is refilled only when the loaded range actually changes.

### Repositories Return Concrete Collections

Repository methods return `List<T>` or arrays — never `IEnumerable<T>`.
This prevents hidden re-enumeration and LINQ allocation in render paths,
and makes the cost of each call legible at the call site.

### Single Event Cache

`_eventsByDate` (on `MainWindow`) is the only event cache in the
application. Renderers do not keep their own copy of event lists; they
read from `_eventsByDate` (or receive the relevant slice as a parameter)
each render. This keeps cache invalidation a one-place problem.