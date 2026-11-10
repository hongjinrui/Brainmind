"""Keep the public source-free example compatible with real native exports."""
from collections import Counter
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import uuid
import zipfile

from brain_toolkit import Archive
from brain_toolkit.diff import compare_archives


ROOT = Path(__file__).resolve().parents[1]
EXAMPLE = ROOT / "examples/nist-ai-rmf"
NATIVE = ROOT / "tests/fixtures/thebrain14-native-nist.brz"


class NistExampleTests(unittest.TestCase):
    def setUp(self):
        self.content = json.loads((EXAMPLE / "content.json").read_text(encoding="utf-8"))

    def test_published_brain_and_native_export_preserve_the_complete_content(self):
        nodes = self.content["nodes"]
        by_key = {node["key"]: node for node in nodes}
        root = next(node for node in nodes if node["parent_key"] is None)
        expected_parents = Counter((by_key[node["parent_key"]]["name"], node["name"])
                                   for node in nodes if node["parent_key"] is not None)
        expected_jumps = Counter(tuple(sorted((by_key[jump["a"]]["name"], by_key[jump["b"]]["name"])))
                                 for jump in self.content["jumps"])
        self.assertEqual((38, 37, 13), (len(nodes), sum(expected_parents.values()), sum(expected_jumps.values())))
        for path in (EXAMPLE / "nist-from-content.brz", NATIVE):
            with self.subTest(archive=path.name):
                archive = Archive(path)
                self.assertEqual({"errors": [], "warnings": []}, archive.validate())
                thoughts = archive.rows("thoughts")
                self.assertEqual(38, len(thoughts))
                self.assertTrue(all(row["Kind"] == 1 for row in thoughts))
                by_name = {row["Name"]: row for row in thoughts}
                self.assertEqual({node["name"] for node in nodes}, set(by_name))
                by_id = {row["Id"]: row["Name"] for row in thoughts}
                parents, jumps = Counter(), Counter()
                for link in archive.rows("links"):
                    self.assertEqual((1, 1), (link["Kind"], link["Meaning"]))
                    a, b = by_id[link["ThoughtIdA"]], by_id[link["ThoughtIdB"]]
                    if link["Relation"] == 1:
                        parents[(a, b)] += 1
                    elif link["Relation"] == 3:
                        jumps[tuple(sorted((a, b)))] += 1
                    else:
                        self.fail("Unexpected relationship kind")
                self.assertEqual(expected_parents, parents)
                self.assertEqual(expected_jumps, jumps)
                self.assertEqual(38, len(archive.rows("attachments")))
                for node in nodes:
                    notes = archive.notes(by_name[node["name"]]["Id"])
                    self.assertEqual([node["note"]], [note["text"] for note in notes])
                    self.assertFalse(notes[0]["truncated"])
                namespace = uuid.UUID(archive.meta["BrainId"])
                settings = {row["Id"]: row for row in archive.rows("settings")}
                self.assertEqual(self.content["title"], settings[str(uuid.uuid5(namespace, "19"))]["Value"])
                self.assertEqual(by_name[root["name"]]["Id"], settings[str(uuid.uuid5(namespace, "20"))]["Value"])

    def test_native_export_remains_editable_and_preserves_unrelated_bytes(self):
        original = NATIVE.read_bytes()
        provenance = json.loads((NATIVE.parent / "provenance.json").read_text(encoding="utf-8"))
        self.assertEqual(provenance[NATIVE.name]["sha256"], hashlib.sha256(original).hexdigest())
        archive = Archive(NATIVE)
        root = next(node for node in self.content["nodes"] if node["parent_key"] is None)
        target = next(row for row in archive.rows("thoughts") if row["Name"] == root["name"])
        old_note = archive.notes(target["Id"])[0]["text"]
        new_note = old_note + "\n\n## 阅读记录\n\n原生再导出后继续维护。\n"
        archive.apply_plan({"source_sha256": archive.source_sha256, "operations": [
            {"op": "update_thought", "id": target["Id"], "set": {"Label": "原生导出回归"}},
            {"op": "set_note", "id": target["Id"], "text": new_note},
        ]})
        with tempfile.TemporaryDirectory() as directory:
            result_path = archive.write(Path(directory) / "edited.brz")
            result = Archive(result_path)
            self.assertEqual({"errors": [], "warnings": []}, result.validate())
            self.assertEqual(new_note, result.notes(target["Id"])[0]["text"])
            edited_target = next(row for row in result.rows("thoughts") if row["Id"] == target["Id"])
            self.assertEqual("原生导出回归", edited_target["Label"])
            changed = {"thoughts.json", "attachments.json", target["Id"] + "/Notes.md"}
            with zipfile.ZipFile(NATIVE) as before, zipfile.ZipFile(result_path) as after:
                self.assertEqual(set(before.namelist()), set(after.namelist()))
                for name in before.namelist():
                    if name not in changed:
                        self.assertEqual(before.read(name), after.read(name), name)
            diff = compare_archives(Archive(NATIVE), result)
            self.assertEqual({"added": 0, "removed": 0, "changed": 2}, diff["record_counts"])
            self.assertEqual(changed, {item["path"] for item in diff["members"]["changed"]["items"]})
        self.assertEqual(original, NATIVE.read_bytes())


if __name__ == "__main__":
    unittest.main()
