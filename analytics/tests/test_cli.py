import json

from dreadnought.cli import EXIT_ERROR, EXIT_HEALTHY, EXIT_INCONCLUSIVE, EXIT_UNHEALTHY, main
from dreadnought.simulate import cohort, to_jsonl


def write(tmp_path, *, treated_failure, treated=60):
    samples = cohort(
        prefix="t",
        cohort="treated",
        ring="canary",
        devices=treated,
        failure_rate=treated_failure,
        boot_median=24,
        seed=11,
    ) + cohort(
        prefix="c",
        cohort="control",
        ring="fleet",
        devices=200,
        failure_rate=0.02,
        boot_median=24,
        seed=12,
    )
    path = tmp_path / "telemetry.jsonl"
    path.write_text(to_jsonl(samples))
    return str(path)


def test_gate_exit_code_follows_the_verdict(tmp_path, capsys):
    args = ["--rollout", "r-1", "--ring", "canary"]

    assert main(["gate", write(tmp_path, treated_failure=0.02), *args]) == EXIT_HEALTHY
    assert json.loads(capsys.readouterr().out)["status"] == "healthy"

    assert main(["gate", write(tmp_path, treated_failure=0.35), *args]) == EXIT_UNHEALTHY
    assert main(["gate", write(tmp_path, treated_failure=0.02, treated=5), *args]) == EXIT_INCONCLUSIVE


def test_missing_file_is_an_error_not_a_verdict(tmp_path, capsys):
    code = main(["gate", str(tmp_path / "nope.jsonl"), "--rollout", "r", "--ring", "canary"])

    assert code == EXIT_ERROR
    assert "error:" in capsys.readouterr().err


def test_post_without_a_key_fails_closed(tmp_path, monkeypatch, capsys):
    monkeypatch.delenv("DREADNOUGHT_API_KEY", raising=False)
    path = write(tmp_path, treated_failure=0.02)

    code = main(["gate", path, "--rollout", "r", "--ring", "canary", "--post", "http://127.0.0.1:9"])

    assert code == EXIT_ERROR
    assert "DREADNOUGHT_API_KEY" in capsys.readouterr().err
