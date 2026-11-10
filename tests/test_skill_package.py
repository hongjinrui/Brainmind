"""Verify the distributed skill works without a checkout, source BRZ or pip."""
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

from tools.package_skill import bundle_bytes, package_skill


class SkillPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_package_is_reproducible_and_manifest_matches_every_payload(self):
        first, second = bundle_bytes(), bundle_bytes()
        self.assertEqual(first, second)
        with zipfile.ZipFile(io.BytesIO(first)) as archive:
            names = archive.namelist()
            self.assertEqual(sorted(names), names)
            self.assertEqual(len(names), len(set(names)))
            self.assertTrue(all(name.startswith("brz-edit/") for name in names))
            self.assertIn("brz-edit/scripts/brain_toolkit/create.py", names)
            self.assertIn("brz-edit/references/example.content.json", names)
            self.assertIn("brz-edit/references/docs/FORMAT.md", names)
            self.assertFalse(any("__pycache__" in name or name.endswith((".brz", ".exe", ".pyc")) for name in names))
            manifest = json.loads(archive.read("brz-edit/manifest.json"))
            self.assertEqual({"brz-edit/" + name for name in manifest["files"]}, set(names) - {"brz-edit/manifest.json"})
            for name, expected_hash in manifest["files"].items():
                self.assertEqual(expected_hash, hashlib.sha256(archive.read("brz-edit/" + name)).hexdigest())
            for info in archive.infolist():
                self.assertEqual((1980, 1, 1, 0, 0, 0), info.date_time)
                self.assertEqual(zipfile.ZIP_STORED, info.compress_type)

    def test_unpacked_skill_creates_then_edits_from_an_unrelated_directory(self):
        install = self.root / "安装 技能"
        with zipfile.ZipFile(io.BytesIO(bundle_bytes())) as archive:
            archive.extractall(install)
        skill = install / "brz-edit"
        cwd = self.root / "separate working directory"
        cwd.mkdir()
        launcher = skill / "scripts" / "brz.py"
        content = skill / "references" / "example.content.json"

        def cli(*args, expected=0):
            # Isolated mode removes cwd and PYTHONPATH from import discovery.
            result = subprocess.run([sys.executable, "-I", "-X", "utf8", str(launcher), *map(str, args)],
                                    cwd=cwd, capture_output=True, timeout=30)
            self.assertEqual(expected, result.returncode, result.stderr.decode("utf-8") + result.stdout.decode("utf-8"))
            return json.loads(result.stdout)

        dry = cli("create", "--content", content)
        self.assertEqual("dry-run", dry["mode"])
        self.assertEqual([], list(cwd.iterdir()))
        result = cli("create", "--content", content, "--output", "中文 脑图.brz")
        output = cwd / "中文 脑图.brz"
        original = output.read_bytes()
        self.assertEqual(hashlib.sha256(original).hexdigest(), result["output_sha256"])
        self.assertEqual([], cli("validate", output)["errors"])
        inspected = cli("inspect", output, "--list")
        self.assertEqual(3, len(inspected["thoughts"]))
        self.assertEqual(3, inspected["tables"]["links.json"])
        root_id = next(row["Id"] for row in inspected["thoughts"] if row["Name"] == "资料阅读入门")
        details = cli("inspect", output, "--id", root_id, "--notes")
        self.assertIn("原创演示内容", details["node"]["notes"][0]["text"])
        plan = cwd / "edit.json"
        plan.write_text(json.dumps({"source_sha256": result["output_sha256"], "operations": [
            {"op": "update_thought", "id": root_id, "set": {"Label": "独立包继续编辑"}},
        ]}, ensure_ascii=False), encoding="utf-8")
        edited = cwd / "edited.brz"
        cli("apply", output, "--plan", plan, "--output", edited)
        comparison = cli("compare", output, edited)
        self.assertEqual({"added": 0, "removed": 0, "changed": 1}, comparison["record_counts"])
        self.assertEqual(original, output.read_bytes())
        cli("create", "--content", content, "--output", output, expected=1)
        self.assertEqual(original, output.read_bytes())

    def test_publish_never_overwrites_and_cleans_failed_temporary_file(self):
        existing = self.root / "existing.zip"
        existing.write_bytes(b"keep this output")
        with self.assertRaises(FileExistsError):
            package_skill(existing)
        self.assertEqual(b"keep this output", existing.read_bytes())
        target = self.root / "failed.zip"
        with mock.patch("tools.package_skill.os.link", side_effect=OSError("publication failed")):
            with self.assertRaisesRegex(OSError, "publication failed"):
                package_skill(target)
        self.assertFalse(target.exists())
        self.assertEqual([], list(self.root.glob(".brz-skill-*.tmp")))

    def test_check_cli_detects_a_stale_release_without_writing(self):
        release = self.root / "brz-edit.zip"
        package_skill(release)
        helper = Path(__file__).resolve().parents[1] / "tools" / "package_skill.py"
        command = [sys.executable, "-I", str(helper), "--check", str(release)]
        original = release.read_bytes()
        before_files = set(self.root.iterdir())
        good = subprocess.run(command, cwd=self.root, capture_output=True, timeout=30)
        self.assertEqual(0, good.returncode, good.stderr.decode("utf-8"))
        self.assertTrue(json.loads(good.stdout)["matches_source"])
        self.assertEqual(original, release.read_bytes())
        # A ZIP may still be readable after trailing bytes change, but exact
        # source consistency must reject it rather than silently accepting it.
        stale = original + b"stale release"
        release.write_bytes(stale)
        bad = subprocess.run(command, cwd=self.root, capture_output=True, timeout=30)
        self.assertEqual(1, bad.returncode)
        self.assertIn("does not match current sources", json.loads(bad.stderr)["error"])
        self.assertEqual(stale, release.read_bytes())
        self.assertEqual(before_files, set(self.root.iterdir()))


if __name__ == "__main__":
    unittest.main()
