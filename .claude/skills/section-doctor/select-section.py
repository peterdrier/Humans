#!/usr/bin/env python3
"""Section selection for a section-doctor run (Phase 2).

Usage: python select-section.py --prs <open-prs.json> [--blocked-only]

Computes everything mechanical about selection: blocked set, pool, feature-active
down-rank, and the changed-since-last-run ranking. Prints SECTION:/BASE:/RATIONALE:.
Exit 2 = NOTHING CHANGED (every eligible section is untouched since its last run);
exit 3 = ALL BLOCKED. No build and no reforge: the ranking needs only git and a line count.

<open-prs.json> is the open-PR list as JSON: [{number, headRefName, title}, ...].
Locally that is one `gh pr list --repo peterdrier/Humans --state open --limit 200
--json number,headRefName,title` call; a cloud session without gh writes the same
shape from its GitHub MCP tools. Each PR's changed files come from git, never the
API: `refs/pull/<n>/head` is fetched and diffed against origin/main (the API's file
list carries patch bodies and silently caps at 100 files). A fetch that fails stops
the selector: it cannot see in-flight work, so it must not pick.

Ranking: age of the last run in days plus the share of the section rewritten since
it (CHURN_DAYS_PER_PERCENT days per percent of LOC changed), so a heavy recent rewrite
can outrank an older quiet run. A section with no run yet ranks from the commit that
created it, the whole section as churn, once it is NEW_SECTION_COOLDOWN_DAYS old — a
section doctored the week it lands is reviewed mid-flight.

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
CHURN_DAYS_PER_PERCENT = 1.0   # one percent of a section's LOC changed since its last run == one day of age
NEW_SECTION_COOLDOWN_DAYS = 7
RUN_FILE_RE = re.compile(r"docs/health/runs/\d{4}-\d{2}-\d{2}-([A-Za-z0-9]+)")


def pr_files(pr):
    """Changed files of an open PR, from origin/pr/<n> (fetched by fetch_pr_heads)."""
    rc, out = run(["git", "diff", "--name-only", "origin/main...origin/pr/%s" % pr.get("number")])
    if rc != 0:
        sys.exit("cannot diff origin/pr/%s -- selection would be blind to in-flight work; stop" % pr.get("number"))
    return out.split()


def fetch_pr_heads(prs):
    """A selector that cannot see the open PRs' files cannot build the blocked set or the
    feature-active down-rank, so a failed fetch stops the run rather than selecting blind."""
    if not prs:
        return
    refs = ["+refs/pull/%s/head:refs/remotes/origin/pr/%s" % (pr["number"], pr["number"]) for pr in prs]
    rc, out = run(["git", "fetch", "--quiet", "origin"] + refs)
    if rc != 0:
        sys.exit("fetching refs/pull/*/head failed -- selection would be blind to in-flight work; stop\n" + out.strip())


def path_section(path):
    m = re.match(r"src/Sections/Humans\.([A-Za-z0-9]+?)(?:\.Contracts)?/", path.replace("\\", "/"))
    return m.group(1) if m else None


def section_paths(s):
    """Everything Phase 3a inventories for the section."""
    return ["src/Sections/Humans." + s, "src/Sections/Humans." + s + ".Contracts",
            "tests/Humans." + s + ".Tests", "docs/guide/" + s + ".md"]


def run(cmd):
    try:
        p = subprocess.run(cmd, cwd=REPO_ROOT, capture_output=True, text=True)
    except OSError as e:
        return 127, str(e)
    return p.returncode, p.stdout + p.stderr


def section_loc(s):
    """Non-blank .cs/.cshtml lines of the section and its Contracts leaf -- the churn ratio's denominator."""
    loc = 0
    for suffix in ("", ".Contracts"):
        for root, dirs, files in os.walk(os.path.join(SECTIONS_DIR, "Humans." + s + suffix)):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
            for f in files:
                if f.endswith((".cs", ".cshtml")):
                    with open(os.path.join(root, f), encoding="utf-8", errors="ignore") as fh:
                        loc += sum(1 for line in fh if line.strip())
    return loc


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
    """(unix-time, sha, new?) of the origin/main commit that added the section's newest run file --
    a doctor run is exactly that; any other edit to health.md is not one. Falls back to the
    last commit to health.md, then (new=True) to the parent of the commit that created the
    section, so the whole section counts as churn."""
    queries = (
        ["-1", "--diff-filter=A", "origin/main", "--",
         "docs/health/runs/????-??-??-%s.md" % s, "docs/health/runs/????-??-??-%s-*.md" % s],
        ["-1", "origin/main", "--", "src/Sections/Humans.%s/Docs/health.md" % s],
        ["--reverse", "--diff-filter=A", "origin/main", "--", "src/Sections/Humans.%s" % s],
    )
    for i, q in enumerate(queries):
        rc, out = run(["git", "log", "--format=%ct %H"] + q)
        if rc == 0 and out.strip():
            t, sha = out.strip().splitlines()[0].split()
            return int(t), sha + ("^" if i == 2 else ""), i == 2
    return None


def churn_since(sha, s):
    """(any, code): lines added+deleted on origin/main since sha across everything the run
    inventories (any; 0 = unchanged, a binary file counts as one line), and within the
    .cs/.cshtml files of the section and its Contracts leaf (code) -- the scope LOC is measured
    over, so the churn ratio compares like with like."""
    rc, out = run(["git", "diff", "--numstat", sha + "..origin/main", "--"] + section_paths(s))
    if rc != 0:
        return 0, 0
    total, code = 0, 0
    code_roots = tuple(section_paths(s)[:2])
    for line in out.splitlines():
        parts = line.split("\t")
        if len(parts) != 3:
            continue
        n = int(parts[0]) + int(parts[1]) if parts[0].isdigit() and parts[1].isdigit() else 1
        total += n
        if parts[2].startswith(code_roots) and parts[2].endswith((".cs", ".cshtml")):
            code += n
    return total, code


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--prs", required=True)
    ap.add_argument("--blocked-only", action="store_true", help="print the blocked set and exit (for --section runs)")
    args = ap.parse_args()

    with open(args.prs, encoding="utf-8") as f:
        prs = json.load(f)

    # Every rank below is a git log over origin/main; a shallow clone (cloud containers) sees the
    # boundary commit as the birth of every file and would cool-down or drop the oldest sections.
    rc, out = run(["git", "rev-parse", "--is-shallow-repository"])
    if out.strip() == "true":
        rc, out = run(["git", "fetch", "--quiet", "--unshallow", "origin", "main"])
        if rc != 0:
            sys.exit("shallow clone and `git fetch --unshallow origin main` failed -- ranking would be wrong; stop\n" + out.strip())

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
    fetch_pr_heads(prs)
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

    doctored = {s: last_doctored(s) for s in eligible}
    now_t = time.time()
    cooling = sorted(s for s in eligible if doctored[s] and doctored[s][2]
                     and now_t - doctored[s][0] < NEW_SECTION_COOLDOWN_DAYS * 86400)
    ranked_pool = [s for s in eligible if doctored[s] and s not in cooling]
    # Eligible only if the section changed since its last run merged (a new section: since it
    # landed); ranked by age of that run plus how much of the section was rewritten since.
    churned = {s: churn_since(doctored[s][1], s) for s in ranked_pool}
    churn = {s: churned[s][0] for s in churned}          # any change: eligibility
    code_churn = {s: churned[s][1] for s in churned}     # code change: the ratio
    loc = {s: section_loc(s) for s in ranked_pool}

    def priority(s):
        age_days = (now_t - doctored[s][0]) / 86400
        return age_days + CHURN_DAYS_PER_PERCENT * 100.0 * code_churn[s] / max(loc[s], 1)

    stale = sorted((s for s in churn if churn[s]), key=lambda s: -priority(s))

    for w in warnings:
        print("WARNING: " + w)
    print("pool=%d blocked=%s feature-active=%s cooling-down=%s"
          % (len(pool), blocked_text(), ", ".join(active) or "none", ", ".join(cooling) or "none"))
    for s in sorted(ranked_pool, key=lambda s: (s not in stale, -priority(s) if s in stale else 0)):
        print("  %-24s loc=%-7d%s last-run=%s%s%s" % (
            s, loc[s], " [feature-active]" if s in active else "",
            "new " if doctored[s][2] else "", time.strftime("%Y-%m-%d", time.gmtime(doctored[s][0])),
            " churn=%d priority=%.0f" % (churn[s], priority(s)) if s in stale else " unchanged"))

    def first_pick(ordered):
        return next((s for s in ordered if s not in active), ordered[0])

    if stale:
        pick = first_pick(stale)
        print("\nSECTION: %s" % pick)
        print("BASE: %s" % doctored[pick][1])
        print("RATIONALE: highest age-plus-churn priority among sections changed since their last run "
              "merged (%s %s, %d lines churned); Phase 3 diffs against BASE."
              % ("landed" if doctored[pick][2] else "last run",
                 time.strftime("%Y-%m-%d", time.gmtime(doctored[pick][0])), churn[pick]))
    else:
        print("\nNOTHING CHANGED: every eligible section is unchanged since its last run.")
        return 2

    # Non-binding forecast: the next 4 picks if nothing else changes (each pick blocks itself).
    # Purely informational for the PR body; never stored, and later runs recompute from scratch.
    upcoming = [s for s in stale if s != pick][:4]
    print("UPCOMING: %s" % (", ".join(upcoming) or "none -- nothing else changed since its last run"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
