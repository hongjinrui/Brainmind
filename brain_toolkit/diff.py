"""Auditable comparisons of two loaded BRZ files, without matching by names.

ZIP member hashes cover uncompressed bytes. Record comparisons use stable IDs;
they do not infer equivalence when an application reassigns IDs on import.
"""
from __future__ import annotations

import hashlib
import json

from .archive import Archive, ArchiveError


def _same_json(left, right):
    # Python equality conflates True with 1. Compare JSON representations so
    # changes in field types remain visible, while object key order is ignored.
    options = dict(ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)
    return json.dumps(left, **options) == json.dumps(right, **options)


def _limited(items, limit):
    return {"count": len(items), "items": items[:limit], "truncated": len(items) > limit}


def _fingerprint(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def _indexed(archive, table):
    result = {}
    for row in archive.rows(table):
        ident = row.get("Id")
        if not isinstance(ident, str) or ident in result:
            raise ArchiveError(f"Cannot compare {table}: missing, invalid or duplicate Id {ident!r}")
        result[ident] = row
    return result


def _changed_fields(before, after):
    fields = sorted(
        key for key in before.keys() | after.keys()
        if key not in before or key not in after or not _same_json(before[key], after[key])
    )
    return {
        "changed_fields": fields,
        # An absent key remains absent: a missing field differs from JSON null.
        "before": {key: before[key] for key in fields if key in before},
        "after": {key: after[key] for key in fields if key in after},
    }


def compare_archives(before: Archive, after: Archive, limit: int = 100) -> dict:
    """Compare two newly loaded files, returning bounded details and full counts.

    ``limit`` bounds the number of entries in each added/removed/changed list;
    it does not truncate field values. Both inputs must be freshly loaded, not
    objects changed with ``apply_plan``. This keeps the reported file hashes
    tied to the exact compared snapshots, including when files change on disk
    after being read.
    """
    if type(limit) is not int or limit < 1:
        raise ArchiveError("Comparison limit must be a positive integer")
    if before._changed or after._changed:
        raise ArchiveError("Compare freshly loaded BRZ files, not edited in-memory objects")
    if not _same_json(before.meta, before._meta_initial) or not _same_json(after.meta, after._meta_initial):
        raise ArchiveError("Compare freshly loaded BRZ files, not edited in-memory metadata")

    old_members, new_members = before._render_members(), after._render_members()
    old_paths, new_paths = set(old_members), set(new_members)
    added_members = [
        {"path": path, **_fingerprint(new_members[path])}
        for path in sorted(new_paths - old_paths)
    ]
    removed_members = [
        {"path": path, **_fingerprint(old_members[path])}
        for path in sorted(old_paths - new_paths)
    ]
    changed_members = [
        {"path": path, "before": _fingerprint(old_members[path]), "after": _fingerprint(new_members[path])}
        for path in sorted(old_paths & new_paths)
        if old_members[path] != new_members[path]
    ]

    record_changes = {}
    totals = {"added": 0, "removed": 0, "changed": 0}
    for table in sorted(Archive.TABLES):
        old_rows, new_rows = _indexed(before, table), _indexed(after, table)
        old_ids, new_ids = set(old_rows), set(new_rows)
        added = [{"id": ident, "record": new_rows[ident]} for ident in sorted(new_ids - old_ids)]
        removed = [{"id": ident, "record": old_rows[ident]} for ident in sorted(old_ids - new_ids)]
        changed = [
            {"id": ident, **_changed_fields(old_rows[ident], new_rows[ident])}
            for ident in sorted(old_ids & new_ids)
            if not _same_json(old_rows[ident], new_rows[ident])
        ]
        if added or removed or changed:
            record_changes[table] = {
                "added": _limited(added, limit),
                "removed": _limited(removed, limit),
                "changed": _limited(changed, limit),
            }
            for key, items in (("added", added), ("removed", removed), ("changed", changed)):
                totals[key] += len(items)

    meta = _changed_fields(before.meta, after.meta)
    meta_fields = meta["changed_fields"]
    meta = {
        "count": len(meta_fields),
        "changed_fields": meta_fields[:limit],
        "before": {key: meta["before"][key] for key in meta_fields[:limit] if key in meta["before"]},
        "after": {key: meta["after"][key] for key in meta_fields[:limit] if key in meta["after"]},
        "truncated": len(meta_fields) > limit,
    }
    return {
        "source_sha256": before.source_sha256,
        "target_sha256": after.source_sha256,
        "detail_limit_per_list": limit,
        "members": {
            "added": _limited(added_members, limit),
            "removed": _limited(removed_members, limit),
            "changed": _limited(changed_members, limit),
            "unchanged_count": len(old_paths & new_paths) - len(changed_members),
        },
        "record_counts": totals,
        "records": record_changes,
        "meta": meta,
        "comparison_scope": "Uncompressed member bytes and known records matched by exact ID",
        "application_import_verified": False,
    }
