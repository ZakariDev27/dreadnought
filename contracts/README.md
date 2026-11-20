# Contracts

Documents that cross the C# / Python boundary. Each example here is deserialised by a
test on **both** sides (`ContractTests.cs`, `test_contract.py`), so a field renamed in
one language fails the build in the other.

| File | Producer | Consumer |
| --- | --- | --- |
| `verdict.example.json` | `blackcanary.gate` | `POST /v1/rollouts/{id}/verdicts` |
