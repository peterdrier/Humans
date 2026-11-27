#!/usr/bin/env python3
"""Record wall time, tokens and estimated cost of logo-library drawing runs.

Usage: python .claude/skills/redraw-logo/stats.py [slug ...]
No slugs: every 2026-11-* entry that has no stats.json yet.

Finds each run's session transcripts by the git branch `logo/<slug>` they were started on
(Claude Code JSONL under ~/.claude*/projects, Codex rollouts under ~/.codex/sessions), sums
re-runs, and writes <entry>/stats.json. Gemini/Antigravity keeps no per-session usage for these
runs, so those slugs are skipped. Never invents a price: unpriced model -> costUsd null.
`minutes` is working time: first to last event, minus the idle wait after each turn ends.
"""
import json
import sys
from datetime import datetime
from pathlib import Path

# USD per million tokens. Source: claude-api skill model reference (cached 2026-11-03).
# Cache writes: 1.25x input (5 min TTL), 2x input (1 h TTL). Cache read per model as listed there,
# 0.1x input where it lists none. Haiku 5.5 is dearer when one request's prompt exceeds 100K tokens.
# Models absent here (e.g. claude-opus-4-5, all OpenAI models) have no documented price -> null.
def _price(inp, out, read=None):
    return {"in": inp, "out": out, "read": read if read is not None else inp * 0.1,
            "w5": inp * 1.25, "w1h": inp * 2}

PRICES = {
    "claude-fable-5-2": _price(8, 40, 0.20),
    "claude-opus-5-5": _price(4, 20, 0.20),
    "claude-opus-5": _price(5, 25),
    "claude-opus-4-8": _price(5, 25),
    "claude-opus-4-7": _price(5, 25),
    "claude-opus-4-6": _price(5, 25),
    "claude-sonnet-5-5": _price(2, 10, 0.10),
    "claude-haiku-5-5": _price(0.10, 0.50),
}
HAIKU_LONG = _price(0.50, 2.50)
HAIKU_LONG_PROMPT = 100_000

TURN_END_SUBTYPES = {"stop_hook_summary", "turn_duration"}

ROOT = Path(__file__).resolve().parents[3]
LIBRARY = ROOT / "src" / "Humans.Web" / "wwwroot" / "img" / "logo-library"
HOME = Path.home()
CLAUDE_ROOTS = [HOME / d / "projects" for d in (".claude", ".claude_nobodies", ".claude_kuba")]
CODEX_ROOTS = [HOME / ".codex" / "sessions", HOME / ".codex" / "archived_sessions"]


def parse_ts(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def head_value(path, getter, limit=60):
    with open(path, encoding="utf-8", errors="replace") as f:
        for _, line in zip(range(limit), f):
            try:
                value = getter(json.loads(line))
            except ValueError:
                continue
            if value:
                return value
    return None


def index_claude():
    index = {}
    for root in CLAUDE_ROOTS:
        for path in root.glob("*logo*/**/*.jsonl") if root.exists() else []:
            branch = head_value(path, lambda o: o.get("gitBranch"))
            if branch:
                index.setdefault(branch, []).append(path)
    return index


def index_codex():
    index = {}
    for root in CODEX_ROOTS:
        for path in root.rglob("rollout-*.jsonl") if root.exists() else []:
            branch = head_value(path, lambda o: (o.get("payload") or {}).get("git", {}).get("branch"), limit=1)
            if branch:
                index.setdefault(branch, []).append(path)
    return index


def active_minutes(events):
    """Sum of gaps between consecutive (timestamp, ends_turn) events, skipping the wait after a
    turn ends: later events there are PR-merge notices and follow-ups, not drawing work."""
    events.sort(key=lambda e: e[0])
    return sum(b[0] - a[0] for a, b in zip(events, events[1:]) if not a[1]) / 60


def claude_cost(model, usage):
    price = PRICES.get(model)
    if price is None:
        return None
    inp = usage.get("input_tokens", 0)
    read = usage.get("cache_read_input_tokens", 0)
    created = usage.get("cache_creation_input_tokens", 0)
    split = usage.get("cache_creation") or {}
    w1h = split.get("ephemeral_1h_input_tokens", 0)
    w5 = split.get("ephemeral_5m_input_tokens", created - w1h)
    if model == "claude-haiku-5-5" and inp + read + created > HAIKU_LONG_PROMPT:
        price = HAIKU_LONG
    return (inp * price["in"] + usage.get("output_tokens", 0) * price["out"] + read * price["read"]
            + w5 * price["w5"] + w1h * price["w1h"]) / 1e6


def claude_stats(paths):
    messages = {}  # message id -> (model, usage); one API call is logged several times, last wins
    minutes = 0.0
    for path in paths:
        events = []
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                try:
                    o = json.loads(line)
                except ValueError:
                    continue
                if o.get("timestamp"):
                    events.append((parse_ts(o["timestamp"]),
                                   o.get("type") == "system" and o.get("subtype") in TURN_END_SUBTYPES))
                m = o.get("message")
                if o.get("type") == "assistant" and isinstance(m, dict) and m.get("usage") and m.get("id"):
                    messages[m["id"]] = (m.get("model", ""), m["usage"])
        minutes += active_minutes(events)
    totals = dict(inputTokens=0, outputTokens=0, cacheReadTokens=0, cacheWriteTokens=0)
    cost, priced = 0.0, True
    for model, usage in messages.values():
        if model == "<synthetic>":
            continue
        totals["inputTokens"] += usage.get("input_tokens", 0)
        totals["outputTokens"] += usage.get("output_tokens", 0)
        totals["cacheReadTokens"] += usage.get("cache_read_input_tokens", 0)
        totals["cacheWriteTokens"] += usage.get("cache_creation_input_tokens", 0)
        c = claude_cost(model, usage)
        if c is None:
            priced = False
        else:
            cost += c
    return minutes, totals, round(cost, 2) if priced else None


def codex_stats(paths):
    totals = dict(inputTokens=0, outputTokens=0, cacheReadTokens=0, cacheWriteTokens=0)
    minutes = 0.0
    for path in paths:
        events, last = [], None
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                try:
                    o = json.loads(line)
                except ValueError:
                    continue
                p = o.get("payload") or {}
                if o.get("timestamp"):
                    events.append((parse_ts(o["timestamp"]), p.get("type") == "task_complete"))
                if p.get("type") == "token_count" and p.get("info"):
                    last = p["info"]["total_token_usage"]  # cumulative for the session
        minutes += active_minutes(events)
        if last:
            cached = last.get("cached_input_tokens", 0)
            totals["inputTokens"] += last["input_tokens"] - cached
            totals["cacheReadTokens"] += cached
            totals["cacheWriteTokens"] += last.get("cache_write_input_tokens", 0)
            totals["outputTokens"] += last["output_tokens"]
    return minutes, totals, None  # OpenAI prices are not in any local reference


def main(argv):
    slugs = argv or sorted(d.name for d in LIBRARY.glob("2026-11-*") if not (d / "stats.json").exists())
    claude, codex = index_claude(), index_codex()
    for slug in slugs:
        entry = LIBRARY / slug
        if not (entry / "entry.json").exists():
            print(f"{slug}: skipped, no entry.json")
            continue
        branch = f"logo/{slug}"
        if branch in claude:
            source, (minutes, totals, cost) = "claude", claude_stats(claude[branch])
        elif branch in codex:
            source, (minutes, totals, cost) = "codex", codex_stats(codex[branch])
        else:
            hint = " (Gemini keeps no per-session usage on disk)" if "-gemini-" in slug else ""
            print(f"{slug}: skipped, no Claude or Codex transcript on branch {branch}{hint}")
            continue
        stats = {"minutes": round(minutes, 1), **totals, "costUsd": cost, "source": source}
        (entry / "stats.json").write_text(json.dumps(stats) + "\n", encoding="utf-8", newline="\n")
        tokens = sum(totals.values())
        cost_text = "unpriced" if cost is None else f"${cost:.2f}"
        print(f"{slug}: {source} {stats['minutes']} min, {tokens:,} tokens, {cost_text}")


if __name__ == "__main__":
    main(sys.argv[1:])
