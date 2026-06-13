# Execution Plan

## Current Objective

Build the complete provider-agnostic calendar experience before beginning account integrations.

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

## Current Milestone

Additional Views — Week view (see Next Milestones)

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

### Additional Views

- Week view
- Day view

### Recurrence

- Recurrence editing
- Recurrence rendering

## Provider Integration Phase

After local UX is mature:

- Google Calendar
- Outlook Calendar

## Definition of Ready for Integrations

Before Google integration begins:

- Month view complete
- Week view complete
- Day view complete
- Calendar management complete
- Recurrence complete