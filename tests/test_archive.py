"""Behavioral tests for safe BRZ edits; fixtures are synthetic and self-contained."""

import hashlib
import json
from pathlib import Path
import stat
import struct
import tempfile
import unittest
import warnings
import zipfile

from brain_toolkit.archive import Archive, ArchiveError


BRAIN = "00000000-0000-4000-8000-000000000001"
PARENT = "00000000-0000-4000-8000-000000000002"
CHILD = "00000000-0000-4000-8000-000000000003"
TYPE = "00000000-0000-4000-8000-000000000004"
TAG = "00000000-0000-4000-8000-000000000005"
EDGE = "00000000-0000-4000-8000-000000000006"
ICON = "00000000-0000-4000-8000-000000000007"
DEFINITION = "00000000-0000-4000-8000-000000000008"
FIELD = "00000000-0000-4000-8000-000000000009"
NEW = "00000000-0000-4000-8000-00000000000a"
NEW_EDGE = "00000000-0000-4000-8000-00000000000b"
TIME = "2025-03-21T01:02:03.1234567"
BOM = b"\xef\xbb\xbf"
# A complete 1x1 PNG, treated as an attachment rather than parsed by this tool.
PNG = bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010804000000b51c0c02"
    "0000000b4944415478da6364f80f00010501012718e3660000000049454e44ae426082"
)


def record(ident, **fields):
    return dict(fields, BrainId=BRAIN, Id=ident,
                CreationDateTime=TIME, ModificationDateTime=TIME)


def thought(ident, name, kind=1, **fields):
    return record(ident, Name=name, Kind=kind, ACType=0, TagIds=[],
                  ThoughtIconInfo="1::0:False:False:0:", **fields)


def ndjson(rows):
    return BOM + b"".join(
        (json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\r\n")
        .encode("utf-8") for row in rows
    )


def read_rows(path, table):
    with zipfile.ZipFile(path) as archive:
        return [json.loads(line) for line in archive.read(table + ".json")
                .decode("utf-8-sig").splitlines() if line.strip()]


def rows_by_id(path, table):
    return {row["Id"]: row for row in read_rows(path, table)}


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.source = self.directory / "source.brz"
        parent = thought(PARENT, "知识", VendorExtension={"keep": [1, "原值"]})
        parent["ThoughtIconInfo"] = "7::0:False:False:0:"
        self.tables = {
            "thoughts": [parent, thought(CHILD, "子节点"),
                         thought(TYPE, "已学", kind=2), thought(TAG, "标签", kind=4)],
            "links": [record(EDGE, ThoughtIdA=PARENT, ThoughtIdB=CHILD,
                             Kind=1, Relation=1, Direction=-1, Meaning=1, Thickness=-1)],
            "attachments": [record(
                ICON, SourceId=PARENT, Name="Icon.png", Location="Icon.png",
                Position=0.0, Type=5, SourceType=2, NoteType=0, IsIcon=True,
                IsWallpaper=False, IsBrainIcon=False, IsBrainAttachment=False,
                DataLength=len(PNG), FileModificationDateTime=TIME,
            )],
            "fielddefinitions": [record(DEFINITION, Name="custom.boolean", DataType=2)],
            "fieldinstances": [record(FIELD, FieldDefinitionId=DEFINITION,
                                      SourceId=CHILD, SourceType=2, DecimalValue=1.0)],
            "modificationlogs": [], "settings": [], "brainusers": [],
            "syncpoints": [], "tombstones": [], "calendarevents": [], "access": [],
        }
        self.members = {name + ".json": ndjson(rows) for name, rows in self.tables.items()}
        self.members["meta.json"] = BOM + json.dumps({
            "BrainId": BRAIN, "ExchangeFormatVersion": 5,
            "AttachmentFileStates": {ICON: 1}, "FutureMeta": {"keep": True},
        }).encode("utf-8")
        self.members[f"{PARENT}/.data/Icon.png"] = PNG
        self.members["unknown.bin"] = b"\x00\xffopaque\x00"
        self.members["futuretable.json"] = ndjson([{"opaque": ["future", 42]}])
        self.members["unknown-schema.json"] = b'["opaque future format", {"rows": [1, 2]}]\n'
        self.members["opaque-extension.json"] = b"\xffThis unknown member is not JSON.\x00"
        self.create_archive()

    def create_archive(self, extra=()):
        with zipfile.ZipFile(self.source, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.comment = b"original archive comment"
            for name, data in self.members.items():
                info = zipfile.ZipInfo(name, date_time=(2025, 3, 21, 1, 2, 4))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.comment = b"original member comment"
                info.extra = struct.pack("<HH", 0xCAFE, 4) + b"data"
                info.create_system = 3
                info.internal_attr = 1
                info.external_attr = (stat.S_IFREG | 0o600) << 16
                archive.writestr(info, data)
            for info, data in extra:
                archive.writestr(info, data)

    def plan(self, archive, *operations):
        return {"source_sha256": archive.source_sha256, "operations": list(operations)}

    def edit(self, *operations):
        archive = Archive(self.source)
        changes = archive.apply_plan(self.plan(archive, *operations), base_dir=self.directory)
        output = self.directory / "edited.brz"
        archive.write(output)
        return archive, output, changes

    def test_unmodified_archive_roundtrip_preserves_all_members_and_metadata(self):
        archive = Archive(self.source)
        self.assertEqual([], archive.validate()["errors"])
        output = self.directory / "roundtrip.brz"
        archive.write(output)
        with zipfile.ZipFile(self.source) as old, zipfile.ZipFile(output) as new:
            self.assertEqual(old.comment, new.comment)
            self.assertEqual(old.namelist(), new.namelist())
            for info in old.infolist():
                other = new.getinfo(info.filename)
                self.assertEqual(old.read(info.filename), new.read(info.filename))
                for attribute in ("date_time", "comment", "extra", "external_attr",
                                  "internal_attr", "create_system", "compress_type"):
                    self.assertEqual(getattr(info, attribute), getattr(other, attribute))

    def test_rename_keeps_identity_unknown_fields_and_other_raw_records(self):
        original_hash = hashlib.sha256(self.source.read_bytes()).hexdigest()
        archive, output, changes = self.edit(
            {"op": "update_thought", "id": PARENT, "set": {"Name": "知识 · 整理后"}},
        )
        changed = rows_by_id(output, "thoughts")[PARENT]
        self.assertEqual("知识 · 整理后", changed["Name"])
        self.assertEqual({"keep": [1, "原值"]}, changed["VendorExtension"])
        self.assertEqual(TIME, changed["CreationDateTime"])
        self.assertEqual(original_hash, hashlib.sha256(self.source.read_bytes()).hexdigest())
        self.assertTrue(changes)
        with zipfile.ZipFile(output) as result:
            before = self.members["thoughts.json"].splitlines(keepends=True)
            after = result.read("thoughts.json").splitlines(keepends=True)
            self.assertEqual(before[1:], after[1:])
            self.assertTrue(result.read("thoughts.json").startswith(BOM))
            for name, data in self.members.items():
                if name != "thoughts.json":
                    self.assertEqual(data, result.read(name), name)
        self.assertEqual([], archive.validate()["errors"])

    def test_add_thought_and_parent_and_jump_relations(self):
        archive, output, _ = self.edit(
            {"op": "add_thought", "id": NEW, "name": "新想法", "kind": 1},
            {"op": "add_link", "id": NEW_EDGE, "a": PARENT, "b": NEW, "relation": "parent"},
            {"op": "add_link", "a": CHILD, "b": NEW, "relation": "jump"},
        )
        self.assertEqual("新想法", rows_by_id(output, "thoughts")[NEW]["Name"])
        edges = read_rows(output, "links")
        ordinary = next(row for row in edges if row["Id"] == NEW_EDGE)
        self.assertEqual((PARENT, NEW, 1, 1),
                         (ordinary["ThoughtIdA"], ordinary["ThoughtIdB"],
                          ordinary["Relation"], ordinary["Meaning"]))
        jump = next(row for row in edges if row["ThoughtIdA"] == CHILD)
        self.assertEqual((3, 1), (jump["Relation"], jump["Meaning"]))
        self.assertEqual([], archive.validate()["errors"])

    def test_type_and_tag_have_consistent_graph_records(self):
        archive, output, _ = self.edit(
            {"op": "set_type", "id": CHILD, "type_id": TYPE},
            {"op": "set_tag", "id": CHILD, "tag_id": TAG, "present": True},
        )
        child = rows_by_id(output, "thoughts")[CHILD]
        self.assertEqual(TYPE, child["TypeId"])
        self.assertEqual([], child["TagIds"])
        edges = read_rows(output, "links")
        self.assertEqual(1, sum(row["Meaning"] == 2 and row["ThoughtIdA"] == TYPE
                                and row["ThoughtIdB"] == CHILD for row in edges))
        self.assertEqual(1, sum(row["Meaning"] == 5 and row["ThoughtIdA"] == TAG
                                and row["ThoughtIdB"] == CHILD for row in edges))
        self.assertEqual([], archive.validate()["errors"])
        second = Archive(output)
        second.apply_plan(self.plan(second,
            {"op": "set_type", "id": CHILD, "type_id": None},
            {"op": "set_tag", "id": CHILD, "tag_id": TAG, "present": False},
        ))
        cleared = self.directory / "cleared.brz"
        second.write(cleared)
        self.assertIsNone(rows_by_id(cleared, "thoughts")[CHILD].get("TypeId"))
        self.assertFalse(any(row["Meaning"] in (2, 5) for row in read_rows(cleared, "links")))

    def test_note_text_updates_utf8_byte_length_and_manifest(self):
        text = "# 中文笔记\n\n新的内容 🧠\n"
        archive, output, _ = self.edit({"op": "set_note", "id": CHILD, "text": text})
        notes = [row for row in read_rows(output, "attachments")
                 if row["SourceId"] == CHILD and row["Location"] == "Notes.md"]
        self.assertEqual(1, len(notes))
        note = notes[0]
        self.assertEqual((1, 4, 2), (note["Type"], note["NoteType"], note["SourceType"]))
        self.assertEqual(len(text.encode("utf-8")), note["DataLength"])
        with zipfile.ZipFile(output) as result:
            self.assertEqual(text.encode("utf-8"), result.read(f"{CHILD}/Notes.md"))
            meta = json.loads(result.read("meta.json").decode("utf-8-sig"))
        self.assertEqual(1, meta["AttachmentFileStates"][note["Id"]])
        self.assertEqual({"keep": True}, meta["FutureMeta"])
        second = Archive(output)
        second.apply_plan(self.plan(second, {"op": "set_note", "id": CHILD, "text": "更新"}))
        rewritten = self.directory / "note-updated.brz"
        second.write(rewritten)
        notes2 = [row for row in read_rows(rewritten, "attachments")
                  if row["SourceId"] == CHILD and row["Location"] == "Notes.md"]
        self.assertEqual([note["Id"]], [row["Id"] for row in notes2])
        self.assertEqual(6, notes2[0]["DataLength"])
        self.assertEqual([], archive.validate()["errors"])

    def test_markdown_note_does_not_remove_existing_html_note(self):
        html_id = "00000000-0000-4000-8000-00000000000c"
        html_path = f"{CHILD}/Notes/notes.html"
        html = b"<p>Original HTML note</p>"
        self.tables["attachments"].append(record(
            html_id, SourceId=CHILD, Name="notes.html", Location="notes.html",
            Type=4, SourceType=2, NoteType=2, IsIcon=False,
            IsWallpaper=False, IsBrainIcon=False, IsBrainAttachment=False,
            DataLength=len(html), Position=0.0,
        ))
        self.members["attachments.json"] = ndjson(self.tables["attachments"])
        self.members[html_path] = html
        meta = json.loads(self.members["meta.json"].decode("utf-8-sig"))
        meta["AttachmentFileStates"][html_id] = 1
        self.members["meta.json"] = BOM + json.dumps(meta).encode("utf-8")
        self.create_archive()
        _, output, _ = self.edit({"op": "set_note", "id": CHILD, "text": "# Markdown"})
        with zipfile.ZipFile(output) as result:
            self.assertEqual(html, result.read(html_path))
            self.assertEqual(b"# Markdown", result.read(f"{CHILD}/Notes.md"))
        self.assertIn(html_id, rows_by_id(output, "attachments"))

    def test_reuse_icon_clones_file_and_preserves_source_attachment(self):
        archive, output, _ = self.edit({"op": "reuse_icon", "id": CHILD, "from_id": PARENT})
        icons = [row for row in read_rows(output, "attachments") if row.get("IsIcon")]
        child_icon = next(row for row in icons if row["SourceId"] == CHILD)
        parent_icon = next(row for row in icons if row["SourceId"] == PARENT)
        self.assertNotEqual(ICON, child_icon["Id"])
        self.assertEqual(ICON, parent_icon["Id"])
        with zipfile.ZipFile(output) as result:
            self.assertEqual(PNG, result.read(f"{CHILD}/.data/Icon.png"))
            self.assertEqual(PNG, result.read(f"{PARENT}/.data/Icon.png"))
        thoughts = rows_by_id(output, "thoughts")
        self.assertEqual(thoughts[PARENT]["ThoughtIconInfo"], thoughts[CHILD]["ThoughtIconInfo"])
        self.assertEqual([], archive.validate()["errors"])

    def test_reuse_icon_rejects_cross_kind_or_complex_cached_icon(self):
        for fields in ({"Kind": 2}, {"ThoughtIconInfo": "7::0:True:False:0:"}):
            with self.subTest(source_fields=fields):
                original = self.tables["thoughts"][0].copy()
                self.tables["thoughts"][0].update(fields)
                self.members["thoughts.json"] = ndjson(self.tables["thoughts"])
                self.create_archive()
                archive = Archive(self.source)
                with self.assertRaises(ArchiveError):
                    archive.apply_plan(self.plan(archive,
                        {"op": "reuse_icon", "id": CHILD, "from_id": PARENT},
                    ))
                self.tables["thoughts"][0] = original

    def test_existing_field_value_and_color_edit_preserve_schema(self):
        _, output, _ = self.edit(
            {"op": "update_field", "id": FIELD, "value": 0.0},
            {"op": "update_thought", "id": CHILD,
             "set": {"BackgroundColor": -16728064, "Label": "状态"}},
        )
        field = rows_by_id(output, "fieldinstances")[FIELD]
        self.assertEqual(0.0, field["DecimalValue"])
        self.assertEqual(DEFINITION, field["FieldDefinitionId"])
        self.assertEqual(CHILD, field["SourceId"])
        self.assertEqual(-16728064, rows_by_id(output, "thoughts")[CHILD]["BackgroundColor"])

    def test_unobserved_foreground_color_field_is_rejected(self):
        archive = Archive(self.source)
        with self.assertRaises(ArchiveError):
            archive.apply_plan(self.plan(archive,
                {"op": "update_thought", "id": CHILD, "set": {"ForegroundColor": -1}},
            ))

    def test_names_may_repeat_without_reusing_identity(self):
        _, output, _ = self.edit({"op": "add_thought", "id": NEW, "name": "知识", "kind": 1})
        thoughts = rows_by_id(output, "thoughts")
        self.assertEqual(thoughts[PARENT]["Name"], thoughts[NEW]["Name"])
        self.assertNotEqual(thoughts[PARENT]["Id"], thoughts[NEW]["Id"])

    def test_failed_plan_rolls_back_prior_operations_and_new_files(self):
        archive = Archive(self.source)
        with self.assertRaises(ArchiveError):
            archive.apply_plan(self.plan(archive,
                {"op": "set_note", "id": CHILD, "text": "should roll back"},
                {"op": "update_thought", "id": PARENT, "set": {"Id": NEW}},
            ))
        output = self.directory / "after-failure.brz"
        archive.write(output)
        with zipfile.ZipFile(output) as result:
            self.assertEqual(set(self.members), set(result.namelist()))
            for name, data in self.members.items():
                self.assertEqual(data, result.read(name), name)

    def test_wrong_source_hash_cannot_edit(self):
        archive = Archive(self.source)
        plan = self.plan(archive, {"op": "update_thought", "id": CHILD, "set": {"Name": "bad"}})
        plan["source_sha256"] = "0" * 64
        with self.assertRaises(ArchiveError):
            archive.apply_plan(plan)

    def test_output_never_overwrites_source_or_existing_destination(self):
        archive = Archive(self.source)
        original = self.source.read_bytes()
        with self.assertRaises((ArchiveError, FileExistsError)):
            archive.write(self.source)
        existing = self.directory / "existing.brz"
        existing.write_bytes(b"existing")
        with self.assertRaises((ArchiveError, FileExistsError)):
            archive.write(existing)
        self.assertEqual(b"existing", existing.read_bytes())
        self.assertEqual(original, self.source.read_bytes())

    def test_duplicate_semantic_relation_is_rejected(self):
        archive = Archive(self.source)
        with self.assertRaises(ArchiveError):
            archive.apply_plan(self.plan(archive,
                {"op": "add_link", "a": PARENT, "b": CHILD, "relation": "parent"},
            ))

    def test_type_assignment_requires_a_type_node(self):
        archive = Archive(self.source)
        with self.assertRaises(ArchiveError):
            archive.apply_plan(self.plan(archive, {"op": "set_type", "id": CHILD, "type_id": TAG}))

    def test_remove_ordinary_link(self):
        _, output, _ = self.edit({"op": "remove_link", "id": EDGE})
        self.assertEqual([], read_rows(output, "links"))

    def test_archive_path_traversal_is_rejected(self):
        self.create_archive(extra=[("../outside.bin", b"no")])
        with self.assertRaises(ArchiveError):
            Archive(self.source)

    def test_archive_duplicate_member_is_rejected(self):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            self.create_archive(extra=[("thoughts.json", self.members["thoughts.json"])])
        with self.assertRaises(ArchiveError):
            Archive(self.source)

    def test_archive_symlink_is_rejected(self):
        info = zipfile.ZipInfo("a-symlink")
        info.create_system = 3
        info.external_attr = (stat.S_IFLNK | 0o777) << 16
        self.create_archive(extra=[(info, b"../outside")])
        with self.assertRaises(ArchiveError):
            Archive(self.source)

    def test_encrypted_archive_member_is_rejected(self):
        # The ZIP crypto flag is sufficient for readers to require a password.
        data = bytearray(self.source.read_bytes())
        for signature, flag_offset in ((b"PK\x03\x04", 6), (b"PK\x01\x02", 8)):
            position = data.index(signature)
            flags = struct.unpack_from("<H", data, position + flag_offset)[0]
            struct.pack_into("<H", data, position + flag_offset, flags | 1)
        self.source.write_bytes(data)
        with self.assertRaises(ArchiveError):
            Archive(self.source)

    def test_known_table_rejects_duplicate_json_keys_and_nonfinite_numbers(self):
        original = self.members["thoughts.json"]
        for payload in (b'{"Name":"one","Name":"two"}\n',
                        b'{"Name":"bad","Value":NaN}\n'):
            with self.subTest(payload=payload):
                self.members["thoughts.json"] = BOM + payload
                self.create_archive()
                with self.assertRaises(ArchiveError):
                    Archive(self.source)
        self.members["thoughts.json"] = original

    def test_missing_link_endpoint_is_reported_as_an_error(self):
        self.tables["links"][0]["ThoughtIdB"] = NEW
        self.members["links.json"] = ndjson(self.tables["links"])
        self.create_archive()
        try:
            archive = Archive(self.source)
        except ArchiveError:
            return
        self.assertTrue(archive.validate()["errors"])

    def test_newer_exchange_format_cannot_be_mutated(self):
        meta = json.loads(self.members["meta.json"].decode("utf-8-sig"))
        meta["ExchangeFormatVersion"] = 99
        self.members["meta.json"] = BOM + json.dumps(meta).encode("utf-8")
        self.create_archive()
        try:
            archive = Archive(self.source)
        except ArchiveError:
            return
        with self.assertRaises(ArchiveError):
            archive.apply_plan(self.plan(archive,
                {"op": "update_thought", "id": CHILD, "set": {"Name": "unsafe"}},
            ))

    def test_original_attachment_size_mismatch_is_a_warning(self):
        self.tables["attachments"][0]["DataLength"] = len(PNG) + 100
        self.members["attachments.json"] = ndjson(self.tables["attachments"])
        self.create_archive()
        validation = Archive(self.source).validate()
        self.assertEqual([], validation["errors"])
        self.assertTrue(validation["warnings"])


class ProvidedExportSmokeTests(unittest.TestCase):
    def test_each_available_provided_export_is_structurally_valid(self):
        root = Path(__file__).resolve().parents[1]
        fixtures = sorted((root / "work/extracted/TheBrain14 - v2").glob("*.brz"))
        if not fixtures:
            self.skipTest("Original split archive has not been extracted; synthetic tests are sufficient in CI")
        self.assertEqual(5, len(fixtures))
        for path in fixtures:
            with self.subTest(export=path.name):
                archive = Archive(path)
                self.assertEqual([], archive.validate()["errors"])
                self.assertIsInstance(archive.summary(), dict)


if __name__ == "__main__":
    unittest.main()
