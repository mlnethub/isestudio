"""Export the current Python backend's contract surface as deterministic JSON.

The api-mcp stage of the .NET migration plan parameterizes its parity tests
against ``migration/baseline/openapi-python.json`` and
``migration/baseline/mcp-tools-python.json``. This script is the single source of
those artifacts and must run byte-identically twice in a row so subsequent diff
comparisons stay meaningful.

Run from the repository root::

    cd backend
    python scripts/export_contract_baseline.py ../migration/baseline
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
from dataclasses import dataclass
from pathlib import Path

# Allow `python scripts/export_contract_baseline.py ...` to find the `app` package.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


_HTTP_METHODS = {"get", "post", "put", "patch", "delete"}


@dataclass(frozen=True)
class Baseline:
    openapi: dict
    mcp_tools: list[dict]
    operations: list[tuple[str, str]]
    openapi_bytes: bytes
    mcp_bytes: bytes


def _canonical(value: object) -> bytes:
    """Encode ``value`` as compact UTF-8 JSON with sorted keys and a trailing newline."""
    text = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return (text + "\n").encode("utf-8")


def _read_openapi() -> dict:
    """Import the FastAPI ``app`` lazily so this module can be loaded without a DB."""
    from app.main import app  # imported here to avoid hitting the DB at module load

    schema = app.openapi()
    # FastAPI may inject an ``operationId`` derived from ``path`` + ``method`` when none is
    # declared; it is stable across runs because both inputs are stable. No further stripping
    # is required today. Should non-deterministic fields appear later (timestamps, generated
    # IDs), drop them here and note the change in docs/migration/dotnet-contract-baseline.md.
    return schema


def _read_tools_list_response() -> list[dict]:
    """Invoke the MCP ``tools/list`` handler and return a plain-list payload.

    ``mcp.list_tools()`` is async; ``mcp_app`` already exposes the protocol response
    via ``tools/list`` JSON-RPC, and ``tests/test_mcp.py::test_streamable_http_lists_authenticated_tools``
    asserts the same 20-tool surface. Calling ``list_tools()`` directly sidesteps the
    Streamable HTTP transport while still going through FastMCP's tool registry.
    """
    from app.mcp_server import mcp

    tools = asyncio.run(mcp.list_tools())
    return [tool.model_dump() for tool in tools]


def export_baseline(output: Path) -> Baseline:
    """Write both baseline files into ``output`` and return the in-memory artefacts.

    The output directory is created if missing. Operations and tool entries are sorted
    before serialisation so two calls produce identical bytes.
    """
    output.mkdir(parents=True, exist_ok=True)

    openapi = _read_openapi()
    operations = sorted(
        (method.upper(), path)
        for path, methods in openapi["paths"].items()
        for method in methods
        if method.lower() in _HTTP_METHODS
    )
    tools = sorted(_read_tools_list_response(), key=lambda item: item["name"])

    openapi_bytes = _canonical(openapi)
    mcp_bytes = _canonical(tools)
    (output / "openapi-python.json").write_bytes(openapi_bytes)
    (output / "mcp-tools-python.json").write_bytes(mcp_bytes)

    return Baseline(openapi, tools, operations, openapi_bytes, mcp_bytes)


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "output",
        type=Path,
        help="Directory that will receive openapi-python.json and mcp-tools-python.json.",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(sys.argv[1:] if argv is None else argv)
    baseline = export_baseline(args.output.resolve())
    print(
        f"wrote {len(baseline.operations)} REST operations and "
        f"{len(baseline.mcp_tools)} MCP tools to {args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())