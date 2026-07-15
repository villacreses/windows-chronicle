# Notifications / Reminders

Chronicle delivers event reminders as Windows toast notifications that the
OS fires on schedule — including when Chronicle is closed. This document is
the living design record for the subsystem: the model, the rationale, the
spike findings that shaped it, the reconciliation contract, and the
deliberate deferrals. It is written alongside the implementation, not after
it.

## Implementation status

Local Baseline Phase C, on branch `feat/local-notifications`. Built in
units; this doc tracks which have landed. (The domain model was corrected
from a scalar column to a child entity before implementation proceeded —
see "Design history" below; the branch was rebuilt on the entity model.)

| Unit | Scope | Status |
|------|-------|--------|
| 1 | `Reminder` child entity + `Reminders` table + repository + `EventProjection.ReminderSchedule` + `ReminderOccurrence` | **landed** |
| 2 | "Remind me" picker in `EventEditPopover` (one reminder, master path) | **landed** |
| 3 | `IReminderScheduler` seam + toast adapter + reconciler wired to launch/CRUD | **landed** — pending live verification |
| 4 | Custom `Main` single-instancing + classic toast activation → deep-link | **landed** — pending live verification |
| 5 | Cross-doc updates (DECISIONS / DATA_MODEL / AGENT_ONBOARDING) | planned (AGENT_ONBOARDING done) |

Anything marked *planned* is designed here but not yet in the code.

**Pending live verification:** units 3–4 are code-complete and build clean,
but the packaged toast/activation behavior has not yet been verified against
the real OS. Before any further work, run
`.context/testing/MANUAL_VERIFICATION.md` MV-003 first (custom `Main` /
single-instance — launch-critical), then MV-001/002 (cold/warm activation
deep-link) and MV-004 (delivery). Iterate on any failures before unit 5.

## Architectural model

`Reminder` is a **composed child of the `Event` aggregate** — the same
shape as `EventOverride`: it exists only in the context of its event, is
cascade-deleted with it, and is never referenced from outside the
aggregate. It is a pure domain concept and carries **no notification
state** (no toast id, no last-fired, no snooze) — that state belongs to the
notification pipeline, never to the reminder.

The scheduler is a projection, end to end. Reminders are never aware of
Windows notifications.

```
Event
  └── Reminder*                (OffsetQuantity, OffsetUnit; pure domain)
        │
        │  expand events over a forward horizon (recurrence applied);
        │  join reminders by EventId (occurrence.Id == master.Id)
        ▼
EventProjection.ReminderSchedule(occurrences, remindersByEventId, window)
        │
        ▼
ReminderOccurrence*            (EventRef, ReminderId, FireTimeUtc, EventStartTimeUtc, Title)
        │
        │  IReminderScheduler.Reconcile(desired)      [planned, unit 3]
        ▼
Windows scheduled toasts       (disposable cache in one reserved OS group)
        │
        │  user clicks a toast                        [planned, unit 4]
        ▼
classic activation → decode EventRef → focus window + open the event
```

**Notifications are not a special subsystem — they are another projection
over the calendar model.** Expanded occurrences fan out to every consumer;
the reminder projection is one branch among peers:

```
Expanded occurrences
        │
        ├── UI projections        (_eventsByDate day-grouping, flat search list)
        ├── Reminder projection   (ReminderSchedule → ReminderOccurrence)
        └── (future consumers)
```

Reminders are the **third projection output shape** — after the
day-grouped render cache (`_eventsByDate`) and the flat search list —
consumed by the scheduler rather than a renderer. This is the evidence we
were watching for that `EventProjection`, not `_eventsByDate`, is the
stable seam.

## Why OS-scheduled toasts (`ScheduledToastNotification`)

Chronicle schedules concrete future toasts with the Windows notification
platform (`ToastNotifier.AddToSchedule`) and lets the OS deliver them. It
does **not** run an in-app timer, a polling loop, or a background service.

The decision rests on two facts, **not** on CPU/idle-cost minimization:

1. **The modern `AppNotificationManager` (Windows App SDK) cannot
   schedule.** It is a show-*now* API with no `AddToSchedule` equivalent.
2. **A reminder must fire while Chronicle is closed** — a product
   requirement; an unreliable reminder erodes trust more than a missing
   feature.

Together these make the real choice *OS-owned scheduling vs. building a
scheduler ourselves*. The modern APIs were evaluated and **rejected because
they do not provide future scheduling — not because they are less
performant.** The Idle Cost Budget *permits* explicitly-scheduled future
work; only continuous observation is banned. OS-scheduled toasts are the
purest form of permitted scheduling: the OS holds the schedule, Chronicle
keeps no handle or thread.

## Data model

`Reminder` is a child entity of the `Event` aggregate, stored in its own
table, mirroring `EventOverrides`:

```
Reminders
  Id             TEXT PRIMARY KEY
  EventId        TEXT NOT NULL          -- FK → Events(Id); cascade in the repo
  OffsetQuantity INTEGER NOT NULL       -- >= 0
  OffsetUnit     TEXT NOT NULL          -- 'Minutes' | 'Hours' | 'Days' | 'Weeks'
  index on EventId
```
```csharp
enum ReminderOffsetUnit { Minutes, Hours, Days, Weeks }

sealed class Reminder
{
    Guid Id;
    Guid EventId;
    int  OffsetQuantity;          // >= 0
    ReminderOffsetUnit OffsetUnit;
    // Derived, not stored. NO notification state on this type.
    int OffsetMinutes => OffsetQuantity * MinutesPer(OffsetUnit);
    void Validate();              // OffsetQuantity >= 0
}
```

**Table is created via `Schema.sql` (`CREATE TABLE IF NOT EXISTS`), like
`EventOverrides` — a new table, so no `ALTER TABLE` migration and no
column-add ordering hazard.** (The earlier scalar approach needed a guarded
`ALTER`; the entity model does not.)

**Preserve the user's representation, don't normalize it.** A reminder set
to "2 weeks before" is stored as `(2, Weeks)`, not `20160` minutes. The
scheduler derives minutes; the editor must never reconstruct intent from a
normalized integer (is `10080` "1 week" or "7 days"?). This is the same
principle Chronicle already applies by storing RRULE rather than a
materialized date list, and by keeping the recurrence anchor zone
authoritative rather than normalizing to UTC.

**Offset convention.** For the MVP a reminder fires `OffsetQuantity
OffsetUnit` *before* the event start (`FireTimeUtc = StartTimeUtc −
offset`). "Before" is a convention, not baked into the name — if
after-event reminders are ever needed, a direction is introduced then, not
speculatively now.

**Offsets are fixed durations, not calendar-relative periods.** The unit
set deliberately stops at `Weeks`. Minutes, hours, days, and weeks are
fixed spans, so `FireTimeUtc = StartTimeUtc − offset` is plain arithmetic
on a UTC instant. A month is *not* a fixed duration — "1 month before"
requires calendar arithmetic (variable month lengths, end-of-month
clamping) and is a different semantic concept: a *calendar-relative*
reminder. If Chronicle ever wants that, it enters the model as its own
concept, not as another `OffsetUnit` value. The exclusion of `Months` is a
position, not an oversight.

**Loading — side collection, not a nav property.** Reminders are loaded
explicitly where needed (the editor and the reconciler), keyed by
`EventId`, exactly as `EventOverride` is loaded by `SeriesEventId`. They are
**not** an always-populated `Event.Reminders` navigation property, because
(1) the render path (month/week/day/agenda/year) never shows reminders, so
auto-loading them on every range query is wasted work, and (2) a
sometimes-populated collection creates a "empty = none, or not loaded?"
ambiguity. The aggregate boundary lives in the schema (FK + cascade), not
in an always-loaded object graph.

**Occurrence inheritance is free.** Because an occurrence carries its
master's `Id` (`occurrence.Id == master.Id`), the projection's
`EventId`-keyed reminder lookup gives each occurrence its master's
reminders automatically. There is **no reminder code in
`RecurrenceExpander.CloneAsOccurrence`** — the expander stays
reminder-agnostic.

**Cascade.** `Reminders` rows are deleted with their event
(`EventRepository.DeleteAsync`) and with their calendar
(`CalendarRepository.DeleteAsync`), in the same transactions that already
cascade `EventOverrides` — the established pattern.

## The projection: `ReminderSchedule` → `ReminderOccurrence`

The reminder projection is the **Cartesian expansion of occurrences with
their event's reminder definitions**:

```
Occurrence  ×  Reminder  =  ReminderOccurrence
```

One occurrence with two reminders yields two intents; a three-occurrence
window of a series with one reminder yields three. This is why toast
identity is `(EventRef, ReminderId)` rather than `EventRef` alone — each
intent is one cell of the product, not one event.

`EventProjection.ReminderSchedule(expandedEvents, remindersByEventId,
windowStartUtc, windowEndUtc)` computes that product within a UTC window.
For each occurrence, for each of its reminders, it derives
`FireTimeUtc = StartTimeUtc − reminder offset`, keeps those in
`[windowStart, windowEnd]`, and emits them ordered by fire time (then
title). Pure — no DB, no platform calls.

```
ReminderOccurrence { EventRef Ref; Guid ReminderId; DateTime FireTimeUtc;
                     DateTime EventStartTimeUtc; string Title }
```

- **`EventRef`** is the occurrence identity (`Master(id)` for a standalone,
  `Occurrence(seriesId, anchorUtc)` for a recurring instance) — stable
  across reloads and rule-version changes.
- **`ReminderId`** discriminates *which* reminder on that occurrence,
  because one occurrence may carry several. A scheduled toast is therefore
  identified by **(occurrence anchor, ReminderId)** — a per-reminder,
  per-occurrence intent. The scalar model could not express this.

`GroupRemindersByEvent(reminders)` buckets a flat reminder list into the
`remindersByEventId` dictionary, mirroring `GroupOverridesBySeries`.

**Horizon and padding.** The reconciler expands events over a range padded
past `windowEndUtc` by the largest reminder offset in use, so an event that
starts just after the window but whose reminder fires inside it is not
missed. The horizon is a tunable **policy** (default ~60 days), not an
architectural invariant.

## Reminder identity across saves

`Reminder` has identity, so an event save must not needlessly churn it. The
editor's rule:

- **Offset unchanged → identity preserved.** Editing only an event's title
  (or any non-reminder field) and saving keeps the existing reminder's `Id`.
  A title-only edit does not mint a new reminder entity.
- **Offset changed → new identity.** A changed offset is a new intent, so it
  gets a new `Id`.

This is a **deliberate modeling decision**, not an artifact of the
`SetForEventAsync` replace-the-set write. `SetForEventAsync` still deletes
and re-inserts the row (the write is a set-replace), but the editor carries
the prior `Id` forward when the offset is unchanged, so the *entity's*
identity is stable across unrelated edits even though the *row* is rewritten.

**Deferred:** whether an offset *change* should mutate the existing reminder
in place (preserving `Id`) rather than mint a new one. This becomes
load-bearing only when something external keys on reminder identity —
specifically Stage 2's `ReminderState` (snooze/dismiss). Until then, "changed
offset = new identity" is correct and simplest: a snooze set against an
old offset should not silently apply to a re-timed reminder anyway. Revisit
when Stage 2 lands.

## Editor scope vs. domain capability

The editor and the domain are **intentionally different**, and the gap is
staging, not a constraint:

| | Editor (MVP) | Domain |
|--|--------------|--------|
| Count | zero or one reminder | a collection (0..N) |
| Value | fixed presets (10 min, 1 hour, 1 day, 2 weeks, …) | any `(OffsetQuantity, OffsetUnit)` |

The single-reminder preset dropdown is a **UI limitation, not a domain
constraint**. The persistence model, the projection, and the scheduler all
already handle N reminders per event with arbitrary expressed offsets (the
projection tests exercise the multi-reminder case directly). The editor can
grow into an "Add reminder" collection UI — repeated `(quantity, unit)` rows
— with **no persistence or projection redesign**: only the popover's
read/seed of the reminder set changes. Nobody should read the dropdown as
evidence that Chronicle events carry at most one reminder.

## Reconciliation contract

The OS schedule is a **disposable cache** of the `ReminderOccurrence`
projection, never a second source of truth. Reconciliation converges the OS
schedule toward the projection's current output.

- **One reserved group.** All reminder toasts live in a single OS group,
  `chronicle-reminders`. The whole group is the disposable unit — not
  per-calendar, not per-horizon — because reconciliation always recomputes
  the full horizon set from Chronicle state.
- **Clear-and-rebuild.** Each reconcile clears the group and re-adds the
  desired set. Required, not merely convenient: the spike proved
  `AddToSchedule` is purely additive (see *Spike findings*), so stale
  toasts must be removed explicitly. A surgical diff is a later
  optimization that does not change the contract.
- **Reconciliation is the sole mutator of the OS schedule.** The editor,
  repositories, and event services mutate *Chronicle state* and then
  *request a reconcile*; only the scheduler calls `AddToSchedule` /
  `RemoveFromSchedule`, always by rebuilding from the derived
  `ReminderSchedule`. Mirrors the projection model — derived state is
  rebuilt from source, never hand-edited.
- **Toast identity = (occurrence anchor, ReminderId).** The short OS
  tag/group is only for group-clear; the full identity (`EventRef` +
  `ReminderId`) travels in the launch arguments.
- **Bounded schedule (policy).** The scheduler reconciles the desired set
  *subject to an implementation-defined maximum*, to avoid exhausting the
  platform's scheduled-toast limit. When the desired set exceeds that
  maximum, the **earliest-firing** reminders are preferred. This is part of
  the seam's contract, not an implementation accident — the current value is
  1000 (well under the ~4096 platform ceiling), tunable without changing the
  contract. The horizon keeps realistic datasets far below it.
- **Reconcile triggers:** app launch, resume, event/calendar CRUD (which
  includes reminder edits — the editor mutates reminders then requests a
  reconcile). **Not** calendar-visibility toggles — visibility is ephemeral
  view state; a reminder on a hidden calendar still fires.
- **Partial-failure repair.** If a reconcile is interrupted mid-rebuild, no
  special handling is needed: the next reconcile (launch/resume/mutation)
  recomputes the full desired set and repairs it.

Recurring mutations converge for free: changing a rule or start time
changes the walk anchors, hence the `EventRef` keys, so old intents leave
the desired set and new ones enter it.

### As implemented (unit 3)

- **Two layers, two single-responsibilities.** `MainWindow.ReconcileRemindersAsync`
  is the single *compute* site — it runs its own load over the horizon
  (`GetInRangeAsync` → `ExpandRecurrences` → `GroupRemindersByEvent` →
  `ReminderSchedule`) and hands the `ReminderOccurrence[]` to the scheduler.
  `ScheduledToastReminderScheduler.ReconcileAsync` is the single *OS-mutate*
  site — clear the group, add the desired set. The scheduler receives a fully
  projected list and knows nothing of events, recurrence, or repositories.
- **Own horizon load, not `_eventsByDate`.** Reminders need ~60 days ahead
  (`ReminderHorizon`), not the active view's month, so the reconciler is an
  independent projection consumer with its own range — structurally the same
  as Search. The event query is padded (`ReminderHorizonPad`, 31 days) past
  the horizon so an event starting just beyond it, whose reminder fires
  inside it, is still expanded.
- **The reconciler is one consumer of a generic chokepoint.**
  `AfterDataMutationAsync` is a semantically generic "persisted calendar data
  changed" lifecycle event (invalidate + refresh); `ReconcileRemindersAsync`
  is one downstream consumer hung off it, after the view refresh. The
  chokepoint is deliberately *not* "the place reminders happen" — future
  consumers (provider-sync bookkeeping, search indexing) would attach to the
  same event. Launch reconciles because startup funnels through the same
  chokepoint via `ReloadCalendarsAndRefreshAsync`.
- **Bounded schedule.** `ScheduledToastReminderScheduler` realizes the
  contract's bound with a max of 1000 (see the reconciliation contract's
  "bounded schedule" clause).

### Failure and observability contract

A reminder-scheduling failure must **never** abort the mutation that
triggered it. The seam surfaces failures to the reconcile orchestration
(the adapter does not swallow them internally); `ReconcileRemindersAsync`
catches and logs, and the app stays fully usable. The guarantee to the user:

> The application remains usable regardless of scheduling failures. A failed
> reconcile leaves the previous OS schedule in place (or partially updated);
> the **next** reconcile — the next launch, resume, or reminder-affecting
> mutation — recomputes the full desired set and repairs it. Failures are at
> minimum visible in the debug log.

This is deliberately the *floor*, not the ceiling. If Chronicle ever grows a
diagnostics or sync-status surface, reminder-scheduling failures are a
natural feed into it — the seam already routes failures to a single place
where such a surface could observe them, rather than hiding them in the
adapter.

## Activation

Validated by the spike: a clicked scheduled toast activates through the
**classic** path — `AppInstance.GetCurrent().GetActivatedEventArgs()`
returns `ExtendedActivationKind.ToastNotification` with the launch argument
intact. `AppNotificationManager.NotificationInvoked` is not involved, and no
manifest COM-activator declaration is required for a packaged app.

**Activation is translation, not logic.** Its sole job is to turn an OS
activation into the *existing* Chronicle navigation model. It knows nothing
of scheduling, reminders, or recurrence expansion. The flow is deliberately
small:

```
Toast activation
   → decode ReminderActivationPayload  (EventRef + ReminderId)
   → resolve the event's day           (occurrence anchor, or a loaded master's start)
   → focus the existing instance / launch
   → navigate (SelectDate + Day view)
   → open the event                     (found by identity in the loaded projection)
```

- **Payload.** The launch arguments encode `EventRef` + `ReminderId` (via
  `ReminderActivationPayload`); activation decodes them. The short OS tag is
  never used for reconstruction. A malformed argument decodes to null and
  activation degrades to "just focus the window."
- **No recurrence in activation.** The target event is found by matching the
  decoded `EventRef` against the projection the normal load pipeline already
  expanded for that day (occurrence: `Id` + `SeriesAnchorUtc`; master:
  `Id`, no anchor). Activation never expands anything itself. Best-effort: if
  the event was deleted since the toast was scheduled, the user lands on the
  day with nothing to open.
- **Single-instancing (keeper).** A clicked toast focuses the existing window
  rather than spawning a second one. `AppInstance` redirection lives in a
  **custom `Main`** (`Program.cs`, with the `DISABLE_XAML_GENERATED_MAIN`
  define replacing the generated entry point), *before* `Application.Start` —
  the primary instance registers a key and routes redirected activations into
  `App.OnRedirectedActivation`; a secondary instance redirects and exits.
  Doing the redirect in `OnLaunched` deadlocks the XAML STA thread with a
  `COMException` (learned during the spike), which is why it must be in Main.
- **Two entry points, one handler.** A cold toast-launch is handled in
  `App.OnLaunched` (via `GetActivatedEventArgs`); a warm toast-click (app
  already open) arrives through the primary instance's `Activated` event and
  `App.OnRedirectedActivation`. Both funnel into one `HandleActivation`.

## Spike findings (empirical record)

A throwaway spike validated the activation mechanics against the real OS
before any reconciler was built. Removed from the tree; its conclusions:

1. **Activation path = classic.** `GetActivatedEventArgs()` delivers
   `ToastNotification` kind with the argument; modern `NotificationInvoked`
   does not fire.
2. **Payload round-trips.** `occ|<guid>|<ticks>` reconstructed to
   `EventRef.Occurrence` exactly, including a UTC anchor.
3. **Cold launch works.** A toast click launches the closed app with args.
4. **`AppNotificationManager.Register()` fails** in the packaged app
   ("No COM servers are registered") without a manifest COM-activator —
   **and is not needed.** Classic scheduling + activation work with no
   manifest change. This *simplified* the design.
5. **`AddToSchedule` is purely additive.** Same tag/id scheduled twice
   yields two toasts; the OS does not dedupe. Hence clear-and-rebuild.

## Idle Cost Budget compliance

- No timers, no polling loops, no background threads. The OS is the
  scheduler.
- Reconciliation is event-driven (launch, resume, CRUD) and bounded
  (O(reminders in horizon)); never ambient.
- The bounded horizon caps how many concrete toasts are registered, well
  under the platform's scheduled-toast limit.

## Horizon policy and the closed-app contract

The scheduling horizon (default ~60 days) is a **policy**, not an
invariant. The contract:

> Chronicle guarantees reminders within the horizon as of the most recent
> reconciliation — not indefinitely into the future. A reminder whose fire
> time is beyond the horizon is scheduled once reconciliation next runs
> (app launch, resume, or a reminder-relevant mutation).

Canonical example: a yearly birthday with a 1-day reminder, on a machine
where Chronicle stays closed for six months, has no OS-scheduled toast for
that occurrence until the app next opens and reconciles. Acceptable for the
MVP; stated so it is a deliberate position, not a surprise.

## Deliberate deferrals

- **Editor exposes one reminder; the domain supports many.** See "Editor
  scope vs. domain capability" above — the single-reminder dropdown is a UI
  limitation, not a domain constraint, and growing it is a UI change with no
  persistence or projection redesign.
- **No snooze / dismiss.** Stage 1 is fire-only. Snooze/dismiss (a
  `ReminderState` table keyed on `(EventRef.Occurrence, ReminderId)`,
  interactive toast buttons, background activation) is Stage 2, likely a
  separate branch. Note this state lives *outside* `Reminder` — the reminder
  entity stays pure.
- **No per-occurrence reminder overrides.** An occurrence inherits the
  series reminders. The occurrence-scoped editor omits the picker.
- **No default reminder.** New events are silent unless the user opts in.
- **No email/other reminder methods.** Toast only.

## Design history: scalar → child entity

The subsystem was first designed with a scalar `Events.ReminderMinutesBefore`
column, on the grounds that a single reminder is a derived scalar and a
table would be speculative structure. That was reconsidered and **reversed
before the model shipped**, for three reasons:

1. **Preserving the user's representation already breaks the scalar.**
   Storing "2 weeks" faithfully requires structured `(Quantity, Unit)` data;
   normalized minutes lose intent. Once the value is structured, a child
   collection is a small further step.
2. **Single-reminder is an artificial constraint, not a faithful model.**
   Multiple reminders are table stakes for a real calendar (Chronicle's
   stated goal). The scalar encodes "≤ 1 reminder" as a structural
   invariant the domain does not actually have.
3. **The expensive migration is the API reshape, not the SQL.** Moving
   every read site from `event.ReminderMinutesBefore` (scalar) to
   `event.Reminders` (collection) only gets costlier as more units build on
   it. The correction was made at the cheapest moment — units 1–2, on an
   unmerged branch.

`Reminder` is modeled as a child entity of the `Event` aggregate (like
`EventOverride`), **not** an independent root: cascade-owned, never
referenced externally, loaded as a side collection. This keeps "first-class
domain concept" from sprawling into an independent service layer.

## Assembly boundary and the platform principle

The goal is **not to hide Windows APIs**. Chronicle is a native Windows
application and uses Windows APIs freely wherever they naturally fit. The
principle that governs where they go is about *responsibility*, not
avoidance:

- Windows APIs are used freely where they belong.
- Each subsystem owns the platform APIs associated with its responsibility.
- Platform concerns don't reshape the domain model or bleed across subsystem
  boundaries.

Applied here: pure parts live in `Chronicle.Core` and are unit-tested — the
`Reminder` entity + `Validate`, the schema, the `ReminderRepository` (bulk
load by event, cascade helpers), `EventProjection.ReminderSchedule` producing
`ReminderOccurrence`, and the `ReminderActivationPayload` codec (pure identity
serialization, shared by the scheduler and activation). These carry no
notification concepts. The notification subsystem's platform APIs
(`Windows.UI.Notifications`) live in `src/Chronicle` in exactly one place —
`ScheduledToastReminderScheduler`, behind the `IReminderScheduler` seam. That
`ScheduledToastReminderScheduler` depends directly on `Windows.UI.Notifications`
is correct: that is where the dependency belongs. What the boundary prevents
is the reverse — toast concepts leaking back into `Reminder` /
`ReminderOccurrence`, or unrelated code manipulating scheduled toasts. The
seam is a responsibility boundary, not a Windows firewall.
