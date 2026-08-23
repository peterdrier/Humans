#!/usr/bin/env python3
"""Cost report for a section-doctor run (Phase 7).

Usage: python cost-report.py <branch-name> <phase-log-path>

Finds this run's own session transcript under ~/.claude/projects (the file that
mentions the run branch and was modified after the run started), sums per-API-call
token usage bucketed by the phase boundaries in the phase log, adds one row per
subagent transcript (named by its `thread:` marker where it has one), and prints a
markdown table with per-row model and API-equivalent cost.

Exits 0 with "Cost: unmeasured (...)" on any discovery failure — never fail the run.
"""
import glob
import json
import os
import re
import sys
from datetime import datetime

# $/MTok: fresh input, output, cache write, cache read (API list rates)
RATES = {
    "fable": (10, 50, 12.50, 1.00),
    "mythos": (10, 50, 12.50, 1.00),
    "opus": (5, 25, 6.25, 0.50),
    "sonnet": (3, 15, 3.75, 0.30),
    "haiku": (1, 5, 1.25, 0.10),
}


def rate(model):
    for key, r in RATES.items():
        if key in (model or ""):
            return r
    return RATES["opus"]


def ts(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00")).timestamp()


def usage_entries(path):
    for line in open(path, encoding="utf-8", errors="ignore"):
        try:
            j = json.loads(line)
        except ValueError:
            continue
        u = (j.get("message") or {}).get("usage")
        if u:
            yield j.get("timestamp"), (j.get("message") or {}).get("model"), u


def thread_name(path):
    """The `thread: <Name>` marker a dispatched Phase 3d prompt opens with (SKILL.md §3d)."""
    with open(path, encoding="utf-8", errors="ignore") as f:
        for line in list(f)[:3]:  # the prompt is the first record; don't match a later mention
            m = re.search(r'thread:\s*([A-Za-z][A-Za-z &]*)', line)
            if m:
                return m.group(1).strip()
    return None


def add(bucket, model, u):
    r = rate(model)
    for key in RATES:
        if key in (model or ""):
            bucket.setdefault("models", set()).add(key)
    fresh = u.get("input_tokens", 0)
    out = u.get("output_tokens", 0)
    cw = u.get("cache_creation_input_tokens", 0)
    cr = u.get("cache_read_input_tokens", 0)
    b = bucket.setdefault("tok", [0, 0, 0, 0])
    for i, v in enumerate((fresh, out, cw, cr)):
        b[i] += v
    bucket["usd"] = bucket.get("usd", 0) + (
        fresh * r[0] + out * r[1] + cw * r[2] + cr * r[3]
    ) / 1e6


def main():
    branch, phase_log = sys.argv[1], sys.argv[2]
    phases = []  # (start_ts, name)
    for line in open(phase_log, encoding="utf-8"):
        t, name = line.split(None, 1)
        phases.append((ts(t), name.strip()))
    run_start = phases[0][0]

    own = None
    for p in glob.glob(os.path.expanduser("~/.claude/projects/*/*.jsonl")):
        try:
            if os.path.getmtime(p) >= run_start and branch in open(
                p, encoding="utf-8", errors="ignore"
            ).read():
                own = p
                break
        except OSError:
            pass
    if not own:
        print("Cost: unmeasured (no session transcript found under ~/.claude/projects)")
        return

    rows = {}  # label -> {tok, usd}
    for t, model, u in usage_entries(own):
        # entries before the phase log belong to whatever the session did earlier
        # (interactive invocation) — not to this run
        if not t or ts(t) < run_start:
            continue
        label = f"main:{phases[0][1]}"
        for start, name in phases:
            if ts(t) >= start:
                label = f"main:{name}"
        add(rows.setdefault(label, {}), model, u)

    for p in glob.glob(own[: -len(".jsonl")] + "/subagents/*.jsonl"):
        if os.path.getmtime(p) < run_start:
            continue
        label = "agent:" + (
            thread_name(p) or os.path.basename(p)[len("agent-") : -len(".jsonl")]
        )
        for _, model, u in usage_entries(p):
            add(rows.setdefault(label, {}), model, u)

    print("| Component | Model | Fresh in | Out | Cache write | Cache read | ~$ |")
    print("|---|---|---|---|---|---|---|")
    total = [0, 0, 0, 0]
    usd = 0.0
    for label in sorted(rows):
        b = rows[label]
        f, o, cw, cr = b["tok"]
        models = "+".join(sorted(b.get("models", set()))) or "?"
        print(f"| {label} | {models} | {f:,} | {o:,} | {cw:,} | {cr:,} | {b['usd']:.2f} |")
        for i, v in enumerate((f, o, cw, cr)):
            total[i] += v
        usd += b["usd"]
    print(
        f"| **total** | | {total[0]:,} | {total[1]:,} | {total[2]:,} | {total[3]:,} "
        f"| **{usd:.2f}** |"
    )
    print()
    print(
        "API-equivalent $, list rates; run under subscription quota. "
        "Measured Phase 1 to PR creation; PR create/backfill and Phase 8 excluded."
    )


if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # never fail the run over bookkeeping
        print(f"Cost: unmeasured ({e})")
