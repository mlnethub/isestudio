"""Export the Python chunker output as a deterministic JSON manifest.

The .NET migration plan (``docs/superpowers/plans/2026-08-16-ontopilot-dotnet-documents-llm.md``,
Task 2) freezes the text-only chunking behaviour by capturing a small set of inputs and the
spans produced by ``app.parsing.chunker.chunk_text``. The .NET parity tests
(``OnToPilot.Tests.Parsing.ChunkerParityTests``) load the resulting JSON and assert byte-for-byte
equality per span.

The manifest must be deterministic: re-running this script produces byte-identical output so
that ``dotnet test`` can verify the .NET port without spurious diffs.

Run from the repository root::

    cd backend
    python scripts/export_parsing_fixtures.py ../migration/fixtures/parsing/manifest.json
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import asdict
from pathlib import Path
from typing import Any

# Allow `python scripts/export_parsing_fixtures.py ...` to find the `app` package.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


# Three representative cases covering Latin-only, CJK-only and mixed-script text.
# Inputs are intentionally short so the manifest is human-readable while still exercising
# paragraph splitting, sentence-boundary alignment, and overlap.
_CASES: dict[str, str] = {
    "english": "First sentence. Second sentence.\n\nThird paragraph.",
    "chinese": "第一句。第二句。\n\n第三段。",
    "mixed": "Pump P-101 温度为 80°C。Next sentence.",
}

# Fixed chunking parameters — the .NET port must match these exactly.
_SIZE = 24
_OVERLAP = 6


def _canonical(value: Any) -> bytes:
    """Encode ``value`` as compact UTF-8 JSON with sorted keys and a trailing newline.

    Sorted keys make the JSON diff-friendly even though our top-level object already has
    fixed insertion order.
    """
    text = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return (text + "\n").encode("utf-8")


def export_manifest() -> dict[str, Any]:
    """Run the Python chunker on each case and assemble the manifest payload.

    The manifest schema is::

        {
            "size": 24,
            "overlap": 6,
            "cases": {
                "english": [{"idx": ..., "text": ..., "char_start": ..., "char_end": ..., "token_estimate": ...}, ...],
                "chinese":  [...],
                "mixed":    [...]
            }
        }

    Returns the in-memory manifest (also written to disk by :func:`main`).
    """
    # Import here so the module is loadable without ``app.config`` side-effects during --help.
    from app.parsing.chunker import chunk_text

    cases: dict[str, list[dict[str, Any]]] = {}
    for name, text in _CASES.items():
        spans = chunk_text(text, size=_SIZE, overlap=_OVERLAP)
        cases[name] = [asdict(span) for span in spans]

    return {"size": _SIZE, "overlap": _OVERLAP, "cases": cases}


def _parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "output",
        type=Path,
        help="Path that will receive the parsing manifest JSON.",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(sys.argv[1:] if argv is None else argv)
    manifest = export_manifest()
    output: Path = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(_canonical(manifest))
    total_spans = sum(len(spans) for spans in manifest["cases"].values())
    print(
        f"wrote {total_spans} spans across {len(manifest['cases'])} cases to {output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())