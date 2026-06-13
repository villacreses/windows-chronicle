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

## Current Milestone

Calendar Management (see Next Milestones)

## Recently Completed: Mini Month Navigator

Delivered:

- Sidebar mini-month with prev/next + month label
- Selected-date state separate from displayed month (defaults to today)
- Jump navigation (click a day; adjacent-month days advance the month)
- Today and selected-date highlighting
- Shared month-grid geometry (`DateHelpers.BuildMonthGrid`) reused by the
  main grid and the mini month

## Next Milestones

### Calendar Management

- Create calendar
- Edit calendar
- Delete calendar
- Color selection

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