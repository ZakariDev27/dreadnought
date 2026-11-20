"""Command line entry point: ``blackcanary-analytics``."""

from __future__ import annotations

import argparse
import json
import os
import sys
from collections.abc import Sequence
from pathlib import Path

from .anomaly import fleet_outliers
from .client import ControlPlaneClient, ControlPlaneError
from .gate import GatePolicy, evaluate_ring
from .telemetry import TelemetryError, read_jsonl, summarise

# Exit codes let a pipeline branch on the verdict without parsing JSON.
EXIT_HEALTHY, EXIT_UNHEALTHY, EXIT_INCONCLUSIVE, EXIT_ERROR = 0, 1, 3, 2


def _gate(args: argparse.Namespace) -> int:
    devices = summarise(read_jsonl(args.telemetry))
    policy = GatePolicy(min_devices=args.min_devices, alpha=args.alpha)
    verdict = evaluate_ring(devices, rollout_id=args.rollout, ring=args.ring, policy=policy)
    print(json.dumps(verdict.to_json(), indent=2))

    if args.post:
        key = os.environ.get("BLACKCANARY_API_KEY")
        if not key:
            print("error: --post needs BLACKCANARY_API_KEY in the environment", file=sys.stderr)
            return EXIT_ERROR
        rollout = ControlPlaneClient(args.post, key).post_verdict(verdict)
        print(f"posted; rollout is now {rollout['state']}", file=sys.stderr)

    return {"healthy": EXIT_HEALTHY, "unhealthy": EXIT_UNHEALTHY, "inconclusive": EXIT_INCONCLUSIVE}[
        verdict.status
    ]


def _outliers(args: argparse.Namespace) -> int:
    boots = {d.device_id: d.boot_seconds for d in summarise(read_jsonl(args.telemetry)) if d.boot_seconds}
    found = fleet_outliers(boots, args.threshold)
    for outlier in found:
        print(f"{outlier.device_id}\tboot {outlier.value:.1f}s\tscore {outlier.score}")
    print(f"{len(found)} outlier(s) among {len(boots)} devices", file=sys.stderr)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="blackcanary-analytics", description=__doc__)
    commands = parser.add_subparsers(required=True)

    gate = commands.add_parser("gate", help="judge one rollout ring from telemetry")
    gate.add_argument("telemetry", type=Path, help="JSON-lines telemetry file")
    gate.add_argument("--rollout", required=True)
    gate.add_argument("--ring", required=True)
    gate.add_argument("--min-devices", type=int, default=GatePolicy.min_devices)
    gate.add_argument("--alpha", type=float, default=GatePolicy.alpha)
    gate.add_argument("--post", metavar="URL", help="also submit the verdict to this control plane")
    gate.set_defaults(handler=_gate)

    outliers = commands.add_parser("outliers", help="list devices with abnormal boot times")
    outliers.add_argument("telemetry", type=Path)
    outliers.add_argument("--threshold", type=float, default=3.5)
    outliers.set_defaults(handler=_outliers)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return int(args.handler(args))
    except (TelemetryError, ControlPlaneError, OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return EXIT_ERROR


if __name__ == "__main__":
    sys.exit(main())
