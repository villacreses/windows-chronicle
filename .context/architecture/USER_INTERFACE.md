# User Interface

Chronicle's UI is composed without an MVVM framework, DI container, or
event bus. Plain interfaces, records, and shared callback objects are
the only composition tools. `MainWindow` coordinates; single-purpose
renderer classes do the drawing.

## MainWindow Responsibilities

`MainWindow` is a coordinator, not a renderer:

- Owns application state (loaded events, calendars, navigation).
- Wires renderer instances together at construction.
- Routes events from renderers (selection, taps, mutation requests) to
  repositories and dialog services.
- Triggers refreshes.

It does not build UI itself.

The justification for this shape — versus extracting state into an
MVVM ViewModel — is iteration cost. A single host interface that
bundles renderer→`MainWindow` callbacks is not MVVM, not DI, and not
an event bus. It is the seam that lets renderers stop taking N
`Action<…>` parameters per `Render()` call (which would allocate a
closure per chip and violate the engine's Idle Cost Budget).

## UI Shell

Rendering and dialog construction live in dedicated, single-purpose
classes under `Views/`.

### Renderers (`Views/Rendering/`)

- `CalendarGridRenderer` — main month grid and day cells.
- `WeekViewRenderer` — 7-column 24-hour timeline grid for the week
  (Sun→Sat) containing `_selectedDate`: clickable day headers, an
  optional all-day events band (shown only when all-day events exist),
  and seven day content columns built by
  `TimelineRenderHelper.BuildDayColumnContent` behind a single shared
  gutter from `TimelineRenderHelper.BuildSharedGutter`. Reports
  day-header taps, empty time-slot taps, and event taps back to
  `MainWindow`. Retains per-day day-number visuals from the last
  `Render()` so `UpdateSelectedDate` can mutate the previous and new
  selected-day highlights in place without rebuilding columns,
  gridlines, chips, or scroll state. Full `Render()` is reserved for
  range changes (cross-week navigation).
- `DayViewRenderer` — single-day all-day band over a scrollable 24-hour
  timeline, derived from `_selectedDate`.
- `MiniMonthRenderer` — compact sidebar month navigator.
- `SelectedDayRenderer` — selected-day detail panel (date, event count,
  event list, empty state).
- `SidebarRenderer` — calendar list, visibility toggles, and the add /
  edit / delete calendar affordances.
- `TimelineRenderHelper` — stateless helper that builds a single day's
  timed-event timeline (gutter, gridlines, now-line, overlap-packed
  event blocks). Extracted from `DayViewRenderer` so `WeekViewRenderer`
  can reuse it; each call returns a self-contained `UIElement` with no
  shared mutable state.
- `CalendarRenderHelper` — shared rendering primitives (event chip,
  day-container and day-number visuals, common colors) used by the
  month grid and week view; renderers still own their own layout.

### Dialog Services and Popovers

- `Views/Dialogs/CalendarDialogService` — modal Create / Edit / Delete
  Calendar dialogs.
- `Views/Popovers/EventPopover` — read-only event summary; its Edit
  button forwards to `EventEditPopover` via the host.
- `Views/Popovers/EventEditPopover` — light-dismiss create/edit event
  editor: a static helper that shows a programmatic form (name,
  calendar, start/end date+time, Repeats picker, Ends picker) in a
  `Flyout` anchored to a window point. Two entry points:
  - `ShowCreateEventAsync` / `ShowEditEventAsync` — master-edit form,
    includes the Repeats picker, defaults `TimeZoneId` on the create
    path.
  - `ShowEditOccurrenceAsync` — stripped form for the This-event scope:
    no Calendar, no Repeats picker, no recurring banner.

  Returns the resulting `Event` on save or `null` on cancel/dismiss.
  `EventEditPopover` is the sole event-editing UI — it builds and
  returns the `Event` only; `MainWindow` persists via
  `EventRepository` (master path) or `OverrideRepository` (the
  occurrence-edit branch).

Shared date and color conversions live in `Helpers/` (`DateHelpers`,
`ColorHelper`) so renderers don't duplicate them.

## Stateless Rendering Philosophy

"Stateless renderer" in this codebase means **no parallel event
cache**, not no state at all.

Renderers may retain UI bookkeeping needed for incremental updates:
references to per-day visuals so a selection change can mutate two
highlights in place, dictionaries keyed by date, last-rendered range
for diff decisions. They must not retain event data.

`_eventsByDate` on `MainWindow` is the single source of truth for
events. The engine's Idle Cost Budget depends on this — a parallel
cache in any renderer would double the invalidation surface and
introduce DST-crossing bugs when the app is left open across a
transition.

These renderer classes are plain classes instantiated directly by
`MainWindow`. No DI container, event bus, or MVVM framework.

## Navigation State

`MainWindow` owns two pieces of date state and one display mode, kept
separate on purpose:

- `_displayMonth` — the month both the main grid and the mini month
  render.
- `_selectedDate` — the user's focused calendar day. Defaults to
  today.
- `_currentView` (`Month` | `Week` | `Day`) — the active main view.

`_currentView` is a display mode, not date state. Week View *derives*
its visible week from `_selectedDate` via `DateHelpers.BuildWeek`, and
Day View *is* `_selectedDate`. There is no separate "displayed week"
or "displayed day" field — moving the week or day means moving
`_selectedDate`.

This separation is the foundation Week/Day views build on: they need a
stable focused date independent of which month is shown.

On window construction, `_displayMonth` is set to the first day of the
current month and `_selectedDate` is set to today's local day key.

## View Switching

`SwitchView` sets `_currentView`, toggles `MonthViewRoot` /
`WeekViewRoot` / `DayViewRoot` visibility, syncs the toggle buttons,
and calls `RefreshActiveViewAsync`. It introduces no new date state.

**View switching within the loaded range issues zero SQLite queries.**
`_eventsByDate` is the shared event store and is refilled only when
the loaded range actually changes.

### Selection Paths

`SelectDate` is the in-range incremental path:

- Normalizes the target to a local day key.
- Updates `_selectedDate`.
- Updates mini-month, main-grid, and week-view selection visuals in
  place.
- Re-renders the selected-day panel.
- Does not reload events — in-range events are already loaded.

`RefreshActiveViewAsync` is the full refresh path for range changes:

- Loads events via `LoadEventsAsync` for the active view's range: the
  `_displayMonth` month range in Month view, the `_selectedDate` week
  range in Week view, or the day range in Day view. One repository
  call, one filter, one `_eventsByDate` store — no separate event
  pipeline per view.
- Renders the active main view (month grid + day headers, the week
  columns, or the day timeline), then the mini month and the
  selected-day panel.
- Updates the period header (month name, week range, or full day
  date).

### Date Navigation Paths

The user can change the focused date or loaded range through several
entry points. Each maps onto one of the two patterns above
(incremental `SelectDate` or full `RefreshActiveViewAsync`):

**Toolbar Previous/Next** is view-aware via `StepPeriod`. The rule is
that navigation advances by the primary temporal unit of the active
view — days step by days, weeks by weeks, months by months, years by
years. Views without a temporal unit disable the arrows so the
non-action is visible rather than a silent no-op.

- **Month view** — subtracts / adds one month to `_displayMonth`,
  then calls `RefreshActiveViewAsync`. Does not change
  `_selectedDate`.
- **Week view** — `StepWeek(±1)` moves `_selectedDate` by seven days
  and re-anchors `_displayMonth` to the selected date's month. Paging
  the week and moving the selected day are the same operation.
- **Day view** — `StepDay(±1)` moves `_selectedDate` by one day and
  re-anchors `_displayMonth`. Day View *is* the selected day.
- **Year view** — steps `_displayMonth` by ±12 months.
  `_selectedDate` is not touched; the highlight follows the user
  across years only if they drill down and back.
- **Agenda view** — arrows are disabled. Agenda is anchored to today
  and shows a fixed "upcoming" horizon; there is no temporal unit to
  page. `UpdateHeader` toggles `PrevMonthButton.IsEnabled` /
  `NextMonthButton.IsEnabled` so the disabled state is coincident
  with entering Agenda.

**Today** (`TodayButton_Click`) sets `_displayMonth` to the first day
of today's month, sets `_selectedDate` to today's local day key, then
calls `RefreshActiveViewAsync`.

**Mini-month date selection:**

- If the chosen date is outside `_displayMonth`,
  `OnMiniMonthDateSelected` sets both navigation fields and calls
  `RefreshActiveViewAsync`.
- If the chosen date is inside `_displayMonth`, it calls `SelectDate`.

**Mini-month header arrows** (`OnMiniMonthPrevMonth` /
`OnMiniMonthNextMonth`) change `_displayMonth` by ±1 month, then call
`RefreshActiveViewAsync`. Neither path changes `_selectedDate`.

After month-arrow navigation, `_selectedDate` may refer to a day
outside `_displayMonth`. In that state the main grid and mini month
render the new `_displayMonth`, no selected-day highlight appears
unless `_selectedDate` is visible in the rendered grid, and the
selected-day panel still labels `_selectedDate` but reads its events
from the currently loaded month data. This is existing behavior;
revisit deliberately when Week / Day navigation semantics are
redesigned.

### Mutation Flows

- **Tap-to-create** is the uniform gesture across views. Tapping empty
  space (a Month-view cell, a Week-view time slot, a Day-view time
  slot) opens `EventEditPopover` pre-filled with the chosen day and
  hour. Week and Day View share an `OnTimeSlotCreateRequested(day,
  hour)` callback.
- **Event edit** patterns on `EventRef.From(evt)`. Master / standalone
  → master-edit popover. Occurrence → `PromptEditScopeAsync`
  ContentDialog (This event / All events / Cancel) → either
  `EditMasterByIdAsync` (fetch master, route to master-edit popover)
  or `EditOccurrenceAsync` (occurrence-edit popover, save via
  `OverrideRepository.UpsertAsync`).
- **Event delete** branches on `IsOccurrence`. Non-recurring →
  existing two-step confirm in the popover. Occurrence → skips the
  two-step (the scope dialog is the confirmation) and shows a scope
  ContentDialog (Skip this occurrence / Delete entire series /
  Cancel). Skip appends `SeriesAnchorUtc` to the master's EXDATE list
  verbatim. Delete-series uses
  `EventRepository.DeleteAsync(occurrence.Id)` — works because
  `occurrence.Id == master.Id` by the identity contract.

Both flows enforce the persistence boundary via
`EventRepository.RefuseOccurrence` at the repository chokepoint.

Calendar create / edit / delete, event delete, and event dialog
completion reload the active view's data without changing navigation
state (`RefreshActiveViewAsync` for the existing range, not a new
range).

Calendar **visibility** toggles are different: visibility is a
client-side filter held only in `MainWindow._calendarVisibility` (a
runtime `Dictionary<Guid, bool>`, defaulted to visible on load, not
persisted to SQLite). A toggle re-runs `ApplyVisibilityFilter()` over
the already-loaded projection and re-renders the active view. **No
SQLite query is issued.** This is what the engine's Idle Cost Budget
calls for on a checkbox click, and what the test suite encodes as a
behavioral contract — see `TESTING.md` "Protect Performance
Contracts." Persisted visibility is not currently a product position;
if it becomes one, the persistence shape and the zero-query rule have
to be reconciled deliberately.

## Visual Design

Chronicle uses a dark "Fluent" visual language with a teal-green
accent, ported from the Claude Design handoff. It is applied in two
coordinated layers:

- **Standard WinUI controls** (dialogs, buttons, the event popover,
  toggles) pick it up from `App.xaml`: `RequestedTheme="Dark"` plus
  `SystemAccentColor` overrides set to the teal ramp. The window
  keeps the Mica backdrop.
- **Code-built renderers** read explicit tokens from `Helpers/Theme.cs`
  — the single source of truth for surface, hairline, text-ramp, and
  accent colors. `ColorHelper` exposes the accent and per-calendar
  helpers (`Soften` / `LightenForText` derive the filled event-pill
  background and text from a calendar's stored hex).
  `CalendarRenderHelper` applies the shared day-cell, day-number
  badge, and event-pill visuals so Month and Week stay consistent.

To retune the palette, edit `Theme.cs` (renderers) and the `App.xaml`
accent colors (standard controls). Renderer color constants no longer
exist as a parallel source.

Month-grid geometry (week count, Sunday-aligned cell dates) is
produced once by `DateHelpers.BuildMonthGrid` and consumed by both the
main grid and the mini month, so the two never drift apart.

### Theme System Status

The current Theme layer is a minimal, development-only abstraction
used to centralize renderer colors.

Constraints:

- Hard-coded dark mode is used during development.
- No full theming system (light / dark switching, user preferences)
  exists.
- Theme is not part of product architecture at this stage.
- It exists only to stabilize visual consistency during feature
  development.

It will be replaced by proper system-aware theme support during the
design-overhaul phase. Until then, theme changes are restricted to bug
fixes and rendering correctness.

## Rendering Constraints

These constraints are how the UI honors the engine's Idle Cost Budget
and zero-allocation read paths.

### Bounded Visuals Are Reused, Not Rebuilt

Several visual element counts are fixed by the calendar model:

- 24 hour rows in a timeline.
- 7 day columns in Week View.
- 42 cells in a month grid (6 weeks × 7 days).

These are built once and updated in place on subsequent renders (text,
brushes, highlight state). Event chips, whose count is unbounded, may
be rebuilt — preferably from a pool — but the surrounding scaffolding
is not.

Selection-only changes (`SelectDate` to a day already in the loaded
range) must not reallocate cell, gridline, gutter, or column visuals.
Reserve full rebuild for range changes (month / week / day moved).

### Read from `_eventsByDate`, Not from a Parallel Cache

Renderers either receive a slice as a parameter or read directly from
`_eventsByDate`. They do not keep a copy. Cache invalidation remains a
one-place problem.

### Scroll Offset Is View State

Where a view is scrollable and its user context is encoded in the scroll
position (Day View's 24-hour timeline, Agenda View's chronological list),
the `ScrollViewer` instance is held as a persistent field on the
renderer. Subsequent renders swap only `ScrollViewer.Content` —
`VerticalOffset` survives event CRUD, calendar visibility toggles, and
calendar-list mutations.

The rule matters most for views that have no `_selectedDate` fallback.
Month / Week / Day re-anchor to `_selectedDate` on refresh, so a stray
scroll reset is recoverable. Agenda has no selected date; its range is
anchored to today. Recreating its `ScrollViewer` on any refresh would
silently teleport the user back to today whenever anything changed.
Treat this as a UX invariant, not a micro-optimization.

### Local-Time Conversion Is Point-of-Use

`evt.StartTimeUtc.ToLocalTime()` per chip is fine. Pre-converted
projections, parallel "local time" caches, and `TimeZoneInfo` lookups
inside per-event loops are banned. See CORE_ENGINE.md "Performance
Constraints" for the full rule.
