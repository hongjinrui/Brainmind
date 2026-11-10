"""Public, source-free BRZ creation and its failure/publication boundaries."""
import contextlib
import copy
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid
import zipfile

from brain_toolkit import Archive, ArchiveError, create_archive
from brain_toolkit.__main__ import main
from brain_toolkit.diff import compare_archives


class CreateTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.content = {
            "schema_version": 1, "title": "原创脑图 🧠",
            "nodes": [
                {"key": "root", "name": "根节点", "note": "# 中文根笔记 🧠"},
                {"key": "left", "name": "同名", "parent_key": "root", "note": "左侧资料"},
                {"key": "right", "name": "同名", "parent_key": "root", "note": "右侧资料",
                 "label": "不同身份", "background_color": -256},
                {"key": "shared", "name": "多父节点", "parent_key": "left", "note": ""},
            ],
            "jumps": [{"a": "left", "b": "right", "reason": "两份资料互补"}],
            "edges": [{"a": "right", "b": "shared", "relation": "parent"}],
        }
        self.content_path = self.root / "content.json"
        self.content_path.write_text(json.dumps(self.content, ensure_ascii=False), encoding="utf-8")

    def run_cli(self, *args):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = main(list(map(str, args)))
        return code, json.loads(output.getvalue())

    def test_creation_requires_no_source_file_and_preserves_content_graph(self):
        original = copy.deepcopy(self.content)
        # Constructor failure proves creation does not open an existing BRZ,
        # including a hidden seed bundled alongside the package.
        with patch.object(Archive, "__init__", side_effect=AssertionError("No BRZ may be read")):
            archive, manifest = create_archive(self.content)
        self.assertEqual(original, self.content)
        self.assertIsNone(archive.path)
        self.assertIsNone(archive.source_sha256)
        path = archive.write(self.root / "created.brz")
        reopened = Archive(path)
        self.assertEqual({"errors": [], "warnings": []}, reopened.validate())
        ids = manifest["node_ids"]
        self.assertNotEqual(ids["left"], ids["right"])
        self.assertEqual([ids["root"]], manifest["root_ids"])
        for node in self.content["nodes"]:
            self.assertEqual([node["note"]], [n["text"] for n in reopened.notes(ids[node["key"]])])
        actual_links = {(r["ThoughtIdA"], r["ThoughtIdB"], r["Relation"]) for r in reopened.rows("links")}
        self.assertEqual({(ids["root"], ids["left"], 1), (ids["root"], ids["right"], 1),
                          (ids["left"], ids["shared"], 1), (ids["right"], ids["shared"], 1),
                          (ids["left"], ids["right"], 3)}, actual_links)
        right = next(r for r in reopened.rows("thoughts") if r["Id"] == ids["right"])
        self.assertEqual(-256, right["BackgroundColor"])
        self.assertEqual("不同身份", right["Label"])
        for table in ("access", "brainusers", "syncpoints", "modificationlogs", "tombstones"):
            self.assertEqual([], reopened.rows(table), table)
        with zipfile.ZipFile(path) as result:
            self.assertTrue(Archive.TABLES.issubset(result.namelist()))

    def test_every_creation_uses_fresh_ids_and_correct_brain_specific_name_home_keys(self):
        all_ids = []
        for _ in range(2):
            archive, manifest = create_archive(self.content)
            ids = [archive.meta["BrainId"]]
            ids.extend(row["Id"] for table in Archive.TABLES for row in archive.rows(table))
            self.assertEqual(len(ids), len(set(ids)))
            self.assertEqual(4, uuid.UUID(archive.meta["BrainId"]).version)
            self.assertTrue(all(uuid.UUID(row["Id"]).version == 4
                                for table in ("thoughts", "links", "attachments") for row in archive.rows(table)))
            namespace = uuid.UUID(archive.meta["BrainId"])
            self.assertEqual({str(uuid.uuid5(namespace, "19")): self.content["title"],
                              str(uuid.uuid5(namespace, "20")): manifest["node_ids"]["root"]},
                             {row["Id"]: row["Value"] for row in archive.rows("settings")})
            self.assertTrue(all(row["BrainId"] == archive.meta["BrainId"]
                                for table in Archive.TABLES for row in archive.rows(table)))
            all_ids.append(set(ids))
        self.assertTrue(all_ids[0].isdisjoint(all_ids[1]))

    def test_cli_dry_run_does_not_write_and_write_supports_existing_edit_workflow(self):
        original = self.content_path.read_bytes()
        files = set(self.root.iterdir())
        code, dry = self.run_cli("create", "--content", self.content_path)
        self.assertEqual(0, code)
        self.assertEqual("dry-run", dry["mode"])
        self.assertNotIn("output", dry)
        self.assertEqual(files, set(self.root.iterdir()))
        target = self.root / "新的脑图🧠.brz"
        code, written = self.run_cli("create", "--content", self.content_path, "--output", target)
        self.assertEqual(0, code)
        self.assertEqual("write", written["mode"])
        self.assertFalse(written["application_import_verified"])
        archive = Archive(target)
        self.assertEqual(written["output_sha256"], archive.source_sha256)
        self.assertEqual(original, self.content_path.read_bytes())
        before = target.read_bytes()
        node_id = written["node_ids"]["left"]
        archive.apply_plan({"source_sha256": archive.source_sha256, "operations": [
            {"op": "update_thought", "id": node_id, "set": {"Label": "创建后继续维护"}},
        ]})
        second = archive.write(self.root / "edited.brz")
        self.assertEqual(before, target.read_bytes())
        diff = compare_archives(Archive(target), Archive(second))
        self.assertEqual({"added": 0, "removed": 0, "changed": 1}, diff["record_counts"])
        self.assertEqual(1, diff["members"]["changed"]["count"])

    def test_existing_output_or_content_path_is_never_overwritten(self):
        target = self.root / "existing.brz"
        target.write_bytes(b"existing output")
        for path in (target, self.content_path):
            with self.subTest(path=path.name):
                before = path.read_bytes()
                code, report = self.run_cli("create", "--content", self.content_path, "--output", path)
                self.assertEqual(1, code)
                self.assertIn("error", report)
                self.assertEqual(before, path.read_bytes())
                self.assertEqual([], list(self.root.glob(".brz-*.tmp")))

    def test_invalid_content_rejects_without_publishing(self):
        invalid = [None, {}, {"nodes": []}, {"nodes": "bad"},
                   {**self.content, "source_sha256": "not-needed"},
                   {**self.content, "schema_version": True}, {**self.content, "schema_version": 2},
                   {**self.content, "title": ""}, {**self.content, "jumps": {}},
                   {"nodes": [{"key": "a", "name": "x", "unknown": 1}]},
                   {"nodes": [{"key": "a", "name": "x", "note": None}]},
                   {"nodes": [{"key": "a", "name": "x", "background_color": True}]},
                   {"nodes": [{"key": "a", "name": "x", "parent_key": "missing"}]},
                   {"nodes": [{"key": "a", "name": "x", "parent_key": "a"}]},
                   {"nodes": [{"key": "a", "name": "x"}, {"key": "a", "name": "y"}]},
                   {"nodes": [{"key": "a", "name": "x", "parent_key": "b"},
                              {"key": "b", "name": "y", "parent_key": "a"}]}]
        for mutation in ("bad-endpoint", "duplicate-parent", "reverse-jump", "bad-relation"):
            content = copy.deepcopy(self.content)
            if mutation == "bad-endpoint":
                content["jumps"][0]["a"] = "missing"
            elif mutation == "duplicate-parent":
                content["edges"].append({"a": "root", "b": "left", "relation": "parent"})
            elif mutation == "reverse-jump":
                content["jumps"].append({"a": "right", "b": "left"})
            else:
                content["edges"][0]["relation"] = "type"
            invalid.append(content)
        target = self.root / "must-not-exist.brz"
        for index, content in enumerate(invalid):
            with self.subTest(case=index):
                self.content_path.write_text(json.dumps(content), encoding="utf-8")
                code, report = self.run_cli("create", "--content", self.content_path, "--output", target)
                self.assertEqual(1, code, report)
                self.assertIn("error", report)
                self.assertFalse(target.exists())
                self.assertEqual([], list(self.root.glob(".brz-*.tmp")))

    def test_duplicate_json_keys_and_nonfinite_numbers_are_rejected(self):
        for content in ('{"nodes":[],"nodes":[{"key":"a","name":"x"}]}',
                        '{"nodes":[{"key":"a","name":"x","name":"y"}]}',
                        '{"nodes":[{"key":"a","name":"x"}],"design":{"value":NaN}}'):
            with self.subTest(content=content):
                self.content_path.write_text(content, encoding="utf-8")
                target = self.root / "invalid.brz"
                code, report = self.run_cli("create", "--content", self.content_path, "--output", target)
                self.assertEqual(1, code, report)
                self.assertFalse(target.exists())

    def test_combined_operation_limit_is_checked_before_allocating_an_archive(self):
        content = {"nodes": [{"key": str(index), "name": "节点", "note": ""} for index in range(5001)]}
        with patch.object(Archive, "empty", side_effect=AssertionError("Should reject before allocating")):
            with self.assertRaisesRegex(ArchiveError, "10000 combined"):
                create_archive(content)

    def test_creation_still_uses_serialized_validation_before_publication(self):
        archive, _ = create_archive(self.content)
        destination = self.root / "damaged.brz"
        original_write = zipfile.ZipFile.writestr
        def corrupt(zip_file, name, data, *args, **kwargs):
            filename = name.filename if isinstance(name, zipfile.ZipInfo) else name
            return original_write(zip_file, name, b"invalid" if filename == "thoughts.json" else data, *args, **kwargs)
        with patch.object(zipfile.ZipFile, "writestr", corrupt):
            with self.assertRaises(ArchiveError):
                archive.write(destination)
        self.assertFalse(destination.exists())
        self.assertEqual([], list(self.root.glob(".brz-*.tmp")))

    def test_bundled_nist_content_creates_without_a_seed(self):
        content_path = Path(__file__).resolve().parents[1] / "examples/nist-ai-rmf/content.json"
        content = json.loads(content_path.read_text(encoding="utf-8"))
        archive, manifest = create_archive(content)
        result = Archive(archive.write(self.root / "nist.brz"))
        self.assertEqual([], result.validate()["errors"])
        self.assertEqual((38, 50, 38), tuple(len(result.rows(table)) for table in ("thoughts", "links", "attachments")))
        for node in content["nodes"]:
            self.assertEqual([node["note"]], [n["text"] for n in result.notes(manifest["node_ids"][node["key"]])])


if __name__ == "__main__":
    unittest.main()
