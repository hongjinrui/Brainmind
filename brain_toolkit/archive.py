"""An offline editor for the BRZ exchange-format-5 files supplied with this repo.

This module does not open live databases or claim application-level compatibility.
Unknown fields and untouched table lines are preserved. Writes always create a
separate file. Mutation plans are checked against the original file's SHA-256.
"""

from __future__ import annotations

import copy
import hashlib
import io
import json
import math
import os
import stat
import tempfile
import uuid
import zipfile
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path, PurePosixPath


class ArchiveError(ValueError):
    """Malformed archives, unsupported operations, and failed preconditions."""


def _json(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ArchiveError(f"Duplicate JSON key: {key}")
            result[key] = value
        return result

    def invalid(value):
        raise ArchiveError(f"Non-finite JSON number: {value}")

    def finite_float(value):
        parsed = float(value)
        if not math.isfinite(parsed):
            raise ArchiveError(f"Non-finite JSON number: {value}")
        # Editing one field reserializes its entire record. Refuse a token if
        # Python would silently round a different (possibly unknown) field.
        if Decimal(value) != Decimal(str(parsed)):
            raise ArchiveError(f"JSON number cannot round-trip without precision loss: {value}")
        return parsed

    try:
        if isinstance(data, bytes):
            data = data.decode("utf-8-sig")
        return json.loads(data, object_pairs_hook=unique, parse_constant=invalid, parse_float=finite_float)
    except (ValueError, UnicodeError) as exc:
        raise ArchiveError(f"Invalid JSON: {exc}") from exc


def _encode(obj):
    try:
        return json.dumps(obj, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    except (ValueError, TypeError) as exc:
        raise ArchiveError(f"Value cannot be encoded as finite JSON: {exc}") from exc


def _now():
    return datetime.now(timezone.utc).replace(tzinfo=None).isoformat(timespec="microseconds")


def _uuid(value, label="ID"):
    try:
        parsed = uuid.UUID(value)
    except (ValueError, TypeError, AttributeError) as exc:
        raise ArchiveError(f"Invalid {label}: {value!r}") from exc
    if str(parsed) != value:
        raise ArchiveError(f"{label} must be a canonical lowercase UUID: {value!r}")
    return value


def _safe_name(name):
    if not isinstance(name, str):
        raise ArchiveError("Archive paths must be strings")
    path = PurePosixPath(name)
    if (
        not name or "\\" in name or path.is_absolute() or "\x00" in name
        or any(part in (".", "..", "") for part in name.rstrip("/").split("/"))
        or any(":" in part for part in path.parts)
    ):
        raise ArchiveError(f"Unsafe archive path: {name!r}")
    return name


@dataclass
class _Line:
    record: dict | None
    original: dict | None
    raw: bytes


class _Table:
    def __init__(self, data=b"\xef\xbb\xbf"):
        self.bom = data.startswith(b"\xef\xbb\xbf")
        payload = data[3:] if self.bom else data
        self.newline = b"\r\n" if b"\r\n" in payload else b"\n"
        self.lines = []
        for raw in payload.splitlines(keepends=True):
            if not raw.strip():
                self.lines.append(_Line(None, None, raw))
                continue
            record = _json(raw)
            if not isinstance(record, dict):
                raise ArchiveError("Each NDJSON line must be an object")
            self.lines.append(_Line(record, copy.deepcopy(record), raw))

    def records(self):
        return [line.record for line in self.lines if line.record is not None]

    def add(self, record):
        self.lines.append(_Line(record, None, b""))

    def remove(self, record):
        self.lines = [line for line in self.lines if line.record is not record]

    def render(self):
        parts = []
        for line in self.lines:
            if line.record == line.original:
                raw = line.raw
            else:
                newline = b"\r\n" if line.raw.endswith(b"\r\n") else self.newline
                raw = _encode(line.record) + newline
            if parts and not parts[-1].endswith((b"\n", b"\r")):
                parts[-1] += self.newline
            parts.append(raw)
        return (b"\xef\xbb\xbf" if self.bom else b"") + b"".join(parts)


class Archive:
    """Read and edit one BRZ. Instances retain the input bytes for SHA checking."""

    MAX_MEMBERS = 20_000
    MAX_MEMBER_BYTES = 128 * 1024 * 1024
    MAX_TOTAL_BYTES = 512 * 1024 * 1024
    REQUIRED = ("meta.json", "thoughts.json", "links.json", "attachments.json")
    TABLES = {name + ".json" for name in (
        "thoughts", "links", "settings", "attachments", "modificationlogs",
        "brainusers", "syncpoints", "tombstones", "calendarevents", "access",
        "fielddefinitions", "fieldinstances",
    )}

    def __init__(self, path):
        self.path = Path(path).resolve()
        if self.path.stat().st_size > self.MAX_TOTAL_BYTES:
            raise ArchiveError("Input file exceeds archive size limit")
        source_bytes = self.path.read_bytes()
        self.source_sha256 = hashlib.sha256(source_bytes).hexdigest()
        self._members = {}
        self._infos = {}
        self._tables = {}
        self._changed = []
        try:
            with zipfile.ZipFile(io.BytesIO(source_bytes)) as source:
                infos = source.infolist()
                if len(infos) > self.MAX_MEMBERS:
                    raise ArchiveError("Archive has too many members")
                if sum(info.file_size for info in infos) > self.MAX_TOTAL_BYTES:
                    raise ArchiveError("Archive exceeds uncompressed size limit")
                for info in infos:
                    name = _safe_name(info.filename)
                    if name in self._members:
                        raise ArchiveError(f"Duplicate ZIP member: {name}")
                    if info.file_size > self.MAX_MEMBER_BYTES:
                        raise ArchiveError(f"Member exceeds size limit: {name}")
                    if info.flag_bits & 1:
                        raise ArchiveError(f"Encrypted member unsupported: {name}")
                    if stat.S_ISLNK(info.external_attr >> 16):
                        raise ArchiveError(f"Symlink member unsupported: {name}")
                    self._members[name] = source.read(info)
                    self._infos[name] = copy.copy(info)
                self._comment = source.comment
        except (zipfile.BadZipFile, RuntimeError, NotImplementedError) as exc:
            raise ArchiveError(f"Cannot read ZIP archive: {exc}") from exc
        missing = set(self.REQUIRED) - self._members.keys()
        if missing:
            raise ArchiveError(f"Missing required files: {sorted(missing)}")
        self._meta_original = self._members["meta.json"]
        self.meta = _json(self._meta_original)
        if not isinstance(self.meta, dict):
            raise ArchiveError("meta.json must contain one object")
        self._meta_initial = copy.deepcopy(self.meta)
        for name, data in self._members.items():
            if name in self.TABLES:
                try:
                    self._tables[name] = _Table(data)
                except ArchiveError as exc:
                    raise ArchiveError(f"{name}: {exc}") from exc

    def rows(self, table):
        """Return a copy of a table's objects; table may omit its .json suffix."""
        name = table if table.endswith(".json") else table + ".json"
        return copy.deepcopy(self._tables[name].records()) if name in self._tables else []

    def notes(self, thought_id, limit=20_000):
        """Read the observed Markdown/HTML note formats from the same snapshot.

        Character limits are explicit so inspection cannot silently imply that
        a long note has been read in full.
        """
        self._find("thoughts", thought_id)
        if type(limit) is not int or not 1 <= limit <= 1_000_000:
            raise ArchiveError("Note character limit must be between 1 and 1000000")
        result = []
        for attachment in self.rows("attachments"):
            if attachment.get("SourceId") != thought_id or attachment.get("SourceType") != 2:
                continue
            if (attachment.get("Type"), attachment.get("NoteType")) not in ((1, 4), (4, 2)):
                continue
            path = self._attachment_path(attachment)
            item = {"attachment_id": attachment["Id"], "path": path}
            if path not in self._members:
                item["error"] = "Note file is missing"
            else:
                try:
                    text = self._members[path].decode("utf-8-sig")
                    item.update(text=text[:limit], characters=len(text), truncated=len(text) > limit)
                except UnicodeError:
                    item["error"] = "Note is not UTF-8; no lossy decoding was performed"
            result.append(item)
        return result

    def _table(self, table):
        name = table if table.endswith(".json") else table + ".json"
        if name not in self._tables:
            self._tables[name] = _Table()
        return self._tables[name]

    def _find(self, table, object_id):
        _uuid(object_id)
        found = [row for row in self._table(table).records() if row.get("Id") == object_id]
        if len(found) != 1:
            raise ArchiveError(f"Expected one {table} record for {object_id}; found {len(found)}")
        return found[0]

    def summary(self):
        thoughts = self.rows("thoughts")
        return {
            "source_sha256": self.source_sha256,
            "brain_id": self.meta.get("BrainId"),
            "exchange_format_version": self.meta.get("ExchangeFormatVersion"),
            "members": len(self._members.keys() | self._tables.keys()),
            "tables": {name: len(table.records()) for name, table in self._tables.items()},
            "thought_kinds": dict(Counter(str(row.get("Kind")) for row in thoughts)),
            "link_kinds": dict(Counter(
                f"{row.get('Kind')}/{row.get('Relation')}/{row.get('Meaning')}"
                for row in self.rows("links")
            )),
        }

    @staticmethod
    def _attachment_path(attachment):
        # Type 2 is an external file (the elon sample has a Windows path);
        # Type 3 is a URL. Neither is an archive member to resolve or access.
        kind = attachment.get("Type")
        if type(kind) is not int or kind not in (1, 4, 5):
            return None
        source, location = attachment.get("SourceId"), attachment.get("Location")
        _uuid(source, "attachment SourceId")
        if not isinstance(location, str) or not location:
            raise ArchiveError("Local attachment Location must be a nonempty string")
        _safe_name(location)
        if attachment.get("IsIcon"):
            path = f"{source}/.data/{location}"
        elif attachment.get("NoteType") == 2 and kind == 4:
            path = f"{source}/Notes/{location}"
        else:
            path = f"{source}/{location}"
        return _safe_name(path)

    def validate(self):
        errors, warnings = [], []
        brain_id = self.meta.get("BrainId")
        try:
            _uuid(brain_id, "BrainId")
        except ArchiveError as exc:
            errors.append(str(exc))
        if type(self.meta.get("ExchangeFormatVersion")) is not int or self.meta.get("ExchangeFormatVersion") != 5:
            warnings.append("Only exchange format version 5 is supported for edits")
        indices = {}
        for name, table in self._tables.items():
            ids = {}
            for number, row in enumerate(table.records(), 1):
                object_id = row.get("Id")
                try:
                    _uuid(object_id)
                except ArchiveError as exc:
                    errors.append(f"{name} row {number}: {exc}")
                    continue
                if object_id in ids:
                    errors.append(f"{name}: duplicate Id {object_id}")
                ids[object_id] = row
                if row.get("BrainId") != brain_id:
                    errors.append(f"{name}: BrainId mismatch for {object_id}")
            indices[name] = ids
        thoughts = indices.get("thoughts.json", {})
        links = indices.get("links.json", {})
        edges, type_edges = set(), {}
        for thought_id, thought in thoughts.items():
            if not isinstance(thought.get("Name"), str):
                errors.append(f"Thought {thought_id}: Name must be a string")
            if type(thought.get("Kind")) is not int:
                errors.append(f"Thought {thought_id}: Kind must be an integer")
            elif thought["Kind"] not in range(6):
                warnings.append(f"Thought {thought_id}: unknown Kind preserved")
            if "TagIds" in thought and not isinstance(thought["TagIds"], list):
                errors.append(f"Thought {thought_id}: TagIds must be an array")
            elif thought.get("TagIds"):
                warnings.append(f"Thought {thought_id}: nonempty TagIds is outside the observed sample schema")
        for link in self.rows("links"):
            a, b = link.get("ThoughtIdA"), link.get("ThoughtIdB")
            if not isinstance(a, str) or not isinstance(b, str) or a not in thoughts or b not in thoughts:
                errors.append(f"Link {link.get('Id')}: missing endpoint")
                continue
            if any(type(link.get(key)) is not int for key in ("Kind", "Relation", "Meaning")):
                errors.append(f"Link {link.get('Id')}: Kind, Relation, and Meaning must be integers")
                continue
            key = (a, b, link["Kind"], link["Relation"], link["Meaning"])
            if key in edges:
                warnings.append(f"Duplicate semantic link: {link.get('Id')}")
            edges.add(key)
            if link["Kind"] == 1 and link["Relation"] == 1:
                meaning = link["Meaning"]
                if meaning in (2, 3):
                    type_edges.setdefault(b, []).append(a)
                    if thoughts[a].get("Kind") != 2:
                        errors.append(f"Link {link.get('Id')}: type source is not Kind=2")
                    expected = 1 if meaning == 2 else 2
                    if thoughts[b].get("Kind") != expected:
                        errors.append(f"Link {link.get('Id')}: Meaning={meaning} target must be Kind={expected}")
                if meaning == 5 and thoughts[a].get("Kind") != 4:
                    errors.append(f"Link {link.get('Id')}: tag source is not Kind=4")
        for thought_id, thought in thoughts.items():
            type_id = thought.get("TypeId")
            if type_id is not None:
                if not isinstance(type_id, str) or type_id not in thoughts or thoughts[type_id].get("Kind") != 2:
                    errors.append(f"Thought {thought_id}: invalid TypeId {type_id}")
                if type_edges.get(thought_id, []) != [type_id]:
                    errors.append(f"Thought {thought_id}: TypeId and type links disagree")
            elif type_edges.get(thought_id):
                errors.append(f"Thought {thought_id}: type link exists without TypeId")

        def live_source(row, table):
            source_type, source_id = row.get("SourceType"), row.get("SourceId")
            label = f"{table} {row.get('Id')}"
            if type(source_type) is not int:
                errors.append(f"{label}: SourceType must be an integer")
                return
            if source_type not in (1, 2, 3):
                warnings.append(f"{label}: unknown SourceType={source_type} preserved")
                return
            if not isinstance(source_id, str):
                errors.append(f"{label}: SourceId must be a UUID string")
                return
            if source_type == 1 and source_id != brain_id:
                errors.append(f"{label}: missing source brain")
            if source_type == 2 and source_id not in thoughts:
                errors.append(f"{label}: missing source thought")
            if source_type == 3 and source_id not in links:
                errors.append(f"{label}: missing source link")

        # These are live ownership references. Historical log/tombstone
        # references intentionally may point to removed entities.
        for table in ("attachments", "fieldinstances", "calendarevents"):
            for row in self.rows(table):
                live_source(row, table)
        definitions = indices.get("fielddefinitions.json", {})
        for row in self.rows("fieldinstances"):
            definition = row.get("FieldDefinitionId")
            if not isinstance(definition, str) or definition not in definitions:
                errors.append(f"Field instance {row.get('Id')}: missing definition")
        states = self.meta.get("AttachmentFileStates", {})
        if not isinstance(states, dict):
            errors.append("meta.AttachmentFileStates must be an object")
        else:
            attachments = indices.get("attachments.json", {})
            for attachment_id, state in states.items():
                if attachment_id not in attachments:
                    errors.append(f"AttachmentFileStates: missing attachment {attachment_id}")
                if type(state) is not int:
                    errors.append(f"AttachmentFileStates {attachment_id}: state must be an integer")
        for row in self.rows("attachments"):
            try:
                path = self._attachment_path(row)
            except ArchiveError as exc:
                errors.append(f"Attachment {row.get('Id')}: {exc}")
                continue
            if type(row.get("Type")) is not int:
                errors.append(f"Attachment {row.get('Id')}: Type must be an integer")
            elif row["Type"] not in (1, 2, 3, 4, 5):
                warnings.append(f"Attachment {row.get('Id')}: unknown Type preserved")
            if path and path in self._members:
                actual = len(self._members[path])
                if row.get("DataLength") != actual:
                    warnings.append(f"Attachment {row.get('Id')}: DataLength {row.get('DataLength')} differs from {actual} bytes")
            elif path and isinstance(states, dict) and states.get(row.get("Id")) == 1:
                warnings.append(f"Attachment {row.get('Id')}: expected file not found at {path}")
        return {"errors": errors, "warnings": warnings}

    def _new(self, supplied=None):
        value = supplied if supplied is not None else str(uuid.uuid4())
        _uuid(value)
        if supplied is not None and uuid.UUID(value).version != 4:
            raise ArchiveError("New explicitly supplied IDs must be UUID v4")
        if value == self.meta.get("BrainId") or any(row.get("Id") == value for table in self._tables.values() for row in table.records()):
            raise ArchiveError(f"New Id already exists: {value}")
        return value

    def _base(self, object_id=None):
        now = _now()
        return {"CreationDateTime": now, "ModificationDateTime": now,
                "BrainId": self.meta["BrainId"], "Id": self._new(object_id)}

    def _ordinary(self, thought_id):
        thought = self._find("thoughts", thought_id)
        if thought.get("Kind") != 1:
            raise ArchiveError("This operation requires an ordinary Kind=1 thought")
        return thought

    def _guard_link_removal(self, link):
        """Refuse to orphan content owned by a relationship being removed.

        History rows intentionally survive removal; live notes, attributes and
        events do not have that meaning. Cascading deletion is not implemented.
        """
        link_id = link["Id"]
        for table in ("attachments", "fieldinstances", "calendarevents", "access"):
            if any(row.get("SourceId") == link_id for row in self.rows(table)):
                raise ArchiveError(
                    f"Link {link_id} owns {table} records; removal would orphan content"
                )
        if any(name.startswith(link_id + "/") for name in self._members):
            raise ArchiveError(f"Link {link_id} owns archive members; removal would orphan content")

    @staticmethod
    def _guard_type_link_replacement(link):
        """Only replace a plain observed type link, never discard decoration."""
        allowed = {
            "Id", "BrainId", "ThoughtIdA", "ThoughtIdB", "Kind", "Relation", "Meaning",
            "Direction", "Thickness", "CreationDateTime", "ModificationDateTime",
            "SyncUpdateDateTime", "SyncSentId",
        }
        if set(link) - allowed or link.get("Direction") != -1 or link.get("Thickness") != -1:
            raise ArchiveError(
                f"Type link {link['Id']} has styling or unrecognized metadata; "
                "automatic replacement would discard it"
            )

    @staticmethod
    def _note_cache_parts(thought):
        """Recognize the bounded seven-token icon cache before changing a flag."""
        raw = thought.get("ThoughtIconInfo")
        if not isinstance(raw, str):
            raise ArchiveError("Note editing requires a recognized ThoughtIconInfo cache")
        parts = raw.split(":")
        if len(parts) != 7 or parts[3] not in ("True", "False") or parts[4] not in ("True", "False"):
            raise ArchiveError("Unsupported ThoughtIconInfo cache; refusing to guess note state")
        try:
            if any(not -(2**31) <= int(parts[index]) < 2**31 for index in (0, 2, 5)):
                raise ValueError
        except ValueError as exc:
            raise ArchiveError("Unsupported numeric ThoughtIconInfo tokens") from exc
        return parts

    @staticmethod
    def _note_timestamp_key(value):
        """Compare supported .NET timestamps without losing the seventh digit."""
        import re
        pattern = r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})?"
        match = re.fullmatch(pattern, value) if isinstance(value, str) else None
        if match is None:
            raise ArchiveError("Unsupported note ModificationDateTime; cannot determine the displayed note")
        base, fraction, offset = match.groups()
        try:
            parsed = datetime.fromisoformat(base + ("+00:00" if offset in (None, "Z") else offset))
            parsed = parsed.astimezone(timezone.utc)
        except ValueError as exc:
            raise ArchiveError("Invalid note ModificationDateTime") from exc
        return parsed, int((fraction or "").ljust(7, "0"))

    def _refresh_note_cache(self, thought, parts):
        # Official M607 selects the most recently modified note attachment;
        # older HTML and Markdown notes can coexist, so the edited note is not
        # necessarily the displayed one when an existing timestamp is later.
        notes = [item for item in self._table("attachments").records()
                 if item.get("SourceId") == thought["Id"] and item.get("SourceType") == 2
                 and (item.get("Type") == 4 or (item.get("Type") == 1 and item.get("Name") == "Notes.md"))]
        selected = max(notes, key=lambda item: self._note_timestamp_key(item.get("ModificationDateTime")), default=None)
        if selected is not None and type(selected.get("DataLength")) is not int:
            raise ArchiveError("Note DataLength must be an integer to refresh its cache")
        parts[3] = "True" if selected is not None and selected["DataLength"] > 0 else "False"
        thought["ThoughtIconInfo"] = ":".join(parts)

    @staticmethod
    def _numeric_field_value(value, data_type):
        """Check the proven DecimalValue slot against .NET Decimal limits."""
        from decimal import Decimal
        if type(value) not in (int, float):
            raise ArchiveError("DecimalValue requires a finite JSON number")
        number = Decimal(str(value))
        if not number.is_finite():
            raise ArchiveError("DecimalValue requires a finite JSON number")
        digits = number.as_tuple()
        coefficient = int("".join(map(str, digits.digits)))
        exponent = digits.exponent
        if not coefficient:
            exponent = 0
        while coefficient and exponent < 0 and coefficient % 10 == 0:
            coefficient //= 10
            exponent += 1
        if data_type == 2 and exponent < 0:
            raise ArchiveError("Integer property requires an integral value")
        if exponent > 0:
            coefficient *= 10**exponent
        if exponent < -28 or coefficient > 2**96 - 1:
            raise ArchiveError("Value cannot be represented exactly by .NET Decimal")

    def _link(self, a, b, relation=1, meaning=1, object_id=None):
        self._find("thoughts", a)
        self._find("thoughts", b)
        if a == b:
            raise ArchiveError("Self-links are not supported")
        for link in self._table("links").records():
            same = link.get("ThoughtIdA") == a and link.get("ThoughtIdB") == b
            reverse_jump = relation == 3 and link.get("ThoughtIdA") == b and link.get("ThoughtIdB") == a
            if (same or reverse_jump) and link.get("Relation") == relation and link.get("Meaning") == meaning:
                raise ArchiveError("Duplicate semantic link")
        row = {"ThoughtIdA": a, "ThoughtIdB": b, "Kind": 1, "Relation": relation,
               "Direction": -1, "Meaning": meaning, "Thickness": -1, **self._base(object_id)}
        self._table("links").add(row)
        return row["Id"]

    @staticmethod
    def _keys(operation, allowed, required=()):
        unknown = set(operation) - set(allowed) - {"op"}
        missing = set(required) - set(operation)
        if unknown or missing:
            raise ArchiveError(f"Invalid operation keys: unknown={sorted(unknown)}, missing={sorted(missing)}")

    def apply_plan(self, plan, base_dir=None):
        """Apply all operations or none. base_dir is reserved for future file ops."""
        if not isinstance(plan, dict) or set(plan) - {"source_sha256", "operations"}:
            raise ArchiveError("Plan must contain only source_sha256 and operations")
        if plan.get("source_sha256") != self.source_sha256:
            raise ArchiveError("Source SHA-256 does not match the plan")
        if type(self.meta.get("ExchangeFormatVersion")) is not int or self.meta.get("ExchangeFormatVersion") != 5:
            raise ArchiveError("Editing requires ExchangeFormatVersion=5")
        operations = plan.get("operations")
        if not isinstance(operations, list) or len(operations) > 10_000:
            raise ArchiveError("operations must be a list of at most 10000 operations")
        validation = self.validate()
        if validation["errors"]:
            raise ArchiveError("Input validation failed: " + "; ".join(validation["errors"]))
        backup = copy.deepcopy((self._tables, self._members, self.meta, self._changed))
        changes = []
        try:
            for index, operation in enumerate(operations):
                if not isinstance(operation, dict) or not isinstance(operation.get("op"), str):
                    raise ArchiveError(f"Operation {index} must be an object with op")
                result = self._apply(operation)
                changes.append({"operation": index, "op": operation["op"], **result})
            validation = self.validate()
            if validation["errors"]:
                raise ArchiveError("Result validation failed: " + "; ".join(validation["errors"]))
            self._changed.extend(changes)
        except Exception:
            self._tables, self._members, self.meta, self._changed = backup
            raise
        return changes

    def _apply(self, op):
        action = op["op"]
        if action == "update_thought":
            self._keys(op, ("id", "set"), ("id", "set"))
            row = self._find("thoughts", op["id"])
            if row.get("Kind") not in (1, 2, 4):
                raise ArchiveError("System thoughts cannot be edited")
            values = op["set"]
            allowed = {"Name", "Label", "BackgroundColor"}
            if not isinstance(values, dict) or not values or set(values) - allowed:
                raise ArchiveError(f"set must contain only {sorted(allowed)}")
            for key, value in values.items():
                if key in ("Name", "Label"):
                    if not isinstance(value, str) or (key == "Name" and not value.strip()):
                        raise ArchiveError(f"{key} must be a string; Name must not be empty")
                elif type(value) is not int or not -(2**31) <= value < 2**31:
                    raise ArchiveError(f"{key} must be a signed 32-bit ARGB integer")
            row.update(values)
            row["ModificationDateTime"] = _now()
            return {"id": row["Id"], "fields": sorted(values)}
        if action == "add_thought":
            self._keys(op, ("id", "name", "kind"), ("name",))
            if not isinstance(op["name"], str) or not op["name"].strip():
                raise ArchiveError("name must be a nonempty string")
            kind = op.get("kind", 1)
            if type(kind) is not int or kind not in (1, 2, 4):
                raise ArchiveError("New thought kind must be 1, 2, or 4")
            row = {"Name": op["name"], "ACType": 0, "TagIds": [], "Kind": kind,
                   "ThoughtIconInfo": "8::0:False:False:0:" if kind == 4 else "1::0:False:False:0:",
                   **self._base(op.get("id"))}
            if kind == 2:
                row["Label"] = op["name"]
            self._table("thoughts").add(row)
            return {"id": row["Id"]}
        if action == "add_link":
            self._keys(op, ("id", "a", "b", "relation"), ("a", "b", "relation"))
            if op["relation"] not in ("parent", "jump"):
                raise ArchiveError("relation must be parent or jump")
            self._ordinary(op["a"])
            self._ordinary(op["b"])
            link_id = self._link(op["a"], op["b"], 1 if op["relation"] == "parent" else 3, object_id=op.get("id"))
            return {"id": link_id}
        if action == "remove_link":
            self._keys(op, ("id",), ("id",))
            row = self._find("links", op["id"])
            if row.get("Kind") != 1 or row.get("Meaning") != 1 or row.get("Relation") not in (1, 3):
                raise ArchiveError("Only ordinary parent/jump links may be removed")
            self._guard_link_removal(row)
            self._table("links").remove(row)
            return {"id": op["id"]}
        if action == "set_type":
            self._keys(op, ("id", "type_id"), ("id", "type_id"))
            row = self._ordinary(op["id"])
            type_id = op["type_id"]
            if type_id is not None and self._find("thoughts", type_id).get("Kind") != 2:
                raise ArchiveError("type_id must refer to a Kind=2 thought")
            if row.get("TypeId") == type_id:
                return {"id": row["Id"], "type_id": type_id, "no_op": True}
            links = self._table("links")
            matches = [link for link in links.records()
                       if link.get("Meaning") == 2 and link.get("ThoughtIdB") == row["Id"]]
            for link in matches:
                if link.get("Kind") != 1 or link.get("Relation") != 1:
                    raise ArchiveError("Unsupported type link variant; refusing to change the assignment")
                self._guard_link_removal(link)
                self._guard_type_link_replacement(link)
            for link in matches:
                links.remove(link)
            if type_id is None:
                row.pop("TypeId", None)
            else:
                row["TypeId"] = type_id
                self._link(type_id, row["Id"], meaning=2)
            row["ModificationDateTime"] = _now()
            return {"id": row["Id"], "type_id": type_id}
        if action == "set_tag":
            self._keys(op, ("id", "tag_id", "present"), ("id", "tag_id", "present"))
            row = self._ordinary(op["id"])
            if row.get("TagIds"):
                raise ArchiveError("Editing tags with nonempty TagIds is not supported")
            if self._find("thoughts", op["tag_id"]).get("Kind") != 4:
                raise ArchiveError("tag_id must refer to a Kind=4 thought")
            if type(op["present"]) is not bool:
                raise ArchiveError("present must be true or false")
            table = self._table("links")
            matches = [link for link in table.records() if link.get("Kind") == 1 and link.get("Relation") == 1 and link.get("Meaning") == 5 and link.get("ThoughtIdA") == op["tag_id"] and link.get("ThoughtIdB") == row["Id"]]
            if bool(matches) == op["present"]:
                return {"id": row["Id"], "tag_id": op["tag_id"], "present": op["present"], "no_op": True}
            if op["present"] and not matches:
                self._link(op["tag_id"], row["Id"], meaning=5)
            elif not op["present"]:
                for link in matches:
                    self._guard_link_removal(link)
                for link in matches:
                    table.remove(link)
            row["ModificationDateTime"] = _now()
            return {"id": row["Id"], "tag_id": op["tag_id"], "present": op["present"]}
        if action == "set_note":
            self._keys(op, ("id", "text"), ("id", "text"))
            row = self._ordinary(op["id"])
            cache_parts = self._note_cache_parts(row)
            if not isinstance(op["text"], str):
                raise ArchiveError("text must be a string")
            payload = op["text"].encode("utf-8")
            if len(payload) > self.MAX_MEMBER_BYTES:
                raise ArchiveError("Note exceeds member size limit")
            matches = [item for item in self._table("attachments").records() if item.get("SourceId") == row["Id"] and item.get("SourceType") == 2 and item.get("Location") == "Notes.md" and item.get("Type") == 1 and item.get("NoteType") == 4]
            if len(matches) > 1:
                raise ArchiveError("Multiple Markdown note attachments for this thought")
            if matches:
                attachment = matches[0]
            else:
                if f"{row['Id']}/Notes.md" in self._members:
                    raise ArchiveError("Unregistered Notes.md exists; refusing to overwrite")
                attachment = {"SourceId": row["Id"], "Name": "Notes.md", "Position": 0.0,
                              "Type": 1, "Location": "Notes.md", "IsIcon": False, "SourceType": 2,
                              "NoteType": 4, "IsWallpaper": False, "IsBrainIcon": False,
                              "IsBrainAttachment": False, **self._base()}
                self._table("attachments").add(attachment)
            now = _now()
            attachment.update(DataLength=len(payload), ModificationDateTime=now, FileModificationDateTime=now)
            self._members[f"{row['Id']}/Notes.md"] = payload
            self.meta.setdefault("AttachmentFileStates", {})[attachment["Id"]] = 1
            row["ModificationDateTime"] = now
            self._refresh_note_cache(row, cache_parts)
            return {"id": row["Id"], "attachment_id": attachment["Id"], "bytes": len(payload)}
        if action == "reuse_icon":
            self._keys(op, ("id", "from_id"), ("id", "from_id"))
            target = self._find("thoughts", op["id"])
            source = self._find("thoughts", op["from_id"])
            if target.get("Kind") not in (1, 2, 4):
                raise ArchiveError("System thought icons cannot be edited")
            if target.get("Kind") != source.get("Kind"):
                raise ArchiveError("Icon source and target must have the same Kind")
            if source.get("ThoughtIconInfo") != "7::0:False:False:0:" or target.get("ThoughtIconInfo") not in ("1::0:False:False:0:", "7::0:False:False:0:"):
                raise ArchiveError("Icon reuse supports only observed simple custom-icon state")
            if target.get("TypeId") or target.get("TagIds"):
                raise ArchiveError("Icon target must not have type or tag state")
            if any(item.get("ThoughtIdB") == target["Id"] and item.get("Meaning") in (2, 5) for item in self._table("links").records()):
                raise ArchiveError("Icon target must not have incoming type or tag links")
            if any(item.get("SourceId") == target["Id"] and not item.get("IsIcon") for item in self._table("attachments").records()):
                raise ArchiveError("Icon target must not have note or other attachment state")
            matches = [item for item in self._table("attachments").records() if item.get("SourceId") == source["Id"] and item.get("IsIcon") is True]
            if len(matches) != 1:
                raise ArchiveError("Source must have exactly one icon attachment")
            original = matches[0]
            if original.get("SourceType") != 2 or any(original.get(key) for key in ("IsBrainIcon", "IsBrainAttachment", "IsWallpaper")):
                raise ArchiveError("Source icon must be an ordinary thought icon attachment")
            path = self._attachment_path(original)
            if not path or path not in self._members:
                raise ArchiveError("Source icon file is missing")
            if original.get("Location") != "Icon.png" or original.get("Type") != 5:
                raise ArchiveError("Only observed Type=5 Icon.png templates are supported")
            if not self._members[path].startswith(b"\x89PNG\r\n\x1a\n"):
                raise ArchiveError("Source icon is not a PNG")
            existing = [item for item in self._table("attachments").records() if item.get("SourceId") == target["Id"] and item.get("IsIcon")]
            if existing:
                raise ArchiveError("Target already has an icon; replacement is not supported")
            destination = f"{target['Id']}/.data/Icon.png"
            if destination in self._members:
                raise ArchiveError("Target icon path already exists")
            attachment = copy.deepcopy(original)
            for key in list(attachment):
                if key.startswith("Sync"):
                    attachment.pop(key)
            attachment.update(self._base())
            attachment.update(SourceId=target["Id"], DataLength=len(self._members[path]), FileModificationDateTime=_now())
            self._table("attachments").add(attachment)
            self._members[destination] = self._members[path]
            self.meta.setdefault("AttachmentFileStates", {})[attachment["Id"]] = 1
            target["ThoughtIconInfo"] = source["ThoughtIconInfo"]
            target["ModificationDateTime"] = _now()
            return {"id": target["Id"], "attachment_id": attachment["Id"], "from_id": source["Id"]}
        if action == "update_field":
            self._keys(op, ("id", "value"), ("id", "value"))
            row = self._find("fieldinstances", op["id"])
            fields = [key for key in row if key.endswith("Value")]
            if len(fields) != 1:
                raise ArchiveError("Field instance must have exactly one existing *Value field")
            key = fields[0]
            value = op["value"]
            definition = self._find("fielddefinitions", row["FieldDefinitionId"])
            data_type = definition.get("DataType")
            if key != "DecimalValue" or type(data_type) is not int or data_type not in (2, 3):
                raise ArchiveError("Only DecimalValue for Integer (2) and Decimal (3) properties is supported")
            self._numeric_field_value(value, data_type)
            row[key] = value
            row["ModificationDateTime"] = _now()
            return {"id": row["Id"], "field": key}
        raise ArchiveError(f"Unknown operation: {action}")

    def _render_members(self):
        members = dict(self._members)
        for name, table in self._tables.items():
            members[name] = table.render()
        if self.meta != self._meta_initial:
            bom = b"\xef\xbb\xbf" if self._meta_original.startswith(b"\xef\xbb\xbf") else b""
            newline = b"\r\n" if self._meta_original.endswith(b"\r\n") else b"\n" if self._meta_original.endswith(b"\n") else b""
            members["meta.json"] = bom + _encode(self.meta) + newline
        return members

    def write(self, output):
        """Atomically create output without ever replacing an existing path."""
        if type(self.meta.get("ExchangeFormatVersion")) is not int or self.meta.get("ExchangeFormatVersion") != 5:
            raise ArchiveError("Writing requires ExchangeFormatVersion=5")
        output = Path(output).absolute()
        if output.resolve() == self.path:
            raise ArchiveError("Output must be a separate file")
        if os.path.lexists(output):
            raise ArchiveError(f"Output already exists: {output}")
        validation = self.validate()
        if validation["errors"]:
            raise ArchiveError("Result validation failed: " + "; ".join(validation["errors"]))
        members = self._render_members()
        if sum(map(len, members.values())) > self.MAX_TOTAL_BYTES or len(members) > self.MAX_MEMBERS:
            raise ArchiveError("Result exceeds archive size limits")
        if any(len(data) > self.MAX_MEMBER_BYTES for data in members.values()):
            raise ArchiveError("Result member exceeds size limit")
        descriptor, temporary = tempfile.mkstemp(prefix=".brz-", suffix=".tmp", dir=output.parent)
        os.close(descriptor)
        try:
            with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as destination:
                destination.comment = self._comment
                for name, data in members.items():
                    if name in self._infos:
                        destination.writestr(copy.copy(self._infos[name]), data)
                    else:
                        destination.writestr(name, data)
            # Windows fsync/_commit requires a writable file descriptor.
            with open(temporary, "r+b") as stream:
                os.fsync(stream.fileno())
            # Validate the actual serialized bytes before publishing the file.
            # A successful in-memory check alone cannot detect serialization
            # defects, stale table bytes, or a damaged ZIP writer result.
            reopened = Archive(temporary)
            check = reopened.validate()
            if check["errors"]:
                raise ArchiveError("Serialized result validation failed: " + "; ".join(check["errors"]))
            if reopened._render_members() != members:
                raise ArchiveError("Serialized result differs from the intended member bytes")
            # Hard-link publication is atomic and fails if output exists, even
            # if another process created it after the preflight check.
            os.link(temporary, output)
        except FileExistsError as exc:
            raise ArchiveError(f"Output already exists: {output}") from exc
        finally:
            os.unlink(temporary)
        return output
