#!/usr/bin/env python3
"""Rewrite `store.Match(...)` patterns to `db.MatchPostgres(ksId, RdfLayer, ...)`."""
import re
import sys
from pathlib import Path

FILES = [
    "src/ISEStudio.Tests/Ontology/ABoxApiTests.cs",
    "src/ISEStudio.Tests/Ontology/ABoxAssertionApiTests.cs",
    "src/ISEStudio.Tests/Ontology/ABoxValidationApiTests.cs",
    "src/ISEStudio.Tests/Ontology/HistoryServiceTests.cs",
    "src/ISEStudio.Tests/Ontology/OntologyApiTests.cs",
    "src/ISEStudio.Tests/Ontology/VocabularyApiTests.cs",
]

ROOT = Path(r"E:\GitHub\ontopilot")

def migrate(path: Path):
    text = path.read_text(encoding="utf-8")
    original = text

    # Pattern 1: `var store = app.Services.GetRequiredService<StoreWrapper>();`
    # → `var db = app.CreateDbContext();`
    text = re.sub(
        r"\bvar\s+store\s*=\s*app\.Services\.GetRequiredService<StoreWrapper>\(\);",
        "var db = app.CreateDbContext();",
        text,
    )

    # Pattern 2: replace `store.Match(...)` with `db.MatchPostgres(ksId, RdfLayer.XXX, ...)`
    # Determine the layer from surrounding context (aboxGraph, tboxGraph, vocabGraph).
    def replacer(match):
        call = match.group(0)
        # Inspect preceding lines for context.
        # Heuristic: abox → ABox, tbox → TBox, vocab → Vocabulary.
        # Default: TBox (most common for ontology.* tests).
        before = text[: match.start()]
        # Look back at most 200 chars for layer hints.
        window = before[-400:]
        layer = "TBox"
        if "aboxGraph" in window or "LookupKsAbbox" in window or "abox" in window.split("\n")[-1].lower():
            layer = "ABox"
        elif "tboxGraph" in window or "LookupKsTbox" in window:
            layer = "TBox"
        elif "vocabGraph" in window or "vocabularyGraph" in window or "LookupKsVocab" in window:
            layer = "Vocabulary"
        # Replace store.Match → db.MatchPostgres(ksId, RdfLayer.XXX,
        new_call = call.replace("store.Match", f"db.MatchPostgres(ksId, RdfLayer.{layer}", 1)
        # Add trailing ")" if not present (call.Match already has balanced parens).
        return new_call

    text = re.sub(r"\bstore\.Match\(", replacer, text)

    # Pattern 3: imports — ensure using ISEStudio.Tests.Infrastructure; exists.
    if "PostgresMatchExtensions" not in text:
        # Insert before the last `using` block end.
        m = list(re.finditer(r"^using\s+[^;]+;", text, re.MULTILINE))
        if m:
            last = m[-1]
            insert_at = last.end()
            text = text[:insert_at] + "\nusing ISEStudio.Tests.Infrastructure;" + text[insert_at:]

    if text != original:
        path.write_text(text, encoding="utf-8")
        return True
    return False

if __name__ == "__main__":
    changed = []
    for rel in FILES:
        p = ROOT / rel
        if not p.exists():
            print(f"SKIP (missing): {rel}")
            continue
        if migrate(p):
            changed.append(rel)
            print(f"updated: {rel}")
        else:
            print(f"unchanged: {rel}")
    print(f"\n{len(changed)} file(s) updated")
