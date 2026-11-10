"""Command line interface; Python's standard library is sufficient."""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

from .archive import Archive, ArchiveError, _json
from .diff import compare_archives


def emit(value):
    print(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False))


def node_details(archive: Archive, node_id: str) -> dict:
    thoughts = {row["Id"]: row for row in archive.rows("thoughts")}
    if node_id not in thoughts:
        raise ArchiveError(f"Thought not found: {node_id}")
    related = []
    for link in archive.rows("links"):
        a, b = link.get("ThoughtIdA"), link.get("ThoughtIdB")
        if node_id in (a, b):
            related.append({
                "a_name": thoughts.get(a, {}).get("Name"),
                "b_name": thoughts.get(b, {}).get("Name"),
                "record": link,
            })
    return {
        "thought": thoughts[node_id],
        "links": related,
        "attachments": [r for r in archive.rows("attachments") if r.get("SourceId") == node_id],
        "fields": [r for r in archive.rows("fieldinstances") if r.get("SourceId") == node_id],
    }


def parser() -> argparse.ArgumentParser:
    cli = argparse.ArgumentParser(description="实验性 BRZ v5 编辑器：读取、校验、按计划写入副本")
    commands = cli.add_subparsers(dest="command", required=True)
    inspect = commands.add_parser("inspect", help="查看摘要、节点 UUID 或指定节点的原始记录")
    inspect.add_argument("source", type=Path)
    inspect.add_argument("--id", dest="node_id")
    inspect.add_argument("--list", action="store_true", help="列出节点名称、UUID 和 Kind")
    inspect.add_argument("--notes", action="store_true", help="与 --id 一起读取笔记原文")
    inspect.add_argument("--note-limit", type=int, default=20000, help="每份笔记最多显示的字符数（最多 1000000）")
    validate = commands.add_parser("validate", help="检查内部引用与附件；不代表原软件导入测试")
    validate.add_argument("source", type=Path)
    compare = commands.add_parser("compare", help="比较两份 BRZ 的节点、连线、元数据与成员字节")
    compare.add_argument("source", type=Path)
    compare.add_argument("target", type=Path)
    compare.add_argument("--limit", type=int, default=100, help="每类明细的数量上限；报告包含总数和截断标志")
    apply = commands.add_parser("apply", help="执行 JSON 修改计划；不指定 --output 时仅预演")
    apply.add_argument("source", type=Path)
    apply.add_argument("--plan", type=Path, required=True)
    apply.add_argument("--output", type=Path, help="新的 BRZ 路径，禁止覆盖已有文件")
    return cli


def main(argv=None) -> int:
    args = parser().parse_args(argv)
    try:
        archive = Archive(args.source)
        if args.command == "compare":
            target = Archive(args.target)
            validations = {"source": archive.validate(), "target": target.validate()}
            if any(check["errors"] for check in validations.values()):
                emit({"error": "Comparison requires structurally valid inputs", "validation": validations})
                return 1
            result = compare_archives(archive, target, limit=args.limit)
            result["validation"] = validations
            result["application_import_verified"] = False
            emit(result)
            return 0
        if args.command == "inspect":
            result = archive.summary()
            result["validation"] = archive.validate()
            if args.list:
                result["thoughts"] = [
                    {k: row.get(k) for k in ("Id", "Name", "Kind", "TypeId")}
                    for row in archive.rows("thoughts")
                ]
            if args.node_id:
                result["node"] = node_details(archive, args.node_id)
                if args.notes:
                    result["node"]["notes"] = archive.notes(args.node_id, args.note_limit)
            elif args.notes:
                raise ArchiveError("--notes requires --id")
            emit(result)
            return 0
        if args.command == "validate":
            validation = archive.validate()
            emit({"source_sha256": archive.source_sha256, **validation})
            return 1 if validation["errors"] else 0

        plan_bytes = args.plan.read_bytes()
        plan = _json(plan_bytes)
        before = archive.summary()
        changes = archive.apply_plan(plan, base_dir=args.plan.resolve().parent)
        result = {
            "mode": "write" if args.output else "dry-run",
            "source_sha256": archive.source_sha256,
            "plan_sha256": hashlib.sha256(plan_bytes).hexdigest(),
            "before": before,
            "after": archive.summary(),
            "changes": changes,
            "validation": archive.validate(),
            "application_import_verified": False,
        }
        if args.output:
            output = archive.write(args.output)
            result["output"] = str(args.output)
            result["output_sha256"] = hashlib.sha256(output.read_bytes()).hexdigest()
            result["validation"] = Archive(output).validate()
        emit(result)
        return 0
    except (ArchiveError, OSError, ValueError, KeyError, TypeError) as exc:
        emit({"error": str(exc)})
        return 1


if __name__ == "__main__":
    sys.exit(main())
