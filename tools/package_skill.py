"""Build a reproducible, self-contained brz-edit skill ZIP from one source tree."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import posixpath
import re
import sys
import tempfile
from urllib.parse import quote
import zipfile


REPO_ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = "https://github.com/hongjinrui/Brainmind/blob/main/"
TEXT_SUFFIXES = {".py", ".md", ".json", ".yaml", ".yml", ".txt"}


def read_source(path):
    if path.is_symlink():
        raise ValueError(f"Symlinks cannot be packaged: {path.name}")
    data = path.read_bytes()
    if path.suffix.lower() in TEXT_SUFFIXES:
        data = data.decode("utf-8-sig").replace("\r\n", "\n").encode("utf-8")
    return data


def reference_links(data, source_path, included):
    """Keep included reference links local; send repository-only links upstream."""
    def replace(match):
        target = match.group(1)
        if target.startswith(("https://", "http://", "#", "mailto:")):
            return match.group(0)
        path, separator, anchor = target.partition("#")
        resolved = posixpath.normpath(posixpath.join(posixpath.dirname(source_path), path))
        if resolved in included:
            return match.group(0)
        return "](" + UPSTREAM + quote(resolved, safe="/") + (separator + anchor if separator else "") + ")"
    return re.sub(r"\]\(([^)\s]+)\)", replace, data.decode("utf-8")).encode("utf-8")


def bundle_bytes(repo=REPO_ROOT):
    repo = Path(repo)
    skill = repo / "skills" / "brz-edit"
    required = [skill / "SKILL.md", skill / "scripts" / "brz.py",
                skill / "references" / "example.content.json",
                repo / "brain_toolkit" / "__main__.py", repo / "README.md",
                repo / "docs" / "FORMAT.md"]
    for path in required:
        if not path.is_file():
            raise ValueError(f"Required package source is missing: {path.relative_to(repo)}")
    members = {}
    for path in skill.rglob("*"):
        if "__pycache__" in path.parts or path.suffix in {".pyc", ".pyo"}:
            continue
        if path.is_symlink():
            raise ValueError(f"Symlinks cannot be packaged: {path.relative_to(repo)}")
        if path.is_file():
            members["brz-edit/" + path.relative_to(skill).as_posix()] = read_source(path)
    # Runtime copies are build artifacts only; core code is maintained once.
    for path in (repo / "brain_toolkit").rglob("*.py"):
        target = "brz-edit/scripts/brain_toolkit/" + path.relative_to(repo / "brain_toolkit").as_posix()
        if target in members:
            raise ValueError("Do not vendor a second runtime in the source skill")
        members[target] = read_source(path)
    reference_paths = [repo / "README.md"] + sorted(
        path for path in (repo / "docs").rglob("*")
        if path.is_file() and path.suffix in {".md", ".json"})
    included = {path.relative_to(repo).as_posix() for path in reference_paths}
    for path in reference_paths:
        source_path = path.relative_to(repo).as_posix()
        data = read_source(path)
        if path.suffix == ".md":
            data = reference_links(data, source_path, included)
        target = "brz-edit/references/" + source_path
        if target in members:
            raise ValueError(f"Duplicate package destination: {target}")
        members[target] = data
    manifest = {
        "format_version": 1,
        "source": "https://github.com/hongjinrui/Brainmind",
        "files": {name.removeprefix("brz-edit/"): hashlib.sha256(data).hexdigest()
                  for name, data in sorted(members.items())},
    }
    members["brz-edit/manifest.json"] = (
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, data in sorted(members.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_STORED
            archive.writestr(info, data)
    return buffer.getvalue()


def package_skill(output, repo=REPO_ROOT):
    output = Path(output).absolute()
    if os.path.lexists(output):
        raise FileExistsError(f"Output already exists: {output}")
    payload = bundle_bytes(repo)
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".brz-skill-", suffix=".tmp", dir=output.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, output)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return {"output": str(output), "bytes": len(payload),
            "sha256": hashlib.sha256(payload).hexdigest()}


def check_skill(path, repo=REPO_ROOT):
    """Read only: reject a distributed ZIP that differs from current sources."""
    actual = Path(path).read_bytes()
    expected = bundle_bytes(repo)
    if actual != expected:
        raise ValueError("Skill ZIP does not match current sources; rebuild to a new path, "
                         "then replace the release ZIP and rerun --check")
    return {"checked": str(path), "matches_source": True, "bytes": len(actual),
            "sha256": hashlib.sha256(actual).hexdigest()}


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--output", type=Path, help="Build a new ZIP path; refuses existing files")
    action.add_argument("--check", type=Path, help="Read-only check that a ZIP exactly matches current source files")
    args = parser.parse_args(argv)
    try:
        result = check_skill(args.check) if args.check is not None else package_skill(args.output)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError) as exc:
        print(json.dumps({"error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
