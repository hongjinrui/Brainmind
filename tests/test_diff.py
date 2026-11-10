"""Regression tests for auditable member and record comparisons."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from brain_toolkit import Archive, ArchiveError
from brain_toolkit.diff import compare_archives


def ident(number):
    return f"00000000-0000-4000-8000-{number:012x}"


class CompareTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.brain = ident(1)
        self.rows = [
            {"Id": ident(n), "BrainId": self.brain, "Kind": 1, "Name": f"节点 {n}",
             "TagIds": [], "CreationDateTime": "2025-01-01T00:00:00", "ModificationDateTime": "2025-01-01T00:00:00"}
            for n in (2, 3, 4)
        ]
        self.meta = {"BrainId": self.brain, "ExchangeFormatVersion": 5, "AttachmentFileStates": {}}

    def make(self, name, *, rows=None, links=None, extra=None, pretty=False, meta=None):
        path = self.root / name
        with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as output:
            output.writestr("meta.json", json.dumps(self.meta if meta is None else meta))
            for table, records in (
                ("thoughts", self.rows if rows is None else rows),
                ("links", links or []), ("attachments", []),
            ):
                data = b"\xef\xbb\xbf" + b"".join(
                    (json.dumps(r, ensure_ascii=False, separators=None if pretty else (",", ":")) + "\n").encode("utf-8")
                    for r in records
                )
                output.writestr(table + ".json", data)
            for filename, content in (extra or {}).items():
                output.writestr(filename, content)
        return Archive(path)

    def test_identical_files_have_no_changes_and_exact_file_hashes(self):
        before = self.make("a.brz")
        report = compare_archives(before, Archive(before.path))
        self.assertEqual({"added": 0, "removed": 0, "changed": 0}, report["record_counts"])
        self.assertEqual({}, report["records"])
        self.assertEqual(4, report["members"]["unchanged_count"])
        self.assertEqual(hashlib.sha256(before.path.read_bytes()).hexdigest(), report["source_sha256"])
        self.assertEqual(report["source_sha256"], report["target_sha256"])

    def test_same_id_rename_reports_values_not_removed_and_added(self):
        rows = copy.deepcopy(self.rows)
        rows[0]["Name"] = "新名称"
        report = compare_archives(self.make("a.brz"), self.make("b.brz", rows=rows))
        change = report["records"]["thoughts.json"]["changed"]["items"][0]
        self.assertEqual(ident(2), change["id"])
        self.assertEqual(["Name"], change["changed_fields"])
        self.assertEqual({"Name": "节点 2"}, change["before"])
        self.assertEqual({"Name": "新名称"}, change["after"])
        self.assertEqual({"added": 0, "removed": 0, "changed": 1}, report["record_counts"])

    def test_added_and_removed_links_keep_distinct_identity(self):
        link = {"Id": ident(10), "BrainId": self.brain, "ThoughtIdA": ident(2), "ThoughtIdB": ident(3),
                "Kind": 1, "Relation": 1, "Meaning": 1, "Direction": -1, "Thickness": -1}
        replacement = {**link, "Id": ident(11), "ThoughtIdA": ident(3), "ThoughtIdB": ident(2)}
        report = compare_archives(self.make("a.brz", links=[link]), self.make("b.brz", links=[replacement]))
        changes = report["records"]["links.json"]
        self.assertEqual(ident(10), changes["removed"]["items"][0]["id"])
        self.assertEqual(ident(11), changes["added"]["items"][0]["id"])
        self.assertEqual(0, changes["changed"]["count"])

    def test_opaque_files_compare_bytes_without_interpreting_json(self):
        before = self.make("a.brz", extra={"future.json": b"\xffold", "old.bin": b"gone"})
        after = self.make("b.brz", extra={"future.json": b"\xffnew", "new.bin": b"added"})
        report = compare_archives(before, after)
        self.assertEqual({}, report["records"])
        changed = report["members"]["changed"]["items"][0]
        self.assertEqual("future.json", changed["path"])
        self.assertEqual(hashlib.sha256(b"\xffnew").hexdigest(), changed["after"]["sha256"])
        self.assertEqual("new.bin", report["members"]["added"]["items"][0]["path"])
        self.assertEqual("old.bin", report["members"]["removed"]["items"][0]["path"])

    def test_formatting_only_is_visible_as_member_change(self):
        report = compare_archives(self.make("a.brz"), self.make("b.brz", pretty=True))
        self.assertEqual({}, report["records"])
        self.assertEqual(1, report["members"]["changed"]["count"])

    def test_limited_details_preserve_totals_and_truncation(self):
        rows = [{**row, "Name": row["Name"] + " edited"} for row in self.rows]
        report = compare_archives(self.make("a.brz"), self.make("b.brz", rows=rows), limit=1)
        changed = report["records"]["thoughts.json"]["changed"]
        self.assertEqual(3, changed["count"])
        self.assertEqual(1, len(changed["items"]))
        self.assertTrue(changed["truncated"])
        self.assertEqual(3, report["record_counts"]["changed"])

    def test_missing_null_and_boolean_number_are_distinguished(self):
        old = copy.deepcopy(self.rows)
        old[0]["VendorFlag"] = True
        new = copy.deepcopy(old)
        new[0]["VendorFlag"] = 1
        new[0]["Extra"] = None
        meta = {**self.meta, "Future": None}
        report = compare_archives(self.make("a.brz", rows=old), self.make("b.brz", rows=new, meta=meta))
        change = report["records"]["thoughts.json"]["changed"]["items"][0]
        self.assertEqual(["Extra", "VendorFlag"], change["changed_fields"])
        self.assertNotIn("Extra", change["before"])
        self.assertIsNone(change["after"]["Extra"])
        self.assertEqual(["Future"], report["meta"]["changed_fields"])
        self.assertEqual({}, report["meta"]["before"])

    def test_invalid_limit_and_modified_objects_are_rejected(self):
        before, after = self.make("a.brz"), self.make("b.brz")
        for limit in (0, -1, True, 1.5):
            with self.subTest(limit=limit), self.assertRaises(ArchiveError):
                compare_archives(before, after, limit)
        after.apply_plan({"source_sha256": after.source_sha256, "operations": [
            {"op": "update_thought", "id": ident(2), "set": {"Name": "编辑中"}},
        ]})
        with self.assertRaisesRegex(ArchiveError, "freshly loaded"):
            compare_archives(before, after)


if __name__ == "__main__":
    unittest.main()
