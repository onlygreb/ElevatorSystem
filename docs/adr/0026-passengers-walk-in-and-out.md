# 0026. Passengers walk in and out of the car themselves, and are counted as they move

- **Status:** Accepted (amended by [0035](0035-a-passenger-per-browser-tab.md): the id is kept per browser tab; and
  [0036](0036-a-closed-tab-leaves-the-building.md): a passenger whose tab closes leaves the building)
- **Date:** 2026-10-20
- **Supersedes:** [0020](0020-occupancy-counting-rules-and-traffic-generator.md); amends [0017](0017-manual-passengers-occupancy-and-admin-service-control.md) §2 and §4

## Context

ADR-0017 and ADR-0020 counted people from the calls they placed: each hall call was one person waiting,
and boarding and getting off were inferred from the doors and the car calls. Nobody actually walked
into the car. That made the passenger view a panel of buttons rather than a journey, the inference
rules were subtle (left-behind people, merged calls, reordered acknowledgements), and the counts could
only approximate what people did. The product now wants each passenger to start on the lowest floor,
walk into the car when its door is open on their floor, choose a floor, ride, and walk out where they
like, riding on if they do not.

## Decision

1. **A passenger is an anonymous random id** (a version 4 UUID) the browser keeps. It identifies nobody
   and is never linked to an address, device or account (ADR-0024). The API returns it on arrival.
2. **Every passenger arrives on the lowest floor.** The steps are: call the car (up or down) from their
   floor, walk in, choose a floor inside, and walk out. Each step is a `PassengerCommandV1` on the broker,
   answered `202` with a request id; the engine checks it against the live car and reports the outcome on
   the hub as a passenger activity (`PassengerJoined`, `PassengerCalled`, `PassengerBoarded`,
   `PassengerChoseFloor`, `PassengerLeft`, `PassengerRemoved` or `PassengerRefused` with a reason).
3. **The rules**, enforced by the engine's `PassengerRegistry` under one lock:
   - walking in needs the door open on the passenger's floor and room in the car (8 people);
   - walking out needs the door open, and puts the passenger on that floor;
   - choosing a floor needs the passenger inside; calling needs them on a floor, in a direction the
     building allows; both also place the hall or car call, like any button;
   - a passenger who does not walk out rides on until they walk out or choose again;
   - a waiting passenger keeps waiting when the car leaves without them (it was full, or they were slow)
     and presses the button again;
   - when the car goes out of service its riders walk out on the floor where it stopped;
   - a person who takes no step for 30 minutes has left the building (they closed the page).
4. **The counts come from the passengers**: waiting = on a floor having called, present = on a floor
   without a call, riding = in the car, rides = walking out on another floor than the one boarded, and the
   average wait runs from the call to walking in. Calls placed without a passenger (the requirement's
   `requestElevator` and `RequestDestination` endpoints, which stay) move the car but count nobody.
5. **Passengers live in the leading engine's memory.** After a failover the new leader starts with an
   empty building; a browser whose passenger is unknown is told so (`UnknownPassenger`) and arrives again.
   The API keeps a cache of where each passenger is, from the activities, for `GET /api/v1/passengers/{id}`.

## Alternatives considered

- **Keep counting from calls:** no journey for the user, and the inference rules cannot know who
  actually boarded.
- **Passengers tracked by the API instead of the engine:** the API would decide boarding without the
  live car state, and occupancy would be split between two processes.
- **Persist passengers with the snapshot:** survives failover, but adds a table and a migration for
  anonymous, short-lived data; re-arriving is cheap.

## Consequences

- `OccupancyTracker` and the traffic generator are removed; simulated passengers now go through the same
  passenger steps (ADR-0027).
- The activity feed carries passenger activities with the passenger id; clients follow their own by id
  or by request id.
- A passenger's floor while riding is the car's floor; the status has it.
