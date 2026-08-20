#!/usr/bin/env python3
"""One-shot transform: migrate migration/baseline/openapi-python.json from the
frozen Python FastAPI ``{ks_id}`` form to the .NET ``{id:guid}`` form so the
ApiContract.OpenApiInventoryTests gate stops reporting the backend sweep as
drift.

The Python backend is deprecated; rerunning
``backend/scripts/export_contract_baseline.py`` would regenerate the OLD
``{ks_id}`` form, so we rewrite the frozen artifact in place. The original
file is produced by FastAPI's ``app.openapi()`` JSON encoder, which emits
compact JSON (``separators=(",", ":")``, no indentation, sorted keys, trailing
newline). The output of this script preserves that exact byte layout so
``git diff`` is purely a content change and reviewers can scan only the keys
that moved.

Rules applied (see .superpowers/sdd/2026-08-20-guid-primary-key-migration/task-24-brief.md):

1. ``paths`` keys: ``{ks_id}`` -> ``{id}`` (also in components-level path
   templates, which FastAPI doesn't emit, but defensive).
2. Every operation ``operationId``: ``__ks_id__`` -> ``__id__``.
3. Every path parameter named ``ks_id``: rename to ``id`` and rewrite the
   inline schema from ``{"type": "integer", ...}`` to
   ``{"type": "string", "format": "uuid"}`` (dropping any
   ``minimum``/``maximum``/``exclusiveMinimum``/``exclusiveMaximum``
   integer-only constraints).
4. Component-schemas whose auto-generated name contains ``__ks_id__``
   (FastAPI's body wrapper names): rename the key + update the matching
   ``$ref`` pointers that point at them (defensive; the current baseline
   happens not to have such refs, but the rename is symmetric).
5. Schema ``title`` of those path parameters: ``"Ks Id"`` -> ``"Id"``.

Everything else is preserved byte-for-byte.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
BASELINE = REPO_ROOT / "migration" / "baseline" / "openapi-python.json"

INT_CONSTRAINT_KEYS = (
    "minimum",
    "maximum",
    "exclusiveMinimum",
    "exclusiveMaximum",
    "multipleOf",
)


def _rewrite_path_param_schema(schema: dict[str, Any]) -> dict[str, Any]:
    """Rewrite an integer ``ks_id`` path-parameter schema to a uuid string."""
    rewritten = dict(schema)
    if rewritten.get("type") == "integer":
        rewritten["type"] = "string"
        rewritten["format"] = "uuid"
    for key in INT_CONSTRAINT_KEYS:
        rewritten.pop(key, None)
    if rewritten.get("title") == "Ks Id":
        rewritten["title"] = "Id"
    return rewritten


def _rewrite_ref(ref: str) -> str:
    """Rewrite a ``#/components/schemas/X`` pointer when X changed."""
    prefix = "#/components/schemas/"
    if ref.startswith(prefix):
        return prefix + ref[len(prefix):].replace("__ks_id__", "__id__")
    return ref


def _walk_rewrite_refs(value: Any) -> Any:
    """Recursively rewrite every ``$ref`` string under ``value``."""
    if isinstance(value, dict):
        new_dict: dict[str, Any] = {}
        for k, v in value.items():
            if k == "$ref" and isinstance(v, str):
                new_dict[k] = _rewrite_ref(v)
            else:
                new_dict[k] = _walk_rewrite_refs(v)
        return new_dict
    if isinstance(value, list):
        return [_walk_rewrite_refs(v) for v in value]
    return value


def _rewrite_operation(operation: dict[str, Any]) -> dict[str, Any]:
    new_op = _walk_rewrite_refs(operation)
    if "operationId" in new_op and isinstance(new_op["operationId"], str):
        new_op["operationId"] = new_op["operationId"].replace("__ks_id__", "__id__")
    params = new_op.get("parameters")
    if isinstance(params, list):
        new_params: list[Any] = []
        for p in params:
            if not isinstance(p, dict):
                new_params.append(p)
                continue
            pname = p.get("name")
            if pname == "ks_id":
                rewritten = dict(p)
                rewritten["name"] = "id"
                if isinstance(rewritten.get("schema"), dict):
                    rewritten["schema"] = _rewrite_path_param_schema(rewritten["schema"])
                new_params.append(rewritten)
            else:
                new_params.append(p)
        new_op["parameters"] = new_params
    return new_op


def _rewrite_paths(paths: dict[str, Any]) -> dict[str, Any]:
    new_paths: dict[str, Any] = {}
    for route, ops in paths.items():
        new_route = route.replace("{ks_id}", "{id}")
        if not isinstance(ops, dict):
            new_paths[new_route] = ops
            continue
        new_ops: dict[str, Any] = {}
        for verb, body in ops.items():
            if isinstance(body, dict):
                new_ops[verb] = _rewrite_operation(body)
            else:
                new_ops[verb] = body
        new_paths[new_route] = new_ops
    return new_paths


def _rewrite_components(components: dict[str, Any]) -> dict[str, Any]:
    if not components:
        return components
    new_components = _walk_rewrite_refs(components)
    schemas = new_components.get("schemas")
    if isinstance(schemas, dict):
        new_schemas: dict[str, Any] = {}
        for name, body in schemas.items():
            new_name = name.replace("__ks_id__", "__id__")
            new_body = body
            if isinstance(body, dict) and body.get("title") == name:
                new_body = dict(body)
                new_body["title"] = new_name
            new_schemas[new_name] = new_body
        new_components["schemas"] = new_schemas
    return new_components


def transform(doc: dict[str, Any]) -> dict[str, Any]:
    new_doc = dict(doc)
    if "paths" in new_doc:
        new_doc["paths"] = _rewrite_paths(new_doc["paths"])
    if "components" in new_doc:
        new_doc["components"] = _rewrite_components(new_doc["components"])
    return new_doc


def main() -> int:
    if not BASELINE.exists():
        print(f"baseline not found: {BASELINE}", file=sys.stderr)
        return 1
    with BASELINE.open("r", encoding="utf-8") as fh:
        doc = json.load(fh)
    rewritten = transform(doc)
    with BASELINE.open("w", encoding="utf-8", newline="\n") as fh:
        json.dump(
            rewritten,
            fh,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
        )
        fh.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
