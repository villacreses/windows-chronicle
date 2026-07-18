# Reminders

Chronicle delivers event reminders as Windows toast notifications that the
OS fires on schedule — including when Chronicle is closed. This document
owns the reminder subsystem's contract: the model, the projection, the
reconciliation contract, activation, and the subsystem's scope boundaries.

## Vocabulary

Three concepts, three words — never interchangeable (canonical rationale in
DECISIONS.md "Reminder → Notification → Toast Vocabulary"):

- **Reminder** — a domain entity tied to an event ("remind me 10 minutes
  before"). The cause.
- **Notification** — a user-facing message Chronicle surfaces. The effect a
  reminder triggers.
- **Toast** — one Windows mechanism for presenting a notification.

A reminder *is not* a notification, just as it is not a toast. Reminders
are today's only notification producer; the delivery layer
(`src/Chronicle/Notifications/`) is named for its responsibility —
delivering notifications — not for its current sole consumer.

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
        │  IReminderScheduler.Reconcile(desired)
        ▼
Windows scheduled toasts       (disposable cache in one reserved OS group)
        │
        │  user clicks a toast
        ▼
classic activation → decode EventRef → focus window + open the event
```

**Reminders are not a special subsystem — they are another projection over
the calendar model.** Expanded occurrences fan out to every consumer; the
reminder projection is one branch among peers:

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

*Decision rationale — including the app-owned-scheduler alternative — is
recorded canonically in DECISIONS.md "Reminders: OS-Scheduled Toasts,
Reminder as a Child Entity." This section states the resulting position.*

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
title, then reminder id). Pure — no DB, no platform calls.

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
past `windowEndUtc` by a **fixed pad** (`ReminderHorizonPad`, 31 days), so
an event that starts just after the window but whose reminder fires inside
it is not missed. The fixed pad covers every offset the editor can produce
(largest preset: 2 weeks) but is **not** derived from the largest offset in
the data — a reminder whose offset exceeds the pad, on an event starting
beyond `horizon + pad`, would be silently missed. Unreachable through the
editor today; becomes reachable the moment another writer (provider sync,
import) can persist larger offsets. The horizon is a tunable **policy**
(default ~60 days), not an architectural invariant.

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

**Open question:** whether an offset *change* should mutate the existing
reminder in place (preserving `Id`) rather than mint a new one. This becomes
load-bearing only when something external keys on reminder identity — the
deferred snooze/dismiss `ReminderState` (BACKLOG.md "Reminders"). Until
then, "changed offset = new identity" is correct and simplest: a snooze set
against an old offset should not silently apply to a re-timed reminder
anyway. Revisit when snooze/dismiss is built.

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
  desired set. Required, not merely convenient: `AddToSchedule` is purely
  additive — the same tag/id scheduled twice yields two toasts; the OS does
  not dedupe — so stale toasts must be removed explicitly. A surgical diff
  is a later optimization that does not change the contract.
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
- **Reconcile triggers:** app launch and event/calendar CRUD (which
  includes reminder edits — the editor mutates reminders then requests a
  reconcile). **Not** calendar-visibility toggles — visibility is ephemeral
  view state; a reminder on a hidden calendar still fires. There is **no
  time-based or resume trigger**: a session left open without data
  mutations does not re-reconcile (see "Horizon policy" below for the
  consequence).
- **Partial-failure repair.** If a reconcile is interrupted mid-rebuild, no
  special handling is needed: the next reconcile (launch or mutation)
  recomputes the full desired set and repairs it.

Recurring mutations converge for free: changing a rule or start time
changes the walk anchors, hence the `EventRef` keys, so old intents leave
the desired set and new ones enter it.

### Reconciler shape

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
> the **next** reconcile — the next launch or reminder-affecting mutation —
> recomputes the full desired set and repairs it. Failures are at minimum
> visible in the debug log.

This is deliberately the *floor*, not the ceiling. If Chronicle ever grows a
diagnostics or sync-status surface, reminder-scheduling failures are a
natural feed into it — the seam already routes failures to a single place
where such a surface could observe them, rather than hiding them in the
adapter.

## Activation

A clicked scheduled toast activates through the **classic** path —
`AppInstance.GetCurrent().GetActivatedEventArgs()` returns
`ExtendedActivationKind.ToastNotification` with the launch argument intact,
for both cold launches and warm clicks. The modern
`AppNotificationManager.NotificationInvoked` is not involved, and no
manifest COM-activator declaration is required for a packaged app
(`AppNotificationManager.Register()` in fact *fails* without one — "No COM
servers are registered" — and is simply not needed on the classic path).

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
  `COMException`, which is why it must be in Main.
- **Two entry points, one handler.** A cold toast-launch is handled in
  `App.OnLaunched` (via `GetActivatedEventArgs`); a warm toast-click (app
  already open) arrives through the primary instance's `Activated` event and
  `App.OnRedirectedActivation`. Both funnel into one `HandleActivation`.
- **Decode the argument on the activation's own thread, not across the
  marshal.** The primary instance's `Activated` event fires on a **background
  thread**. The raw `AppActivationArguments` (and its `.Data`
  `ToastNotificationActivatedEventArgs`) is a WinRT object with apartment
  affinity: marshaling it to the UI thread via `DispatcherQueue.TryEnqueue`
  and *then* reading `.Data` throws `COMException` (RPC_E_WRONG_THREAD,
  0x8001010E). So `OnRedirectedActivation` pulls the plain launch-argument
  string out on the background thread (`ExtractToastArgument`) and marshals
  only that `string?`; `HandleActivation` takes the string, never the WinRT
  args. The cold path (`OnLaunched`) extracts on the UI thread, where
  `GetActivatedEventArgs()` was already valid — which is why the cold path
  never exhibits this failure: both ends are the UI thread.
- **Focus needs `SetForegroundWindow`, not just `Activate()`.** The warm path
  runs on the *primary* instance, which does not hold foreground rights, so a
  background-owned window will not raise from `AppWindow.Show()` /
  `Window.Activate()` alone — Windows blocks that. `FocusWindow` restores the
  window if minimized (`IsIconic` → `ShowWindow(SW_RESTORE)`) and calls the
  Win32 `SetForegroundWindow` before `Activate()`. Classic `[DllImport]` on
  `user32` (not `[LibraryImport]`, whose generated `bool` marshalling would
  force `AllowUnsafeBlocks` across the whole app project for three trivial
  calls; the app project isn't AOT-marked). The toast-redirect path carries
  enough foreground rights for a plain `SetForegroundWindow` to take; the
  `AttachThreadInput` bypass is not needed.

These platform behaviors cannot be exercised by the automated suite (they
live outside the process the test host can drive); their reproduction steps
are `.context/testing/MANUAL_VERIFICATION.md` MV-001 through MV-004.

## Idle Cost Budget compliance

- No timers, no polling loops, no background threads. The OS is the
  scheduler.
- Reconciliation is event-driven (launch, CRUD) and bounded
  (O(reminders in horizon)); never ambient.
- The bounded horizon caps how many concrete toasts are registered, well
  under the platform's scheduled-toast limit.

## Horizon policy and the closed-app contract

The scheduling horizon (default ~60 days) is a **policy**, not an
invariant. The contract:

> Chronicle guarantees reminders within the horizon as of the most recent
> reconciliation — not indefinitely into the future. A reminder whose fire
> time is beyond the horizon is scheduled once reconciliation next runs
> (app launch or a reminder-relevant mutation).

Canonical example: a yearly birthday with a 1-day reminder, on a machine
where Chronicle stays closed for six months, has no OS-scheduled toast for
that occurrence until the app next opens and reconciles. Acceptable for the
MVP; stated so it is a deliberate position, not a surprise.

The same aging applies to a **long-running open session**: because there is
no time-based or resume trigger, an app left open without any data
mutation keeps the schedule as of its last reconcile, and the effective
coverage window shrinks as time passes. With a 60-day horizon this needs
roughly two months of mutation-free uptime before anything is missed —
but Chronicle is designed to be left open, so this is a real (if slow)
gap, not a theoretical one.

## Scope boundaries

What the subsystem deliberately does not do. (The larger deferrals are
tracked in `BACKLOG.md` "Reminders"; the scalar-vs-entity design rationale
is in DECISIONS.md "Reminders: OS-Scheduled Toasts, Reminder as a Child
Entity.")

- **Editor exposes one reminder; the domain supports many.** See "Editor
  scope vs. domain capability" above — the single-reminder dropdown is a UI
  limitation, not a domain constraint, and growing it is a UI change with no
  persistence or projection redesign.
- **Fire-only — no snooze / dismiss.** When built, that state lives
  *outside* `Reminder` (see BACKLOG.md) — the reminder entity stays pure.
- **No per-occurrence reminder overrides.** An occurrence inherits the
  series reminders. The occurrence-scoped editor omits the picker.
- **No default reminder.** New events are silent unless the user opts in.
- **Toast only.** No email or other notification channels.

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
notification concepts. The notification-delivery layer's platform APIs
(`Windows.UI.Notifications`) live in `src/Chronicle` in exactly one place —
`ScheduledToastReminderScheduler`, behind the `IReminderScheduler` seam. That
`ScheduledToastReminderScheduler` depends directly on `Windows.UI.Notifications`
is correct: that is where the dependency belongs. What the boundary prevents
is the reverse — toast concepts leaking back into `Reminder` /
`ReminderOccurrence`, or unrelated code manipulating scheduled toasts. The
seam is a responsibility boundary, not a Windows firewall.
