# Agent Onboarding

Read files in this order:

1. `README.md` (repo root — product framing, principles, non-goals)
2. `architecture/CORE_ENGINE.md`
3. `DECISIONS.md`
4. `EXECUTION_PLAN.md`

Read the subsystem docs (`architecture/DATA_MODEL.md`,
`architecture/USER_INTERFACE.md`, `architecture/RECURRENCE.md`,
`architecture/REMINDERS.md`) when your work touches that subsystem.

Then inspect the repository.

## Code Layout

Conventional `src/` + `tests/` layout:

- `src/Chronicle.Core/` — pure domain library (`Models/`, `Data/`,
  `Projection/`, `Layout/`, `Helpers/DateHelpers`). Plain `net8.0`,
  no WinUI.
- `src/Chronicle/` — the WinUI 3 app (`Views/`, renderers, popovers,
  `Notifications/` toast-delivery adapter, custom `Program.cs` entry
  point, UI helpers). References `Chronicle.Core`.
- `tests/Chronicle.Tests/` — xUnit, references `Chronicle.Core`.

Solution: `Chronicle.slnx` at the repo root. Build/test with
`dotnet build Chronicle.slnx -p:Platform=x64` and
`dotnet test Chronicle.slnx`. Repo-level config: `global.json`
(SDK pin), `Directory.Build.props` (shared MSBuild props),
`Directory.Packages.props` (central NuGet versions). See `DECISIONS.md`
"Domain Extracted to Chronicle.Core" and `.context/TESTING.md`.

## Context Organization

The `.context/` directory uses a spine + subsystem layout. Knowing
where a fact belongs makes the directory cheap to read and easy to
keep coherent.

- **`architecture/CORE_ENGINE.md`** is the spine. It introduces every
  major engine concept, gives just enough definition for the file to
  read coherently, and points to subsystem docs for depth. It never
  duplicates subsystem detail.
- **`architecture/DATA_MODEL.md`**, **`architecture/USER_INTERFACE.md`**,
  **`architecture/RECURRENCE.md`**, **`architecture/REMINDERS.md`**
  own the full contract for their subsystem — invariants, mutation
  shapes, correctness rules, operational semantics. Read the one that
  matches what you are touching.
- **`DECISIONS.md`** records *why* — alternatives weighed, tradeoffs,
  product positions. Not contracts. A fact stays here only if it
  would survive a complete rewrite of the relevant subsystem.
- **`EXECUTION_PLAN.md`** is operational state: what shipped, what is
  next.
- **`BACKLOG.md`** captures deferred work; consult as needed.

Contribution rules:

- A concept has one canonical home. Other docs name it, summarize it
  in a sentence, and point to the canonical doc.
- CORE_ENGINE keeps only the minimum a concept needs for later
  CORE_ENGINE sections referencing it to stay coherent. Anything
  beyond that minimum is duplication.
- DECISIONS keeps rationale, not contracts. If an entry is describing
  *how the system currently behaves* rather than *why this approach
  was chosen*, the operational half belongs in a subsystem doc with
  DECISIONS pointing to it.
- Feature work is incomplete when the implementation contract changes
  but the corresponding architectural, UI, testing, or onboarding
  documentation does not. Update affected `.context` documents as part
  of the same change. This requires impact assessment, not mechanical
  edits — most features touch one or two documents, not all of them;
  the failure mode this rule exists to prevent is the *peripheral*
  doc (the spine's concept list, the renderer inventory, the testing
  map) silently drifting while only the subsystem doc gets updated.
  A doc that no longer matches the implementation is a bug.

## Summary

Chronicle is a native Windows calendar application built with WinUI 3 and SQLite.

The project exists because Windows lacks a compelling vendor-neutral desktop calendar experience.

## Current Strategy

**Local Baseline Completion is done** (see `EXECUTION_PLAN.md`
"Status"): month / week / day views, calendar management, recurrence
with overrides and wall-clock anchoring, all-day polish, notes, search,
agenda view, year view, and reminders (OS-scheduled toast
notifications) are all shipped on `main`. The current milestone is
**Provider Integrations**, starting with Google Calendar.

Still not prioritized:

- AI features (kept off the roadmap until provider integrations
  ship and a design overhaul lands).

## Expectations

Favor:

- simple solutions
- incremental progress
- native Windows UX
- performance

Avoid:

- major rewrites
- unnecessary abstractions
- speculative architecture

## Current Focus

**Provider Integrations**, starting with Google Calendar. See
`EXECUTION_PLAN.md` "Next Milestones" → "Provider Integration Phase"
for open questions (OAuth/token storage, sync model, adapter shape,
conflict resolution, sidebar display of provider calendars) and
"Definition of Ready for Integrations" for the now-complete
Local Baseline checklist.

Followed by:

- Outlook Calendar
- Design overhaul (replaces the dev-only dark-theme override)

## Important Principle

Chronicle is not trying to become Outlook.

Chronicle is trying to become the calendar application Windows should have shipped.