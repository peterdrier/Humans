#!/usr/bin/env python3
"""Generate a PR surface report from git and Reforge deltas."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from collections import defaultdict
from pathlib import Path


INTERFACES_PREFIX = "src/Humans.Application/Interfaces/"
MIGRATIONS_PREFIX = "src/Humans.Infrastructure/Migrations/"


def run_git(args: list[str]) -> str:
    return subprocess.check_output(["git", *args], text=True, encoding="utf-8")


def try_run_git(args: list[str]) -> str:
    try:
        return run_git(args)
    except subprocess.CalledProcessError:
        return ""


def normalize(path: str) -> str:
    return path.replace("\\", "/")


def classify_path(path: str) -> str:
    path = normalize(path)
    if path.startswith(MIGRATIONS_PREFIX):
        return "migrations"
    if path.startswith("tests/") or ".Tests/" in path:
        return "tests"
    if path.startswith(("docs/", "memory/")) or path.endswith(".md"):
        return "docs"
    if path.startswith((".github/", ".config/")) or not path.endswith((".cs", ".cshtml", ".razor", ".csproj", ".props", ".targets", ".json")):
        return "other"
    return "code"


def is_real_migration_file(path: str) -> bool:
    path = normalize(path)
    name = Path(path).name
    return (
        path.startswith(MIGRATIONS_PREFIX)
        and name.endswith(".cs")
        and not name.endswith(".Designer.cs")
        and name != "HumansDbContextModelSnapshot.cs"
    )


def parse_name_status(base: str, head: str) -> tuple[list[str], list[str], list[str]]:
    raw = run_git(["diff", "--name-status", "--find-renames", "--find-copies", f"{base}...{head}"])
    added_files: list[str] = []
    changed_files: list[str] = []
    migration_files: list[str] = []

    for line in raw.splitlines():
        parts = line.split("\t")
        if not parts:
            continue
        status = parts[0]
        path = normalize(parts[-1])
        changed_files.append(path)
        if status.startswith("A"):
            added_files.append(path)
        if is_real_migration_file(path):
            migration_files.append(path)

    return added_files, changed_files, migration_files


def parse_numstat(base: str, head: str) -> dict[str, dict[str, int]]:
    raw = run_git(["diff", "--numstat", "--find-renames", "--find-copies", f"{base}...{head}"])
    counts: dict[str, dict[str, int]] = defaultdict(lambda: {"added": 0, "deleted": 0})
    for line in raw.splitlines():
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        added_raw, deleted_raw, path_raw = parts[0], parts[1], parts[-1]
        if added_raw == "-" or deleted_raw == "-":
            continue
        path = normalize(path_raw)
        bucket = classify_path(path)
        counts[bucket]["added"] += int(added_raw)
        counts[bucket]["deleted"] += int(deleted_raw)
    return counts


def limited(items: list[str], limit: int = 25) -> list[str]:
    if len(items) <= limit:
        return items
    return [*items[:limit], f"... {len(items) - limit} more"]


def md_safe(text: str) -> str:
    # Strip characters a fork PR could use to break out of an inline-code span or
    # markdown table cell (backtick, pipe) plus control chars / newlines. The bot
    # posts this report verbatim, so fork-controlled identifiers and .cs paths are
    # untrusted input; legitimate C# names/paths contain none of these, so this is
    # lossless for real content while neutralizing markdown/@mention injection.
    return "".join(c for c in str(text) if c not in "`|" and (c == " " or c >= "!"))


def bullet_list(items: list[str]) -> str:
    return "\n".join(f"- `{md_safe(item)}`" for item in limited(items))


def short_ref(ref: str) -> str:
    return ref[:8] if len(ref) == 40 and all(c in "0123456789abcdefABCDEF" for c in ref) else ref


def load_json(path: str | None) -> dict | None:
    if not path:
        return None
    data = Path(path).read_bytes()
    for encoding in ("utf-8", "utf-8-sig", "utf-16"):
        try:
            return json.loads(data.decode(encoding))
        except UnicodeError:
            continue
    return json.loads(data.decode("utf-8"))


def format_delta(delta: int) -> str:
    return f"+{delta}" if delta > 0 else str(delta)


def compare_number_maps(base: dict[str, int], head: dict[str, int]) -> list[tuple[str, int, int, int]]:
    rows: list[tuple[str, int, int, int]] = []
    for key in sorted(set(base) | set(head)):
        base_value = int(base.get(key) or 0)
        head_value = int(head.get(key) or 0)
        delta = head_value - base_value
        if delta != 0:
            rows.append((key, base_value, head_value, delta))
    return sorted(rows, key=lambda row: (-abs(row[3]), row[0]))


def groups_by_name(score: dict) -> dict[str, int]:
    return {
        str(group["name"]): int(group.get("total") or 0)
        for group in score.get("groups", [])
    }


def git_files(ref: str, prefix: str) -> list[str]:
    raw = run_git(["ls-tree", "-r", "--name-only", ref, "--", prefix])
    return [normalize(line) for line in raw.splitlines() if line.endswith(".cs")]


def normalize_signature(signature: str) -> str:
    return " ".join(signature.replace("\t", " ").split()).rstrip(";")


def extract_interface_symbols(ref: str) -> dict[str, dict[str, object]]:
    interfaces: dict[str, dict[str, object]] = {}
    interface_re = re.compile(r"\binterface\s+(I[A-Za-z0-9_]*)\b")

    for path in git_files(ref, INTERFACES_PREFIX):
        content = try_run_git(["show", f"{ref}:{path}"])
        current: str | None = None
        depth = 0
        pending_signature: list[str] = []

        for raw_line in content.splitlines():
            line = raw_line.split("//", 1)[0].strip()
            if not line or line.startswith("["):
                continue

            if current is None:
                match = interface_re.search(line)
                if not match:
                    continue
                current = match.group(1)
                interfaces.setdefault(current, {"path": path, "methods": set()})
                depth = line.count("{") - line.count("}")
                continue

            depth += line.count("{") - line.count("}")
            if "(" in line or pending_signature:
                pending_signature.append(line)
                if ";" in line:
                    signature = normalize_signature(" ".join(pending_signature))
                    pending_signature.clear()
                    if "(" in signature and ")" in signature:
                        interfaces[current]["methods"].add(signature)

            if depth <= 0:
                current = None
                depth = 0
                pending_signature.clear()

    return interfaces


def interface_delta(base: str, head: str) -> dict[str, object]:
    base_interfaces = extract_interface_symbols(base)
    head_interfaces = extract_interface_symbols(head)
    new_interfaces = [
        f"{name} ({head_interfaces[name]['path']})"
        for name in sorted(set(head_interfaces) - set(base_interfaces))
    ]

    added_methods: dict[str, list[str]] = {}
    for name in sorted(set(base_interfaces) & set(head_interfaces)):
        base_methods = base_interfaces[name]["methods"]
        head_methods = head_interfaces[name]["methods"]
        added = sorted(head_methods - base_methods)
        if added:
            added_methods[name] = added

    return {
        "new_interfaces": new_interfaces,
        "added_interface_methods": added_methods,
    }


def interface_delta_markdown(delta: dict[str, object]) -> str:
    new_interfaces = list(delta.get("new_interfaces", []))
    added_methods = dict(delta.get("added_interface_methods", {}))
    if not new_interfaces and not added_methods:
        return "### Interface Surface\n\nNo new interfaces or interface methods."

    sections = ["### Interface Surface"]
    if new_interfaces:
        sections.extend(["", "**New interfaces**", "", bullet_list(new_interfaces)])
    if added_methods:
        sections.extend(["", "**Added interface methods**"])
        for name, methods in added_methods.items():
            sections.extend(["", f"`{md_safe(name)}`", "", bullet_list(list(methods))])
    return "\n".join(sections)


def reforge_delta_markdown(base_score: dict | None, head_score: dict | None) -> str:
    if not base_score or not head_score:
        return "### Reforge Surface Score\n\nNot available for this run.\n"

    sections: list[str] = ["### Reforge Surface Score", ""]
    total_delta = int(head_score.get("total") or 0) - int(base_score.get("total") or 0)
    sections.extend(
        [
            "| metric | base | head | delta |",
            "|---|---:|---:|---:|",
            f"| total | {int(base_score.get('total') or 0)} | {int(head_score.get('total') or 0)} | {format_delta(total_delta)} |",
            "",
        ]
    )

    section_rows = compare_number_maps(groups_by_name(base_score), groups_by_name(head_score))
    if section_rows:
        sections.extend(["#### Section Deltas", "", "| section | base | head | delta |", "|---|---:|---:|---:|"])
        sections.extend(
            f"| `{md_safe(name)}` | {base} | {head} | {format_delta(delta)} |"
            for name, base, head, delta in section_rows
        )
        sections.append("")
    else:
        sections.extend(["#### Section Deltas", "", "No section score changes.", ""])

    rule_rows = compare_number_maps(base_score.get("byRule", {}), head_score.get("byRule", {}))
    if rule_rows:
        sections.extend(["#### Rule Deltas", "", "| rule | base | head | delta |", "|---|---:|---:|---:|"])
        sections.extend(
            f"| `{md_safe(name)}` | {base} | {head} | {format_delta(delta)} |"
            for name, base, head, delta in rule_rows
        )
        sections.append("")
    else:
        sections.extend(["#### Rule Deltas", "", "No rule score changes.", ""])

    return "\n".join(sections)


def build_markdown(
    base: str,
    head: str,
    counts: dict[str, dict[str, int]],
    added_files: list[str],
    changed_files: list[str],
    migration_files: list[str],
    base_score: dict | None,
    head_score: dict | None,
    interfaces: dict[str, object],
    base_label: str,
    head_label: str,
    reforge_version: str | None,
) -> str:
    categories = ["code", "migrations", "tests", "docs", "other"]
    rows = [
        f"| {category} | {counts.get(category, {}).get('added', 0)} | {counts.get(category, {}).get('deleted', 0)} |"
        for category in categories
        if counts.get(category, {}).get("added", 0) or counts.get(category, {}).get("deleted", 0)
    ]
    loc_section = (
        "### Diff Size\n\n| bucket | added | deleted |\n|---|---:|---:|\n" + "\n".join(rows)
        if rows
        else "### Diff Size\n\nNo line changes detected."
    )
    migration_status = "OK" if len(migration_files) <= 1 else "BLOCK"
    summary = f"{len(changed_files)} changed file(s) | EF migrations: {len(migration_files)}/1"

    compared_line = f"Compared `{short_ref(base_label)}`...`{short_ref(head_label)}`."
    if reforge_version:
        compared_line += f" Scored with reforge `{md_safe(reforge_version)}`."

    sections = [
        "<!-- pr-surface-report -->",
        "## PR Surface Report",
        "",
        compared_line,
        "",
        f"**Summary:** {summary}",
        "",
        reforge_delta_markdown(base_score, head_score).rstrip(),
        "",
        interface_delta_markdown(interfaces).rstrip(),
        "",
        loc_section,
    ]

    if added_files:
        sections.extend(["", "### New Files", "", bullet_list(added_files)])

    if migration_files or migration_status == "BLOCK":
        sections.extend(
            [
                "",
                f"### EF Migrations ({migration_status})",
                "",
                bullet_list(migration_files),
            ]
        )

    return "\n".join(sections) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--base-label")
    parser.add_argument("--head-label")
    parser.add_argument("--reforge-base-json")
    parser.add_argument("--reforge-head-json")
    parser.add_argument("--reforge-version")
    parser.add_argument("--output", default="pr-surface-report.md")
    parser.add_argument("--json-output", default="pr-surface-report.json")
    args = parser.parse_args()

    added_files, changed_files, migration_files = parse_name_status(args.base, args.head)
    counts = parse_numstat(args.base, args.head)
    base_score = load_json(args.reforge_base_json)
    head_score = load_json(args.reforge_head_json)
    interfaces = interface_delta(args.base, args.head)
    base_label = args.base_label or args.base
    head_label = args.head_label or args.head
    markdown = build_markdown(
        args.base,
        args.head,
        counts,
        added_files,
        changed_files,
        migration_files,
        base_score,
        head_score,
        interfaces,
        base_label,
        head_label,
        args.reforge_version,
    )

    Path(args.output).write_text(markdown, encoding="utf-8")
    Path(args.json_output).write_text(
        json.dumps(
            {
                "base": args.base,
                "head": args.head,
                "base_label": base_label,
                "head_label": head_label,
                "counts": counts,
                "added_files": added_files,
                "changed_files": changed_files,
                "migration_files": migration_files,
                "migration_count": len(migration_files),
                "reforge": {
                    "version": args.reforge_version,
                    "base": base_score,
                    "head": head_score,
                },
                "interfaces": interfaces,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(markdown)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
