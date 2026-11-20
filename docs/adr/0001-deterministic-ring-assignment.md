# 1. Ring assignment is a hash, not a table

## Context
Each change reaches the fleet in rings. Something has to decide which devices are in which ring.

## Decision
`bucket = SHA-256(changeId + "\n" + deviceId) mod 10000`; a device belongs to the first ring whose
cumulative percentage covers its bucket.

## Consequences
- No assignment table to store, migrate or keep consistent between services.
- Widening a ring is monotonic by construction (tested as a property over the fleet).
- Different changes pick different canaries, spreading the risk of being first.
- Rings are sized in expectation, not exactly: 2% of 5,000 devices is 100 ± a handful.
- There is no way to hand-pick canaries. If that is needed (e.g. IT's own machines first), it
  should be a separate explicit "ring zero" list, not a change to the hash.
