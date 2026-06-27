# Agent Onboarding

Read files in this order:

1. PROJECT.md
2. ARCHITECTURE.md
3. DECISIONS.md
4. EXECUTION_PLAN.md

Then inspect the repository.

## Summary

Chronicle is a native Windows calendar application built with WinUI 3 and SQLite.

The project exists because Windows lacks a compelling vendor-neutral desktop calendar experience.

## Current Strategy

The local calendar experience is mature (month / week / day views,
calendar management, recurrence with overrides and wall-clock
anchoring). The next milestone is provider integrations, starting
with Google Calendar.

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

Provider Integrations — Google Calendar (first adapter). Planning
round before sub-step implementation; open questions (OAuth + token
storage, sync model, adapter shape, conflict resolution, sidebar
display) are listed in `EXECUTION_PLAN.md`.

Followed by:

- Outlook Calendar
- Design overhaul (replaces the dev-only dark-theme override)

## Important Principle

Chronicle is not trying to become Outlook.

Chronicle is trying to become the calendar application Windows should have shipped.