#!/usr/bin/env python3
"""Cost report for a section-doctor run (Phase 7).

Usage: python cost-report.py <branch-name> <phase-log-path>

Finds this run's own session transcript under ~/.claude/projects (the file that
mentions the run branch and was modified after the run started), sums per-API-call
token usage bucketed by the phase boundaries in the phase log, adds one row per
subagent transcript (named by its `thread:` marker where it has one), and prints a
markdown table with per-row model and API-equivalent cost, followed by footer lines
reporting the peak main-thread context and any mid-run compactions detected.

Rows are named by what the run was DOING, not by phase number: each phase-log line
is `<iso-ts> <phase-id> <label>` and the label becomes the row. Phase 4 writes one
line per strike item, so the strike rows break down per item rather than collapsing
into one bucket. The phase id is a trailing column. A line with no label falls back
to its id, so an older phase log still reports.

Exits 0 with "Cost: unmeasured (...)" on any discovery failure — never fail the run.
"""
import glob
import json
import os
import re
import sys
from datetime import datetime

# $/MTok: fresh input, output, cache write, cache read (API list rates).
# Order matters: rate() takes the first substring match, so "sonnet-5" ($2/$10)
# must precede the plain "sonnet" (4.6, $3/$15) fallback.
RATES = {
    "fable": (10, 50, 12.50, 1.00),
    "mythos": (10, 50, 12.50, 1.00),
    "opus": (5, 25, 6.25, 0.50),
    "sonnet-5": (2, 10, 2.50, 0.20),
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


def read_phase_log(path):
    """`<iso-ts> <phase-id> [label...]` -> [(start_ts, phase_id, label)], time-ordered.

    The label says what the run was doing; it is the row name. Older logs carry no
    label, so the id stands in for one."""
    out = []
    for line in open(path, encoding="utf-8"):
        parts = line.split(None, 2)
        if len(parts) < 2:
            continue
        out.append((ts(parts[0]), parts[1], parts[2].strip() if len(parts) > 2 else parts[1]))
    return sorted(out)


def phase_at(t, phases):
    """The (phase_id, label) in force at timestamp t."""
    cur = phases[0]
    for entry in phases:
        if t >= entry[0]:
            cur = entry
    return cur[1], cur[2]


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
            break  # first match only, mirroring rate()
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
    phases = read_phase_log(phase_log)
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

    rows = {}  # key -> {tok, usd, models, phase, label, first}

    def row(key, phase, label, t):
        b = rows.setdefault(key, {"phase": phase, "label": label, "first": t})
        b["first"] = min(b["first"], t)
        return b

    contexts = []  # (label, context tokens) per main-thread call, time-ordered
    for t, model, u in usage_entries(own):
        # entries before the phase log belong to whatever the session did earlier
        # (interactive invocation) — not to this run
        if not t or ts(t) < run_start:
            continue
        pid, label = phase_at(ts(t), phases)
        add(row(f"main:{pid}:{label}", pid, label, ts(t)), model, u)
        contexts.append((label, sum(u.get(k, 0) for k in (
            "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"))))

    for p in glob.glob(own[: -len(".jsonl")] + "/subagents/*.jsonl"):
        if os.path.getmtime(p) < run_start:
            continue
        entries = [e for e in usage_entries(p) if e[0]]
        if not entries:
            continue
        start = min(ts(e[0]) for e in entries)
        label = thread_name(p) or os.path.basename(p)[len("agent-") : -len(".jsonl")]
        pid, _ = phase_at(max(start, run_start), phases)
        b = row(f"agent:{pid}:{label}", pid, f"{label} (subagent)", start)
        for _, model, u in entries:
            add(b, model, u)

    print("| Component | Phase | Model | Fresh in | Out | Cache write | Cache read | ~$ |")
    print("|---|---|---|---|---|---|---|---|")
    total = [0, 0, 0, 0]
    usd = 0.0
    for key in sorted(rows, key=lambda k: rows[k]["first"]):
        b = rows[key]
        f, o, cw, cr = b["tok"]
        models = "+".join(sorted(b.get("models", set()))) or "?"
        print(
            f"| {b['label']} | {b['phase']} | {models} | {f:,} | {o:,} | {cw:,} "
            f"| {cr:,} | {b['usd']:.2f} |"
        )
        for i, v in enumerate((f, o, cw, cr)):
            total[i] += v
        usd += b["usd"]
    print(
        f"| **total** | | | {total[0]:,} | {total[1]:,} | {total[2]:,} | {total[3]:,} "
        f"| **{usd:.2f}** |"
    )
    print()
    print(
        "API-equivalent $, list rates; run under subscription quota. "
        "Measured Phase 1 to PR creation; PR create/backfill and Phase 8 excluded."
    )

    # Context telemetry: where the run's context peaked, and whether it was
    # compacted mid-run. Compaction is detected from the usage data itself — a
    # sustained drop of >50% and >100k tokens between consecutive main-thread
    # calls (a one-call dip, e.g. a small utility request, does not count).
    if contexts:
        peak_label, peak_ctx = max(contexts, key=lambda c: c[1])
        print()
        print(f"Peak main-thread context: {peak_ctx:,} tokens ({peak_label}).")
        compactions = []
        for i in range(1, len(contexts)):
            prev, cur = contexts[i - 1][1], contexts[i][1]
            sustained = i + 1 >= len(contexts) or contexts[i + 1][1] < prev
            if cur < prev * 0.5 and prev - cur > 100_000 and sustained:
                compactions.append(contexts[i][0])
        if compactions:
            print(f"Compactions detected: {len(compactions)} ({', '.join(compactions)}).")
        else:
            print("Compactions detected: none.")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # never fail the run over bookkeeping
        print(f"Cost: unmeasured ({e})")
