# 0035. Every browser tab is a passenger of its own

- **Status:** Accepted
- **Date:** 2026-10-21
- **Amends:** [0026](0026-passengers-walk-in-and-out.md) §1

## Context

ADR-0026 kept the passenger's random id in `localStorage`, so every tab of one browser shared it: a second tab
showed the same person. To try the system by hand with several people in the building, you had to open other
browsers or private windows.

## Decision

The id lives in `sessionStorage` (`elevator.passenger`), which every tab has to itself. A reload keeps the
tab's journey, as before; a new tab, or a tab opened later, is a new person who arrives on the lowest floor. Nothing
else changes: the server still decides every step, and still forgets a passenger idle for `IdleTimeout`.

The language preference stays in `localStorage` (ADR-0013): it belongs to the viewer, not to a passenger.

## Consequences

- Several tabs are several passengers, so the building can be filled by hand. They share one address, so the
  per-address passenger rate limit (a burst of 20, then 10 a second) applies to all of them together; that is
  plenty for people pressing buttons.
- Closing a tab leaves its passenger in the building until the server forgets them; a reopened tab ("restore closed
  tab") may pick the journey up again, as the browser restores the tab's storage too.
- The id is kept for less time than before, which only shortens the life of an already anonymous value (ADR-0024).
