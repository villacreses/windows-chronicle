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

Build provider-agnostic functionality first.

Do not prioritize:

- Google integration
- Outlook integration
- AI features

until the local calendar experience is mature.

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

Mini Month Navigator

Followed by:

- Calendar CRUD UI
- Week View
- Day View
- Recurrence UI

## Important Principle

Chronicle is not trying to become Outlook.

Chronicle is trying to become the calendar application Windows should have shipped.