# 0020. Count passengers by explicit rules and mirror them in the traffic generator

- **Status:** Superseded by [0026](0026-passengers-walk-in-and-out.md)
- **Date:** 2026-10-19

## Context

ADR-0017 decided that the engine counts people from the calls they place, and listed the main
rules. Implementing `OccupancyTracker` and the `TrafficGenerator` raised questions those rules do
not answer:

- The domain merges repeated calls and serves every call at a floor when its door opens. The
  events of one stop arrive as `DoorsOpened` followed by `CallServed` for each call, hall and car
  calls mixed in queue order. Who gets off and who boards first?
- People left behind by a full car "call again". When the button is pressed at that floor again,
  is that a new person or one of them?
- What happens to riders and waiting people when the car goes out of service?
- Safety commands publish their event from the caller's thread, so a stop that was already
  happening can be published after the out-of-service event.
- The generator only sees the elevator's events. How do its simulated people know whether they
  boarded, so that they press their destination?

## Decision

1. **Riders get off before anyone boards.** Boarding happens on `DoorsOpened` and again after each
   served car call at that floor, so the result does not depend on the order of the stop's events.
2. **A call at a floor where someone was left behind is theirs.** The tracker keeps, per floor, the
   people waiting (oldest first) and how many of them have a pending call. A new hall call there
   counts as one of the left-behind people calling again, not as a new person; only when nobody is
   left behind does it add a person, taking one from `Present` if anyone is there. A press with
   nobody left behind adds exactly one person, whether it was queued, merged or served on the spot.
3. **Going out of service evacuates the car.** Riders get off at the car's floor (the floor being
   travelled to, if it was moving). They become present there and are counted as `Evacuated`, not
   delivered. Waiting people stay waiting, but every call was cancelled, so they are all left behind
   and must call again once the car is back. Events published after the out-of-service event (a
   stop overtaken by the safety command) move nobody. Requests acknowledged in that window are
   counted from the event's cancelled calls instead, each as one more person left behind, never
   taken for someone already waiting, so the result does not depend on the order of the calls.
4. **Inconsistent events are clamped and logged.** A car call that lets off more riders than are
   in the car only lets off those riding (event ID 1800). A served or cancelled hall call carrying
   requests the tracker never saw, for example restored after a restart, adds them as waiting people
   (1801). Events for a floor outside the building are ignored (1803). `OccupancyTotals.Corrections`
   counts each case.
5. **The traffic generator mirrors the tracker's rules.** Its people board on the same events, in the
   same order and up to the same capacity (`OccupancyOptions`), including rule 2: a newcomer whose
   press is counted as a left-behind person's call presses again later. With the generator as the
   only source of calls, its people are exactly where the tracker counts them after every event,
   and the tests check this event by event. Because the tracker only knows floors, the generator
   applies the rule per floor too. Every press it sees taken in (even one by someone who has
   since boarded) becomes the call of someone left behind on that floor if there is anyone.
   Otherwise it counts one more person there: the presser if not counted yet, else another of its
   people on that floor not counted yet. So the counts do not depend on which of two racing presses
   the loop takes in first. A test delivers every pair of acknowledgements swapped to check this.
   - **Who presses again, and from where.** People left behind press again when a door closes,
     from the control loop's own event. The loop then takes the press in before its next stop.
     A press from the arrival timer, or from the thread that returned the car to service, could
     reach the loop after the door opened at that floor. The person would board with the press
     still in flight, and the loop would later take it for someone new. So the timer only presses
     for people not counted yet (newcomers, and those whose press was taken for someone else).
     After a return to service, one counted person calls the car back, and the rest press when its
     door closes. The first runs on CI caught this race.
6. **The generator keeps its own clock.** Arrival times are the running sum of gaps drawn from a
   seeded `Random`, checked by a one-shot `TimeProvider` timer. Arrivals do not depend on how the
   timer fires or how a test advances fake time, and the draws are independent of the elevator, so
   a seed reproduces the same calls at the same simulated times.
7. **Log event IDs:** 1800–1819 for occupancy counting, 1820–1839 for the traffic generator. The
   generator logs its start at Warning level, so a generator running where it should not is visible.

## Alternatives considered

- **Every hall call is a new person, even after being left behind:** simpler, but people left
  behind would be counted twice as soon as they call again.
- **Boarding only on `CallServed` for hall calls:** riders who get off later in the same stop would
  not make room, and the counts would depend on queue order.
- **Riders stay in an out-of-service car:** the counts would show people riding a car that is not
  moving for as long as the outage lasts, and nobody could ever let them off, because the car calls
  were cancelled.
- **The generator asks the tracker who boarded:** couples the test tool to the tracker's internals
  and leaves nothing independent to compare the counts with.

## Consequences

- Counts stay estimates (ADR-0017), but deterministic: the same events always give the same counts,
  and the property tests check conservation (waiting + left behind + riding + present = counted)
  and that nothing goes negative.
- Mixing manual calls with generated traffic still gives consistent counts, but the generator's
  people may then disagree with the tracker about who boarded. Load tests that compare the two must
  run the generator alone.
- A press by a person who boards before the loop takes it in (a window of microseconds) is counted
  as one more person waiting. The tracker cannot tell it apart from a real call.
