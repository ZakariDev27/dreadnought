from datetime import UTC, datetime

import pytest

from blackcanary.gate import GatePolicy, evaluate_ring
from blackcanary.simulate import cohort
from blackcanary.telemetry import DeviceHealth, summarise

NOW = datetime(2026, 11, 20, 16, 0, tzinfo=UTC)


def fleet(*, treated_failure=0.02, treated_boot=24.0, treated=60, control=200, seed=7):
    samples = cohort(
        prefix="t",
        cohort="treated",
        ring="canary",
        devices=treated,
        failure_rate=treated_failure,
        boot_median=treated_boot,
        seed=seed,
    ) + cohort(
        prefix="c",
        cohort="control",
        ring="fleet",
        devices=control,
        failure_rate=0.02,
        boot_median=24.0,
        seed=seed + 1,
    )
    return summarise(samples)


def judge(devices, **policy):
    return evaluate_ring(devices, rollout_id="r-1", ring="canary", policy=GatePolicy(**policy), now=NOW)


def test_matching_cohorts_are_healthy():
    verdict = judge(fleet())

    assert verdict.status == "healthy"
    assert verdict.reasons == ()
    assert verdict.sample_size == 60


def test_failure_regression_is_unhealthy_and_explained():
    verdict = judge(fleet(treated_failure=0.30))

    assert verdict.status == "unhealthy"
    assert any("ceiling" in r for r in verdict.reasons)
    assert any("vs" in r and "control" in r for r in verdict.reasons)


def test_boot_time_regression_is_unhealthy_even_when_nothing_crashes():
    verdict = judge(fleet(treated_boot=31.0))

    assert verdict.status == "unhealthy"
    assert len(verdict.reasons) == 1
    assert "boot time" in verdict.reasons[0]


def test_small_boot_shift_within_tolerance_passes():
    assert judge(fleet(treated_boot=25.0)).status == "healthy"


def test_too_few_devices_is_inconclusive_not_healthy():
    verdict = judge(fleet(treated=12))

    assert verdict.status == "inconclusive"
    assert "12 treated" in verdict.reasons[0]


def test_only_the_named_ring_counts_as_treated():
    devices = [
        *fleet(),
        *[DeviceHealth(f"x-{i}", "treated", "early", failed=True, boot_seconds=None) for i in range(50)],
    ]

    assert judge(devices).status == "healthy"


def test_a_noisy_device_counts_once():
    # One device crash-looping through 500 check-ins is still one failed device out of 60.
    noisy = cohort(
        prefix="loop",
        cohort="treated",
        ring="canary",
        devices=1,
        failure_rate=1.0,
        boot_median=24.0,
        seed=3,
        checkins=500,
    )
    quiet = cohort(
        prefix="t",
        cohort="treated",
        ring="canary",
        devices=59,
        failure_rate=0.0,
        boot_median=24.0,
        seed=4,
    )
    control = cohort(
        prefix="c",
        cohort="control",
        ring="fleet",
        devices=200,
        failure_rate=0.02,
        boot_median=24.0,
        seed=5,
    )

    assert judge(summarise(noisy + quiet + control)).status == "healthy"


def test_trivial_but_significant_difference_does_not_block():
    # 1.5% vs 0.5% over a huge fleet is statistically real and operationally irrelevant.
    def rows(prefix, cohort_name, ring, n, failed):
        return [DeviceHealth(f"{prefix}-{i}", cohort_name, ring, i < failed, None) for i in range(n)]

    devices = rows("t", "treated", "canary", 20_000, 300) + rows("c", "control", "fleet", 20_000, 100)

    assert judge(devices).status == "healthy"


def test_missing_boot_telemetry_is_not_a_failure():
    devices = [DeviceHealth(d.device_id, d.cohort, d.ring, d.failed, None) for d in fleet()]

    assert judge(devices).status == "healthy"


def test_policy_rejects_cohorts_too_small_for_the_statistics():
    with pytest.raises(ValueError):
        GatePolicy(min_devices=3)
