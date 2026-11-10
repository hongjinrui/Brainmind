"""Check the public CLI's dry-run/write boundary and strict plan parsing."""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
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
                "ThoughtIconInfo": "1::0:False:False:0:",
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
        code, comparison = self.run_cli("compare", self.source, target)
        self.assertEqual(0, code)
        self.assertEqual({"added": 1, "removed": 0, "changed": 1}, comparison["record_counts"])
        self.assertEqual(1, comparison["members"]["added"]["count"])
        change = comparison["records"]["thoughts.json"]["changed"]["items"][0]
        self.assertEqual("新名称", change["after"]["Name"])

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

    def test_real_cli_emits_utf8_after_write_under_legacy_stdio_encoding(self):
        target = self.root / "中文脑图🧠.brz"
        environment = {**os.environ, "PYTHONIOENCODING": "ascii", "PYTHONUTF8": "0"}
        command = [sys.executable, "-m", "brain_toolkit"]
        written = subprocess.run(command + ["apply", str(self.source), "--plan", str(self.plan),
                                           "--output", str(target)],
                                 env=environment, capture_output=True, check=False,
                                 cwd=Path(__file__).resolve().parents[1])
        self.assertEqual(0, written.returncode, written.stderr)
        report = json.loads(written.stdout.decode("utf-8"))
        self.assertEqual(str(target), report["output"])
        inspected = subprocess.run(command + ["inspect", str(target), "--id", self.node, "--notes"],
                                   env=environment, capture_output=True, check=False,
                                   cwd=Path(__file__).resolve().parents[1])
        self.assertEqual(0, inspected.returncode, inspected.stderr)
        result = json.loads(inspected.stdout.decode("utf-8"))
        self.assertEqual("新名称", result["node"]["thought"]["Name"])
        self.assertEqual("中文笔记", result["node"]["notes"][0]["text"])


if __name__ == "__main__":
    unittest.main()
