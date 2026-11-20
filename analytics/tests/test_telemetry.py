import json

import pytest

from blackcanary.simulate import cohort, to_jsonl
from blackcanary.telemetry import TelemetryError, read_jsonl, summarise


def test_jsonl_round_trip(tmp_path):
    samples = cohort(
        prefix="t",
        cohort="treated",
        ring="canary",
        devices=5,
        failure_rate=0.5,
        boot_median=20,
        seed=1,
    )
    path = tmp_path / "telemetry.jsonl"
    path.write_text(to_jsonl(samples) + "\n")  # trailing blank line is tolerated

    assert list(read_jsonl(path)) == samples


def test_bad_record_reports_file_and_line(tmp_path):
    good = to_jsonl(
        cohort(
            prefix="t",
            cohort="treated",
            ring="canary",
            devices=1,
            failure_rate=0,
            boot_median=20,
            seed=1,
            checkins=1,
        )
    )
    path = tmp_path / "telemetry.jsonl"
    path.write_text(good + json.dumps({"deviceId": "x", "cohort": "treated"}) + "\n")

    with pytest.raises(TelemetryError, match=r"telemetry\.jsonl:2: missing field 'ring'"):
        list(read_jsonl(path))


def test_unknown_cohort_is_rejected(tmp_path):
    path = tmp_path / "t.jsonl"
    path.write_text(json.dumps({"deviceId": "x", "cohort": "placebo"}) + "\n")

    with pytest.raises(TelemetryError, match="unknown cohort"):
        list(read_jsonl(path))


def test_summarise_marks_a_device_failed_if_any_checkin_failed():
    samples = cohort(
        prefix="t",
        cohort="treated",
        ring="canary",
        devices=3,
        failure_rate=1.0,
        boot_median=20,
        seed=1,
    )

    rows = summarise(samples)

    assert [r.device_id for r in rows] == ["t-0000", "t-0001", "t-0002"]
    assert all(r.failed for r in rows)
    assert all(r.boot_seconds is not None for r in rows)
