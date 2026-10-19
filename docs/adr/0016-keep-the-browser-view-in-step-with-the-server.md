# 0016. Keep the browser view in step with the server

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

The passenger view (ADR-0006) learns the elevator's state from two channels: `GET /api/v1/elevator`
when the page loads, and the SignalR hub, which sends `StatusChanged` on connect and after every
change. Each status carries a `Version` that increases with every change. Messages can arrive out of
order: the HTTP response can land after a newer hub message, and a reconnect replays the current
status. The phase 2 API keeps the controller in memory, so after a restart the version counts from
the start again while open browser tabs still hold the old, higher number.

The view runs on lobby screens that nobody reloads by hand. It must recover from server restarts and
network drops on its own, and it must never show the car moving backwards.

The production CSP (`script-src 'self' 'wasm-unsafe-eval'`) forbids inline scripts. The Blazor
`<ImportMap />` component renders one.

## Decision

- One client-side `ElevatorStateStore` holds the configuration, the newest status, a bounded activity
  log (30 entries) and the connection state. Components only render what it holds.
- The store ignores a status whose `Version` is lower than or equal to the one shown, **except** for
  the first status after the connection was lost. That status comes from the server that is live now
  and replaces whatever is shown, even with a lower version.
- The hub client reconnects automatically with a 0, 2, 5, 10, 20 and then 30 second schedule and never
  gives up. The first connection, which SignalR's automatic reconnect does not cover, retries on the
  same schedule using `TimeProvider`.
- While the hub is down the view keeps the last known status under a "Reconnecting…" banner. Pressing
  buttons still works over HTTP.
- The host page does not render `<ImportMap />`. The WebAssembly app loads without it, and the page has
  no inline script at all.

## Alternatives considered

- **Always apply the newest message by arrival** — simplest, but a late HTTP response would move the
  car backwards on screen.
- **Compare versions strictly with no exception** — after an API restart every tab would ignore the
  new server until someone reloaded the page.
- **Reload the page when the connection comes back** — loses the activity log and flashes the screen,
  and the reload would fail anyway while the server is still starting.
- **Allow the import map with a CSP hash** — the hash changes whenever fingerprinted assets change, so
  the API and the UI would have to agree on it at every build. That buys nothing the app needs.

## Consequences

- A stale or duplicated message can't move the car backwards, and a restarted server is shown as soon
  as it answers. Unit tests in `ElevatorSystem.Web.Tests` cover both rules, and both were checked in a
  browser against a restarted server.
- Phase 3 persists the elevator state. If versions then survive restarts, the exception stays harmless:
  the first status after a reconnect is simply the newest one.
- Anything that adds an inline script to the host page breaks under the CSP. Static files only.
