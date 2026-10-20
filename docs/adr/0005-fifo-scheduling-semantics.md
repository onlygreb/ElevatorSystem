# 0005. FIFO scheduling semantics

- **Status:** Accepted
- **Date:** 2026-10-19
- **Updated:** 2026-10-20: the setting that selects LOOK, and where the average wait is compared

## Context

The elevator must use a first-in, first-out (FIFO) scheduling algorithm. "FIFO" leaves several
questions open: does the car stop at a pending floor it passes on the way to the head of the
queue? What happens when the same button is pressed twice? How large can the queue grow under load?

## Decision

- **Strict arrival order:** the oldest pending request is the target, and the car travels to it
  without stopping at other pending floors along the way.
- **Duplicates are merged:** a request identical to one already pending (same floor and same
  kind, plus the same direction for hall calls) is absorbed and keeps its original place in line.
- **A bounded queue as a consequence:** with merging, at most 18 hall calls (no "up" at the top
  floor, no "down" at the bottom) plus 10 car calls can be pending. Memory is constant regardless
  of request volume.
- **A call at the current floor** while the car is idle opens the doors immediately.
- **A replaceable strategy:** scheduling sits behind `ISchedulingStrategy`.
  `FifoSchedulingStrategy` is the default; `LookSchedulingStrategy` (serve every request in the
  current direction, then reverse) can be chosen per deployment with `Elevator:Scheduling` (`Fifo` or
  `Look`; `ELEVATOR_SCHEDULING` in Docker Compose), or switched by an admin while the car is out of service
  (ADR-0033). The admin view shows the average wait with the strategy it was measured under, so the two can
  be compared run by run, for example with the same demo scenario.

## Alternatives considered

- **FIFO that also stops at passed floors:** that's really a hybrid of LOOK and no longer FIFO.
  It's offered as the separate LOOK strategy instead of blurring the definition.
- **An unbounded list of raw requests:** lets duplicate presses inflate the queue and the latency,
  and memory grows with the load.

## Consequences

- The behaviour is predictable and easy to assert in tests: the served order equals the arrival
  order after merging.
- FIFO is visibly inefficient under mixed traffic. That's expected, and the LOOK comparison makes
  the trade-off measurable instead of hidden.
