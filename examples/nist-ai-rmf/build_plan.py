"""Build a SHA-bound edit plan from a native BRZ; never write a BRZ itself."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import tempfile
import uuid


EXAMPLE_DIR = Path(__file__).resolve().parent
REPO_ROOT = EXAMPLE_DIR.parents[1]
sys.path.insert(0, str(REPO_ROOT))

from brain_toolkit.archive import Archive, ArchiveError  # noqa: E402


# Opaque, pale ARGB colors for the seven top-level reading paths.
BRANCH_COLORS = (0xFFDBEAFE, 0xFFDCFCE7, 0xFFF3E8FF, 0xFFCFFAFE,
                 0xFFFEF3C7, 0xFFFFE4E6, 0xFFE0E7FF)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate content JSON key: {key}")
        result[key] = value
    return result


def reject_constant(value):
    raise ValueError(f"Non-finite content JSON value: {value}")


def validate_content(content):
    """Validate every tree/jump endpoint before allocating plan identities."""
    if not isinstance(content, dict):
        raise ValueError("Content must be an object")
    nodes, jumps = content.get("nodes"), content.get("jumps")
    if not isinstance(nodes, list) or not nodes or not isinstance(jumps, list):
        raise ValueError("Content requires a nonempty nodes list and a jumps list")
    by_key = {}
    for node in nodes:
        if not isinstance(node, dict):
            raise ValueError("Every content node must be an object")
        for field in ("key", "name", "note"):
            if not isinstance(node.get(field), str) or not node[field].strip():
                raise ValueError(f"Every node needs a nonempty string {field}")
        if "parent_key" not in node:
            raise ValueError(f"Node {node['key']} is missing parent_key")
        if node["key"] in by_key:
            raise ValueError(f"Duplicate node key: {node['key']}")
        by_key[node["key"]] = node
    roots = [node for node in nodes if node["parent_key"] is None]
    if len(roots) != 1:
        raise ValueError("Content must have exactly one root with parent_key=null")
    root_key = roots[0]["key"]
    for node in nodes:
        parent = node["parent_key"]
        if parent is not None and (not isinstance(parent, str) or parent not in by_key):
            raise ValueError(f"Missing parent for node {node['key']}: {parent!r}")
    for key in by_key:
        visited = set()
        cursor = key
        while cursor is not None:
            if cursor in visited:
                raise ValueError(f"Parent cycle at node {cursor}")
            visited.add(cursor)
            cursor = by_key[cursor]["parent_key"]
    seen_jumps = set()
    for jump in jumps:
        if not isinstance(jump, dict):
            raise ValueError("Every jump must be an object")
        a, b = jump.get("a"), jump.get("b")
        if (not isinstance(a, str) or not isinstance(b, str)
                or a not in by_key or b not in by_key or a == b):
            raise ValueError(f"Invalid jump endpoints: {a!r}, {b!r}")
        edge = tuple(sorted((a, b)))
        if edge in seen_jumps:
            raise ValueError(f"Duplicate jump: {a}, {b}")
        seen_jumps.add(edge)
        if not isinstance(jump.get("reason"), str) or not jump["reason"].strip():
            raise ValueError(f"Jump {a}, {b} needs a nonempty reason")
    return nodes, jumps, root_key


def build_plan(archive, root_id, content):
    if str(uuid.UUID(root_id)) != root_id:
        raise ValueError("root-id must be a canonical lowercase UUID")
    validation = archive.validate()
    if validation["errors"]:
        raise ArchiveError("Input validation failed: " + "; ".join(validation["errors"]))
    roots = [row for row in archive.rows("thoughts") if row.get("Id") == root_id]
    if len(roots) != 1 or roots[0].get("Kind") != 1:
        raise ValueError("root-id must identify an existing ordinary (Kind=1) thought")
    nodes, jumps, root_key = validate_content(content)
    used_ids = {row.get("Id") for table in Archive.TABLES for row in archive.rows(table)}
    used_ids.add(archive.meta.get("BrainId"))

    def allocate_id():
        new_id = str(uuid.uuid4())
        while new_id in used_ids:
            new_id = str(uuid.uuid4())
        used_ids.add(new_id)
        return new_id

    ids = {node["key"]: root_id if node["key"] == root_key else allocate_id()
           for node in nodes}
    root = next(node for node in nodes if node["key"] == root_key)
    operations = [{"op": "update_thought", "id": root_id, "set": {"Name": root["name"]}}]
    operations.extend({"op": "add_thought", "id": ids[node["key"]],
                       "name": node["name"], "kind": 1}
                      for node in nodes if node["key"] != root_key)
    branches = [node for node in nodes if node["parent_key"] == root_key]
    for index, node in enumerate(branches):
        color = BRANCH_COLORS[index % len(BRANCH_COLORS)] - (1 << 32)
        operations.append({"op": "update_thought", "id": ids[node["key"]],
                           "set": {"BackgroundColor": color}})
    for node in nodes:
        if node["parent_key"] is not None:
            operations.append({"op": "add_link", "id": allocate_id(),
                               "a": ids[node["parent_key"]], "b": ids[node["key"]],
                               "relation": "parent"})
    operations.extend({"op": "add_link", "id": allocate_id(),
                       "a": ids[jump["a"]], "b": ids[jump["b"]], "relation": "jump"}
                      for jump in jumps)
    operations.extend({"op": "set_note", "id": ids[node["key"]], "text": node["note"]}
                      for node in nodes)
    plan = {"source_sha256": archive.source_sha256, "operations": operations}
    # Check supported versions, root-note layout and all operations in memory.
    # This does not write an archive or change the original source file.
    archive.apply_plan(plan)
    return plan


def publish_plan(output, plan):
    """Publish a complete UTF-8 plan without ever replacing an existing path."""
    payload = (json.dumps(plan, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
    output = output.absolute()
    if os.path.lexists(output):
        raise FileExistsError(f"Output already exists: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".nist-plan-", suffix=".tmp", dir=output.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, output)
    finally:
        Path(temporary).unlink(missing_ok=True)


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path, help="Native BRZ v5 export; preserved unchanged")
    parser.add_argument("--root-id", required=True, help="Explicit existing ordinary thought UUID to rename and replace its note")
    parser.add_argument("--output", required=True, type=Path, help="New JSON plan path; existing files are never replaced")
    args = parser.parse_args(argv)
    try:
        if args.source.resolve() == args.output.resolve():
            raise ValueError("Source and output must be different paths")
        content = json.loads((EXAMPLE_DIR / "content.json").read_text(encoding="utf-8-sig"),
                             object_pairs_hook=unique_object, parse_constant=reject_constant)
        archive = Archive(args.source)
        before = archive.validate()
        other_thoughts = sum(row.get("Kind") == 1 and row.get("Id") != args.root_id
                             for row in archive.rows("thoughts"))
        plan = build_plan(archive, args.root_id, content)
        publish_plan(args.output, plan)
        print(json.dumps({"plan": str(args.output.resolve()),
                          "source_sha256": plan["source_sha256"],
                          "operations": len(plan["operations"]),
                          "preserved_other_ordinary_thoughts": other_thoughts,
                          "input_warnings": before["warnings"],
                          "application_import_verified": False}, ensure_ascii=False, indent=2))
        return 0
    except (ArchiveError, OSError, ValueError, KeyError, TypeError) as exc:
        print(json.dumps({"error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
