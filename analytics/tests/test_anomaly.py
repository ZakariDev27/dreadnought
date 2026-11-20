import pytest

from blackcanary.anomaly import ewma_breaches, fleet_outliers


def test_fleet_outliers_finds_the_slow_machines_worst_first():
    boots = {f"mac-{i:02d}": 22.0 + (i % 5) for i in range(40)}
    boots["mac-slow"] = 95.0
    boots["mac-dying"] = 400.0

    found = fleet_outliers(boots)

    assert [o.device_id for o in found] == ["mac-dying", "mac-slow"]
    assert found[0].score > found[1].score > 3.5


def test_fleet_outliers_ignores_unusually_good_values():
    boots = {f"mac-{i:02d}": 22.0 + (i % 5) for i in range(40)} | {"mac-fast": 1.0}

    assert fleet_outliers(boots) == []


def test_ewma_chart_catches_a_slow_drift_no_single_point_would():
    stable = [50.0, 51.0, 49.0, 50.0, 52.0, 48.0, 50.0, 51.0, 49.0, 50.0]
    drift = [50.0 + 0.6 * step for step in range(1, 15)]  # every point within 3 sigma of the last

    breaches = ewma_breaches(stable + drift)

    assert breaches, "a sustained drift must be flagged"
    assert breaches[0] > len(stable)
    assert breaches == list(range(breaches[0], len(stable) + len(drift)))


def test_ewma_chart_stays_quiet_on_stable_data():
    series = [50.0, 51.0, 49.0, 50.0, 52.0, 48.0, 50.0, 51.0, 49.0, 50.0] * 3

    assert ewma_breaches(series) == []


def test_ewma_chart_edge_cases():
    assert ewma_breaches([1.0] * 5) == []  # shorter than the baseline
    assert ewma_breaches([5.0] * 10 + [5.0, 6.0]) == [11]  # flat baseline: any change is a breach
    with pytest.raises(ValueError):
        ewma_breaches([1.0, 2.0, 3.0], baseline=1)
