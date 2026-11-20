"""Pins the wire format shared with the C# control plane."""

import json
from datetime import timedelta
from pathlib import Path

import pytest

from blackcanary.gate import Verdict

EXAMPLE = json.loads((Path(__file__).parents[2] / "contracts" / "verdict.example.json").read_text())


def test_example_round_trips_unchanged():
    assert Verdict.from_json(EXAMPLE).to_json() == EXAMPLE


def test_timestamp_carries_an_explicit_offset():
    assert Verdict.from_json(EXAMPLE).evaluated_at.utcoffset() == timedelta(0)


def test_unknown_fields_and_statuses_are_rejected():
    with pytest.raises(ValueError):
        Verdict.from_json({**EXAMPLE, "rnig": "typo"})
    with pytest.raises(ValueError):
        Verdict.from_json({**EXAMPLE, "status": "fine"})
