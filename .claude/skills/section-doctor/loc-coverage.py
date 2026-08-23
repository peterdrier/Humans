#!/usr/bin/env python3
"""Localization coverage of a section's Razor views (nobodies-collective/Humans#1115).

`SectionResourceParityTests` covers the resx side — every key present in all six files —
and says nothing about user-facing view text that never became a key at all.
`LocalizationCoverageSweep` (tests/Humans.Integration.Tests/Localization/) is the
authoritative reading of *that* direction: it renders the app through a pseudo-localizer,
so unbracketed prose on a member page is a hard-coded string, and it classifies each
route's audience from the endpoint's real authorization metadata.

**Prefer the sweep.** This script exists for the three places it cannot reach:

  * **Routes with a required parameter.** The sweep never crawls them — they land in its
    `## Coverage gaps — parameterized routes` list. That gap is why #1115's own example
    leaked: eight of Expenses' eleven GET routes are `{id:guid}` routes, `/Expenses/{id}`
    among them.
  * **View components and partials**, rendered into a page and never crawled as one.
    Ninety `_*.cshtml` partials carry user-facing text that no route of their own reaches.
  * **A run with no Docker or no compiler**, where the sweep cannot run at all.

Plus the per-view ranking the sweep's per-route findings do not give: literal user-facing
strings against localized ones per `.cshtml`, worst coverage first.

Where both speak, the sweep wins — it renders, this parses Razor, and a heuristic over
markup mistakes dynamic data for prose in a way a bracket test does not.

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
# `@Localizer[...]`, `@SharedLocalizer[...]`, the `Localizer[...]` form inside an `@{ }`
# block, and the two `IStringLocalizer` extensions that resolve a key without an indexer
# (`EnumDisplay`/`EnumSelectItems` → `Enum_<Type>_<Value>`, per EnumLocalizationExtensions).
LOCALIZED = re.compile(
    r"\b[A-Za-z]*Localizer\s*(?:\[|\.\s*(?:EnumDisplay|EnumSelectItems)\s*\()"
)
# The whole call, for removing localized branches before reading an expression's literals.
LOCALIZED_CALL = re.compile(r"\b[A-Za-z]*Localizer\s*\[[^\]]*\]")
STRING_LITERAL = re.compile(r"\"([^\"\\]*)\"")

# --- user-facing strings a script emits ----------------------------------------------------
# `<script>` bodies are not scanned as markup, but these sinks put text on the page, and a
# localized string inside a script already counts toward the numerator — leaving these out
# would inflate coverage rather than merely miss it.
SCRIPT_SINKS = re.compile(
    r"(?:\b(?:alert|confirm|prompt)\s*\(\s*|"
    r"\.(?:textContent|innerText|innerHTML)\s*=\s*)"
    r"(['\"])(.*?)(?<!\\)\1",
    re.DOTALL,
)

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
CONTROL_FLOW_KEYWORDS = (
    "@else if", "@if", "@foreach", "@for", "@while", "@switch", "@lock", "@using", "@catch",
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
    "func", "action", "task",  # bare generic type names, left behind when `<…>` reads as a tag
}
# Razor switches to code context inside a control-flow body, so statements sit in what looks
# like a text node once the tags come out. `Expenses/Index.cshtml` had 21 "literals" and every
# one was code: `var net = …`, `.Column(…)`, `else if (…)`, a `//` comment, an `is { Length: > 60 }`
# pattern. Tracking the context properly needs a Razor parser; rejecting what reads as code is
# the cheaper half, and it is the precision that matters for a ranking.
CODE_PUNCT = re.compile(r"[;{}\[\]<>=]|//|/\*")
CODE_KEYWORD_START = re.compile(
    r"^(?:else|if|foreach|for|while|switch|case|try|catch|finally|do|using|lock"
    r"|var|return|await|new|public|private|internal|static)\b"
)


def _strip_balanced(text: str, opener: str, open_ch: str, close_ch: str) -> str:
    """Remove every `opener`-introduced balanced `open_ch`…`close_ch` region."""
    out = []
    i = 0
    n = len(text)
    while i < n:
        # A word opener (`@functions`) needs a boundary; a punctuation one (`@(`) does not.
        boundary = not opener[-1].isalpha() or not text[i + len(opener) : i + len(opener) + 1].isalnum()
        if text.startswith(opener, i) and boundary:
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
    if fragment.startswith(".") or CODE_PUNCT.search(fragment):
        return False
    if CODE_KEYWORD_START.match(fragment):
        return False
    words = WORDLIKE.findall(fragment)
    if not words:
        return False
    return any(w.lower() not in CODE_NOISE for w in words)


def _keep_expression_strings(text: str) -> str:
    """Reduce each `@(…)` expression to the string literals Razor renders out of it.

    Dropping the whole expression would lose a rendered literal: `@(isEdit ? "Edit Event" :
    "Submit an Event")` puts two untranslated headings on the page. Localized branches come
    out first, so `@(x ? Localizer["A"] : Localizer["B"])` contributes nothing.
    """
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        if text.startswith("@(", i):
            depth = 0
            j = i + 1
            while j < n:
                if text[j] == "(":
                    depth += 1
                elif text[j] == ")":
                    depth -= 1
                    if depth == 0:
                        j += 1
                        break
                j += 1
            expression = LOCALIZED_CALL.sub(" ", text[i:j])
            out.append("\n" + "\n".join(STRING_LITERAL.findall(expression)) + "\n")
            i = j
            continue
        out.append(text[i])
        i += 1
    return "".join(out)


def _literal_text_nodes(markup: str) -> list[str]:
    """Literal user-facing text nodes left after tags and Razor expressions come out."""
    markup = _keep_expression_strings(markup)
    markup = TAG.sub("\n", markup)
    for keyword in CONTROL_FLOW_KEYWORDS:  # the keyword *and* its condition
        markup = _strip_balanced(markup, keyword, "(", ")")
    markup = CONTROL_FLOW.sub("\n", markup)  # the ones with no condition (`@else`, `@try`)
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


def _literal_script_strings(scripts: str) -> list[str]:
    """Text a script puts on the page — `alert(…)`, `.textContent = …` and friends."""
    found = []
    for m in SCRIPT_SINKS.finditer(scripts):
        value = m.group(2)
        if LOCALIZED.search(value):
            continue
        if _is_literal(RAZOR_EXPR.sub(" ", value)):
            found.append(f"script: {' '.join(value.split())}")
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

    scripts = "\n".join(m.group(0) for m in SCRIPT_STYLE.finditer(live))
    markup = DIRECTIVE.sub("", SCRIPT_STYLE.sub(" ", live))
    for pure_code in ("@functions", "@code"):
        markup = _strip_balanced(markup, pure_code, "{", "}")
    markup, code = _split_code_blocks(markup)

    literals = (
        _literal_text_nodes(markup)
        + _literal_attrs(markup)
        + _literal_titles(code)
        + _literal_script_strings(scripts)
    )
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
# Exempt as a whole route or as a route prefix. Hand-synced with the list in
# `memory/code/localization-admin-exempt.md` — nothing here parses that file, so a route added
# there and not here goes on being ranked as member-facing. Change both in one commit.
EXEMPT_ROUTES = ("Admin", "TeamAdmin", "Shifts/Dashboard")
ADMIN_SEGMENT = {"admin", "teamadmin"}
INFRASTRUCTURE_VIEWS = {"_ViewImports", "_ViewStart"}


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
        # A component is identified by its folder (the file is always Default.cshtml); a
        # shared partial by its file name.
        kind = "component" if len(parts) > 1 and parts[1] == "Components" else "shared"
        label = "/".join(parts)
        if view.stem != "Default":
            label = f"{label}/{view.stem}"
        return f"({kind}) {label}"
    # A view folder is not always its controller's name: `Views/Admin/Agent/Settings.cshtml`
    # is served by `AdminAgentController` (`[Route("Agent/Admin")]`), not `AgentController`.
    # Try the folder path as a controller name most-specific first, then its segments.
    for candidate in ("".join(parts), "".join(reversed(parts)), parts[-1], parts[0]):
        if candidate in routes:
            return f"{routes[candidate]}/{view.stem}"
    return f"{'/'.join(parts)}/{view.stem}"


def bucket_for(route: str) -> str:
    if route.startswith(("(component)", "(shared)")):
        return "member-facing"
    clean = route.strip("/")
    if any(clean == r or clean.startswith(r + "/") for r in EXEMPT_ROUTES):
        return "exempt"
    segments = clean.split("/")
    if any(seg.lower() in ADMIN_SEGMENT for seg in segments):
        return "admin-route"
    # The view name is not the action's route template: `GovernanceApplicationsController`'s
    # `[HttpGet("Admin/{id:guid}")]` renders `AdminDetail.cshtml`, so the `Admin/` segment never
    # reaches the resolved route. Resolving a view to its own action needs the MVC action
    # descriptors — which `LocalizationCoverageSweep` has and a static pass does not — so take
    # the name as the signal it is.
    if segments and segments[-1].lower().startswith(tuple(ADMIN_SEGMENT)):
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
        if view.stem in INFRASTRUCTURE_VIEWS:
            continue
        stats = scan_view(view)
        route = route_for(view, views_root, routes)
        bucket = bucket_for(route)  # bucket by the parent route, before the partial marker
        rows.append(
            {
                "path": str(view.relative_to(root)),
                # A partial has no route of its own; it renders into the page at this one.
                # A shared/component label already says it is not a page.
                "route": f"(partial) {route}"
                if view.name.startswith("_") and not route.startswith("(")
                else route,
                "bucket": bucket,
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
