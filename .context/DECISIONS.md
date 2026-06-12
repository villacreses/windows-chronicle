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