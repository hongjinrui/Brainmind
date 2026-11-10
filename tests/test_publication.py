"""Publishing a BRZ requires a valid read-back, not just valid memory state."""
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from unittest.mock import patch

from brain_toolkit import Archive, ArchiveError


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source.brz"
        with zipfile.ZipFile(self.source, "w") as output:
            output.writestr("meta.json", json.dumps({
                "BrainId": "00000000-0000-4000-8000-000000000001", "ExchangeFormatVersion": 5,
            }))
            for table in ("thoughts", "links", "attachments"):
                output.writestr(table + ".json", b"\xef\xbb\xbf")
        self.archive = Archive(self.source)

    def test_invalid_serialized_json_is_never_published(self):
        members = self.archive._render_members()
        members["thoughts.json"] = b"invalid JSON"
        target = self.root / "output.brz"
        with patch.object(self.archive, "_render_members", return_value=members):
            with self.assertRaises(ArchiveError):
                self.archive.write(target)
        self.assertFalse(target.exists())
        self.assertEqual([], list(self.root.glob(".brz-*.tmp")))

    def test_oversize_generated_member_is_not_published(self):
        members = self.archive._render_members()
        members["opaque.bin"] = b"a" * 1025
        target = self.root / "output.brz"
        with patch.object(self.archive, "MAX_MEMBER_BYTES", 1024), patch.object(self.archive, "_render_members", return_value=members):
            with self.assertRaisesRegex(ArchiveError, "member exceeds"):
                self.archive.write(target)
        self.assertFalse(target.exists())

    def test_unknown_version_cannot_bypass_plan_guard_via_write(self):
        for version in (6, 5.0, "5"):
            with self.subTest(version=version):
                self.archive.meta["ExchangeFormatVersion"] = version
                with self.assertRaisesRegex(ArchiveError, "ExchangeFormatVersion=5"):
                    self.archive.write(self.root / "output.brz")


if __name__ == "__main__":
    unittest.main()
