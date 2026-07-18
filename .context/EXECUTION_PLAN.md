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
  Extracted to Chronicle.Core"). Layers 1–5 landed (~194 tests): pure
  domain, recurrence invariants + DST, SQLite repositories, projection +
  cache (`EventProjection`, incl. the explicit per-day ordering contract
  `OrderForDay`), timeline packing (`TimelinePacker`), and the
  recurrence-picker rule construction (`RecurrencePickerModel` /
  `RecurrenceTimeZone`) — the app-layer pieces after extracting those pure
  helpers out of `MainWindow` / `TimelineRenderHelper` / `EventEditPopover`
  into `Chronicle.Core`. Plus a coverage-gap pass and an event-pipeline
  integration test. All Layer 5 targets are now covered.
- Local Baseline Phase A — all-day polish + notes/description field
  (editor toggle + Notes box, occurrence-override support, selected-day
  description peek; all-day constrained to single-day, see BACKLOG.md).
  Merged via PR #16.
- Local Baseline Phase B — Search (`SearchCandidatesAsync` LIKE +
  override union → `EventProjection.SearchOccurrences`; header
  AutoSuggestBox + results flyout; PR #15), Agenda view (today → end of
  next month, anchored not paged; PR #17), Year view (4×3 density-tinted
  mini-months, tap-to-drill; PR #17). All reuse the `EventProjection`
  pipeline; suite grew to ~214.
- Local Baseline Phase C — Reminders. `Reminder` child entity of the
  `Event` aggregate, projected by `EventProjection.ReminderSchedule`
  into `ReminderOccurrence[]`, reconciled (clear-and-rebuild) by
  `ScheduledToastReminderScheduler` into OS-scheduled toasts that fire
  even when Chronicle is closed; classic-path activation deep-links back
  to the event via a custom single-instancing `Main`. Full contract in
  `architecture/REMINDERS.md`; rationale in DECISIONS.md. Suite grew to
  250; all four manual OS-integration verifications
  (`testing/MANUAL_VERIFICATION.md` MV-001–004) pass. Merged to main via
  PR #18 — **Local Baseline Completion is done.**

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
6. **Reminders** — scheduled, reliable, persistence-aware.

### Status (2026-07-18): Local Baseline Completion is DONE

All six features are shipped and merged to `main`: PRs #15, #16, #17
(Phases A/B — see Completed above) and **PR #18 (Phase C — Reminders)**.
Their open questions were settled as follows: search is SQLite `LIKE`
over Title/Description with an in-SQL `EventOverrides` union (FTS5
deliberately rejected — rationale and revisit triggers in BACKLOG.md
"Search backend upgrade"); Agenda is anchored today→end-of-next-month
with Prev/Next disabled; Year is a 4×3 density-tinted grid with
tap-to-drill; Reminders is OS-scheduled toasts with a rolling-horizon
reconciler (full contract `architecture/REMINDERS.md`, rationale
DECISIONS.md). Deferred reminder work (snooze/dismiss, multi-reminder
editor UI, per-occurrence overrides, default reminder) is tracked in
BACKLOG.md "Reminders."

Suite is at 250 tests; all four Reminders manual OS-integration
verifications pass (`testing/MANUAL_VERIFICATION.md` MV-001–004).

**Next up: the Local Baseline Addendum below, then the Provider
Integration Phase.**

### Local Baseline Addendum — Reminder Correctness (2026-07-18)

A post-ship surface-area review of the reminder subsystem (classified
into blockers / provider prerequisites / the Reminder UX Parity area /
non-goals — see DECISIONS.md "Reminders: Post-Ship Audit Positions" and
BACKLOG.md "Reminders — UX Parity") found exactly **two** items that
extend the Local Baseline. Both are internal-consistency fixes, not features, and
they are the only items permitted to extend this milestone:

1. **Editor preservation.** The 0..1-preset editor currently has a
   destructive write path over the 0..N domain model: save replaces the
   event's whole reminder set, and a non-preset offset displays as "No
   reminder." Fix: preserve reminders the editor cannot represent, and
   never render a non-preset offset as "No reminder." Not the
   multi-reminder editor (BACKLOG) — preservation only.
2. **Bounded offsets.** Enforce the decided maximum reminder offset
   (4 weeks — see DECISIONS) at the write boundary, converting the
   fixed 31-day scheduling pad from a coincidence into an
   invariant-backed constant.

Each lands with its tests (editor logic likely extracts to Core per the
Layer-5 pattern; the pad gets a horizon-boundary test) and updates
REMINDERS.md in the same change, per the drift rule.

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

The Local Baseline is complete except the Addendum above, which is the
active work. After it: provider integration, with Reminder UX Parity as
a named capability area scheduled deliberately around it (see below).

### Provider Integration Phase

- Google Calendar (first adapter)
- Outlook Calendar

Open questions, to resolve as this phase starts:

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
- Settings storage (this phase needs it for accounts/sync prefs
  regardless; reminder settings in BACKLOG ride on it later).

Reminder contracts the adapter is designed against — **decided, not
open** (rationale in DECISIONS.md "Reminders: Post-Ship Audit
Positions" → "Provider-era reminder contracts"):

- Preserve `Reminder.Id` across pulls by `(EventId, offset)` matching.
- Bulk writers reconcile the reminder schedule once per batch, not per
  row.
- Outlook export keeps the earliest-firing reminder (Graph models one).
- Imported minutes re-express as the largest exact unit; provider
  round-trips may normalize the expressed unit.
- Local-only reminders on provider events are tracked by provenance;
  write-back neither pushes nor clobbers them.
- One deliberately open item, resolved at Google-adapter design time:
  representing `reminders.useDefault` (materialize-at-import vs. an
  explicit flag).

### Reminder UX Parity

A coherent capability area, not a grab-bag: the work that takes
reminders from "engine complete" to the experience users expect from a
modern Windows calendar app. Product direction and admission test:
DECISIONS.md "Reminders: Calendar Parity, Not Notification Platform."
Scope: BACKLOG.md "Reminders — UX Parity" (editor parity, notification
parity — snooze/dismiss, trust parity).

Sequencing: this area does **not** gate provider integration and is not
a single monolithic phase — slices are scheduled deliberately. Two
natural orderings to honor when slicing: default-reminder and all-day
fire-time settings ride on the settings storage the provider phase
introduces, and the multi-reminder editor builds on the Addendum's
preservation work (which extracts the editor's reminder logic to a
testable seam).

### Design Overhaul

Replaces the dev-only dark-theme override (see DECISIONS.md).

## Definition of Ready for Integrations

Before Google integration begins:

- Month view ✓
- Week view ✓
- Day view ✓
- Calendar management ✓
- Recurrence (Phase 1 + 2A + 2B) ✓
- All-day event polish (Phase A) ✓
- Notes / description field (Phase A) ✓
- Search (Phase B) ✓
- Agenda view (Phase B) ✓
- Year view (Phase B) ✓
- Reminders (Phase C) ✓ — merged via PR #18; see "Status" above
- Local Baseline Addendum — reminder correctness (editor preservation +
  bounded offsets; see "Status" above) — **pending**

**All original items satisfied; the Addendum is the remaining gate
before Google integration begins.**
