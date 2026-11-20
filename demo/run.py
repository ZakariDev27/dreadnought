#!/usr/bin/env python3
"""Guided end-to-end demo of Black Canary.

Starts the C# control plane against ``demo/fleet`` and drives it with the Python
analytics package: devices check in and drift is scored, a guarded remediation
plan is drawn up, one rollout is walked through every ring, and a second one is
stopped by its own canary. Every step is asserted, so this doubles as the
end-to-end test in CI: any surprise exits non-zero.

    python demo/run.py                # start a control plane, run, tear down
    python demo/run.py --url URL      # use one that is already running
"""

from __future__ import annotations

import argparse
import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "analytics" / "src"))

from blackcanary.client import ControlPlaneClient, ControlPlaneError  # noqa: E402
from blackcanary.gate import evaluate_ring  # noqa: E402
from blackcanary.remediation import Executor, Finding, Guard, RulePlanner, Tool, plan  # noqa: E402
from blackcanary.simulate import cohort  # noqa: E402
from blackcanary.telemetry import summarise  # noqa: E402

COLOUR = sys.stdout.isatty() and "NO_COLOR" not in os.environ


def paint(code: str, text: str) -> str:
    return f"\033[{code}m{text}\033[0m" if COLOUR else text


def heading(text: str) -> None:
    print(f"\n{paint('1;33', '━━ ' + text + ' ')}{paint('33', '━' * max(4, 66 - len(text)))}")


def step(text: str) -> None:
    print(f"  {paint('36', '▸')} {text}")


def good(text: str) -> None:
    print(f"    {paint('32', '✓')} {text}")


def blocked(text: str) -> None:
    print(f"    {paint('31', '✗')} {text}")


def expect(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"demo expectation failed: {message}")


def refused(call, *, status: int) -> str:
    """Run a call that must be rejected by the control plane and return its reason."""
    try:
        call()
    except ControlPlaneError as error:
        expect(error.status == status, f"expected HTTP {status}, got {error.status}")
        return error.detail
    raise SystemExit("demo expectation failed: the control plane accepted a call it must refuse")


# ---------------------------------------------------------------------------


def start_control_plane(port: int) -> subprocess.Popen[bytes]:
    env = os.environ | {
        "ASPNETCORE_ENVIRONMENT": "Development",  # loads the non-secret dev API keys
        "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
        "BlackCanary__FleetPath": str(ROOT / "demo" / "fleet"),
        "Logging__LogLevel__Default": "Warning",
    }
    return subprocess.Popen(
        ["dotnet", "run", "--no-launch-profile", "--project", str(ROOT / "src" / "BlackCanary.ControlPlane")],
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        start_new_session=True,
    )


def wait_until_healthy(url: str, process: subprocess.Popen[bytes] | None, timeout: float = 180) -> None:
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            raise SystemExit(f"control plane exited early:\n{process.stderr.read().decode()}")  # type: ignore[union-attr]
        try:
            with opener.open(f"{url}/healthz", timeout=2):
                return
        except (urllib.error.URLError, OSError):
            time.sleep(0.5)
    raise SystemExit("control plane did not become healthy in time")


# ---------------------------------------------------------------------------

COMPLIANT = {
    "disk.encryption": "enabled",
    "firewall.enabled": "true",
    "screenlock.timeoutSeconds": "300",
    "os.autoUpdate": "true",
    "gatekeeper.enabled": "true",
    "browser.extensionAllowlist": "enforced",
    "browser.autoUpdate": "true",
}


def act_drift(agent: ControlPlaneClient, author: ControlPlaneClient) -> list[Finding]:
    heading("1. Devices check in; drift is scored against config-as-code")

    fleet = {f"mac-{i:03d}": dict(COMPLIANT) for i in range(1, 25)}
    for name in ("mac-004", "mac-011", "mac-019"):
        fleet[name]["firewall.enabled"] = "false"
    fleet["mac-011"].pop("disk.encryption")
    fleet["mac-017"]["screenlock.timeoutSeconds"] = "3600"

    reports = {
        name: agent.check_in(name, {"platform": "macOS", "tier": "standard", "osVersion": "15.1", "observedSettings": s})
        for name, s in fleet.items()
    }
    step(f"{len(fleet)} macOS devices reported their settings")

    summary = author.compliance()
    expect(summary["devices"] == 24 and summary["compliant"] == 20, "20 of 24 devices should be compliant")
    good(f"{summary['compliant']}/{summary['devices']} compliant, mean score {summary['meanScore']:.1%}")
    for spot in summary["hotspots"]:
        print(f"      hotspot  {spot['key']:<28} {spot['severity']:<9} {spot['devices']} device(s)")

    worst = reports["mac-011"]
    expect(worst["complianceScore"] < 0.6, "a device missing disk encryption should score badly")
    step(f"worst device: mac-011 at {worst['complianceScore']:.0%} (severity-weighted)")
    for f in worst["findings"]:
        found = "not reported" if f["actual"] is None else f"'{f['actual']}'"
        print(f"      [{f['severity']:<8}] {f['key']}: expected '{f['expected']}', {found}")

    return [Finding("mac-011", f["key"], f["expected"], f["actual"], f["severity"]) for f in worst["findings"]]


def act_remediation(findings: list[Finding]) -> None:
    heading("2. Guarded remediation: the planner proposes, policy decides")

    applied: list[str] = []

    def record(name: str):
        return lambda device, args: applied.append(f"{name}({device})")

    guard = Guard(
        [
            Tool("apply_setting", "device:configure", frozenset({"key", "value"}), False, record("apply_setting"), record("undo")),
            Tool("enable_disk_encryption", "device:encrypt", frozenset(), True, record("enable_disk_encryption")),
        ],
        granted_scopes=frozenset({"device:configure", "device:encrypt"}),
    )
    decisions = plan(findings, RulePlanner(), guard)
    for d in decisions:
        mark = {"allowed": good, "needs_review": blocked, "rejected": blocked}[d.disposition]
        mark(f"{d.proposal.tool:<24} {d.disposition:<13} {d.proposal.rationale}")

    executor = Executor(guard)
    step("execute without a human approver")
    trace = executor.run(decisions, dry_run=False)
    for t in trace:
        print(f"      step {t.step}  {t.tool:<24} {t.outcome:<8} {t.detail}")
    expect("enable_disk_encryption(mac-011)" not in applied, "sensitive tool must not run unapproved")
    expect("apply_setting(mac-011)" in applied, "routine fix should have run")

    step("grace signs off the sensitive step")
    sensitive = [i for i, d in enumerate(decisions) if d.disposition == "needs_review"]
    trace = executor.run([decisions[i] for i in sensitive], approved_by={0: "grace"}, dry_run=False)
    expect(trace[0].outcome == "done", "approved sensitive step should run")
    good(f"{trace[0].tool} done — {trace[0].detail}")


def telemetry(ring: str, *, failure_rate: float, boot: float, seed: int):
    return summarise(
        cohort(prefix=f"{ring}", cohort="treated", ring=ring, devices=80, failure_rate=failure_rate, boot_median=boot, seed=seed)
        + cohort(prefix="holdback", cohort="control", ring="fleet", devices=240, failure_rate=0.02, boot_median=24.0, seed=seed + 1)
    )


def show_verdict(verdict) -> None:
    mark = good if verdict.status == "healthy" else blocked
    mark(f"gate: {verdict.status} (n={verdict.sample_size})")
    for reason in verdict.reasons:
        print(f"        {reason}")


def act_good_rollout(author, reviewer1, reviewer2, analytics) -> None:
    heading("3. A sensitive change earns its way across the fleet")
    rid = "r-macos-core-3"

    rollout = author.create_rollout(rid, "macos-core", 3)
    expect(rollout["sensitive"] and rollout["state"] == "awaitingApproval", "sensitive rollout must await approval")
    step(f"ada opens {rid} for {rollout['changeId']} — sensitive, so two approvals required")

    blocked(f"ada tries to start it: {refused(lambda: author.start(rid), status=409)}")
    blocked(f"ada (author-only key) tries to approve: {refused(lambda: author.approve(rid), status=403)}")
    reviewer1.approve(rid)
    blocked(f"grace approves twice: {refused(lambda: reviewer1.approve(rid), status=409)}")
    rollout = reviewer2.approve(rid)
    expect(rollout["state"] == "approved", "two distinct approvals should approve")
    good(f"approved by {', '.join(rollout['approvers'])}")

    rollout = author.start(rid)
    step(f"started in ring '{rollout['currentRing']['name']}' ({rollout['currentRing']['cumulativePercent']:g}% of fleet)")
    blocked(f"advance with no evidence: {refused(lambda: author.advance(rid), status=409)}")

    for index, ring in enumerate(["canary", "broad", "fleet"]):
        verdict = evaluate_ring(telemetry(ring, failure_rate=0.02, boot=24.0, seed=100 + index), rollout_id=rid, ring=ring)
        expect(verdict.status == "healthy", f"{ring} should be healthy")
        analytics.post_verdict(verdict)
        show_verdict(verdict)

        if ring == "canary":
            blocked(f"advance immediately: {refused(lambda: author.advance(rid), status=409)}")
            time.sleep(3.2)

        rollout = author.advance(rid)
        where = "completed" if rollout["state"] == "completed" else f"→ ring '{rollout['currentRing']['name']}'"
        good(f"advanced {where}")

    expect(rollout["state"] == "completed", "rollout should complete")


def act_bad_rollout(author, reviewer1, analytics) -> None:
    heading("4. A bad change is stopped by its own canary")
    rid = "r-macos-browser-1"

    author.create_rollout(rid, "macos-browser", 1)
    reviewer1.approve(rid)
    author.start(rid)
    step(f"{rid} approved (standard change, one reviewer) and live in 'canary'")

    step("this build crashes on ~1 in 4 machines and slows boot by a third")
    verdict = evaluate_ring(telemetry("canary", failure_rate=0.25, boot=32.0, seed=200), rollout_id=rid, ring="canary")
    expect(verdict.status == "unhealthy", "bad canary must be unhealthy")
    show_verdict(verdict)

    rollout = analytics.post_verdict(verdict)
    expect(rollout["state"] == "rolledBack", "unhealthy verdict must roll back")
    good("rolled back automatically — 95% of the fleet never saw it")
    blocked(f"ada tries to push on anyway: {refused(lambda: author.advance(rid), status=409)}")


def act_audit(author) -> None:
    heading("5. Every action is on a tamper-evident record")
    entries = author.audit()
    for e in entries:
        print(f"    {e['sequence']:>2}  {e['hash'][:10]}  {e['actor']:<13} {e['action']:<20} {e['subject']:<18} {e['detail'][:44]}")
    check = author.verify_audit()
    expect(check["intact"] and check["entries"] == len(entries), "audit chain must verify")
    good(f"hash chain verified across {check['entries']} entries")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", help="use an already-running control plane (Development environment)")
    parser.add_argument("--port", type=int, default=5099)
    args = parser.parse_args()

    process = None if args.url else start_control_plane(args.port)
    url = args.url or f"http://127.0.0.1:{args.port}"
    try:
        print(paint("1", "Black Canary") + " — fleet configuration as code, with rollouts that have to earn their way")
        wait_until_healthy(url, process)

        author = ControlPlaneClient(url, "dev-author")
        reviewer1 = ControlPlaneClient(url, "dev-reviewer-1")
        reviewer2 = ControlPlaneClient(url, "dev-reviewer-2")
        agent = ControlPlaneClient(url, "dev-agent")
        analytics = ControlPlaneClient(url, "dev-analytics")

        findings = act_drift(agent, author)
        act_remediation(findings)
        act_good_rollout(author, reviewer1, reviewer2, analytics)
        act_bad_rollout(author, reviewer1, analytics)
        act_audit(author)

        print(f"\n{paint('1;32', 'Demo complete.')} Every step above was asserted.")
        return 0
    finally:
        if process is not None:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=15)


if __name__ == "__main__":
    sys.exit(main())
