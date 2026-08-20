#!/usr/bin/env python3
"""Generate a PR surface report from git and Reforge deltas."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from collections import defaultdict
from pathlib import Path


LEGACY_INTERFACES_PREFIX = "src/Humans.Application/Interfaces/"
INTERFACE_SEARCH_ROOTS = ("src/Humans.Application/Interfaces", "src/Sections")
# Section-owned interfaces live either in a dedicated `Humans.<Section>.Contracts`
# project or, when a section hasn't earned a separate Contracts project yet, in an
# in-project `Contracts/` folder (e.g. Humans.Store).
SECTION_CONTRACTS_PROJECT_RE = re.compile(r"^src/Sections/[^/]+\.Contracts/")
SECTION_CONTRACTS_FOLDER_RE = re.compile(r"^src/Sections/[^/]+/Contracts/")

HOST_MIGRATIONS_PREFIX = "src/Humans.Web/Migrations/"
# Since the per-section DbContext split (nobodies-collective/Humans#858) and the
# move to standalone section projects (nobodies-collective/Humans#866), migrations
# also live under src/Sections/<Project>/Data/Migrations/.
SECTION_MIGRATIONS_RE = re.compile(r"^src/Sections/([^/]+)/Data/Migrations/")


def run_git(args: list[str]) -> str:
    return subprocess.check_output(["git", *args], text=True, encoding="utf-8")


def try_run_git(args: list[str]) -> str:
    try:
        return run_git(args)
    except subprocess.CalledProcessError:
        return ""


def normalize(path: str) -> str:
    return path.replace("\\", "/")


def is_migration_path(path: str) -> bool:
    path = normalize(path)
    return path.startswith(HOST_MIGRATIONS_PREFIX) or bool(SECTION_MIGRATIONS_RE.match(path))


def is_interface_path(path: str) -> bool:
    path = normalize(path)
    return (
        path.startswith(LEGACY_INTERFACES_PREFIX)
        or bool(SECTION_CONTRACTS_PROJECT_RE.match(path))
        or bool(SECTION_CONTRACTS_FOLDER_RE.match(path))
    )


def classify_path(path: str) -> str:
    path = normalize(path)
    if is_migration_path(path):
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
        is_migration_path(path)
        and name.endswith(".cs")
        and not name.endswith(".Designer.cs")
        and not name.endswith("ModelSnapshot.cs")
    )


def migration_context(path: str) -> str:
    """Grouping key identifying which DbContext chain a migration file belongs to."""
    path = normalize(path)
    match = SECTION_MIGRATIONS_RE.match(path)
    if match:
        return match.group(1)
    return str(Path(path).parent)


def max_migrations_per_context(migration_files: list[str]) -> int:
    """Max real migrations ADDED in any one migration context.

    Two layouts feed this: the host's own chain under src/Humans.Web/Migrations/
    (Migrations/<Section>/ per context since nobodies-collective/Humans#858; the
    root Migrations/ chain was deleted at #858 peel 15, and the folder followed
    Humans.Infrastructure's deletion into Humans.Web at G5 lane 5b-6); and standalone section
    projects (nobodies-collective/Humans#866), where each section owns its
    migrations under its own project and the context is the section project
    name (the Humans.<Section> path segment under src/Sections/). The
    one-migration-per-PR rule applies per chain and only to additions —
    deleting or relocating a chain authors no migration (the peel-15 chain
    deletion tripped the old changed-files count at 144/1); a peel PR
    legitimately carries one baseline addition in the new section's directory
    or project.
    """
    per_context: dict[str, int] = {}
    for path in migration_files:
        context = migration_context(path)
        per_context[context] = per_context.get(context, 0) + 1
    return max(per_context.values(), default=0)


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
        # A = added; C<score> = copy destination, which is equally a new file —
        # --find-copies can classify a new migration that resembles an existing
        # one as C, and treating only A as an addition would let it evade the
        # one-migration-per-PR gate. Deletions and pure renames stay excluded.
        if status.startswith(("A", "C")):
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


METRIC_ROWS: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("locProd", ("locProd",)),
    ("files", ("files",)),
    ("classes", ("classes",)),
    ("interfaces", ("interfaces",)),
    ("methods", ("methods",)),
    ("cognitive p95", ("cognitive", "p95")),
    ("cognitive max", ("cognitive", "max")),
    ("cyclomatic p95", ("cyclomatic", "p95")),
    ("cyclomatic max", ("cyclomatic", "max")),
    ("maxClassLoc", ("maxClassLoc",)),
)


def metric_at(score: dict, path: tuple[str, ...]) -> int:
    node: object = score.get("metrics") or {}
    for key in path:
        if not isinstance(node, dict):
            return 0
        node = node.get(key)
    return int(node or 0)


def metrics_by_name(score: dict) -> dict[str, int]:
    return {label: metric_at(score, path) for label, path in METRIC_ROWS}


def write_surface_by_section(score: dict) -> dict[str, set[str]]:
    by_section = (score.get("publicWriteSurface") or {}).get("bySection") or {}
    return {str(name): set(map(str, ifaces)) for name, ifaces in by_section.items()}


def groups_by_name(score: dict) -> dict[str, int]:
    return {
        str(group["name"]): int(group.get("total") or 0)
        for group in score.get("groups", [])
    }


def git_files(ref: str, prefixes: tuple[str, ...]) -> list[str]:
    raw = run_git(["ls-tree", "-r", "--name-only", ref, "--", *prefixes])
    return [normalize(line) for line in raw.splitlines() if line.endswith(".cs")]


def normalize_signature(signature: str) -> str:
    return " ".join(signature.replace("\t", " ").split()).rstrip(";")


def extract_interface_symbols(ref: str) -> dict[str, dict[str, object]]:
    interfaces: dict[str, dict[str, object]] = {}
    interface_re = re.compile(r"\binterface\s+(I[A-Za-z0-9_]*)\b")

    for path in git_files(ref, INTERFACE_SEARCH_ROOTS):
        if not is_interface_path(path):
            continue
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
    sections.extend(["| metric | base | head | delta |", "|---|---:|---:|---:|"])
    # The total alone hides an offsetting trade: API added while method complexity is deleted nets
    # to zero. The two axes are scored separately, so report them separately.
    for label, key in (("total", "total"), ("surface", "surfaceTotal"), ("internal complexity", "internalComplexityTotal")):
        if key not in base_score and key not in head_score:
            continue  # an older reforge that never emitted the key: no row beats a row of zeros
        base_value = int(base_score.get(key) or 0)
        head_value = int(head_score.get(key) or 0)
        sections.append(f"| {label} | {base_value} | {head_value} | {format_delta(head_value - base_value)} |")
    sections.append("")

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

    # Size is the context a score delta lacks: surface can fall because the API shrank or because
    # the code did, and most internal-complexity points are satisfiable without moving any code.
    metric_rows = compare_number_maps(metrics_by_name(base_score), metrics_by_name(head_score))
    if metric_rows:
        sections.extend(["#### Corpus Size & Complexity", "", "| metric | base | head | delta |", "|---|---:|---:|---:|"])
        sections.extend(
            f"| {md_safe(name)} | {base} | {head} | {format_delta(delta)} |"
            for name, base, head, delta in metric_rows
        )
        head_metrics = head_score.get("metrics") or {}
        holders = [
            f"largest class `{md_safe(str(head_metrics.get('maxClassLocName') or ''))}`"
            if head_metrics.get("maxClassLocName")
            else "",
            f"most complex method `{md_safe(str((head_metrics.get('cognitive') or {}).get('maxMethod') or ''))}`"
            if (head_metrics.get("cognitive") or {}).get("maxMethod")
            else "",
        ]
        holders = [h for h in holders if h]
        if holders:
            sections.extend(["", "At head: " + ", ".join(holders) + "."])
        sections.append("")

    sections.extend(write_surface_markdown(base_score, head_score))

    return "\n".join(sections)


def write_surface_markdown(base_score: dict, head_score: dict) -> list[str]:
    """Which sections publish write capability another assembly can call. Reported, never scored."""
    base_by_section = write_surface_by_section(base_score)
    head_by_section = write_surface_by_section(head_score)
    head_summary = head_score.get("publicWriteSurface") or {}
    if not base_by_section and not head_by_section and not head_summary:
        return []

    lines = ["#### Published Write Surface", ""]
    published = int(head_summary.get("publishingSections") or 0)
    of_sections = int(head_summary.get("sections") or 0)
    interfaces = int(head_summary.get("interfaces") or 0)
    base_summary = base_score.get("publicWriteSurface") or {}
    lines.append(
        f"{published} of {of_sections} sections publish write capability, {interfaces} interfaces "
        f"({format_delta(interfaces - int(base_summary.get('interfaces') or 0))})."
    )

    added: list[str] = []
    removed: list[str] = []
    for name in sorted(set(base_by_section) | set(head_by_section)):
        before = base_by_section.get(name, set())
        after = head_by_section.get(name, set())
        added.extend(f"`{md_safe(name)}` -> `{md_safe(iface)}`" for iface in sorted(after - before))
        removed.extend(f"`{md_safe(name)}` -> `{md_safe(iface)}`" for iface in sorted(before - after))
    if added:
        lines.extend(["", "**Newly published**", "", bullet_list(added)])
    if removed:
        lines.extend(["", "**No longer published**", "", bullet_list(removed)])
    lines.append("")
    return lines


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
    max_per_context = max_migrations_per_context(migration_files)
    migration_status = "OK" if max_per_context <= 1 else "BLOCK"
    summary = (
        f"{len(changed_files)} changed file(s) | EF migrations: "
        f"{len(migration_files)} added file(s), max {max_per_context}/1 per context"
    )

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
                "migration_count": max_migrations_per_context(migration_files),
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
