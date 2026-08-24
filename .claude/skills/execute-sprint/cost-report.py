#!/usr/bin/env python3
"""Cost report for an execute-sprint run (Step 4).

Usage: python cost-report.py <sprint-date> <run-start-iso>

Finds this run's own session transcript under ~/.claude/projects (the file that
mentions a `claude/sprint-<date>-batch-` branch and was modified after the run
started), sums per-API-call token usage, and prints a markdown table with
API-equivalent cost: the main thread as `orchestrator`, each subagent transcript
assigned to the batch whose branch name appears in it.

Adapted from .claude/skills/section-doctor/cost-report.py (phase buckets swapped
for batch grouping). Exits 0 with "Cost: unmeasured (...)" on any discovery
failure — never fail the run.
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


def add(bucket, model, u):
    r = rate(model)
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
    date, run_start_iso = sys.argv[1], sys.argv[2]
    run_start = ts(run_start_iso.strip())
    branch_prefix = f"claude/sprint-{date}-batch-"
    batch_re = re.compile(re.escape(branch_prefix) + r"(\d+)")

    own = None
    for p in glob.glob(os.path.expanduser("~/.claude/projects/*/*.jsonl")):
        try:
            if os.path.getmtime(p) >= run_start and branch_prefix in open(
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
        # entries before .run-start belong to whatever the session did earlier
        if not t or ts(t) < run_start:
            continue
        add(rows.setdefault("orchestrator", {}), model, u)

    for p in glob.glob(own[: -len(".jsonl")] + "/subagents/*.jsonl"):
        if os.path.getmtime(p) < run_start:
            continue
        m = batch_re.search(open(p, encoding="utf-8", errors="ignore").read())
        agent_id = os.path.basename(p)[len("agent-") : -len(".jsonl")]
        label = f"batch-{m.group(1)}" if m else f"agent:{agent_id}"
        for _, model, u in usage_entries(p):
            add(rows.setdefault(label, {}), model, u)

    print("| Component | Fresh in | Out | Cache write | Cache read | ~$ |")
    print("|---|---|---|---|---|---|")
    total = [0, 0, 0, 0]
    usd = 0.0
    for label in sorted(rows):
        b = rows[label]
        f, o, cw, cr = b["tok"]
        print(f"| {label} | {f:,} | {o:,} | {cw:,} | {cr:,} | {b['usd']:.2f} |")
        for i, v in enumerate((f, o, cw, cr)):
            total[i] += v
        usd += b["usd"]
    print(
        f"| **total** | {total[0]:,} | {total[1]:,} | {total[2]:,} | {total[3]:,} "
        f"| **{usd:.2f}** |"
    )
    print()
    print(
        "API-equivalent $, list rates. Measured from local/.run-start to report time; "
        "the report comment itself lands after measurement."
    )


if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # never fail the run over bookkeeping
        print(f"Cost: unmeasured ({e})")
