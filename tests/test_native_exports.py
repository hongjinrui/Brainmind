"""Native TheBrain 14 shapes observed during import/reopen/export checks.

Fixtures are synthetic and contain no user content or account identifiers.
"""
import copy
import unittest
import zipfile

from brain_toolkit import Archive, ArchiveError
from brain_toolkit.diff import compare_archives
import test_archive as fixtures
from test_archive import BRAIN, CHILD, NEW, NEW_EDGE, PNG, ndjson, record


class NativeExportTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.ArchiveTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.access = {"BrainId": BRAIN, "AccessorId": NEW, "AccessType": 3}
        self.fixture.tables["access"] = [self.access]

    def rebuild(self):
        f = self.fixture
        f.members.update({name + ".json": ndjson(rows) for name, rows in f.tables.items()})
        f.create_archive()
        return Archive(f.source)

    def wallpaper(self):
        row = record(NEW_EDGE, SourceId=NEW, SourceType=1, Type=1, NoteType=0,
                     Name="wallpaper.png", Location="wallpaper.png", IsWallpaper=True,
                     IsBrainAttachment=True, IsIcon=False, IsBrainIcon=False, DataLength=len(PNG))
        self.fixture.tables["attachments"].append(row)
        self.fixture.members[NEW + "/wallpaper.png"] = PNG
        return row

    def test_native_access_survives_edit_write_and_compare_byte_for_byte(self):
        a = self.rebuild()
        self.assertEqual([], a.validate()["errors"])
        original = a.path.read_bytes()
        a.apply_plan({"source_sha256": a.source_sha256, "operations": [
            {"op": "set_note", "id": CHILD, "text": "再导出后继续编辑 🧠"},
        ]})
        target = self.fixture.directory / "edited.brz"
        a.write(target)
        self.assertEqual(original, a.path.read_bytes())
        with zipfile.ZipFile(a.path) as before, zipfile.ZipFile(target) as after:
            self.assertEqual(before.read("access.json"), after.read("access.json"))
        reopened = Archive(target)
        self.assertEqual([], reopened.validate()["errors"])
        self.assertEqual("再导出后继续编辑 🧠", reopened.notes(CHILD)[0]["text"])
        report = compare_archives(Archive(a.path), reopened)
        self.assertNotIn("access.json", report["records"])

    def test_access_value_changes_align_by_brain_and_accessor(self):
        before = self.rebuild()
        self.access["AccessType"] = 2
        after = self.rebuild()
        change = compare_archives(before, after)["records"]["access.json"]
        self.assertEqual(0, change["added"]["count"])
        self.assertEqual(0, change["removed"]["count"])
        self.assertEqual([{"id": f"access:{BRAIN}:{NEW}", "changed_fields": ["AccessType"],
                           "before": {"AccessType": 3}, "after": {"AccessType": 2}}],
                         change["changed"]["items"])

    def test_malformed_or_mismatched_access_is_not_accepted(self):
        for field, value in (("AccessorId", None), ("AccessorId", []), ("AccessorId", "bad"),
                             ("BrainId", NEW), ("BrainId", None),
                             ("AccessType", True), ("AccessType", "3"), ("AccessType", None),
                             ("Id", None)):
            with self.subTest(field=field, value=value):
                self.fixture.tables["access"] = [{**self.access, field: value}]
                self.assertTrue(self.rebuild().validate()["errors"])

    def test_duplicate_native_access_identity_is_rejected_even_if_values_differ(self):
        self.fixture.tables["access"].append({**self.access, "AccessType": 2})
        a = self.rebuild()
        self.assertTrue(any("duplicate" in e for e in a.validate()["errors"]))
        with self.assertRaisesRegex(ArchiveError, "duplicate"):
            compare_archives(a, a)

    def test_native_wallpaper_owner_is_warned_and_preserved_on_edit(self):
        row = self.wallpaper()
        a = self.rebuild()
        validation = a.validate()
        self.assertEqual([], validation["errors"])
        self.assertTrue(any("wallpaper retains a different source brain" in w for w in validation["warnings"]))
        old_attachments = a._members["attachments.json"]
        a.apply_plan({"source_sha256": a.source_sha256, "operations": [
            {"op": "update_thought", "id": CHILD, "set": {"Name": "不改壁纸"}},
        ]})
        target = self.fixture.directory / "wallpaper-copy.brz"
        a.write(target)
        copied = Archive(target)
        self.assertEqual([], copied.validate()["errors"])
        self.assertEqual(old_attachments, copied._members["attachments.json"])
        self.assertEqual(PNG, copied._members[NEW + "/wallpaper.png"])
        self.assertEqual(row, next(r for r in copied.rows("attachments") if r["Id"] == NEW_EDGE))

    def test_wallpaper_exception_requires_observed_flags_and_exact_embedded_bytes(self):
        row = self.wallpaper()
        original = copy.deepcopy(row)
        for field, value in (("SourceId", "invalid"), ("SourceId", CHILD),
                             ("Type", 2), ("Type", True), ("NoteType", 4), ("NoteType", False),
                             ("IsWallpaper", False), ("IsWallpaper", 1),
                             ("IsBrainAttachment", False), ("IsIcon", True), ("IsBrainIcon", True),
                             ("DataLength", len(PNG) + 1), ("DataLength", True),
                             ("Location", "missing.png"), ("Location", "../escape")):
            with self.subTest(field=field, value=value):
                row.clear()
                row.update(original)
                row[field] = value
                self.assertTrue(self.rebuild().validate()["errors"])

    def test_other_live_brain_owners_still_fail(self):
        for table in ("fieldinstances", "calendarevents"):
            with self.subTest(table=table):
                original = list(self.fixture.tables[table])
                self.fixture.tables[table].append(record(
                    NEW_EDGE, SourceType=1, SourceId=NEW, IsWallpaper=True,
                    FieldDefinitionId=fixtures.DEFINITION,
                ))
                errors = self.rebuild().validate()["errors"]
                self.assertTrue(any("missing source brain" in e for e in errors))
                self.fixture.tables[table] = original


if __name__ == "__main__":
    unittest.main()
