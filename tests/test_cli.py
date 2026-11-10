"""Check the public CLI's dry-run/write boundary and strict plan parsing."""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from brain_toolkit.__main__ import main
from brain_toolkit import Archive


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "input.brz"
        self.node = "00000000-0000-4000-8000-000000000002"
        brain = "00000000-0000-4000-8000-000000000001"
        with zipfile.ZipFile(self.source, "w") as output:
            output.writestr("meta.json", json.dumps({"BrainId": brain, "ExchangeFormatVersion": 5}))
            output.writestr("thoughts.json", json.dumps({
                "Id": self.node, "BrainId": brain, "Name": "原名称", "Kind": 1, "TagIds": [],
            }))
            for name in ("links", "attachments"):
                output.writestr(name + ".json", b"\xef\xbb\xbf")
        self.sha = hashlib.sha256(self.source.read_bytes()).hexdigest()
        self.plan = self.root / "plan.json"
        self.plan.write_text(json.dumps({"source_sha256": self.sha, "operations": [
            {"op": "update_thought", "id": self.node, "set": {"Name": "新名称"}},
            {"op": "set_note", "id": self.node, "text": "中文笔记"},
        ]}), encoding="utf-8")

    def run_cli(self, *arguments):
        stream = io.StringIO()
        with contextlib.redirect_stdout(stream):
            code = main(list(map(str, arguments)))
        return code, json.loads(stream.getvalue())

    def test_dry_run_then_write_and_read_note(self):
        target = self.root / "result.brz"
        code, result = self.run_cli("apply", self.source, "--plan", self.plan)
        self.assertEqual(0, code)
        self.assertEqual("dry-run", result["mode"])
        self.assertFalse(target.exists())
        self.assertEqual(self.sha, hashlib.sha256(self.source.read_bytes()).hexdigest())
        code, result = self.run_cli("apply", self.source, "--plan", self.plan, "--output", target)
        self.assertEqual(0, code)
        self.assertFalse(result["application_import_verified"])
        self.assertEqual([], Archive(target).validate()["errors"])
        code, result = self.run_cli("inspect", target, "--id", self.node, "--notes", "--note-limit", 2)
        self.assertEqual(0, code)
        note = result["node"]["notes"][0]
        self.assertEqual("中文", note["text"])
        self.assertTrue(note["truncated"])
        self.assertEqual(4, note["characters"])

    def test_duplicate_plan_keys_and_nonfinite_numbers_are_rejected(self):
        target = self.root / "result.brz"
        for text in (
            '{"source_sha256":"' + self.sha + '","operations":[],"operations":[]}',
            '{"source_sha256":"' + self.sha + '","operations":[],"other":NaN}',
        ):
            with self.subTest(text=text):
                self.plan.write_text(text, encoding="utf-8")
                code, result = self.run_cli("apply", self.source, "--plan", self.plan, "--output", target)
                self.assertEqual(1, code)
                self.assertIn("error", result)
                self.assertFalse(target.exists())


if __name__ == "__main__":
    unittest.main()
