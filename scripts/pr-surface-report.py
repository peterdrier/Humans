#!/usr/bin/env python3
"""Generate a PR surface report from a git diff."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from collections import defaultdict
from pathlib import Path


COMMENT_PREFIXES = ("//", "/*", "*", "*/", "#", "@*", "<!--", "--")
MIGRATIONS_PREFIX = "src/Humans.Infrastructure/Migrations/"


def run_git(args: list[str]) -> str:
    return subprocess.check_output(["git", *args], text=True, encoding="utf-8")


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
    return "code"


def classify_line(path: str, line: str) -> str:
    path_bucket = classify_path(path)
    if path_bucket in {"migrations", "tests", "docs"}:
        return path_bucket

    stripped = line.strip()
    if stripped == "":
        return "blank"
    if stripped.startswith(COMMENT_PREFIXES):
        return "comments"
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


def parse_diff(base: str, head: str) -> tuple[dict[str, dict[str, int]], dict[str, list[str]]]:
    raw = run_git(["diff", "--find-renames", "--find-copies", "--unified=0", f"{base}...{head}"])
    counts: dict[str, dict[str, int]] = defaultdict(lambda: {"added": 0, "deleted": 0})
    inventory: dict[str, list[str]] = defaultdict(list)

    old_path = ""
    new_path = ""
    old_line = 0
    new_line = 0

    public_type_re = re.compile(r"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:class|record|interface|enum|struct)\s+\w+")
    interface_method_re = re.compile(r"^\s*(?:Task|ValueTask|IReadOnly|IAsync|bool|int|string|Guid|void|[A-Z]\w+)\S*\s+\w+\s*\([^;]*\)\s*;")
    service_method_re = re.compile(r"^\s*(?:public|internal)\s+(?:async\s+)?[\w<>,\[\]\?]+\s+\w+\s*\(")
    route_re = re.compile(r"^\s*\[(?:Route|HttpGet|HttpPost|HttpPut|HttpDelete|HttpPatch)\b")
    action_re = re.compile(r"^\s*public\s+(?:async\s+)?(?:Task<)?IActionResult\b")
    di_re = re.compile(r"\bservices\.Add(?:Keyed)?(?:Scoped|Singleton|Transient)\b")
    package_re = re.compile(r"<PackageReference\b")

    for line in raw.splitlines():
        if line.startswith("diff --git "):
            old_path = ""
            new_path = ""
            continue
        if line.startswith("--- "):
            path = line[4:].strip()
            old_path = "" if path == "/dev/null" else normalize(path.removeprefix("a/"))
            continue
        if line.startswith("+++ "):
            path = line[4:].strip()
            new_path = "" if path == "/dev/null" else normalize(path.removeprefix("b/"))
            continue
        if line.startswith("@@ "):
            match = re.search(r"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", line)
            if match:
                old_line = int(match.group(1))
                new_line = int(match.group(2))
            continue
        if line.startswith("+") and not line.startswith("+++"):
            content = line[1:]
            path = new_path or old_path
            bucket = classify_line(path, content)
            counts[bucket]["added"] += 1

            stripped = content.strip()
            location = f"{path}:{new_line}: {stripped}"
            if stripped and not stripped.startswith(COMMENT_PREFIXES):
                if public_type_re.match(content):
                    inventory["public_types"].append(location)
                if "/Interfaces/" in path and interface_method_re.match(content):
                    inventory["interface_methods"].append(location)
                if ("/Services/" in path or "/Repositories/" in path) and service_method_re.match(content):
                    inventory["service_repository_methods"].append(location)
                if route_re.match(content) or ("/Controllers/" in path and action_re.match(content)):
                    inventory["routes_actions"].append(location)
                if di_re.search(content):
                    inventory["di_registrations"].append(location)
                if path.endswith((".csproj", ".props", ".targets")) and package_re.search(content):
                    inventory["package_references"].append(location)
            new_line += 1
            continue
        if line.startswith("-") and not line.startswith("---"):
            content = line[1:]
            path = old_path or new_path
            bucket = classify_line(path, content)
            counts[bucket]["deleted"] += 1
            old_line += 1
            continue
        if old_path or new_path:
            old_line += 1
            new_line += 1

    return counts, inventory


def limited(items: list[str], limit: int = 25) -> list[str]:
    if len(items) <= limit:
        return items
    return [*items[:limit], f"... {len(items) - limit} more"]


def bullet_list(items: list[str]) -> str:
    return "\n".join(f"- `{item}`" for item in limited(items))


def short_ref(ref: str) -> str:
    return ref[:8] if re.fullmatch(r"[0-9a-fA-F]{40}", ref) else ref


def build_markdown(
    base: str,
    head: str,
    counts: dict[str, dict[str, int]],
    added_files: list[str],
    changed_files: list[str],
    migration_files: list[str],
    inventory: dict[str, list[str]],
    base_label: str,
    head_label: str,
) -> str:
    categories = ["code", "comments", "tests", "migrations", "docs", "blank"]
    rows = [
        f"| {category} | {counts.get(category, {}).get('added', 0)} | {counts.get(category, {}).get('deleted', 0)} |"
        for category in categories
        if counts.get(category, {}).get("added", 0) or counts.get(category, {}).get("deleted", 0)
    ]
    loc_section = (
        "### LOC\n\n| bucket | added | deleted |\n|---|---:|---:|\n" + "\n".join(rows)
        if rows
        else "### LOC\n\nNo line changes detected."
    )
    migration_status = "OK" if len(migration_files) <= 1 else "BLOCK"
    summary = f"{len(changed_files)} changed file(s) | EF migrations: {len(migration_files)}/1"

    sections = [
        "<!-- pr-surface-report -->",
        "## PR Surface Report",
        "",
        f"Compared `{short_ref(base_label)}`...`{short_ref(head_label)}`.",
        "",
        f"**Summary:** {summary}",
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

    inventory_sections = [
        ("Public types", inventory.get("public_types", [])),
        ("Interface methods", inventory.get("interface_methods", [])),
        ("Service/repository methods", inventory.get("service_repository_methods", [])),
        ("Routes/actions", inventory.get("routes_actions", [])),
        ("DI registrations", inventory.get("di_registrations", [])),
        ("Package references", inventory.get("package_references", [])),
    ]
    non_empty_inventory = [(title, items) for title, items in inventory_sections if items]
    if non_empty_inventory:
        sections.extend(["", "### Surface Inventory"])
        for title, items in non_empty_inventory:
            sections.extend(["", f"**{title}**", "", bullet_list(items)])

    return "\n".join(sections) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--base-label")
    parser.add_argument("--head-label")
    parser.add_argument("--output", default="pr-surface-report.md")
    parser.add_argument("--json-output", default="pr-surface-report.json")
    args = parser.parse_args()

    added_files, changed_files, migration_files = parse_name_status(args.base, args.head)
    counts, inventory = parse_diff(args.base, args.head)
    base_label = args.base_label or args.base
    head_label = args.head_label or args.head
    markdown = build_markdown(
        args.base,
        args.head,
        counts,
        added_files,
        changed_files,
        migration_files,
        inventory,
        base_label,
        head_label,
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
                "inventory": inventory,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(markdown)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
