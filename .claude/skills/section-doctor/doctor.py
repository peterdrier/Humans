#!/usr/bin/env python3
"""Shell mechanics of a section-doctor run, in one place (SKILL.md tooling table).

    doctor.py rundir [--ts TS]          print the run's scratch dir (created); TS from the branch by default
    doctor.py mark <phase-id> <label…>  append a timestamped phase-log line for cost-report.py
    doctor.py push                      origin gate, then push the current branch
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
COUNT_RE = re.compile(
    r"^\+.*\b([0-9]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+[a-z-]+s\b",
    re.IGNORECASE,
)


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


def cmd_prose_gate(a):
    diff_args = ["diff", "-U0"] + (["--cached"] if a.base is None else [a.base])
    hits = [l for l in git(*diff_args).splitlines() if COUNT_RE.search(l) and not l.startswith("+++")]
    for h in hits:
        print(h)
    if hits:
        sys.exit(f"prose gate: {len(hits)} line(s) carry a typed count — delete, list, or justify each")
    print("prose gate: clean")


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
    s = sub.add_parser("dispatch-log"); s.add_argument("thread"); s.add_argument("model")
    s.add_argument("agent_type", nargs="?"); s.set_defaults(fn=cmd_dispatch_log)
    s = sub.add_parser("resolve-check"); s.add_argument("sha"); s.set_defaults(fn=cmd_resolve_check)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
