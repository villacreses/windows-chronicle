# Major Decisions

## WinUI 3

Reason:

Native Windows experience.

Alternatives considered:

- Electron
- Avalonia
- WPF

Decision:

WinUI 3.

---

## Provider-Neutral Architecture

Reason:

The product should not inherit UX decisions from Google or Microsoft.

Chronicle owns the experience.

Providers supply data.

---

## Build Provider-Agnostic Features First

Reason:

Most complexity lies in calendar UX.

Integrations should become mapping problems rather than product-design problems.

Current strategy:

1. Solve local calendar experience.
2. Solve calendar management.
3. Solve views and recurrence.
4. Add providers.

---

## UTC Storage

Reason:

Interoperability and synchronization.

Tradeoff:

Local-time conversion required at UI boundaries.

---

## Avoid Premature MVVM

Current complexity does not justify a framework migration.

Rapid iteration is prioritized.

This decision may be revisited later.

---

## MainWindow Decomposition into Helper Classes

Reason:

MainWindow.xaml.cs had grown into a dumping ground for state, calendar grid
rendering, sidebar rendering, and dialog construction, making it hard to
reason about.

Decision:

Extract cohesive rendering/dialog responsibilities into small focused
classes (`CalendarGridRenderer`, `SidebarRenderer`, `CalendarDialogService`,
plus `DateHelpers`/`ColorHelper`), instantiated directly by MainWindow.
MainWindow remains a coordinator: state ownership, event handlers, and
refresh orchestration.

No new architectural pattern (MVVM, DI, event bus) was introduced, in
keeping with "Avoid Premature MVVM." Behavior and repository usage are
unchanged.

---

## Deleting a Calendar Cascade-Deletes Its Events

Reason:

The `Events.CalendarId` foreign key references `Calendars.Id` with foreign
keys enforced (`PRAGMA foreign_keys = ON`). Deleting a calendar that still
has events would otherwise violate the constraint. Two options were weighed:
delete the events with the calendar, or reassign them to another calendar.

Decision:

Cascade-delete. It matches the user's mental model ("delete this calendar
and everything in it") and is the simplest behavior consistent with the
current architecture — reassignment would require a target-calendar picker
and special handling when no other calendar exists.

The cascade is performed in `CalendarRepository.DeleteAsync` inside a single
transaction (DELETE events, then the calendar), rather than via a schema
`ON DELETE CASCADE`. This keeps the operation in the repository layer, works
on existing databases without a migration (the schema is `CREATE TABLE IF
NOT EXISTS` only), and never trips the FK constraint. The delete dialog
surfaces the affected event count so the action is never silent.

---

## DEV-ONLY THEME OVERRIDE (2026-06-14)

A temporary hard-coded Dark Mode theme was introduced to reduce visual fatigue during development and enable sharing progress without UI distraction.

Rationale:
- UI polish is explicitly not in current roadmap phase.
- However, existing default UI quality was interfering with development motivation and communication needs.
- This change reduces cognitive friction without altering functional architecture.

Decision:
- Dark mode is hard-coded for development only.
- Theme system exists solely as a minimal infrastructure layer (`Theme.cs`).
- No expansion into full design system is allowed at this stage.
- Light mode support is deferred until formal “design overhaul” phase.

Constraints:
- No additional UI polish work until Day View + Recurrence are complete.
- Theme changes are restricted to bug fixes or rendering correctness issues.
- This does NOT shift project phase sequencing.

Status:
- Temporary, intentionally non-architectural UI deviation.
- To be revisited in design overhaul phase.