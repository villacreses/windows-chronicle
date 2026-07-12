# Manual Verification Registry

A curated inventory of behaviors that **cannot reasonably be verified by the
automated test suite**, each with the reason automation is impractical and
the minimum steps to reproduce.

## The rule

> If a behavior cannot be reasonably verified by automated tests, it should
> have an entry here explaining *why* automation isn't appropriate and giving
> the minimum reproducible steps.

## Scope — what belongs here

Only behaviors where automation is genuinely impractical or impossible —
almost always **OS integration points** that live outside the process the
test host can drive:

- toast notification delivery and activation,
- single-instance / activation redirection,
- protocol / file-type activation,
- drag-and-drop from Explorer,
- installer / packaging behavior.

This is **not** a general QA checklist. Anything that *can* be unit- or
integration-tested belongs in `Chronicle.Tests`, not here. Keep each entry
terse; if an entry can later be automated, delete it and write the test.

Related: `.context/TESTING.md` (the automated test architecture) and
`architecture/NOTIFICATIONS.md` (the reminder subsystem these entries cover).

---

## MV-001 — Reminder toast activation (cold launch)

**Reason:** Packaged WinUI toast activation launching a closed process cannot
be exercised by the unit suite (out-of-process OS scheduler + cold launch).

**Steps:**
1. Create an event one minute in the future.
2. Add an "At start time" reminder; save.
3. Fully close Chronicle (confirm the process exits).
4. Click the toast when it fires.

**Expected:** Chronicle cold-launches, navigates to the event's day (Day
view), and opens the event.

---

## MV-002 — Reminder toast activation (warm — app already open)

**Reason:** Same OS activation path as MV-001, plus single-instance
redirection through the primary instance's `Activated` event — a
cross-process handshake the suite can't drive.

**Steps:**
1. With Chronicle open, create an event one minute out with an "At start
   time" reminder; save.
2. Click the toast when it fires.

**Expected:** The **existing** window focuses (no second window opens),
navigates to the event's day, and opens the event.

---

## MV-003 — Single-instance focus (non-toast relaunch)

**Reason:** `AppInstance` redirection in the custom `Main` is a process- and
OS-level behavior; launching a second instance can't be simulated in-process.

**Steps:**
1. Open Chronicle.
2. Launch it again (Start menu / re-run).

**Expected:** The existing window is focused; no second window or process
lingers.

---

## MV-004 — Reminder toast delivery (OS scheduling)

**Reason:** Reminders are registered with the Windows scheduler
(`ScheduledToastNotification`) and delivered out-of-process, even when
Chronicle is closed — there is no in-process signal for the suite to assert.

**Steps:**
1. Create an event a couple of minutes in the future with an "At start time"
   reminder; save.
2. Optionally close Chronicle.
3. Wait for the fire time.

**Expected:** A toast appears at (or just before, per the offset) the event
time, showing the event title and time — whether or not Chronicle is running.
