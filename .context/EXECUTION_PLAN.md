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
- Recurrence Phase 1 (engine + skip-this-occurrence via EXDATE)
- Recurrence Phase 2A (occurrence overrides + scope picker + `EventRef`)
- Recurrence Phase 2B (wall-clock anchoring via `TimeZoneId`)

### UI CONSTRAINT (TEMPORARY)

A dev-only dark theme override is active to reduce visual fatigue.

This does NOT change execution priority:
1. Recurrence
2. Provider integrations
3. Design overhaul

No UI polish work should be introduced outside of Theme infrastructure stability.

## Current Milestone

Provider Integrations — Google Calendar (first adapter)

Recurrence is complete (Phase 1 + 2A + 2B). The "Definition of Ready
for Integrations" line below is satisfied; this milestone is the
first concrete delivery against the "Provider-Neutral Architecture"
decision in `DECISIONS.md` — Google supplies data, Chronicle's domain
model (RRULE, EXDATE, EventOverride, TimeZoneId) absorbs it without
shape change.

Planning happens in a separate round before sub-step implementation
begins. Open questions to settle first:

- OAuth + token storage (Windows DPAPI? Settings file? Per-account
  key?).
- Sync model (one-shot pull on connect vs. incremental sync token vs.
  delta query). Idle Cost Budget says background polling is opt-in,
  not ambient.
- Adapter shape — where the Google → domain mapping lives, and how
  the domain → Google write path handles round-trip identity (Google
  `iCalUID` vs. our `Event.Id`).
- Conflict resolution (Chronicle vs. Google last-write-wins, or
  user-facing diff).
- Display of provider-sourced calendars in the sidebar.

## Recently Completed: Recurrence Phase 2B

Delivered:

- `Event.TimeZoneId` (IANA string, nullable) — only meaningful with a
  recurrence rule. `Validate()` refuses strings that don't resolve
  via `TimeZoneInfo.FindSystemTimeZoneById`.
- Schema: `TimeZoneId TEXT NULL` on `Events`, fresh-install via
  `Schema.sql` plus guarded `ALTER TABLE` in
  `AppDatabase.MigrateRecurrenceColumns`. Existing rows stay NULL =
  legacy UTC walk (no auto-migration; DECISIONS.md "Anchor zone is
  authoritative").
- `EventEditPopover.GetDefaultRecurringTimeZoneId` — write-boundary
  normalization (Windows ID → IANA via `TryConvertWindowsIdToIanaId`,
  verify-as-IANA fallback, UTC + `Debug.WriteLine` for the rare
  unrecognized-zone case). Guarantees the "always IANA" invariant at
  the single point of origin.
- Create path defaults `TimeZoneId` for new recurring events; edit
  path preserves the master's existing `TimeZoneId` (newly-recurring
  events get the default).
- `WalkAnchorsForMaster` — single tz-aware dispatch shared by
  `RecurrenceExpander.Expand` and `ComputeEndUtc` (DECISIONS.md
  invariant #8). Bad-tz fallback degrades to legacy UTC walk for that
  one series with a debug log (invariant #7).
- `ResolveLocalForDst` — shift-forward at spring-forward gaps,
  matching Google / Apple / libical convention.
- `TzWalkPad` (1 hour, heuristic) — added to walk-termination and
  UNTIL gates when tz-aware; anchors temporarily past unpadded UNTIL
  are skipped without counting toward COUNT.
- `RecurrenceSelection` wrapper removed from `EventEditPopover`;
  picker returns `RecurrenceRule?` only, `buildEvent` computes
  `EndUtcCached` inline with the resolved `TimeZoneId` so the cached
  end is always computed under the same walk strategy the renderer
  will use.
- DECISIONS.md gains invariants #7 (bad-tz fallback) and #8 (single
  walk dispatch), plus an "Anchor zone is authoritative" subsection
  naming the no-auto-migration / preserve-on-edit position as a
  deliberate product decision.

## Recently Completed: Recurrence Phase 2A

Delivered:

- `EventOverrides` table with FK to `Events`, unique
  `(SeriesEventId, OccurrenceAnchorUtc)`, indexes on both lookup
  columns. New table only — no column-add ordering hazard.
- `OverrideRepository`: `UpsertAsync(EventRef.Occurrence, OverrideFields)`
  (SQLite ON CONFLICT DO UPDATE on the unique key); bulk
  `GetForSeriesAsync(IReadOnlyList<Guid>)` for the load pipeline;
  cascade helpers used by `EventRepository.DeleteAsync` and
  `CalendarRepository.DeleteAsync`.
- Expander gains an override-merge step joined to the existing
  per-anchor pipeline (after EXDATE filter, before merged-range gate).
  Walk-termination extended by `maxPastDisplacement` so overrides
  that pull future anchors back into the visible window are reached.
- `EventRef` discriminated identity primitive (`Master(Guid)` /
  `Occurrence(Guid SeriesId, DateTime AnchorUtc)`) at mutation
  boundaries; the wrapper-tripwire deal from Phase 1's "Tolerated
  ambiguity" landed here.
- Scope picker on edit (This event / All events); occurrence-edit
  popover (stripped form); save dispatch via `EventRef.From`.

## Recently Completed: Recurrence Phase 1

Delivered:

- RRULE storage (RFC 5545) + parser + value object.
- `RecurrenceExpander` (Daily / Weekly / Monthly / Yearly, INTERVAL,
  COUNT / UNTIL, weekly BYDAY); pure static; in-memory transient
  occurrences; no persistence.
- Expansion runs before `_eventsByDate` is built; renderers stay
  unaware of recurrence.
- EXDATE handling — "Skip this occurrence" via the delete dialog.
- `EventKey` read-side identity primitive; `EventRepository
  .RefuseOccurrence` chokepoint guard at the persistence boundary.
- Repeats picker in `EventEditPopover` (preset patterns: Daily /
  Weekly with day chips / Monthly on day-of-month / Yearly; Ends:
  Never / On date / After N).

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

After Phase 2B:

### Provider Integration Phase

- Google Calendar
- Outlook Calendar

### Design Overhaul

Replaces the dev-only dark-theme override (see DECISIONS.md).

## Definition of Ready for Integrations

Before Google integration begins:

- Month view complete
- Week view complete
- Day view complete
- Calendar management complete
- Recurrence complete (Phase 1 + Phase 2A + Phase 2B)