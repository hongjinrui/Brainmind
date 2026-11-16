"""Export an allowlisted source repository and a minimal Brainmind launcher.

This script never launches TheBrain, calls a model, decrypts credentials,
downloads dependencies, or copies user settings, tests, examples or research.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "0.5.0"
TARGET = "14.0.102.0"
RUNTIME = f"build/{VERSION}/Brainmind.Runtime.dll"
BUILD_MANIFEST = f"build/{VERSION}/manifest.json"
LAUNCHER_FILES = (
    "README.md", "LICENSE", "launch-brainmind.cmd",
    "tools/launch-brainmind.ps1", "config/supported-build.json",
    RUNTIME, BUILD_MANIFEST,
)
SOURCE_FILES = (
    ".gitignore", ".gitattributes", "tools/brainmind.py", "tools/package_release.py",
)
PINNED_NAMES = {
    "TheBrain.exe", "TheBrain.Windows.App.dll", "TheBrainDesktop.dll",
    "TheBrain.Client.dll", "TheBrainCore.dll", "Microsoft.Web.WebView2.Core.dll",
    "KezaCore.dll", "Xwt.dll", "UglyToad.PdfPig.Core.dll", "UglyToad.PdfPig.dll",
    "UglyToad.PdfPig.DocumentLayoutAnalysis.dll", "UglyToad.PdfPig.Fonts.dll",
    "UglyToad.PdfPig.Package.dll", "UglyToad.PdfPig.Tokenization.dll", "UglyToad.PdfPig.Tokens.dll",
}
HASH = re.compile(r"[a-fA-F0-9]{64}")
ABSOLUTE_DRIVE = re.compile(r"(?i)\b[a-z]:(?:\\{1,2}|/)")
LIVE_KEY = re.compile(r"\bsk-[A-Za-z0-9_-]{20,}\b")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_relative(value):
    path = PurePosixPath(value)
    if not value or "\\" in value or path.is_absolute() or ".." in path.parts or ":" in value:
        raise RuntimeError("Invalid package-relative path.")
    return path


def is_alias(path):
    return path.is_symlink() or bool(getattr(path.lstat(), "st_file_attributes", 0)
                                    & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024))


def project_file(relative):
    safe_relative(relative)
    path = ROOT / relative
    if not path.is_file() or is_alias(path) or not path.resolve().is_relative_to(ROOT.resolve()):
        raise RuntimeError("Missing or unsafe project input: " + relative)
    current = path.parent
    while current != ROOT:
        if is_alias(current):
            raise RuntimeError("Aliased project directory: " + relative)
        current = current.parent
    return path


def load_json(relative):
    return json.loads(project_file(relative).read_text(encoding="utf-8-sig"))


def verify_build():
    manifest = load_json(BUILD_MANIFEST)
    fields = {"format", "runtime_version", "target_version", "runtime_file",
              "runtime_sha256", "sources", "supported_build_sha256"}
    if set(manifest) != fields or manifest["format"] != 1:
        raise RuntimeError("Unexpected build manifest. Rebuild before packaging.")
    if (manifest["runtime_version"] != VERSION or manifest["target_version"] != TARGET
            or manifest["runtime_file"] != "Brainmind.Runtime.dll"):
        raise RuntimeError("Unsupported runtime build.")
    source_names = {p.relative_to(ROOT).as_posix() for p in (ROOT / "src/Brainmind.Runtime").glob("*.cs")}
    if not source_names or set(manifest["sources"]) != source_names:
        raise RuntimeError("Runtime sources differ from the build. Rebuild before packaging.")
    for name, expected in manifest["sources"].items():
        if digest(project_file(name).read_bytes()) != expected:
            raise RuntimeError("Source changed after build: " + name)
    if digest(project_file(RUNTIME).read_bytes()) != manifest["runtime_sha256"]:
        raise RuntimeError("Runtime DLL differs from its build manifest.")
    if digest(project_file("config/supported-build.json").read_bytes()) != manifest["supported_build_sha256"]:
        raise RuntimeError("Supported-build configuration changed after build.")
    supported = load_json("config/supported-build.json")
    if set(supported) != {"target_version", "files"} or supported["target_version"] != TARGET:
        raise RuntimeError("Unexpected supported-build configuration.")
    rows = supported["files"]
    if len(rows) != len(PINNED_NAMES) or {row.get("Name") for row in rows} != PINNED_NAMES:
        raise RuntimeError("Supported-build dependency list is incomplete.")
    if any(set(row) != {"Name", "SHA256"} or not HASH.fullmatch(row["SHA256"]) for row in rows):
        raise RuntimeError("Supported-build entries must contain only filenames and SHA-256 hashes.")
    return manifest


def private_config_fingerprints():
    # Compare in memory only. Never decrypt DPAPI, emit values or copy settings.
    paths = []
    if os.environ.get("LOCALAPPDATA"):
        paths.append(Path(os.environ["LOCALAPPDATA"]) / "Brainmind/model.json")
    if os.environ.get("BRAINMIND_CONFIG_PATH"):
        paths.append(Path(os.environ["BRAINMIND_CONFIG_PATH"]))
    values = []
    checked = 0
    for path in dict.fromkeys(paths):
        if not path.exists():
            continue
        try:
            raw = path.read_bytes()
            config = json.loads(raw.decode("utf-8-sig"))
            checked += 1
            values.append(raw)
            for field in ("base_url", "model", "api_key", "api_key_dpapi"):
                value = config.get(field)
                if not isinstance(value, str) or not value:
                    continue
                # A public default may also occur in ModelConfig.cs.
                if field == "base_url" and value.rstrip("/") == "https://api.openai.com/v1":
                    continue
                variants = {value, json.dumps(value, ensure_ascii=False)[1:-1],
                            json.dumps(value, ensure_ascii=True)[1:-1]}
                for variant in variants:
                    values.extend([variant.encode("utf-8"), variant.encode("utf-16-le")])
                if field == "base_url":
                    from urllib.parse import urlsplit
                    host = urlsplit(value).hostname
                    if host and host not in ("localhost", "127.0.0.1", "::1"):
                        values.extend([host.encode("utf-8"), host.encode("utf-16-le")])
        except (OSError, ValueError, TypeError):
            raise RuntimeError("Local model settings could not be checked; no settings content is reported.") from None
    return values, checked


def verify_privacy(inputs):
    private_values, checked = private_config_fingerprints()
    for name, data in inputs.items():
        for root in (str(ROOT), str(Path.home())):
            for value in (root, root.replace("\\", "/"), root.replace("\\", "\\\\")):
                if any(value.encode(encoding) in data for encoding in ("utf-8", "utf-16-le")):
                    raise RuntimeError("Personal path detected in public input: " + name)
        if any(value and value in data for value in private_values):
            raise RuntimeError("Local model information detected in public input: " + name)
        if name != RUNTIME:
            try:
                text = data.decode("utf-8-sig")
            except UnicodeDecodeError:
                raise RuntimeError("Public text is not UTF-8: " + name) from None
            if ABSOLUTE_DRIVE.search(text) or LIVE_KEY.search(text):
                raise RuntimeError("Potential private path or credential detected: " + name)
    return {"private_model_values_absent": True, "personal_paths_absent": True,
            "credential_pattern_absent": True, "local_configs_checked": checked,
            "dpapi_decrypted": False}


def add_checksums(inputs):
    result = dict(inputs)
    result["SHA256SUMS"] = "".join(digest(data) + "  " + name + "\n"
                                    for name, data in sorted(inputs.items())).encode("utf-8")
    return result


def output_directory(relative):
    safe_relative(relative)
    directory = ROOT / relative
    current = ROOT
    for part in PurePosixPath(relative).parts:
        current = current / part
        if current.exists() and (not current.is_dir() or is_alias(current)):
            raise RuntimeError("Output must be a regular project directory: " + relative)
        current.mkdir(exist_ok=True)
    if not directory.resolve().is_relative_to(ROOT.resolve()):
        raise RuntimeError("Output is outside the project.")
    return directory


def write_file(path, data):
    if path.exists() and (not path.is_file() or is_alias(path)):
        raise RuntimeError("Output is not a regular file: " + path.name)
    temporary = path.with_name(path.name + ".tmp")
    if temporary.exists() and (not temporary.is_file() or is_alias(temporary)):
        raise RuntimeError("Temporary output is not a regular file.")
    temporary.write_bytes(data)
    os.replace(temporary, path)


def export_repository(inputs):
    directory = output_directory(f"publish/Brainmind-{VERSION}")
    for path in directory.rglob("*"):
        if is_alias(path):
            raise RuntimeError("Generated repository contains an aliased path.")
        if path.is_file() and path.relative_to(directory).as_posix() not in inputs:
            raise RuntimeError("Generated repository contains extra files. Preserve them elsewhere before regenerating.")
    for name, data in inputs.items():
        path = directory / safe_relative(name)
        output_directory(path.parent.relative_to(ROOT).as_posix())
        write_file(path, data)
    actual = {p.relative_to(directory).as_posix(): p.read_bytes() for p in directory.rglob("*") if p.is_file()}
    if actual != inputs:
        raise RuntimeError("Generated repository differs from its allowlisted snapshot.")
    return directory


def write_archive(path, prefix, inputs):
    if path.exists() and (not path.is_file() or is_alias(path)):
        raise RuntimeError("Archive output is not a regular file.")
    temporary = path.with_suffix(path.suffix + ".tmp")
    if temporary.exists() and (not temporary.is_file() or is_alias(temporary)):
        raise RuntimeError("Temporary archive is not a regular file.")
    entries = {prefix + name: data for name, data in inputs.items()}
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(entries.items()):
            safe_relative(name)
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
    with zipfile.ZipFile(temporary) as archive:
        if archive.testzip() is not None or archive.namelist() != sorted(entries):
            raise RuntimeError("Archive structure or CRC verification failed.")
        actual = {name: archive.read(name) for name in archive.namelist()}
        if actual != entries:
            raise RuntimeError("Archive bytes differ from the allowlisted snapshot.")
    os.replace(temporary, path)
    return {"sha256": digest(path.read_bytes()), "bytes": path.stat().st_size,
            "root": prefix.rstrip("/"), "members": [
                {"path": name, "bytes": len(data), "sha256": digest(data)} for name, data in sorted(entries.items())]}


def package():
    manifest = verify_build()
    names = set(LAUNCHER_FILES) | set(SOURCE_FILES) | set(manifest["sources"])
    inputs = {name: project_file(name).read_bytes() for name in sorted(names)}
    # Recheck the actual snapshot; no compiler logs/arguments or broad folder
    # copies are permitted, even when run from the development workspace.
    for name, expected in manifest["sources"].items():
        if digest(inputs[name]) != expected:
            raise RuntimeError("Source changed while preparing the package.")
    if (digest(inputs[RUNTIME]) != manifest["runtime_sha256"]
            or digest(inputs["config/supported-build.json"]) != manifest["supported_build_sha256"]
            or json.loads(inputs[BUILD_MANIFEST]) != manifest):
        raise RuntimeError("Build inputs changed while preparing the package.")
    privacy = verify_privacy(inputs)
    repository = add_checksums(inputs)
    launcher = add_checksums({name: inputs[name] for name in LAUNCHER_FILES})
    export_repository(repository)
    directory = output_directory("dist")
    launcher_name = f"Brainmind-{VERSION}-launcher.zip"
    source_name = f"Brainmind-{VERSION}-source.zip"
    archives = {
        launcher_name: write_archive(directory / launcher_name, f"Brainmind-{VERSION}/", launcher),
        source_name: write_archive(directory / source_name, f"Brainmind-{VERSION}-source/", repository),
    }
    report = {"format": 1, "version": VERSION, "target_version": TARGET,
              "repository": f"publish/Brainmind-{VERSION}", "repository_member_count": len(repository),
              "launcher_member_count": len(launcher), "privacy": privacy, "archives": archives}
    write_file(directory / "github-release-manifest.json", (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    return {"success": True, "version": VERSION, "repository": report["repository"],
            "repository_member_count": len(repository), "launcher_member_count": len(launcher),
            "archives": [{"file": "dist/" + name, "bytes": item["bytes"]} for name, item in archives.items()],
            "manifest": "dist/github-release-manifest.json", "privacy": privacy}


def main():
    argparse.ArgumentParser(description=__doc__).parse_args()
    try:
        print(json.dumps(package(), ensure_ascii=False, indent=2))
        return 0
    except (OSError, RuntimeError, ValueError, zipfile.BadZipFile) as error:
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
