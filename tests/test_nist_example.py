"""Exercise the published PDF-to-brain example without native/user fixtures."""
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
import zipfile

from brain_toolkit import Archive
import test_archive as fixtures
from test_archive import CHILD


EXAMPLE = Path(__file__).resolve().parents[1] / "examples/nist-ai-rmf"
SCRIPT = EXAMPLE / "build_plan.py"
spec = importlib.util.spec_from_file_location("nist_example_builder", SCRIPT)
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class NistExampleTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.ArchiveTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.source = self.fixture.source
        self.original = self.source.read_bytes()
        self.content = json.loads((EXAMPLE / "content.json").read_text(encoding="utf-8"))

    def generate(self, output):
        # No repository cwd/PYTHONPATH or UTF-8 console assumptions: a user may
        # invoke the script by absolute path from an unrelated Windows folder.
        environment = {**os.environ, "PYTHONIOENCODING": "ascii", "PYTHONUTF8": "0"}
        environment.pop("PYTHONPATH", None)
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--source", str(self.source),
             "--root-id", CHILD, "--output", str(output)],
            cwd=self.fixture.directory, env=environment, capture_output=True, check=False,
        )

    def test_published_content_generates_a_complete_editable_brain_from_any_cwd(self):
        plan_path = self.fixture.directory / "计划🧠" / "nist.plan.json"
        completed = self.generate(plan_path)
        self.assertEqual(0, completed.returncode, completed.stderr.decode("utf-8"))
        summary = json.loads(completed.stdout.decode("utf-8"))
        self.assertEqual(1, summary["preserved_other_ordinary_thoughts"])
        plan = json.loads(plan_path.read_text(encoding="utf-8"))
        self.assertEqual(hashlib.sha256(self.original).hexdigest(), plan["source_sha256"])
        self.assertEqual(self.original, self.source.read_bytes())

        archive = Archive(self.source)
        archive.apply_plan(plan)
        output = self.fixture.directory / "nist.brz"
        archive.write(output)
        result = Archive(output)
        self.assertEqual([], result.validate()["errors"])
        self.assertEqual(self.original, self.source.read_bytes())

        # Assert the public content rather than the generator's operation order.
        self.assertEqual(38, len(self.content["nodes"]))
        rows = result.rows("thoughts")
        ids = {}
        for node in self.content["nodes"]:
            matches = [row for row in rows if row["Name"] == node["name"]]
            self.assertEqual(1, len(matches), node["key"])
            thought = matches[0]
            self.assertEqual(1, thought["Kind"])
            ids[node["key"]] = thought["Id"]
            self.assertEqual([node["note"]], [note["text"] for note in result.notes(thought["Id"])])
        root = next(node["key"] for node in self.content["nodes"] if node["parent_key"] is None)
        self.assertEqual(CHILD, ids[root])
        expected_parents = {(ids[node["parent_key"]], ids[node["key"]])
                            for node in self.content["nodes"] if node["parent_key"] is not None}
        expected_jumps = {frozenset((ids[jump["a"]], ids[jump["b"]]))
                          for jump in self.content["jumps"]}
        original = Archive(self.source)
        old_links = {row["Id"]: row for row in original.rows("links")}
        new_links = [row for row in result.rows("links") if row["Id"] not in old_links]
        self.assertEqual(37, len(expected_parents))
        self.assertEqual(13, len(expected_jumps))
        self.assertEqual(50, len(new_links))
        self.assertTrue(all(row["Kind"] == 1 and row["Meaning"] == 1 for row in new_links))
        self.assertEqual(expected_parents, {(row["ThoughtIdA"], row["ThoughtIdB"])
                                           for row in new_links if row["Relation"] == 1})
        self.assertEqual(expected_jumps, {frozenset((row["ThoughtIdA"], row["ThoughtIdB"]))
                                         for row in new_links if row["Relation"] == 3})

        for row in original.rows("thoughts"):
            if row["Id"] != CHILD:
                self.assertIn(row, rows)
        for row in original.rows("links"):
            self.assertIn(row, result.rows("links"))
        for row in original.rows("attachments"):
            self.assertIn(row, result.rows("attachments"))
        with zipfile.ZipFile(self.source) as before, zipfile.ZipFile(output) as after:
            for name in before.namelist():
                if name not in ("meta.json", "thoughts.json", "links.json", "attachments.json"):
                    self.assertEqual(before.read(name), after.read(name), name)

    def test_generator_refuses_existing_plan_and_source_paths(self):
        plan_path = self.fixture.directory / "existing.plan.json"
        plan_path.write_bytes(b"preserve the existing plan exactly\n")
        for target in (plan_path, self.source):
            with self.subTest(target=target.name):
                before = target.read_bytes()
                completed = self.generate(target)
                self.assertEqual(1, completed.returncode)
                self.assertIn("error", json.loads(completed.stderr.decode("utf-8")))
                self.assertEqual(before, target.read_bytes())
                self.assertEqual(self.original, self.source.read_bytes())
                self.assertEqual([], list(self.fixture.directory.rglob(".nist-plan-*.tmp")))

    def test_invalid_content_graph_is_rejected_before_the_source_is_mutated(self):
        for defect in ("parent-cycle", "missing-parent", "missing-jump-endpoint", "reverse-duplicate-jump"):
            with self.subTest(defect=defect):
                content = copy.deepcopy(self.content)
                if defect == "parent-cycle":
                    content["nodes"][1]["parent_key"] = content["nodes"][1]["key"]
                elif defect == "missing-parent":
                    content["nodes"][1]["parent_key"] = "nonexistent-key"
                elif defect == "missing-jump-endpoint":
                    content["jumps"][0]["b"] = "nonexistent-key"
                else:
                    edge = content["jumps"][0]
                    content["jumps"].append({**edge, "a": edge["b"], "b": edge["a"]})
                archive = Archive(self.source)
                before = archive.rows("thoughts"), archive.rows("links"), archive.rows("attachments")
                with self.assertRaises(ValueError):
                    builder.build_plan(archive, CHILD, content)
                self.assertEqual(before, (archive.rows("thoughts"), archive.rows("links"), archive.rows("attachments")))
                self.assertEqual(self.original, self.source.read_bytes())


if __name__ == "__main__":
    unittest.main()
