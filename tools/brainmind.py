"""Build, launch and control the version-pinned Brainmind extension.

The application is never patched. Launch uses the original entry point and
sets startup-hook variables only in the child process. Writes require the
context token obtained immediately before the operation.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
VERSION = "0.3.0"
BUILD = ROOT / "build" / VERSION
SUPPORTED = ROOT / "config/supported-build.json"


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest() if hasattr(hashlib, "file_digest") else _hash_stream(stream)


def _hash_stream(stream):
    value = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        value.update(block)
    return value.hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def find_app(explicit=None):
    candidates = []
    if explicit:
        path = Path(os.path.expandvars(explicit)).expanduser()
        candidates.append(path / "TheBrain.exe" if path.is_dir() else path)
    else:
        import winreg
        # The installer may leave InstallLocation empty. Protocol/file
        # associations still record the installed executable on a custom drive.
        for association in ("brain", "TheBrain.Document", "TheBrain.Theme"):
            try:
                with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, association + r"\shell\open\command") as key:
                    command = str(winreg.QueryValueEx(key, "")[0])
                    match = re.match(r'^"([^"]+\.exe)"|^(.+?\.exe)(?:\s|$)', command, re.IGNORECASE)
                    if match:
                        candidates.append(Path(os.path.expandvars(next(g for g in match.groups() if g is not None))))
            except OSError:
                pass
        for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
            for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
                try:
                    with winreg.OpenKey(hive, r"Software\Microsoft\Windows\CurrentVersion\App Paths\TheBrain.exe", 0, winreg.KEY_READ | view) as key:
                        candidates.append(Path(winreg.QueryValueEx(key, "")[0].strip('"')))
                except OSError:
                    pass
                try:
                    with winreg.OpenKey(hive, r"Software\Microsoft\Windows\CurrentVersion\Uninstall", 0, winreg.KEY_READ | view) as parent:
                        for i in range(winreg.QueryInfoKey(parent)[0]):
                            try:
                                with winreg.OpenKey(parent, winreg.EnumKey(parent, i)) as key:
                                    if "thebrain" not in str(winreg.QueryValueEx(key, "DisplayName")[0]).lower():
                                        continue
                                    for field in ("InstallLocation", "DisplayIcon"):
                                        try:
                                            value = os.path.expandvars(str(winreg.QueryValueEx(key, field)[0])).strip()
                                            if not value:
                                                continue
                                            if field == "DisplayIcon":
                                                match = re.match(r'^"([^"]+)"|^(.+?)(?:,\s*-?\d+)?$', value)
                                                value = next(g for g in match.groups() if g is not None)
                                            p = Path(value.strip('"'))
                                            candidates.append(p / "TheBrain.exe" if field == "InstallLocation" else p)
                                        except (OSError, AttributeError):
                                            pass
                            except OSError:
                                pass
                except OSError:
                    pass
        for name in ("ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"):
            base = os.environ.get(name)
            if base:
                candidates.extend(Path(base) / suffix / "TheBrain.exe" for suffix in ("TheBrain", "TheBrain/TheBrain", "Programs/TheBrain"))
    required = read_json(SUPPORTED)
    matches = []
    checked = set()
    for candidate in candidates:
        if candidate.name.lower() != "thebrain.exe":
            candidate = candidate.parent / "TheBrain.exe"
        if not candidate.is_file():
            continue
        candidate = candidate.resolve()
        if str(candidate).lower() in checked:
            continue
        checked.add(str(candidate).lower())
        if all((candidate.parent / item["Name"]).is_file() and sha256(candidate.parent / item["Name"]) == item["SHA256"] for item in required["files"]):
            matches.append(candidate)
    if len(matches) != 1:
        raise RuntimeError("Exactly one supported TheBrain 14.0.102.0 installation is required. Pass --app with its TheBrain.exe path; its pinned file hashes must match config/supported-build.json.")
    return matches[0]


def is_managed_pe(path):
    try:
        with Path(path).open("rb") as stream:
            data = stream.read()
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            return False
        optional = pe + 24
        magic = struct.unpack_from("<H", data, optional)[0]
        directories = optional + (112 if magic == 0x20B else 96)
        return struct.unpack_from("<I", data, directories + 14 * 8)[0] != 0
    except (OSError, ValueError, struct.error):
        return False


def build(args):
    app = find_app(args.app)
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("The installed .NET 6 runtime is required; no runtime will be installed automatically.")
    runtimes = subprocess.run([dotnet, "--list-runtimes"], check=True, capture_output=True, text=True, encoding="utf-8").stdout
    choices = []
    for line in runtimes.splitlines():
        match = re.match(r"Microsoft\.NETCore\.App (6\.\d+\.\d+) \[(.+)\]", line)
        if match:
            choices.append((tuple(map(int, match[1].split("."))), Path(match[2]) / match[1]))
    if not choices:
        raise RuntimeError(".NET 6 runtime was not found. TheBrain version will not be changed.")
    runtime = max(choices)[1]
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    if not compiler.is_file():
        raise RuntimeError("The Windows Framework64 C# compiler is required to build this source package.")
    sources = sorted((ROOT / "src/Brainmind.Runtime").glob("*.cs"))
    if not sources:
        raise RuntimeError("Brainmind runtime source files are missing.")
    BUILD.mkdir(parents=True, exist_ok=True)
    staging = BUILD / "staging"
    staging.mkdir(exist_ok=True)
    temporary = staging / "Brainmind.Runtime.dll"
    target = BUILD / "Brainmind.Runtime.dll"
    options = ["/nologo", "/utf8output", "/nostdlib+", "/langversion:5", "/target:library", '/out:"' + str(temporary) + '"']
    options += ['/reference:"' + str(p) + '"' for p in sorted(runtime.glob("*.dll")) if is_managed_pe(p)]
    options += ['/reference:"' + str(path) + '"' for path in [app.parent / "Xwt.dll"] + sorted(app.parent.glob("UglyToad.PdfPig*.dll"))]
    options += ['"' + str(p) + '"' for p in sources]
    response = BUILD / "Brainmind.Runtime.rsp"
    response.write_text("\n".join(options), encoding="utf-8-sig")
    compiled = subprocess.run([str(compiler), "/noconfig", "@" + str(response)], capture_output=True, text=True, encoding="utf-8", timeout=90)
    (BUILD / "compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    if compiled.returncode:
        raise RuntimeError("C# compilation failed; see build/compile.log.\n" + compiled.stdout + compiled.stderr)
    for attempt in range(8):
        try:
            os.replace(temporary, target)
            break
        except PermissionError:
            if attempt == 7:
                raise RuntimeError("C# compilation succeeded, but the runtime file is in use or temporarily locked. Close any session using this version and retry. The compiled candidate remains at " + str(temporary)) from None
            time.sleep(.2)
    manifest = {"format": 1, "runtime_version": VERSION, "target_version": "14.0.102.0", "runtime_file": target.name,
                "runtime_sha256": sha256(target), "sources": {str(p.relative_to(ROOT)).replace("\\", "/"): sha256(p) for p in sources},
                "supported_build_sha256": sha256(SUPPORTED)}
    (BUILD / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return {"success": True, "runtime": str(target), "manifest": str(BUILD / "manifest.json"), "target_version": "14.0.102.0"}


def launch(args):
    command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools/launch-brainmind.ps1")]
    if args.app:
        command += ["-AppPath", args.app]
    if args.session_dir:
        command += ["-SessionDirectory", str(args.session_dir)]
    # TheBrain inherits its parent's standard handles. Files let the launcher
    # return immediately even when the GUI/GoodLaunch child keeps them open.
    with tempfile.TemporaryFile() as output, tempfile.TemporaryFile() as errors:
        completed = subprocess.run(command, stdout=output, stderr=errors, timeout=30)
        output.seek(0)
        stdout = output.read().decode("utf-8-sig")
        errors.seek(0)
        stderr = errors.read().decode("utf-8-sig")
    if completed.returncode:
        raise RuntimeError("Brainmind launch failed: " + (stderr or stdout).strip())
    return json.loads(stdout.strip())


def session_directory(args):
    value = args.session_dir or os.environ.get("BRAINMIND_SESSION_DIR")
    return Path(value) if value else Path(os.environ["LOCALAPPDATA"]) / "Brainmind/sessions"


def sessions(args):
    result = []
    directory = session_directory(args)
    if not directory.is_dir():
        return result
    for path in sorted(directory.glob("*.json")):
        try:
            item = read_json(path)
            pid = int(item["process_id"])
            if pid <= 0 or item.get("pipe_name") != "Brainmind-" + str(pid):
                continue
            try:
                answer = pipe_request(pid, {"operation": "ping"}, 1.5)
                item["reachable"] = answer.get("success") is True
            except (OSError, TimeoutError, RuntimeError):
                item["reachable"] = False
            result.append(item)
        except (OSError, ValueError, KeyError, TypeError):
            continue
    return result


def select_pid(args):
    if args.pid is not None:
        if args.pid <= 0:
            raise RuntimeError("--pid must be a positive process ID.")
        return args.pid
    available = [s for s in sessions(args) if s["reachable"]]
    if len(available) != 1:
        raise RuntimeError("Select exactly one running Brainmind session with --pid. Use sessions to inspect available sessions.")
    return int(available[0]["process_id"])


class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t), ("Offset", wintypes.DWORD),
                ("OffsetHigh", wintypes.DWORD), ("hEvent", wintypes.HANDLE)]


def pipe_request(pid, request, timeout):
    """One UTF-8 JSON line each way, with cancellable Windows overlapped I/O."""
    if os.name != "nt":
        raise RuntimeError("Brainmind live sessions require Windows.")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
    kernel.CreateEventW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateEventW.restype = wintypes.HANDLE
    for name in ("ReadFile", "WriteFile"):
        getattr(kernel, name).argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(OVERLAPPED)]
    kernel.GetOverlappedResult.argtypes = [wintypes.HANDLE, ctypes.POINTER(OVERLAPPED), ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
    kernel.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(OVERLAPPED)]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    deadline = time.monotonic() + timeout
    name = r"\\.\pipe\Brainmind-" + str(pid)
    handle = kernel.CreateFileW(name, 0xC0000000, 0, None, 3, 0x40000000, None)
    if handle == wintypes.HANDLE(-1).value:
        error = ctypes.get_last_error()
        if error == 231:
            kernel.WaitNamedPipeW(name, max(1, int(timeout * 1000)))
            handle = kernel.CreateFileW(name, 0xC0000000, 0, None, 3, 0x40000000, None)
        if handle == wintypes.HANDLE(-1).value:
            raise OSError(ctypes.get_last_error(), "Brainmind named pipe is unavailable for this user/process.")
    event = kernel.CreateEventW(None, True, False, None)
    if not event:
        kernel.CloseHandle(handle)
        raise OSError(ctypes.get_last_error(), "Cannot create pipe I/O event.")

    def transfer(function, buffer, count):
        kernel.ResetEvent.argtypes = [wintypes.HANDLE]
        kernel.ResetEvent(event)
        overlap = OVERLAPPED()
        overlap.hEvent = event
        done = wintypes.DWORD()
        ok = function(handle, buffer, count, ctypes.byref(done), ctypes.byref(overlap))
        if not ok:
            error = ctypes.get_last_error()
            if error != 997:
                raise OSError(error, "Brainmind pipe I/O failed.")
            remaining = max(0, int((deadline - time.monotonic()) * 1000))
            waited = kernel.WaitForSingleObject(event, remaining)
            if waited != 0:
                kernel.CancelIoEx(handle, ctypes.byref(overlap))
                kernel.WaitForSingleObject(event, 5000)
                raise TimeoutError("Brainmind request timed out; completed edits remain applied. Inspect context before retrying.")
            if not kernel.GetOverlappedResult(handle, ctypes.byref(overlap), ctypes.byref(done), False):
                raise OSError(ctypes.get_last_error(), "Brainmind pipe operation failed.")
        return done.value

    try:
        raw = (json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        if len(raw) > 1900000:
            raise RuntimeError("Brainmind request exceeds the supported size.")
        offset = 0
        while offset < len(raw):
            block = ctypes.create_string_buffer(raw[offset:])
            wrote = transfer(kernel.WriteFile, block, len(raw) - offset)
            if not wrote:
                raise RuntimeError("Brainmind pipe closed before request completion.")
            offset += wrote
        response = bytearray()
        while b"\n" not in response:
            block = ctypes.create_string_buffer(8192)
            got = transfer(kernel.ReadFile, block, len(block))
            if not got:
                raise RuntimeError("Brainmind pipe closed before responding.")
            response.extend(block.raw[:got])
            if len(response) > 8 * 1024 * 1024:
                raise RuntimeError("Brainmind response exceeds the supported size.")
        return json.loads(bytes(response).split(b"\n", 1)[0].decode("utf-8-sig"))
    finally:
        kernel.CloseHandle(event)
        kernel.CloseHandle(handle)


def live_command(args):
    request = {"operation": args.command}
    if args.command == "context":
        request["operation"] = "get_context"
    elif args.command == "read":
        request = {"operation": "read_thought", "id": args.id}
    elif args.command == "panel":
        request["action"] = args.action
    elif args.command == "execute":
        arguments = json.loads(args.arguments) if args.arguments is not None else read_json(args.arguments_file)
        if not isinstance(arguments, dict):
            raise RuntimeError("Tool arguments must be a JSON object.")
        if not args.token.strip():
            raise RuntimeError("A fresh --token from context is required for writes.")
        request = {"operation": "execute", "tool": args.tool, "arguments": arguments, "expected_context_token": args.token}
    elif args.command == "chat":
        request["text"] = " ".join(args.text)
    elif args.command == "generate-map":
        request = {"operation": "create_map_from_sources", "text": " ".join(args.text)}
    elif args.command == "diagnostics":
        request = {"operation": "get_diagnostics"}
    elif args.command == "attach":
        request = {"operation": "attachments", "paths": [str(Path(p).expanduser().resolve()) for p in args.files]}
    elif args.command == "attachments":
        request = {"operation": "list_attachments"}
    elif args.command == "remove-attachment":
        request = {"operation": "remove_attachment", "id": args.id}
    elif args.command == "clear-attachments":
        request = {"operation": "clear_attachments"}
    elif args.command == "read-attachment":
        request = {"operation": "read_attachment", "attachment_id": args.id,
                   "page_start": args.page_start, "page_end": args.page_end or args.page_start}
    elif args.command == "settings":
        settings = {}
        for field, value in (("base_url", args.base_url), ("model", args.model), ("timeout_seconds", args.model_timeout)):
            if value is not None:
                settings[field] = value
        if args.api_key_env:
            if args.api_key_env not in os.environ or not os.environ[args.api_key_env]:
                raise RuntimeError("The requested API-key environment variable is absent or empty.")
            settings["api_key"] = os.environ[args.api_key_env]
        if args.clear_key:
            settings["api_key"] = ""
        request = {"operation": "set_settings", "settings": settings} if settings else {"operation": "get_settings"}
    timeout = args.timeout if args.timeout is not None else (660 if args.command in ("chat", "generate-map") else 180 if args.command == "attach" else 20)
    if timeout <= 0:
        raise RuntimeError("--timeout must be positive.")
    pid = select_pid(args)
    if args.command in ("attach", "attachments", "remove-attachment", "clear-attachments", "read-attachment", "generate-map", "diagnostics"):
        status = pipe_request(pid, {"operation": "ping"}, min(timeout, 5))
        version = str(status.get("result", {}).get("runtime_version", "0.0.0"))
        try:
            current = tuple(int(p) for p in version.split("."))
        except ValueError:
            current = (0,)
        required = (0, 3, 0) if args.command in ("generate-map", "diagnostics") else (0, 2, 0)
        if current < required:
            raise RuntimeError("当前会话版本尚不支持此命令。请正常关闭 TheBrain，再通过 Brainmind 0.3.0 的启动器重新打开；不会自动关闭你的工作。")
    return pipe_request(pid, request, timeout)


def parser():
    result = argparse.ArgumentParser(description=__doc__)

    def globals_for(p, sub=False):
        default = argparse.SUPPRESS if sub else None
        p.add_argument("--pid", type=int, default=default, help="Exact Brainmind process ID")
        p.add_argument("--session-dir", type=Path, default=default, help="Session metadata directory")
        p.add_argument("--timeout", type=float, default=default, help="IPC timeout in seconds (chat: 660, others: 20)")

    globals_for(result)
    sub = result.add_subparsers(dest="command", required=True)
    for command in ("build", "launch", "sessions", "context", "read", "panel", "execute", "chat", "cancel", "settings",
                    "attach", "attachments", "remove-attachment", "clear-attachments", "read-attachment", "generate-map", "diagnostics"):
        p = sub.add_parser(command)
        globals_for(p, True)
        if command in ("build", "launch"):
            p.add_argument("--app", help="TheBrain.exe or installation directory")
        elif command == "read":
            p.add_argument("id", help="Thought ID from the current brain")
        elif command == "panel":
            p.add_argument("action", choices=("status", "show", "hide", "collapse", "expand"))
        elif command == "execute":
            p.add_argument("tool", choices=("create_thought", "rename_thought", "add_link", "replace_note"))
            group = p.add_mutually_exclusive_group(required=True)
            group.add_argument("--arguments", help="JSON argument object")
            group.add_argument("--arguments-file", type=Path, help="UTF-8 JSON argument object file")
            p.add_argument("--token", required=True, help="Fresh context_token from context")
        elif command == "chat":
            p.add_argument("text", nargs="+", help="Message to the configured model")
        elif command == "generate-map":
            p.add_argument("text", nargs="*", help="Create a mind map from selected sources, with actual creation receipts")
        elif command == "attach":
            p.add_argument("files", nargs="+", help="Selected PDF/TXT/MD/CSV files for the current chat")
        elif command in ("remove-attachment", "read-attachment"):
            p.add_argument("id", help="ID from attachments")
            if command == "read-attachment":
                p.add_argument("--page-start", type=int, default=1)
                p.add_argument("--page-end", type=int)
        elif command == "settings":
            p.add_argument("--base-url")
            p.add_argument("--model")
            p.add_argument("--model-timeout", type=int, choices=range(1, 601), metavar="1..600")
            group = p.add_mutually_exclusive_group()
            group.add_argument("--api-key-env", help="Read API key from this environment variable; never print it")
            group.add_argument("--clear-key", action="store_true")
    return result


def main():
    args = parser().parse_args()
    try:
        if args.command == "build":
            response = build(args)
        elif args.command == "launch":
            response = launch(args)
        elif args.command == "sessions":
            response = {"success": True, "sessions": sessions(args)}
        else:
            response = live_command(args)
        print(json.dumps(response, ensure_ascii=False, indent=2))
        inner = response.get("result")
        failed = response.get("success", True) is False or (
            isinstance(inner, dict) and (inner.get("success") is False or inner.get("ok") is False))
        if args.command == "attach" and isinstance(inner, dict):
            failed = failed or any(str(item.get("State", item.get("state", ""))).lower() == "error"
                                   for item in inner.get("files", []) if isinstance(item, dict))
        return 1 if failed else 0
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        # Never echo a request, header or credential. Provider responses also
        # expose only configured/not-configured status for API keys.
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
