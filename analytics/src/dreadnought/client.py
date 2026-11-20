"""Thin HTTP client for the Dreadnought control plane (standard library only)."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any

from .gate import Verdict


class ControlPlaneError(RuntimeError):
    def __init__(self, status: int, detail: str) -> None:
        super().__init__(f"HTTP {status}: {detail}")
        self.status = status
        self.detail = detail


class ControlPlaneClient:
    """One client per API key; a key carries its own scopes."""

    def __init__(self, base_url: str, api_key: str, *, timeout: float = 10.0) -> None:
        if not base_url.startswith(("http://", "https://")):
            raise ValueError("base_url must be http(s)")
        self._base = base_url.rstrip("/")
        self._key = api_key
        self._timeout = timeout
        # Never route control-plane traffic (and its API key) through an ambient proxy.
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def _call(self, method: str, path: str, body: dict[str, Any] | None = None) -> Any:
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self._base + path, data=data, method=method)
        request.add_header("X-Api-Key", self._key)
        if data is not None:
            request.add_header("Content-Type", "application/json")

        try:
            with self._opener.open(request, timeout=self._timeout) as response:
                payload = response.read()
        except urllib.error.HTTPError as error:
            raise ControlPlaneError(error.code, _problem_detail(error.read())) from error

        return json.loads(payload) if payload else None

    def check_in(self, device_id: str, facts: dict[str, Any]) -> Any:
        return self._call("PUT", f"/v1/devices/{device_id}", facts)

    def compliance(self) -> Any:
        return self._call("GET", "/v1/fleet/compliance")

    def create_rollout(self, rollout_id: str, baseline: str, version: int) -> Any:
        return self._call(
            "POST", "/v1/rollouts", {"id": rollout_id, "baseline": baseline, "version": version}
        )

    def rollout(self, rollout_id: str) -> Any:
        return self._call("GET", f"/v1/rollouts/{rollout_id}")

    def approve(self, rollout_id: str) -> Any:
        return self._call("POST", f"/v1/rollouts/{rollout_id}/approvals")

    def start(self, rollout_id: str) -> Any:
        return self._call("POST", f"/v1/rollouts/{rollout_id}/start")

    def advance(self, rollout_id: str) -> Any:
        return self._call("POST", f"/v1/rollouts/{rollout_id}/advance")

    def post_verdict(self, verdict: Verdict) -> Any:
        return self._call("POST", f"/v1/rollouts/{verdict.rollout_id}/verdicts", verdict.to_json())

    def audit(self) -> Any:
        return self._call("GET", "/v1/audit")

    def verify_audit(self) -> Any:
        return self._call("GET", "/v1/audit/verify")


def _problem_detail(payload: bytes) -> str:
    try:
        problem = json.loads(payload)
        return str(problem.get("detail") or problem.get("title") or payload.decode(errors="replace"))
    except (json.JSONDecodeError, AttributeError):
        return payload.decode(errors="replace")
