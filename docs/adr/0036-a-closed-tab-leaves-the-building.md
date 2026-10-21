# 0036. A passenger whose tab closes leaves the building

- **Status:** Accepted
- **Date:** 2026-10-21
- **Amends:** [0026](0026-passengers-walk-in-and-out.md), [0035](0035-a-passenger-per-browser-tab.md)

## Context

With a passenger per tab (ADR-0035), closing a tab left its passenger in the building, waiting or riding, until the
engine forgot idle passengers half an hour later. The admin view kept showing people nobody was playing. A browser
cannot tell a tab that closes from one that reloads, and a reload must keep the journey.

## Decision

1. **The tab says it is going away.** A small static script (`js/departure.js`) sends a beacon on `pagehide`:
   `POST /api/v1/passengers/{id}/depart`, anonymous, under the passenger rate limit, with no body.
2. **The API waits a moment.** `PassengerDepartures` sends `PassengerCommandV1` with `LeaveBuilding` after
   `Passengers:DepartureGrace` (5 s), unless the same passenger is heard from first: any of their steps, or a read of
   the passenger. At most 10,000 departures wait at once.
3. **A reload comes straight back.** The same script, as soon as a reloaded page loads and long before the app has
   started, reads its passenger once, which calls the departure off. The journey carries on.
4. **The engine removes them from wherever they are,** in the car included (`PassengerRegistry.Depart`), with the
   usual `PassengerRemoved` activity. Someone it does not know is ignored without a refusal. Their pending calls
   stay, as with a passenger the building forgets.

## Alternatives considered

- **Presence over the hub connection:** the hub is receive-only by design (no client-callable methods); a
  connection also drops for reasons a passenger did not choose, and background tabs throttle its keep-alives.
- **A heartbeat from every tab:** browsers throttle timers in background tabs to once a minute, so a passenger
  would leave while their tab was merely hidden.
- **Leave at once on `pagehide`:** a reload would lose the journey.

## Consequences

- Closing a tab empties its place in the building within seconds. A browser that crashes or loses power sends no
  beacon; that passenger is still forgotten when idle (`Passengers:IdleTimeout`).
- Knowing a passenger's random id lets a caller send them out, as it already lets them move that passenger; the id
  stays anonymous and unguessable (ADR-0024).
- A duplicated tab shares its passenger: closing one sends that passenger out unless the other tab acts within the
  grace.
