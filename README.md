# Chronicle

Chronicle is a native Windows calendar application designed to provide the calendar experience Windows should have shipped.

Unlike Outlook, Chronicle is not tied to a specific ecosystem. Google, Microsoft, Apple, and other providers are treated as data sources—not user experience platforms. Chronicle delivers a consistent experience regardless of where your events come from.

## Why Chronicle?

Windows currently lacks a compelling desktop calendar application.

Most existing options are either:

- Tightly coupled to a vendor ecosystem
- Web applications wrapped in desktop shells
- Part of larger productivity suites
- Poor offline experiences

Chronicle focuses on a single problem:

> Provide a fast, native, vendor-neutral calendar for Windows.

## Principles

### Native First

Chronicle is built with WinUI 3 and designed to feel like a Windows application, not a website running inside a desktop window.

### Vendor Neutral

Calendar providers should not dictate the user experience.

Whether events come from Google Calendar, Outlook, Apple Calendar, CalDAV, or local storage, the interface remains consistent.

### Offline First

Local storage is the source of truth.

The application remains useful without network connectivity.

### Fast

Responsiveness, memory usage, and idle performance are product features.

Chronicle is designed to remain open throughout an entire workday with effectively zero background activity when idle.

### Familiar

A typical office worker should understand the application immediately.

The design draws inspiration from Samsung Calendar and Apple Calendar while remaining distinctly Windows-native.

## Current Features

### Calendars

- Local calendar creation
- Calendar editing
- Calendar deletion
- Calendar color management
- Calendar visibility toggles

### Events

- Create events
- Edit events
- Delete events
- All-day events (polish pending — see Roadmap)
- Multi-calendar support
- Recurring events (RRULE storage, EXDATE skips, per-occurrence overrides, wall-clock anchoring)

### Views

- Month View
- Week View
- Day View
- Mini-month navigator
- Selected-day details panel

### Storage

- Local SQLite database
- UTC-based event storage
- Offline operation

## Roadmap

### In Progress: Local Baseline

Six features that finish the local experience before provider integration begins:

- All-day event polish
- Notes / description field
- Search
- Agenda view
- Year view
- Reminders

### Planned Integrations

Once the Local Baseline ships:

- Google Calendar
- Outlook Calendar

### Future Integrations

- Apple Calendar
- CalDAV
- Calendly

### Future Views

- Timeline View

## Architecture

Chronicle follows a provider-neutral architecture:

```
Provider
↓
Adapter
↓
Chronicle Domain Model
↓
UI
```

Provider-specific concepts do not leak into the user interface.

Chronicle owns the domain model. Integrations adapt into Chronicle—not the other way around.

## Technical Highlights

- WinUI 3
- .NET
- SQLite
- UTC-based persistence
- No MVVM framework
- No dependency injection container
- No reactive framework
- Designed for AOT and trimming compatibility

The project intentionally minimizes dependencies and avoids architectural complexity unless it provides measurable value.

## Non-Goals

Chronicle is not:

- An email client
- An Outlook replacement
- A productivity suite
- An AI-first workflow tool

Its purpose is simple:

Provide a modern, native calendar experience for Windows.

## Status

Active development.

Current focus: completing the Local Baseline (all-day polish, notes, search, agenda view, year view, reminders) before beginning provider integrations.