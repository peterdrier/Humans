#!/usr/bin/env python3
"""Localization coverage of a section's Razor views (nobodies-collective/Humans#1115).

`SectionResourceParityTests` and the Prose & surface thread already cover the resx side:
every key present in all six files, no dead resources, keys prefixed with the section.
None of that sees the direction that actually leaks — user-facing view text that never
became a key at all. `/Expenses/{id}` was 575 lines with 11 `@Localizer[...]` calls and
perfect resx parity.

This counts the other direction: per `.cshtml`, literal user-facing strings against
localized ones, worst coverage first.

Route-aware, because `memory/code/localization-admin-exempt.md` exempts by route and not
by file path. Three buckets:

  member-facing  ranked, worst first — this is the run's work list
  admin-route    a route segment named Admin/TeamAdmin — reported, not ranked
  exempt         literally exempt by the atom (`Admin/*`, `TeamAdmin/*`, `Shifts/Dashboard`)

The atom's other half — "a view rendered only to coordinators/finance admins reads as an
admin function even on a member-facing route" — is judgment, and judgment is the run's, not
this script's. So a member-facing route the run judges admin stays in the ranked table and
the run says why it passed on it. Report the count, don't backfill, unless the run is *for*
that (same standing rule as `resource-key-prefix`).

Usage:
    loc-coverage.py --section Expenses [--root .] [--min-literals 1] [--json]
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# --- what counts as localized -------------------------------------------------------------
# `@Localizer[...]`, `@SharedLocalizer[...]`, `@ContainersLocalizer[...]`, and the
# `Localizer[...]` form inside an `@{ }` block.
LOCALIZED = re.compile(r"\b[A-Za-z]*Localizer\s*\[")

# --- attributes whose value a user reads --------------------------------------------------
USER_FACING_ATTRS = (
    "placeholder",
    "title",
    "alt",
    "aria-label",
    "aria-description",
    "data-confirm",
    "data-bs-title",
    "data-bs-content",
    "data-bs-original-title",
    "label",
)
ATTR_RE = re.compile(
    r"(?<![-\w])(" + "|".join(USER_FACING_ATTRS) + r")\s*=\s*(\"([^\"]*)\"|'([^']*)')",
    re.IGNORECASE,
)
# `value=` is user-facing only on a button/submit; everywhere else it is form data.
VALUE_ATTR_RE = re.compile(
    r"<(?:button|input)\b[^>]*?\bvalue\s*=\s*\"([^\"]*)\"[^>]*>", re.IGNORECASE
)
VIEWDATA_TITLE_RE = re.compile(r"ViewData\s*\[\s*\"Title\"\s*\]\s*=\s*(.+)")

# --- razor / html noise -------------------------------------------------------------------
RAZOR_COMMENT = re.compile(r"@\*.*?\*@", re.DOTALL)
# Directive lines carry type names, not prose: `@model Foo.Bar`, `@using`, `@inject`, …
DIRECTIVE = re.compile(
    r"^[ \t]*@(?:model|using|inject|addTagHelper|removeTagHelper|tagHelperPrefix|namespace"
    r"|inherits|attribute|implements|typeparam|page|preservewhitespace|rendermode)\b.*$",
    re.MULTILINE,
)
HTML_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
SCRIPT_STYLE = re.compile(r"<(script|style)\b.*?</\1\s*>", re.DOTALL | re.IGNORECASE)
TAG = re.compile(r"<[^>]*>", re.DOTALL)
# `@if (…)`, `@foreach (…)`, `@while (…)`, `@switch (…)`, `@using (…)`, `@await …(…)`
CONTROL_FLOW = re.compile(
    r"@(?:if|else\s+if|foreach|for|while|switch|lock|using|try|catch|finally|else|do)\b"
)
# A Razor expression: `@Model.Foo.Bar()`, `@item.Name`, `@r.Status`, `@Localizer["X"]`, `@("…")`
RAZOR_EXPR = re.compile(r"@[A-Za-z_(][\w.]*")
HTML_ENTITY = re.compile(r"&(?:[a-zA-Z]+|#\d+|#x[0-9a-fA-F]+);")
# Residual text is user-facing only if it carries a word — two+ letters, or a capitalized one.
WORDLIKE = re.compile(r"[A-Za-z]{2,}")
# C# keywords and structural tokens that survive tag-stripping around a control-flow block.
CODE_NOISE = {
    "true", "false", "null", "var", "new", "await", "async", "string", "int", "bool",
    "is", "not", "and", "or", "in", "as", "return", "case", "default", "break",
}


def _strip_balanced(text: str, opener: str, open_ch: str, close_ch: str) -> str:
    """Remove every `opener`-introduced balanced `open_ch`…`close_ch` region."""
    out = []
    i = 0
    n = len(text)
    while i < n:
        if text.startswith(opener, i):
            j = i + len(opener)
            while j < n and text[j].isspace():
                j += 1
            if j < n and text[j] == open_ch:
                depth = 0
                while j < n:
                    if text[j] == open_ch:
                        depth += 1
                    elif text[j] == close_ch:
                        depth -= 1
                        if depth == 0:
                            j += 1
                            break
                    j += 1
                i = j
                continue
        out.append(text[i])
        i += 1
    return "".join(out)


def _split_code_blocks(text: str) -> tuple[str, str]:
    """Return (markup, code) — `@{ … }` explicit code blocks pulled out of the markup."""
    code_parts: list[str] = []
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        if text[i] == "@" and i + 1 < n and text[i + 1] == "{":
            depth = 0
            j = i + 1
            while j < n:
                if text[j] == "{":
                    depth += 1
                elif text[j] == "}":
                    depth -= 1
                    if depth == 0:
                        j += 1
                        break
                j += 1
            code_parts.append(text[i:j])
            i = j
            continue
        out.append(text[i])
        i += 1
    return "".join(out), "\n".join(code_parts)


def _is_literal(fragment: str) -> bool:
    """Does this residual fragment read as user-facing prose?"""
    fragment = HTML_ENTITY.sub(" ", fragment).strip()
    if not fragment:
        return False
    words = WORDLIKE.findall(fragment)
    if not words:
        return False
    return any(w.lower() not in CODE_NOISE for w in words)


def _literal_text_nodes(markup: str) -> list[str]:
    """Literal user-facing text nodes left after tags and Razor expressions come out."""
    markup = _strip_balanced(markup, "@(", "(", ")")  # `@(…)` explicit expressions
    markup = TAG.sub("\n", markup)
    markup = CONTROL_FLOW.sub("\n", markup)
    # `@Localizer["Key"]` / `@Model.Foo` and any `[...]`/`(...)` they carry.
    markup = re.sub(r"@[A-Za-z_][\w.]*(?:\s*\[[^\]]*\])?(?:\s*\([^()]*\))?", "\n", markup)
    markup = RAZOR_EXPR.sub("\n", markup)
    found = []
    for raw in re.split(r"[\n{}]", markup):
        # `@:` literal-line prefix, and the punctuation that separates real text from code.
        frag = raw.replace("@:", "").strip(" \t;,|)(")
        if _is_literal(frag):
            found.append(" ".join(frag.split()))
    return found


def _literal_attrs(markup: str) -> list[str]:
    found = []
    for m in ATTR_RE.finditer(markup):
        value = m.group(3) if m.group(3) is not None else m.group(4)
        if LOCALIZED.search(value):
            continue
        if _is_literal(RAZOR_EXPR.sub(" ", value)):
            found.append(f"{m.group(1)}={value.strip()}")
    for m in VALUE_ATTR_RE.finditer(markup):
        value = m.group(1)
        if LOCALIZED.search(value):
            continue
        if _is_literal(RAZOR_EXPR.sub(" ", value)):
            found.append(f"value={value.strip()}")
    return found


def _literal_titles(code: str) -> list[str]:
    found = []
    for m in VIEWDATA_TITLE_RE.finditer(code):
        rhs = m.group(1).strip()
        if LOCALIZED.search(rhs):
            continue
        if re.match(r'^\s*"', rhs):
            found.append(f'ViewData["Title"]={rhs.rstrip(";")}')
    return found


def scan_view(path: Path) -> dict:
    raw = path.read_text(encoding="utf-8", errors="replace")
    live = HTML_COMMENT.sub(" ", RAZOR_COMMENT.sub(" ", raw))
    # A localized string counts wherever it is written, `<script>` and `@functions` included.
    localized = len(LOCALIZED.findall(live))

    markup = DIRECTIVE.sub("", SCRIPT_STYLE.sub(" ", live))
    for pure_code in ("@functions", "@code"):
        markup = _strip_balanced(markup, pure_code, "{", "}")
    markup, code = _split_code_blocks(markup)

    literals = _literal_text_nodes(markup) + _literal_attrs(markup) + _literal_titles(code)
    total = localized + len(literals)
    return {
        "literals": len(literals),
        "localized": localized,
        "coverage": (localized / total) if total else 1.0,
        "lines": raw.count("\n") + 1,
        "worst": literals[:5],
    }


# --- routes -------------------------------------------------------------------------------
ROUTE_ATTR = re.compile(r'\[\s*Route\s*\(\s*"([^"]*)"')
EXEMPT_PREFIXES = ("Admin/", "TeamAdmin/")
EXEMPT_EXACT = ("Admin", "TeamAdmin", "Shifts/Dashboard")
ADMIN_SEGMENT = {"admin", "teamadmin"}


def controller_routes(section_dir: Path) -> dict[str, str]:
    """`ControllerName` (without suffix) -> route template, from `[Route("…")]`."""
    routes: dict[str, str] = {}
    for cs in section_dir.rglob("*Controller.cs"):
        text = cs.read_text(encoding="utf-8", errors="replace")
        m = ROUTE_ATTR.search(text)
        if m:
            routes[cs.stem[: -len("Controller")]] = m.group(1).strip("/")
    return routes


def route_for(view: Path, views_root: Path, routes: dict[str, str]) -> str:
    rel = view.relative_to(views_root)
    parts = list(rel.parts[:-1])
    if not parts:
        return f"(shared)/{rel.name}"
    if parts[0] == "Shared":
        return f"(component) {'/'.join(parts)}"
    # A view folder is not always its controller's name: `Views/Admin/Agent/Settings.cshtml`
    # is served by `AdminAgentController` (`[Route("Agent/Admin")]`), not `AgentController`.
    # Try the folder path as a controller name most-specific first, then its segments.
    for candidate in ("".join(parts), "".join(reversed(parts)), parts[-1], parts[0]):
        if candidate in routes:
            return f"{routes[candidate]}/{view.stem}"
    return f"{'/'.join(parts)}/{view.stem}"


def bucket_for(route: str) -> str:
    if route.startswith("(component)") or route.startswith("(shared)"):
        return "member-facing"
    clean = route.strip("/")
    for exact in EXEMPT_EXACT:
        if clean == exact or clean.startswith(exact + "/"):
            return "exempt"
    if any(clean.startswith(p) for p in EXEMPT_PREFIXES):
        return "exempt"
    if any(seg.lower() in ADMIN_SEGMENT for seg in clean.split("/")[:-1]):
        return "admin-route"
    return "member-facing"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--section", required=True, help="section name, e.g. Expenses")
    ap.add_argument("--root", default=".", help="repo root (default: cwd)")
    ap.add_argument(
        "--min-literals",
        type=int,
        default=1,
        help="omit views with fewer literal strings than this (default 1)",
    )
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    section_dir = root / "src" / "Sections" / f"Humans.{args.section}"
    views_root = section_dir / "Views"
    if not views_root.is_dir():
        print(f"loc-coverage: no Views/ under {section_dir.relative_to(root)}", file=sys.stderr)
        return 3

    routes = controller_routes(section_dir)
    rows = []
    for view in sorted(views_root.rglob("*.cshtml")):
        if view.name.startswith("_"):  # _ViewImports, _ViewStart, layout fragments
            continue
        stats = scan_view(view)
        route = route_for(view, views_root, routes)
        rows.append(
            {
                "path": str(view.relative_to(root)),
                "route": route,
                "bucket": bucket_for(route),
                **stats,
            }
        )

    if args.json:
        print(json.dumps(rows, indent=2))
        return 0

    def table(title: str, subset: list[dict], note: str) -> None:
        print(f"\n### {title}")
        print(f"_{note}_\n")
        if not subset:
            print("(none)")
            return
        print("| Coverage | Literal | Localized | Route | View |")
        print("|---|---|---|---|---|")
        for r in subset:
            print(
                f"| {r['coverage']:.0%} | {r['literals']} | {r['localized']} "
                f"| `{r['route']}` | `{r['path']}` |"
            )

    ranked = sorted(
        (r for r in rows if r["bucket"] == "member-facing" and r["literals"] >= args.min_literals),
        key=lambda r: (r["coverage"], -r["literals"]),
    )
    admin = sorted(
        (r for r in rows if r["bucket"] == "admin-route" and r["literals"] >= args.min_literals),
        key=lambda r: (r["coverage"], -r["literals"]),
    )
    exempt = [r for r in rows if r["bucket"] == "exempt"]

    print(f"## Localization coverage — {args.section}")
    table(
        "Member-facing views, worst first",
        ranked,
        "the run's work list. Report the count; backfill only if the run is for that.",
    )
    table(
        "Admin-route views",
        admin,
        "an `Admin`/`TeamAdmin` route segment — reported, not ranked "
        "(`memory/code/localization-admin-exempt.md`).",
    )
    print(
        f"\n{len(exempt)} view(s) exempt by route "
        f"(`Admin/*`, `TeamAdmin/*`, `Shifts/Dashboard`); "
        f"{len(rows)} view(s) scanned."
    )
    if ranked:
        print("\nWorst offender's first literals:")
        for lit in ranked[0]["worst"]:
            print(f"  - {lit}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
