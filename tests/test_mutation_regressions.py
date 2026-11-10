"""Regression tests for preserving relationship-owned data during edits."""
import unittest

from brain_toolkit import Archive, ArchiveError
import test_archive as fixtures
from test_archive import CHILD, TYPE, TAG, EDGE, FIELD, NEW, NEW_EDGE, record, ndjson


class MutationRegressionTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.ArchiveTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)

    def source_with_assignment(self, meaning=2, **link_fields):
        f = self.fixture
        source_id = TYPE if meaning == 2 else TAG
        if meaning == 2:
            f.tables["thoughts"][1]["TypeId"] = source_id
        f.tables["links"].append(record(
            NEW_EDGE, ThoughtIdA=source_id, ThoughtIdB=CHILD, Kind=1, Relation=1,
            Direction=-1, Meaning=meaning, Thickness=-1, **link_fields,
        ))
        self.rebuild()
        return Archive(f.source)

    def rebuild(self):
        f = self.fixture
        f.members.update({name + ".json": ndjson(rows) for name, rows in f.tables.items()})
        f.create_archive()

    def apply(self, archive, *operations):
        return archive.apply_plan({"source_sha256": archive.source_sha256, "operations": list(operations)})

    def test_reassigning_current_type_preserves_styled_link_and_all_members(self):
        a = self.source_with_assignment(Name="关系说明", FutureStyle={"preserve": True})
        before = a._render_members()
        changes = self.apply(a, {"op": "set_type", "id": CHILD, "type_id": TYPE})
        self.assertTrue(changes[0]["no_op"])
        self.assertEqual(before, a._render_members())

    def test_already_present_or_absent_tags_are_noops(self):
        a = self.source_with_assignment(meaning=5)
        before = a._render_members()
        changes = self.apply(a, {"op": "set_tag", "id": CHILD, "tag_id": TAG, "present": True})
        self.assertTrue(changes[0]["no_op"])
        self.assertEqual(before, a._render_members())
        self.apply(a, {"op": "set_tag", "id": CHILD, "tag_id": TAG, "present": False})
        before = a._render_members()
        changes = self.apply(a, {"op": "set_tag", "id": CHILD, "tag_id": TAG, "present": False})
        self.assertTrue(changes[0]["no_op"])
        self.assertEqual(before, a._render_members())

    def test_type_change_refuses_to_drop_styling_and_rolls_back(self):
        a = self.source_with_assignment(Name="关系说明", FutureStyle={"preserve": True})
        before = a._render_members()
        with self.assertRaisesRegex(ArchiveError, "styling|metadata"):
            self.apply(a, {"op": "update_thought", "id": CHILD, "set": {"Name": "不应保留"}},
                       {"op": "set_type", "id": CHILD, "type_id": None})
        self.assertEqual(before, a._render_members())

    def test_plain_type_can_change_without_touching_other_links(self):
        a = self.source_with_assignment()
        before = {x["Id"]: x for x in a.rows("links")}
        self.apply(a, {"op": "add_thought", "id": NEW, "name": "新类型", "kind": 2},
                   {"op": "set_type", "id": CHILD, "type_id": NEW})
        after = {x["Id"]: x for x in a.rows("links")}
        self.assertNotIn(NEW_EDGE, after)
        self.assertEqual(before[EDGE], after[EDGE])
        assigned = [x for x in after.values() if x.get("Meaning") == 2]
        self.assertEqual([(NEW, CHILD)], [(x["ThoughtIdA"], x["ThoughtIdB"]) for x in assigned])

    def test_link_owned_fields_block_all_supported_removal_paths(self):
        for operation in ("remove_link", "set_type", "set_tag"):
            with self.subTest(operation=operation):
                if operation == "remove_link":
                    a = Archive(self.fixture.source)
                    link_id = EDGE
                    edit = {"op": operation, "id": link_id}
                else:
                    a = self.source_with_assignment(2 if operation == "set_type" else 5)
                    link_id = NEW_EDGE
                    edit = ({"op": operation, "id": CHILD, "type_id": None} if operation == "set_type"
                            else {"op": operation, "id": CHILD, "tag_id": TAG, "present": False})
                field = a._find("fieldinstances", FIELD)
                field.update(SourceType=3, SourceId=link_id)
                self.assertEqual([], a.validate()["errors"])
                before = a._render_members()
                with self.assertRaisesRegex(ArchiveError, "owns fieldinstances"):
                    self.apply(a, edit)
                self.assertEqual(before, a._render_members())
                # Reset the shared fixture before the next subtest.
                self.fixture.tables["links"] = [self.fixture.tables["links"][0]]
                self.fixture.tables["thoughts"][1].pop("TypeId", None)
                self.rebuild()

    def test_link_owned_file_without_metadata_blocks_removal(self):
        a = Archive(self.fixture.source)
        a._members[EDGE + "/Notes.md"] = b"retain this note"
        before = a._render_members()
        with self.assertRaisesRegex(ArchiveError, "owns archive members"):
            self.apply(a, {"op": "remove_link", "id": EDGE})
        self.assertEqual(before, a._render_members())

    def test_new_type_and_tag_match_official_creator_defaults(self):
        for kind in (1, 2, 4):
            with self.subTest(kind=kind):
                a = Archive(self.fixture.source)
                self.apply(a, {"op": "add_thought", "id": NEW, "name": "新节点", "kind": kind})
                new = next(row for row in a.rows("thoughts") if row["Id"] == NEW)
                self.assertEqual("8::0:False:False:0:" if kind == 4 else "1::0:False:False:0:", new["ThoughtIconInfo"])
                if kind == 2:
                    self.assertEqual("新节点", new["Label"])
                else:
                    self.assertNotIn("Label", new)

    def test_note_cache_updates_only_has_notes_token_and_tracks_empty_note(self):
        a = Archive(self.fixture.source)
        row = a._find("thoughts", CHILD)
        row["ThoughtIconInfo"] = "4:document.pdf:3:False:True:2:extra"
        self.apply(a, {"op": "set_note", "id": CHILD, "text": "中文笔记"})
        self.assertEqual("4:document.pdf:3:True:True:2:extra", a._find("thoughts", CHILD)["ThoughtIconInfo"])
        self.apply(a, {"op": "set_note", "id": CHILD, "text": ""})
        self.assertEqual("4:document.pdf:3:False:True:2:extra", a._find("thoughts", CHILD)["ThoughtIconInfo"])

    def test_note_cache_respects_newest_html_not_merely_last_edited_file(self):
        a = Archive(self.fixture.source)
        html_id = "00000000-0000-4000-8000-000000000020"
        a._table("attachments").add(record(
            html_id, SourceId=CHILD, SourceType=2, Type=4, NoteType=2,
            Name="notes.html", Location="notes.html", DataLength=0,
        ))
        a._find("attachments", html_id)["ModificationDateTime"] = "2100-01-01T00:00:00.1234567"
        a._members[CHILD + "/Notes/notes.html"] = b""
        self.apply(a, {"op": "set_note", "id": CHILD, "text": "new Markdown"})
        self.assertEqual("False", a._find("thoughts", CHILD)["ThoughtIconInfo"].split(":")[3])

    def test_ambiguous_markdown_payload_is_not_overwritten_and_plan_rolls_back(self):
        a = Archive(self.fixture.source)
        self.apply(a, {"op": "set_note", "id": CHILD, "text": "original"})
        original = next(row for row in a.rows("attachments") if row.get("Location") == "Notes.md")
        # A separate ordinary attachment names the same embedded file.
        a._table("attachments").add({**original, "Id": NEW, "NoteType": 0, "Name": "keep this file"})
        before = a._render_members()
        with self.assertRaisesRegex(ArchiveError, "shared by another attachment"):
            self.apply(a, {"op": "update_thought", "id": CHILD, "set": {"Name": "rollback"}},
                       {"op": "set_note", "id": CHILD, "text": "replacement"})
        self.assertEqual(before, a._render_members())

    def test_unrecognized_existing_markdown_metadata_is_not_guessed(self):
        for field, value in (("IsIcon", True), ("Name", "Custom note name")):
            with self.subTest(field=field):
                a = Archive(self.fixture.source)
                self.apply(a, {"op": "set_note", "id": CHILD, "text": "original"})
                note = next(row for row in a._table("attachments").records() if row.get("Location") == "Notes.md")
                note[field] = value
                before = a._render_members()
                with self.assertRaisesRegex(ArchiveError, "Unsupported Markdown note metadata"):
                    self.apply(a, {"op": "set_note", "id": CHILD, "text": "replacement"})
                self.assertEqual(before, a._render_members())

    def test_new_note_cannot_take_a_missing_payload_reserved_by_another_attachment(self):
        a = Archive(self.fixture.source)
        a._table("attachments").add(record(
            NEW, SourceId=CHILD, SourceType=2, Type=1, NoteType=0,
            Name="separate attachment", Location="Notes.md", DataLength=10,
        ))
        before = a._render_members()
        with self.assertRaisesRegex(ArchiveError, "shared by another attachment"):
            self.apply(a, {"op": "set_note", "id": CHILD, "text": "unexpected payload"})
        self.assertEqual(before, a._render_members())

    def test_missing_or_unrecognized_note_cache_rejects_transaction(self):
        for cache in (None, "unknown", "4:C:\\Notes:2:False:False:0:"):
            with self.subTest(cache=cache):
                a = Archive(self.fixture.source)
                a._find("thoughts", CHILD)["ThoughtIconInfo"] = cache
                before = a._render_members()
                with self.assertRaisesRegex(ArchiveError, "ThoughtIconInfo"):
                    self.apply(a, {"op": "update_thought", "id": CHILD, "set": {"Name": "rollback"}},
                               {"op": "set_note", "id": CHILD, "text": "note"})
                self.assertEqual(before, a._render_members())

    def test_note_timestamp_comparison_preserves_submicrosecond_precision(self):
        before = Archive._note_timestamp_key("2025-03-21T01:02:03.1234566")
        after = Archive._note_timestamp_key("2025-03-21T01:02:03.1234567")
        self.assertLess(before, after)
        self.assertEqual(after, Archive._note_timestamp_key("2025-03-21T09:02:03.1234567+08:00"))

    def test_integer_property_rejects_fractional_and_decimal_overflow_values(self):
        for value in (1.5, True, float("nan"), float("inf"), 2**96):
            with self.subTest(value=value):
                a = Archive(self.fixture.source)
                before = a._render_members()
                with self.assertRaises(ArchiveError):
                    self.apply(a, {"op": "update_field", "id": FIELD, "value": value})
                self.assertEqual(before, a._render_members())
        a = Archive(self.fixture.source)
        self.apply(a, {"op": "update_field", "id": FIELD, "value": 42})
        self.assertEqual(42, a._find("fieldinstances", FIELD)["DecimalValue"])

    def test_decimal_property_accepts_fraction_and_rejects_unsupported_precision(self):
        a = Archive(self.fixture.source)
        a._find("fielddefinitions", fixtures.DEFINITION)["DataType"] = 3
        self.apply(a, {"op": "update_field", "id": FIELD, "value": 1.25})
        self.assertEqual(1.25, a._find("fieldinstances", FIELD)["DecimalValue"])
        for value in (1e-29, 1e100):
            with self.subTest(value=value), self.assertRaises(ArchiveError):
                self.apply(a, {"op": "update_field", "id": FIELD, "value": value})

    def test_unknown_property_storage_is_rejected(self):
        for data_type in (1, 4, 5, 99):
            with self.subTest(data_type=data_type):
                a = Archive(self.fixture.source)
                a._find("fielddefinitions", fixtures.DEFINITION)["DataType"] = data_type
                with self.assertRaisesRegex(ArchiveError, "Only DecimalValue"):
                    self.apply(a, {"op": "update_field", "id": FIELD, "value": 0})


if __name__ == "__main__":
    unittest.main()
