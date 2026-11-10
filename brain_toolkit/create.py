"""Create a new BRZ v5 from explicit content, without an existing archive."""
from __future__ import annotations

from collections import deque
import uuid

from .archive import Archive, ArchiveError, _now


TOP_METADATA = {"title", "subtitle", "created_at", "author", "source", "design"}
NODE_METADATA = {"category", "source_pages", "source_pdf_page"}


def _keys(value, allowed, label):
    if not isinstance(value, dict):
        raise ArchiveError(f"{label} must be an object")
    unknown = set(value) - allowed
    if unknown:
        raise ArchiveError(f"Unknown {label} keys: {sorted(unknown)}")


def _text(value, label, *, empty=False):
    if not isinstance(value, str) or (not empty and not value.strip()):
        raise ArchiveError(f"{label} must be {'a' if empty else 'a nonempty'} string")
    return value


def create_archive(content):
    """Return (new Archive, manifest) after validating and creating all content.

    Graph keys are user-owned strings, never UUIDs to reuse. parent_key offers a
    tree shorthand; explicit edges permit multiple parents. Explanatory source
    metadata is accepted for the bundled example but only note text is embedded.
    """
    _keys(content, TOP_METADATA | {"schema_version", "nodes", "jumps", "edges"}, "content")
    if "schema_version" in content and (type(content["schema_version"]) is not int or content["schema_version"] != 1):
        raise ArchiveError("Content schema_version must be 1")
    if "title" in content:
        _text(content["title"], "title")
    nodes = content.get("nodes")
    if not isinstance(nodes, list) or not nodes or len(nodes) > 10_000:
        raise ArchiveError("nodes must contain between 1 and 10000 objects")
    by_key = {}
    for node in nodes:
        _keys(node, NODE_METADATA | {"key", "name", "note", "parent_key", "label", "background_color"}, "node")
        key = _text(node.get("key"), "node key")
        _text(node.get("name"), "node name")
        if key in by_key:
            raise ArchiveError(f"Duplicate node key: {key}")
        if "note" in node:
            _text(node["note"], "node note", empty=True)
        if "label" in node:
            _text(node["label"], "node label", empty=True)
        if "background_color" in node:
            color = node["background_color"]
            if type(color) is not int or not -(2**31) <= color < 2**31:
                raise ArchiveError("background_color must be a signed 32-bit ARGB integer")
        by_key[key] = node

    edges, seen = [], set()
    def edge(a, b, relation):
        if (not isinstance(a, str) or not isinstance(b, str)
                or a not in by_key or b not in by_key):
            raise ArchiveError(f"Missing edge endpoint: {a!r}, {b!r}")
        if a == b:
            raise ArchiveError("Self-links are not supported")
        if relation not in ("parent", "jump"):
            raise ArchiveError("Edge relation must be parent or jump")
        identity = (relation, *(sorted((a, b)) if relation == "jump" else (a, b)))
        if identity in seen:
            raise ArchiveError(f"Duplicate semantic edge: {a}, {b}, {relation}")
        seen.add(identity)
        edges.append((a, b, relation))

    for node in nodes:
        if node.get("parent_key") is not None:
            edge(node["parent_key"], node["key"], "parent")
    for table in ("jumps", "edges"):
        items = content.get(table, [])
        if not isinstance(items, list) or len(items) > 10_000:
            raise ArchiveError(f"{table} must be a list of at most 10000 objects")
        for item in items:
            _keys(item, {"a", "b", "reason"} | ({"relation"} if table == "edges" else set()), table)
            if "reason" in item:
                _text(item["reason"], f"{table} reason", empty=True)
            edge(item.get("a"), item.get("b"), "jump" if table == "jumps" else item.get("relation"))

    indegree = dict.fromkeys(by_key, 0)
    children = {key: [] for key in by_key}
    for a, b, relation in edges:
        if relation == "parent":
            indegree[b] += 1
            children[a].append(b)
    roots = [key for key, degree in indegree.items() if degree == 0]
    pending, visited = deque(roots), 0
    while pending:
        key = pending.popleft()
        visited += 1
        for child in children[key]:
            indegree[child] -= 1
            if indegree[child] == 0:
                pending.append(child)
    if visited != len(nodes):
        raise ArchiveError("Parent edges contain a cycle")

    operation_count = (len(nodes) + len(edges) + sum("note" in node for node in nodes)
                       + sum("label" in node or "background_color" in node for node in nodes))
    if operation_count > 10_000:
        raise ArchiveError("Content requires more than 10000 combined node, style, edge and note operations")

    archive = Archive.empty()
    # Reserve all IDs before adding relationships. Archive's existing creators
    # supply the same proven fields and UUID checks as offline edit plans.
    ids = {}
    reserved = {archive.meta["BrainId"]}
    operations = []
    for node in nodes:
        ident = str(uuid.uuid4())
        while ident in reserved:
            ident = str(uuid.uuid4())
        reserved.add(ident)
        ids[node["key"]] = ident
        operations.append({"op": "add_thought", "id": ident, "name": node["name"]})
        values = {}
        if "label" in node:
            values["Label"] = node["label"]
        if "background_color" in node:
            values["BackgroundColor"] = node["background_color"]
        if values:
            operations.append({"op": "update_thought", "id": ident, "set": values})
    operations.extend({"op": "add_link", "a": ids[a], "b": ids[b], "relation": relation}
                      for a, b, relation in edges)
    operations.extend({"op": "set_note", "id": ids[node["key"]], "text": node["note"]}
                      for node in nodes if "note" in node)
    # A newly created object has no source path/hash. No source token is asked
    # of the user; the internal transaction reuses edit validation and rollback.
    archive.apply_plan({"source_sha256": None, "operations": operations})
    title = content.get("title", by_key[roots[0]]["name"])
    home_id = ids[roots[0]]
    # TheBrainCore BrainSettingKey: BrainName=19, HomeThoughtId=20.
    # Method 1819 hashes the decimal enum key with the brain GUID as namespace.
    # This UUID5 mapping matches the native seed and all five original samples.
    # A brain with empty settings imports but fails to open (null BrainName),
    # so these are required creation records, not optional UI decoration.
    namespace = uuid.UUID(archive.meta["BrainId"])
    now = _now()
    for key, value in ((19, title), (20, home_id)):
        archive._table("settings").add({
            "Id": str(uuid.uuid5(namespace, str(key))), "BrainId": str(namespace),
            "Value": value, "CreationDateTime": now, "ModificationDateTime": now,
        })
    validation = archive.validate()
    if validation["errors"]:
        raise ArchiveError("Created archive validation failed: " + "; ".join(validation["errors"]))
    manifest = {"node_ids": ids, "root_ids": [ids[key] for key in roots],
                "title": title, "home_thought_id": home_id,
                "operations": len(operations),
                "metadata_policy": "Brain title, node names, labels, colors, notes and graph edges are embedded; other content metadata and edge reasons must be included in note text to appear in TheBrain",
                "application_import_verified": False}
    return archive, manifest
