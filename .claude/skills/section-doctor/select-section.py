#!/usr/bin/env python3
"""Section selection for a section-doctor run (Phase 2).

Usage: python select-section.py --prs <open-prs.json> [--no-build] [--blocked-only]

Computes everything mechanical about selection: blocked set, pool, feature-active
down-rank, tiers, reforge scores, and the middle-out median pick. Prints
SECTION:/TIER:/RATIONALE: for the never-doctored era. Prints JUDGMENT REQUIRED
(exit 2) once every eligible section is previously-doctored — staleness ranking is
a judgment call the caller dispatches — and ALL BLOCKED (exit 3) when every
section has an open section-doctor PR.

<open-prs.json> is the open-PR list as JSON: [{number, headRefName, title,
files: ["path", ...] | [{"path": ...}, ...]}, ...]. Locally that is one
`gh pr list --repo peterdrier/Humans --state open --limit 200 --json
number,headRefName,title,files` call; a cloud session without gh writes the same
shape from its GitHub MCP tools. No git or gh is used here.
"""
import argparse
import json
import os
import re
import subprocess
import sys

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
SECTIONS_DIR = os.path.join(REPO_ROOT, "src", "Sections")


def pr_files(pr):
    return [f["path"] if isinstance(f, dict) else f for f in pr.get("files") or []]


def path_section(path):
    m = re.match(r"src/Sections/Humans\.([A-Za-z0-9]+?)(?:\.Contracts)?/", path.replace("\\", "/"))
    return m.group(1) if m else None


def run(cmd):
    try:
        p = subprocess.run(cmd, cwd=REPO_ROOT, capture_output=True, text=True)
    except OSError as e:
        return 127, str(e)
    return p.returncode, p.stdout + p.stderr


def reforge_scores():
    """{section: (score, loc)} from surface-score compact output, or None."""
    rc, out = run(["reforge", "surface-score", "--solution", "Humans.slnx", "--format", "compact"])
    partial = False
    if rc != 0:
        rc, out = run(["reforge", "surface-score", "--solution", "Humans.slnx", "--format", "compact", "--allow-degraded"])
        partial = True
    if rc != 0:
        return None, out.strip().splitlines()[-1] if out.strip() else "reforge failed with no output"
    scores = {}
    for m in re.finditer(r"^\s{2}(\S+)\s+(\d+)\s+loc=(\d+)\b", out, re.M):
        scores[m.group(1)] = (int(m.group(2)), int(m.group(3)))
    if not scores:
        return None, "no per-section score lines in reforge output"
    return scores, "PARTIAL (solution did not compile cleanly)" if partial else "clean"


def loc_fallback(sections):
    """{section: (loc-as-score, loc)} counting .cs/.cshtml lines -- proxy when reforge is unavailable."""
    scores = {}
    for s in sections:
        loc = 0
        for suffix in ("", ".Contracts"):
            for root, dirs, files in os.walk(os.path.join(SECTIONS_DIR, "Humans." + s + suffix)):
                dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
                for f in files:
                    if f.endswith((".cs", ".cshtml")):
                        with open(os.path.join(root, f), encoding="utf-8", errors="ignore") as fh:
                            loc += sum(1 for line in fh if line.strip())
        scores[s] = (loc, loc)
    return scores


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--prs", required=True)
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--blocked-only", action="store_true", help="print the blocked set and exit (for --section runs)")
    args = ap.parse_args()

    with open(args.prs, encoding="utf-8") as f:
        prs = json.load(f)

    pool = sorted(
        d[len("Humans."):]
        for d in os.listdir(SECTIONS_DIR)
        if os.path.isdir(os.path.join(SECTIONS_DIR, d)) and d.startswith("Humans.") and not d.endswith(".Contracts")
    )

    blocked, warnings = {}, []
    for pr in prs:
        if not (pr.get("headRefName") or "").startswith("section-doctor/"):
            continue
        m = re.match(r"doctor\(([^)]+)\)", pr.get("title") or "")
        if m:
            blocked[m.group(1).strip()] = pr["number"]
        else:
            touched = sorted({s for s in map(path_section, pr_files(pr)) if s})
            for s in touched:
                blocked.setdefault(s, pr["number"])
            warnings.append("cannot parse section from open run PR #%s title %r -- blocked %s from its changed files"
                            % (pr.get("number"), pr.get("title"), ", ".join(touched) or "nothing"))

    if args.blocked_only:
        print("blocked: " + (", ".join("%s (#%s)" % kv for kv in sorted(blocked.items())) or "none"))
        for w in warnings:
            print("WARNING: " + w)
        return 0

    active = sorted({s for pr in prs if not (pr.get("headRefName") or "").startswith("section-doctor/")
                     for s in map(path_section, pr_files(pr)) if s})

    eligible = [s for s in pool if s not in blocked]
    if not eligible:
        print("ALL BLOCKED: every section has an open section-doctor PR.")
        for s, n in sorted(blocked.items()):
            print("  %s -- open PR #%s" % (s, n))
        return 3

    build = "skipped (--no-build)"
    if not args.no_build:
        rc, _ = run(["dotnet", "build", "Humans.slnx", "-v", "quiet"])
        build = "ok" if rc == 0 else "FAILED -- reforge scores may under-report"

    scores, source = reforge_scores()
    if scores is None:
        scores, source = loc_fallback(eligible), "loc-fallback (reforge unavailable: %s)" % source

    def score(s):
        return scores.get(s, (sys.maxsize, sys.maxsize))  # unscored sorts last

    never = [s for s in eligible if not os.path.exists(os.path.join(SECTIONS_DIR, "Humans." + s, "Docs", "health.md"))]
    redoctor = [s for s in eligible if s not in never]

    print("select-section: build=%s, score source=%s" % (build, source))
    for w in warnings:
        print("WARNING: " + w)
    print("pool=%d blocked=%s feature-active=%s"
          % (len(pool),
             ", ".join("%s (#%s)" % kv for kv in sorted(blocked.items())) or "none",
             ", ".join(active) or "none"))
    for label, tier in (("never-doctored", never), ("previously-doctored", redoctor)):
        if tier:
            print("tier %s (%d):" % (label, len(tier)))
            for s in sorted(tier, key=score):
                sc = scores.get(s)
                print("  %-24s %-24s%s" % (s,
                                           "score=%-6d loc=%-7d" % sc if sc else "score=n/a    loc=n/a",
                                           " [feature-active]" if s in active else ""))
    unscored = [s for s in eligible if s not in scores]
    if unscored:
        print("WARNING: no score for %s -- ranked last" % ", ".join(unscored))

    if not never:
        print("\nJUDGMENT REQUIRED: every eligible section is previously-doctored -- rank by staleness")
        print("and change volume per the skill's Phase 2 step 5 (judgment call, no formula).")
        return 2

    # Feature-active sections sink to the tier bottom: median over the rest, unless only they remain.
    ranked = sorted((s for s in never if s not in active), key=score) or sorted(never, key=score)
    pick = ranked[(len(ranked) - 1) // 2]
    print("\nSECTION: %s" % pick)
    print("TIER: never-doctored")
    print("RATIONALE: median (lower-middle) of %d never-doctored section(s) by %s after setting"
          % (len(ranked), "reforge score" if "fallback" not in source else "loc"))
    print("  aside %d feature-active; pool %d, blocked %d." % (len([s for s in never if s in active]), len(pool), len(blocked)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
