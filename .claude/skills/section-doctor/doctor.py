#!/usr/bin/env python3
"""Shell mechanics of a section-doctor run, in one place (SKILL.md tooling table).

    doctor.py rundir [--ts TS]          print the run's scratch dir (created); TS from the branch by default
    doctor.py mark <phase-id> <label…>  append a timestamped phase-log line for cost-report.py
    doctor.py push                      origin gate, then push the current branch
    doctor.py commit -F FILE | -m MSG   prose gate over the staged diff, logged to gates.log, then git commit
    doctor.py prose-gate [--base REF]   count-in-prose gate over the staged diff (or REF..HEAD)
    doctor.py dispatch-log <thread> <model> [agent-type]   record a subagent dispatch
    doctor.py resolve-check <sha>       the commit exists and is on origin/<current branch>

Every subcommand derives the run's identity from the branch (`section-doctor/<TS>`), so nothing
depends on shell state surviving between tool calls. Non-zero exit means "stop and look".
"""
import argparse
import os
import re
import subprocess
import sys
from datetime import datetime, timezone

ORIGIN_RE = re.compile(r"github\.com[:/]peterdrier/Humans(\.git)?$")
BRANCH_RE = re.compile(r"^section-doctor/(.+)$")
WORDS = {w: i + 1 for i, w in enumerate(
    "one two three four five six seven eight nine ten eleven twelve".split())}
NUM = r"(?:[0-9]+|" + "|".join(WORDS) + ")"
# Must-fix: a count with a structural tell — it names the rows under it, or it is a total.
STRUCTURAL_RE = re.compile(
    r"(?:"
    r"\(\s*[0-9]+\s*\)"                               # "Routes (3)"
    r"|\b(?:total|count|n)\s*[:=]\s*[0-9]+\b"          # "Total: 3", "count = 3"
    r"|\|\s*(?:total|count)\s*\|\s*[0-9]+\s*\|"      # markdown total row
    r")",
    re.IGNORECASE,
)
HEADING_RE = re.compile(r"^#+\s")
# Advisory: any numeral followed by a plural within three words — most prose about code.
LOOSE_RE = re.compile(rf"\b({NUM})\s+(?:[a-z-]+\s+){{0,2}}[a-z-]+s\b", re.IGNORECASE)
TABLE_ROW_RE = re.compile(r"^\|(?!\s*-)")     # a table row that is not the |---| separator
BULLET_RE = re.compile(r"^\s*(?:[-*+]|[0-9]+[.)])\s")
PROSE_SUFFIXES = (".md", ".yml", ".yaml", ".txt", ".cshtml", ".resx")
COMMENT_RE = re.compile(r"^\s*(?://|/\*|\*)")


def git(*args, check=True):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=check).stdout.strip()


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def branch():
    return git("rev-parse", "--abbrev-ref", "HEAD")


def rundir(ts=None):
    if ts is None:
        m = BRANCH_RE.match(branch())
        if not m:
            sys.exit(f"not on a section-doctor/<TS> branch ({branch()}); pass --ts")
        ts = m.group(1)
    path = os.path.join(os.environ.get("TMPDIR", "/tmp"), "section-doctor", ts)
    os.makedirs(os.path.join(path, "assessment"), exist_ok=True)
    return path


def cmd_rundir(a):
    print(rundir(a.ts))


def cmd_mark(a):
    label = " ".join(a.label)
    with open(os.path.join(rundir(), "phase-log"), "a", encoding="utf-8") as f:
        f.write(f"{now()} {a.phase} {label}\n")


def origin_gate():
    url = git("remote", "get-url", "origin")
    if not ORIGIN_RE.search(url):
        sys.exit(f"origin is {url}, not peterdrier/Humans — stop, raise in Needs Peter")


def cmd_push(a):
    origin_gate()
    b = branch()
    subprocess.run(["git", "push", "-u", "origin", b], check=True)


def _num(tok):
    return int(tok) if tok.isdigit() else WORDS[tok.lower()]


def _rows_under(lines, i):
    """Rows of the table or list that follows line i (blank lines between skipped), else 0."""
    j = i + 1
    while j < len(lines) and not lines[j].strip():
        j += 1
    if j >= len(lines):
        return 0
    if lines[j].startswith("|"):
        n = 0
        while j < len(lines) and lines[j].startswith("|"):
            n += TABLE_ROW_RE.match(lines[j]) is not None
            j += 1
        return max(n - 1, 0)  # minus the header row
    if BULLET_RE.match(lines[j]):
        n = 0
        while j < len(lines) and BULLET_RE.match(lines[j]):
            n += 1
            j += 1
        return n
    return 0


HUNK_RE = re.compile(r"^@@ -\S+ \+([0-9]+)")


def prose_gate_hits(diff, read_file):
    """(must_fix, advisory) lists of `path: line`. Prose files only; in .cs only comment
    lines; nothing under tests/ (assertion literals are the thing pinned, not a count).
    The rows under a count are read from the resulting file (`read_file(path)`), never from
    the diff — a -U0 diff drops the unchanged rows a newly typed count sits above."""
    must, advisory = [], []
    path, added = None, []  # added: (new-file line number, text)

    def flush():
        if path is None:
            return
        cs = path.endswith(".cs")
        if not (cs or path.endswith(PROSE_SUFFIXES)) or path.startswith("tests/"):
            return
        file_lines = None
        for lineno, l in added:
            if cs and not COMMENT_RE.match(l):
                continue
            tag = f"{path}: {l}"
            if STRUCTURAL_RE.search(l):
                must.append(tag)
                continue
            m = LOOSE_RE.search(l)
            if not m:
                continue
            if file_lines is None:
                file_lines = read_file(path).splitlines()
            rows = _rows_under(file_lines, lineno - 1)
            counted = (rows and _num(m.group(1)) == rows) or HEADING_RE.match(l)
            (must if counted else advisory).append(tag)

    lineno = 0
    for l in diff.splitlines():
        if l.startswith("+++ "):
            flush()
            path, added = l[4:].removeprefix("b/"), []
        elif l.startswith("@@"):
            lineno = int(HUNK_RE.match(l).group(1))
        elif l.startswith("+") and not l.startswith("+++"):
            added.append((lineno, l[1:]))
            lineno += 1
        elif not l.startswith("-"):
            lineno += 1
    flush()
    return must, advisory


def staged_file(path):
    return git("show", f":{path}")


def worktree_file(path):
    with open(path, encoding="utf-8", errors="ignore") as f:
        return f.read()


def prose_gate(diff_args, read_file):
    must, advisory = prose_gate_hits(git("diff", "-U0", *diff_args), read_file)
    if advisory:
        print("advisory (a numeral near a plural — read once, no action required):")
        for h in advisory:
            print("  " + h)
    for h in must:
        print(h)
    if must:
        sys.exit(f"prose gate: {len(must)} line(s) type a count of the list under them or a total — delete or list")
    print("prose gate: clean")


def cmd_prose_gate(a):
    if a.base is None:
        prose_gate(["--cached"], staged_file)
    else:
        prose_gate([a.base], worktree_file)


def cmd_commit(a):
    """The gate runs inside the commit so a run cannot skip it; gates.log records each run
    (its own file — a line in the phase log would become a cost-report row)."""
    must, advisory = prose_gate_hits(git("diff", "-U0", "--cached"), staged_file)
    with open(os.path.join(rundir(), "gates.log"), "a", encoding="utf-8") as f:
        f.write(f"{now()} prose-gate must-fix={len(must)} advisory={len(advisory)}\n")
    prose_gate(["--cached"], staged_file)
    msg = ["-F", a.file] if a.file else ["-m", a.message]
    subprocess.run(["git", "commit", *msg], check=True)


def cmd_dispatch_log(a):
    path = os.path.join(rundir(), "assessment", "threads.md")
    new = not os.path.exists(path)
    with open(path, "a", encoding="utf-8") as f:
        if new:
            f.write("| Thread | Model | Agent type | Dispatched |\n|---|---|---|---|\n")
        f.write(f"| {a.thread} | {a.model} | {a.agent_type or '-'} | {now()} |\n")


def cmd_resolve_check(a):
    b = branch()
    if subprocess.run(["git", "cat-file", "-e", a.sha]).returncode != 0:
        sys.exit(f"{a.sha}: no such commit")
    containing = git("branch", "-r", "--contains", a.sha).split()
    if f"origin/{b}" not in containing:
        sys.exit(f"{a.sha} is not on origin/{b} — push first")
    print(f"{a.sha} is on origin/{b}")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("rundir"); s.add_argument("--ts"); s.set_defaults(fn=cmd_rundir)
    s = sub.add_parser("mark"); s.add_argument("phase"); s.add_argument("label", nargs="+"); s.set_defaults(fn=cmd_mark)
    s = sub.add_parser("push"); s.set_defaults(fn=cmd_push)
    s = sub.add_parser("prose-gate"); s.add_argument("--base"); s.set_defaults(fn=cmd_prose_gate)
    s = sub.add_parser("commit"); g = s.add_mutually_exclusive_group(required=True)
    g.add_argument("-F", dest="file"); g.add_argument("-m", dest="message"); s.set_defaults(fn=cmd_commit)
    s = sub.add_parser("dispatch-log"); s.add_argument("thread"); s.add_argument("model")
    s.add_argument("agent_type", nargs="?"); s.set_defaults(fn=cmd_dispatch_log)
    s = sub.add_parser("resolve-check"); s.add_argument("sha"); s.set_defaults(fn=cmd_resolve_check)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
