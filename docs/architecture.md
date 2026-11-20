# Architecture

## Lifecycle of a change

1. **Author** edits a file under `fleet/baselines/` and bumps its `version`. The pull request
   runs `blackcanary validate`, which fails on rule violations (see below) before a human looks.
2. **Merge** makes the new version available to the control plane.
3. **Rollout** is opened with `POST /v1/rollouts`. The control plane looks the baseline up,
   derives whether it is sensitive, and puts it in `awaitingApproval`.
4. **Approval** by one reviewer (standard) or two (sensitive), never the author.
5. **Start** opens the first ring. Agents poll `/targets/{deviceId}` and apply the change once
   their ring is open.
6. **Gate.** The analytics job reads telemetry for the ring and a holdback, and posts a verdict.
   - `healthy` — the rollout may advance once the ring's soak time has passed.
   - `inconclusive` — nothing happens; ask again when more devices have reported.
   - `unhealthy` — the control plane rolls back immediately and records why.
7. **Advance** ring by ring until `completed`.

```
Draft ─submit─▶ AwaitingApproval ─approve×N─▶ Approved ─start─▶ InProgress ─advance (last ring)─▶ Completed
                                                                  │  ▲
                                                                  │  └─ advance (healthy + soaked)
                                                                  └─ unhealthy verdict / rollback ─▶ RolledBack
```

## Validation rules

| Code | Rule | Why |
| --- | --- | --- |
| DN001 | Baseline names are lowercase kebab-case | They become change ids and URL segments. |
| DN002 | Version ≥ 1 | |
| DN003 | At least one tier and one setting | An empty baseline is always a mistake. |
| DN004 | No duplicate keys in a baseline | Last-one-wins is not a review-able behaviour. |
| DN005 | Keys and values are present | |
| DN006 | Sensitive settings are severity high or critical | "Needs two reviewers" and "cosmetic" contradict each other. |
| DN007 | Baselines that can land on the same device agree on shared keys | Otherwise devices flap and drift never converges. |
| DN008 | A name@version is declared once | |
| DN020 | Rings widen monotonically and end at 100% | |

Unknown JSON properties are a parse error, so a misspelled key cannot silently do nothing.

## The health gate

For the ring under test, with *treated* devices (have the change) and *control* devices (holdback):

| Check | Fails the ring when |
| --- | --- |
| Absolute ceiling | treated failure rate > 10% |
| Failure regression | one-sided two-proportion z-test p < α **and** the rate is ≥ 2 points worse |
| Boot regression | one-sided Mann-Whitney p < α **and** median boot time is > 15% worse |
| Sample size | either cohort has fewer than 20 devices → `inconclusive` |

Requiring an effect size as well as significance matters at fleet scale: with tens of thousands
of devices almost any difference is statistically significant, and most are not worth stopping for.
Rank-based tests are used for timings because one machine with a failing disk should not move the
result the way it moves a mean.

All thresholds are fields on `GatePolicy`.

## Trust boundaries

| Caller | Key scopes | Can | Cannot |
| --- | --- | --- | --- |
| Device agent | `device:checkin` | report its own state | read the fleet, touch rollouts |
| Author | `rollout:write`, `fleet:read`, `audit:read` | open, start, advance, roll back | approve |
| Reviewer | `rollout:approve`, `fleet:read` | approve | start or advance |
| Analytics | `verdict:write`, `fleet:read` | post verdicts | advance a rollout |

No single key can take a change from proposal to the whole fleet. Keys are compared in constant
time against stored digests.

## Concurrency

Every transition is load → pure function → compare-and-swap. Two operators advancing the same
rollout at once produce one success and one `409 Conflict`, never a double advance. Audit entries
are written only after the swap succeeds, so a rejected transition leaves no record of having
happened.
