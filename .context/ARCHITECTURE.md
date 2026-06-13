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

- `Views/Rendering/CalendarGridRenderer` — month grid and day cells
- `Views/Rendering/SidebarRenderer` — calendar list/visibility sidebar
- `Views/Dialogs/EventDialogService` — Create/Edit Event dialogs

Shared date/color conversions live in `Helpers/` (`DateHelpers`,
`ColorHelper`) so rendering classes don't duplicate them.

These are plain classes instantiated directly by MainWindow — no DI
container, event bus, or MVVM framework. See "Avoid Premature MVVM" in
DECISIONS.md.

## Performance Philosophy

Calendar applications are read-heavy.

Optimize for:

- startup speed
- rendering speed
- low memory consumption

Avoid abstractions that materially harm responsiveness.