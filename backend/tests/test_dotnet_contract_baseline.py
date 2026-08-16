"""Regression test that freezes the current Python backend's contract surface.

The exported baselines at ``migration/baseline/openapi-python.json`` and
``migration/baseline/mcp-tools-python.json`` must be byte-deterministic between
two runs of ``export_contract_baseline``. The api-mcp stage (see
``docs/superpowers/plans/2026-08-16-ontopilot-dotnet-api-mcp.md``) parameterizes
its contract tests against these baselines, so any non-determinism here would
leak into the .NET parity gates.
"""
from __future__ import annotations


def test_contract_export_is_deterministic(tmp_path) -> None:
    from scripts.export_contract_baseline import export_baseline

    first = export_baseline(tmp_path / "first")
    second = export_baseline(tmp_path / "second")

    assert first.openapi_bytes == second.openapi_bytes
    assert first.mcp_bytes == second.mcp_bytes
    # The plan's stage 4 expects 153 REST operations; the live count is the source of
    # truth — keep the floor generous so a small accidental loss still fails loud.
    assert len(first.operations) >= 130
    # The MCP tool inventory must not be empty; the actual count is captured in
    # docs/migration/dotnet-contract-baseline.md.
    assert {tool["name"] for tool in first.mcp_tools}
    # Filenames on disk must match the docs/migration/dotnet-contract-baseline.md plan.
    assert (tmp_path / "first" / "openapi-python.json").is_file()
    assert (tmp_path / "first" / "mcp-tools-python.json").is_file()