"""Small, dependency-free statistics used by the health gate.

Fleet telemetry is heavy-tailed (one machine with a dying disk boots in ten
minutes), so everything here is rank- or median-based rather than mean-based.
"""

from __future__ import annotations

import math
from collections.abc import Sequence
from dataclasses import dataclass
from statistics import median

# Scales the MAD so it estimates the standard deviation of a normal distribution.
_MAD_TO_SIGMA = 0.6745
# Same idea for the mean absolute deviation, used when the MAD collapses to zero.
_MEANAD_TO_SIGMA = 1.2533


def normal_sf(z: float) -> float:
    """P(Z > z) for a standard normal."""
    return 0.5 * math.erfc(z / math.sqrt(2))


def robust_z_scores(values: Sequence[float]) -> list[float]:
    """Modified z-scores (Iglewicz & Hoaglin): distance from the median in MAD units.

    When more than half the values are identical the MAD is zero; fall back to the
    mean absolute deviation so a single wild value is still scored instead of
    dividing by zero.
    """
    if not values:
        return []

    centre = median(values)
    deviations = [abs(v - centre) for v in values]
    mad = median(deviations)

    if mad > 0:
        return [_MAD_TO_SIGMA * (v - centre) / mad for v in values]

    mean_ad = sum(deviations) / len(deviations)
    if mean_ad == 0:
        return [0.0] * len(values)
    return [(v - centre) / (_MEANAD_TO_SIGMA * mean_ad) for v in values]


def two_proportion_p_value(failures_a: int, n_a: int, failures_b: int, n_b: int) -> float:
    """One-sided p-value for H1: failure rate of A is greater than B (pooled z-test)."""
    if n_a <= 0 or n_b <= 0:
        raise ValueError("both groups need at least one observation")
    if not (0 <= failures_a <= n_a and 0 <= failures_b <= n_b):
        raise ValueError("failures must be between 0 and the group size")

    pooled = (failures_a + failures_b) / (n_a + n_b)
    variance = pooled * (1 - pooled) * (1 / n_a + 1 / n_b)
    if variance == 0:
        return 1.0  # every device agrees; there is no evidence of a difference

    z = (failures_a / n_a - failures_b / n_b) / math.sqrt(variance)
    return normal_sf(z)


@dataclass(frozen=True)
class RankTest:
    """Result of a one-sided Mann-Whitney U test for 'A tends to be larger than B'."""

    p_value: float
    #: P(a > b) + 0.5 * P(a == b). 0.5 means no difference; 1.0 means A always larger.
    superiority: float


def mann_whitney_greater(a: Sequence[float], b: Sequence[float]) -> RankTest:
    """Mann-Whitney U with tie correction and continuity correction (normal approximation).

    The approximation is sound from roughly eight observations per group, which the
    gate's minimum cohort size already guarantees.
    """
    n_a, n_b = len(a), len(b)
    if n_a == 0 or n_b == 0:
        raise ValueError("both groups need at least one observation")

    pooled = sorted([(v, 0) for v in a] + [(v, 1) for v in b])
    total = n_a + n_b

    rank_sum_a = 0.0
    tie_term = 0.0
    i = 0
    while i < total:
        j = i
        while j < total and pooled[j][0] == pooled[i][0]:
            j += 1
        tied = j - i
        mid_rank = (i + 1 + j) / 2  # average of 1-based ranks i+1 .. j
        rank_sum_a += mid_rank * sum(1 for k in range(i, j) if pooled[k][1] == 0)
        tie_term += tied**3 - tied
        i = j

    u_a = rank_sum_a - n_a * (n_a + 1) / 2
    mean_u = n_a * n_b / 2
    variance = n_a * n_b / 12 * ((total + 1) - tie_term / (total * (total - 1)))

    if variance <= 0:
        return RankTest(p_value=1.0, superiority=0.5)

    z = (u_a - mean_u - 0.5) / math.sqrt(variance)
    return RankTest(p_value=normal_sf(z), superiority=u_a / (n_a * n_b))


def ewma(values: Sequence[float], alpha: float) -> list[float]:
    """Exponentially weighted moving average, seeded with the first value."""
    if not 0 < alpha <= 1:
        raise ValueError("alpha must be in (0, 1]")

    smoothed: list[float] = []
    for value in values:
        smoothed.append(value if not smoothed else alpha * value + (1 - alpha) * smoothed[-1])
    return smoothed
