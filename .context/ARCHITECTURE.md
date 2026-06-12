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

## Performance Philosophy

Calendar applications are read-heavy.

Optimize for:

- startup speed
- rendering speed
- low memory consumption

Avoid abstractions that materially harm responsiveness.