#!/usr/bin/env python3
"""Section selection for a section-doctor run (Phase 2).

Usage: python select-section.py --prs <open-prs.json> [--no-build] [--blocked-only]

Computes everything mechanical about selection: blocked set, pool, feature-active
down-rank, tiers, reforge scores, the middle-out median pick for the never-doctored
tier, and the changed-since-last-run rule for the re-doctor tier. Prints
SECTION:/TIER:/RATIONALE: (plus BASE: for a re-doctor). Exit 2 = NOTHING CHANGED
(every eligible section was doctored and untouched since); exit 3 = ALL BLOCKED.

<open-prs.json> is the open-PR list as JSON: [{number, headRefName, title,
files: ["path", ...] | [{"path": ...}, ...]}, ...]. Locally that is one
`gh pr list --repo peterdrier/Humans --state open --limit 200 --json
number,headRefName,title,files` call; a cloud session without gh writes the same
shape from its GitHub MCP tools.

The blocked set also reads `origin`'s `section-doctor/*` branches directly (git,
not gh): a run that has pushed its Phase 2 marker but not yet opened its PR is
visible only there. Branches whose tip is older than BRANCH_MAX_AGE_DAYS are
ignored as strays (a maintainer deletes them).
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
SECTIONS_DIR = os.path.join(REPO_ROOT, "src", "Sections")
BRANCH_MAX_AGE_DAYS = 3
RUN_FILE_RE = re.compile(r"docs/health/runs/\d{4}-\d{2}-\d{2}-([A-Za-z0-9]+)")


def pr_files(pr):
    return [f["path"] if isinstance(f, dict) else f for f in pr.get("files") or []]


def path_section(path):
    m = re.match(r"src/Sections/Humans\.([A-Za-z0-9]+?)(?:\.Contracts)?/", path.replace("\\", "/"))
    return m.group(1) if m else None


def section_paths(s):
    return ["src/Sections/Humans." + s, "src/Sections/Humans." + s + ".Contracts", "tests/Humans." + s + ".Tests"]


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


def branch_blocked(pr_heads, warnings):
    """{section: 'branch <name>'} for origin's recent section-doctor/* branches that have no open PR."""
    blocked = {}
    rc, out = run(["git", "ls-remote", "--heads", "origin", "refs/heads/section-doctor/*"])
    if rc != 0:
        warnings.append("git ls-remote failed -- blocked set built from open PRs only")
        return blocked
    stray = [line.split()[1][len("refs/heads/"):] for line in out.splitlines() if line.strip()]
    stray = [b for b in stray if b not in pr_heads]
    if not stray:
        return blocked
    run(["git", "fetch", "--quiet", "origin"] + ["+refs/heads/%s:refs/remotes/origin/%s" % (b, b) for b in stray])
    cutoff = time.time() - BRANCH_MAX_AGE_DAYS * 86400
    for b in stray:
        rc, when = run(["git", "log", "-1", "--format=%ct", "origin/" + b])
        if rc != 0 or not when.strip() or int(when.strip()) < cutoff:
            continue
        rc, diff = run(["git", "diff", "--name-only", "origin/main...origin/" + b])
        sections = set()
        for path in diff.splitlines():
            m = RUN_FILE_RE.match(path)
            if m:
                sections.add(m.group(1))
            s = path_section(path)
            if s:
                sections.add(s)
        for s in sorted(sections):
            blocked.setdefault(s, "branch " + b)
    return blocked


def last_doctored(s):
    """(unix-time, sha) of the last origin/main commit to the section's health.md, or None."""
    rc, out = run(["git", "log", "-1", "--format=%ct %H", "origin/main", "--",
                   "src/Sections/Humans.%s/Docs/health.md" % s])
    if rc != 0 or not out.strip():
        return None
    t, sha = out.split()
    return int(t), sha


def changed_since(sha, s):
    rc, out = run(["git", "rev-list", "--count", sha + "..origin/main", "--"] + section_paths(s))
    return rc == 0 and out.strip() not in ("", "0")


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

    # PR titles spell the section however the run wrote it (`doctor(expenses)`); the pool is
    # spelled from the directory (`Expenses`). Map titles onto the pool's spelling so the
    # membership test below actually excludes the section.
    canonical = {s.casefold(): s for s in pool}

    blocked, warnings = {}, []
    for pr in prs:
        if not (pr.get("headRefName") or "").startswith("section-doctor/"):
            continue
        m = re.match(r"doctor\(([^)]+)\)", pr.get("title") or "")
        if m:
            named = m.group(1).strip()
            section = canonical.get(named.casefold())
            if section is None:
                warnings.append("open run PR #%s names section %r, which is not a src/Sections project "
                                "-- blocking nothing" % (pr.get("number"), named))
                continue
            blocked[section] = "#%s" % pr["number"]
        else:
            touched = sorted({s for s in map(path_section, pr_files(pr)) if s})
            for s in touched:
                blocked.setdefault(s, "#%s" % pr["number"])
            warnings.append("cannot parse section from open run PR #%s title %r -- blocked %s from its changed files"
                            % (pr.get("number"), pr.get("title"), ", ".join(touched) or "nothing"))
    for s, why in branch_blocked({pr.get("headRefName") for pr in prs}, warnings).items():
        blocked.setdefault(s, why)

    def blocked_text():
        return ", ".join("%s (%s)" % kv for kv in sorted(blocked.items())) or "none"

    if args.blocked_only:
        print("blocked: " + blocked_text())
        for w in warnings:
            print("WARNING: " + w)
        return 0

    active = sorted({s for pr in prs if not (pr.get("headRefName") or "").startswith("section-doctor/")
                     for s in map(path_section, pr_files(pr)) if s})

    eligible = [s for s in pool if s not in blocked]
    if not eligible:
        print("ALL BLOCKED: every section has an open section-doctor PR or branch.")
        for s, n in sorted(blocked.items()):
            print("  %s -- %s" % (s, n))
        return 3

    build = "skipped (--no-build)"
    if not args.no_build:
        rc, _ = run(["dotnet", "build", "Humans.slnx", "-v", "quiet"])
        build = "ok" if rc == 0 else "FAILED -- reforge scores may under-report"

    scores, source = reforge_scores()
    if scores is None:
        scores, source = loc_fallback(eligible), "loc-fallback (reforge unavailable: %s)" % source
    else:
        # A section reforge prints no line for has nothing to report: score 0, not unknown.
        for s, (_, loc) in loc_fallback([s for s in eligible if s not in scores]).items():
            scores[s] = (0, loc)

    def score(s):
        return scores.get(s, (sys.maxsize, sys.maxsize))

    never = [s for s in eligible if not os.path.exists(os.path.join(SECTIONS_DIR, "Humans." + s, "Docs", "health.md"))]
    redoctor = [s for s in eligible if s not in never]
    doctored = {s: last_doctored(s) for s in redoctor}
    # Re-doctor tier: eligible only if the section changed since its last run merged;
    # ranked oldest run first, ties by lowest score.
    stale = sorted((s for s in redoctor if doctored[s] and changed_since(doctored[s][1], s)),
                   key=lambda s: (doctored[s][0], score(s)))

    print("select-section: build=%s, score source=%s" % (build, source))
    for w in warnings:
        print("WARNING: " + w)
    print("pool=%d blocked=%s feature-active=%s" % (len(pool), blocked_text(), ", ".join(active) or "none"))
    for label, tier in (("never-doctored", never), ("previously-doctored", redoctor)):
        if tier:
            print("tier %s (%d):" % (label, len(tier)))
            for s in sorted(tier, key=score):
                sc = scores.get(s)
                print("  %-24s %-24s%s%s" % (s,
                                             "score=%-6d loc=%-7d" % sc if sc else "score=n/a    loc=n/a",
                                             " [feature-active]" if s in active else "",
                                             (" last-run=%s%s" % (time.strftime("%Y-%m-%d", time.gmtime(doctored[s][0])),
                                                                  " changed" if s in stale else " unchanged"))
                                             if s in doctored and doctored[s] else ""))

    # Feature-active sections sink to the tier bottom: median over the rest, unless only they remain.
    def median_pick(tier):
        ranked = sorted((s for s in tier if s not in active), key=score) or sorted(tier, key=score)
        return ranked[(len(ranked) - 1) // 2]

    def first_pick(ordered):
        return next((s for s in ordered if s not in active), ordered[0])

    if never:
        pick = median_pick(never)
        print("\nSECTION: %s" % pick)
        print("TIER: never-doctored")
        print("RATIONALE: median (lower-middle) of %d never-doctored section(s) by %s after setting"
              % (len([s for s in never if s not in active]) or len(never),
                 "reforge score" if "fallback" not in source else "loc"))
        print("  aside %d feature-active; pool %d, blocked %d." % (len([s for s in never if s in active]), len(pool), len(blocked)))
    elif stale:
        pick = first_pick(stale)
        print("\nSECTION: %s" % pick)
        print("TIER: re-doctor")
        print("BASE: %s" % doctored[pick][1])
        print("RATIONALE: oldest previously-doctored section changed since its last run merged "
              "(last run %s); Phase 3 diffs against BASE." % time.strftime("%Y-%m-%d", time.gmtime(doctored[pick][0])))
    else:
        print("\nNOTHING CHANGED: every eligible section is previously-doctored and unchanged since its last run.")
        return 2

    # Non-binding forecast: the next 4 picks if nothing else changes (each pick blocks itself).
    # Purely informational for the PR body; never stored, and later runs recompute from scratch.
    upcoming, future = [], [s for s in never if s != pick]
    while len(upcoming) < 4 and future:
        nxt = median_pick(future)
        upcoming.append(nxt)
        future.remove(nxt)
    upcoming += [s for s in stale if s != pick and s not in upcoming][: 4 - len(upcoming)]
    print("UPCOMING: %s" % (", ".join(upcoming) or "none -- nothing else is never-doctored or changed"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
