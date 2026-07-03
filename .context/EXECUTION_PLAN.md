# Execution Plan

## Current Objective

Make Chronicle's local calendar experience complete, trustworthy, and
fully capable before any provider integration work begins. Sync
multiplies complexity; the local foundation must be solid before
anything is built on top of it.

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
- Test infrastructure: `Chronicle.Core` extraction + `src/`-`tests/`
  layout + xUnit project (see `TESTING.md` / DECISIONS.md "Domain
  Extracted to Chronicle.Core"). Layers 1–3 landed (pure domain,
  recurrence invariants + DST, SQLite repositories; ~134 tests). Layers
  4–5 await the `MainWindow` projection-helper extraction.

### UI CONSTRAINT (TEMPORARY)

A dev-only dark theme override is active to reduce visual fatigue.

This does NOT change execution priority:
1. Local baseline completion
2. Provider integrations
3. Design overhaul

No UI polish work should be introduced outside of Theme infrastructure
stability.

## Current Milestone: Local Baseline Completion

Six features that finish the local experience before provider
integration begins. Each is something a user expects an offline
calendar to do — not a nice-to-have.

**The six features:**

1. **All-day event polish** — create / edit + correct rendering across
   Month, Week, and Day views.
2. **Notes / description field** — present in the editor, popover, and
   selected-day panel.
3. **Search** — find events by title, description, calendar, and
   recurrence instances.
4. **Agenda view** — chronological upcoming-events list.
5. **Year view** — at-a-glance year overview.
6. **Notifications / reminders** — scheduled, reliable,
   persistence-aware.

### Recommended sequencing

Three phases, ordered foundation-first and trust-last:

**Phase A — Event content completeness:**

- All-day event polish
- Notes / description field

Both touch the editor form, the popover, and the renderers; doing
them together means one editor revision, not two. Data-model support
likely already exists (Events table carries `IsAllDay` and
`Description`); the question per item is how much UI / rendering work
remains. Phase A begins with an audit of the current state.

**Phase B — Discovery:**

- Search (data layer + UI surface)
- Agenda view (new renderer, reuses event pipeline)
- Year view (new renderer)

Search first — most user-visible value, and its storage / index
decisions inform the agenda and year queries. Both new views reuse
`_eventsByDate` and `DateHelpers`; per "View Switching Does Not
Query" they should issue zero SQLite queries when the loaded range
hasn't changed.

**Phase C — Reliability:**

- Notifications / reminders

Notifications go last. The subsystem is the largest single piece —
Windows toast scheduling, reminder persistence, snooze / dismiss
state, a new schema, and a NOTIFICATIONS.md doc that will be created
when this phase begins. An unreliable reminder erodes trust more
than a missing one; shipping reminders on top of a polished calendar
means fewer moving parts to debug if a reminder misfires. The Idle
Cost Budget forbids polling, so registration with the platform's
notification scheduler is the only acceptable shape — not an in-app
timer.

### Open questions to settle as each phase begins

- **Phase A** — audit the current all-day path: does the model carry
  `IsAllDay`? Does the editor expose the toggle? Where do renderers
  surface it (month chip variant, week all-day band, day all-day
  band)? Notes similarly: schema column likely exists; is the gap
  UI-only?
- **Phase B** — search storage and query shape (SQLite `LIKE` vs FTS5;
  bulk-write rules apply if FTS5 needs a rebuild). Agenda view's range
  (next-N-events vs next-N-days). Year view interaction model
  (tap-to-drill semantics; how event density gets rendered).
- **Phase C** — Windows toast scheduling (registered with the system,
  not in-app polling). Reminder persistence (column on `Events` vs
  separate table — interacts with `EventOverride` for per-occurrence
  reminder edits). Snooze / dismiss state and how it survives app
  restarts.

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

Deferred until the Local Baseline milestone is complete.

### Provider Integration Phase

- Google Calendar (first adapter)
- Outlook Calendar

Open questions, parked until Phase C completes:

- OAuth + token storage (Windows DPAPI? Settings file? Per-account
  key?).
- Sync model (one-shot pull on connect vs. incremental sync token vs.
  delta query). The Idle Cost Budget says background polling is
  opt-in, not ambient.
- Adapter shape — where the Google → domain mapping lives, and how
  the domain → Google write path handles round-trip identity (Google
  `iCalUID` vs. our `Event.Id`).
- Conflict resolution (Chronicle vs. Google last-write-wins, or
  user-facing diff).
- Display of provider-sourced calendars in the sidebar.

### Design Overhaul

Replaces the dev-only dark-theme override (see DECISIONS.md).

## Definition of Ready for Integrations

Before Google integration begins:

- Month view ✓
- Week view ✓
- Day view ✓
- Calendar management ✓
- Recurrence (Phase 1 + 2A + 2B) ✓
- All-day event polish (Phase A)
- Notes / description field (Phase A)
- Search (Phase B)
- Agenda view (Phase B)
- Year view (Phase B)
- Notifications / reminders (Phase C)
