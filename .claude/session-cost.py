#!/usr/bin/env python3
"""Estimate the cost of a Claude Code session from its transcripts.

Reads ~/.claude/projects/<slug>/<session-id>.jsonl (the main loop) plus
<session-id>/subagents/*.jsonl, sums per-turn token usage, and prices it at
list API rates. Emits a markdown block for an unattended run's PR body.

    .claude/session-cost.py                 # current session, markdown footer
    .claude/session-cost.py --session <id>  # a specific session
    .claude/session-cost.py --json          # machine-readable

Cloud sessions do not record a dollar figure anywhere (no `total_cost_usd`,
no `estimated_usd` — those are local-CLI only), so this derives one. It is an
estimate at list rates, not a billed amount.

Never exits non-zero: a missing or unreadable transcript must not fail the run
that calls it. On failure it prints a one-line note and exits 0.
"""
import argparse
import collections
import glob
import json
import os
import sys

# $ per 1M tokens: (input, output).
# Cache write 5m = 1.25x input, cache write 1h = 2.0x input, cache read = 0.1x input.
PRICES = {
    "claude-fable-5":    (10.00, 50.00),
    "claude-opus-5":      (5.00, 25.00),
    "claude-opus-4-8":    (5.00, 25.00),
    "claude-opus-4-7":    (5.00, 25.00),
    "claude-opus-4-6":    (5.00, 25.00),
    "claude-sonnet-5":    (3.00, 15.00),
    "claude-sonnet-4-6":  (3.00, 15.00),
    "claude-haiku-4-5":   (1.00,  5.00),
}
# Fast mode (Opus 5 / 4.8 only) is billed at a premium.
FAST_PRICES = {"claude-opus-5": (10.00, 50.00), "claude-opus-4-8": (10.00, 50.00)}

CACHE_WRITE_5M = 1.25
CACHE_WRITE_1H = 2.00
CACHE_READ = 0.10


def rates(model, fast):
    table = FAST_PRICES if fast else PRICES
    if model in table:
        return table[model]
    for known, value in table.items():   # tolerate date-suffixed ids
        if model.startswith(known):
            return value
    return None


def transcripts(session_id):
    """Yield (path, label) for the main transcript and each subagent transcript."""
    root = os.path.expanduser("~/.claude/projects")
    main = glob.glob(f"{root}/*/{session_id}.jsonl")
    if not main:
        return []
    found = [(main[0], "main loop")]
    for sub in sorted(glob.glob(f"{main[0][:-6]}/subagents/*.jsonl")):
        meta = {}
        meta_path = sub[:-6] + ".meta.json"
        if os.path.exists(meta_path):
            try:
                with open(meta_path) as handle:
                    meta = json.load(handle)
            except (OSError, ValueError):
                pass
        label = meta.get("agentType", "subagent")
        if meta.get("description"):
            label += f": {meta['description']}"
        found.append((sub, label))
    return found


def tally(path):
    """Sum token usage and cost for one transcript. Returns None if it has no turns.

    One API response is written as several assistant records (thinking, text,
    tool_use), each repeating the SAME usage object — so dedupe on requestId or
    the cost roughly doubles.
    """
    acc = collections.defaultdict(float)
    seen = set()
    unpriced = set()
    with open(path) as handle:
        for line in handle:
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if record.get("type") != "assistant":
                continue
            message = record.get("message") or {}
            usage = message.get("usage") or {}
            if not usage:
                continue
            request_id = record.get("requestId") or message.get("id")
            if request_id:
                if request_id in seen:
                    continue
                seen.add(request_id)

            model = message.get("model", "unknown")
            priced = rates(model, usage.get("speed") == "fast")
            if priced is None:
                unpriced.add(model)
                continue
            per_in, per_out = priced

            creation = usage.get("cache_creation") or {}
            write_5m = creation.get("ephemeral_5m_input_tokens", 0)
            write_1h = creation.get("ephemeral_1h_input_tokens", 0)
            if not creation:  # older records: one undifferentiated bucket
                write_5m = usage.get("cache_creation_input_tokens", 0)
            read = usage.get("cache_read_input_tokens", 0)
            plain = usage.get("input_tokens", 0)
            out = usage.get("output_tokens", 0)

            acc["input"] += plain
            acc["cache_write"] += write_5m + write_1h
            acc["cache_read"] += read
            acc["output"] += out
            acc["turns"] += 1
            acc["usd"] += (
                plain * per_in
                + write_5m * per_in * CACHE_WRITE_5M
                + write_1h * per_in * CACHE_WRITE_1H
                + read * per_in * CACHE_READ
                + out * per_out
            ) / 1_000_000
    acc["unpriced"] = unpriced
    return acc if acc["turns"] else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", default=os.environ.get("CLAUDE_CODE_SESSION_ID", ""))
    parser.add_argument("--json", action="store_true", help="emit JSON instead of markdown")
    args = parser.parse_args()

    if not args.session:
        print("_Session cost unavailable: no session id "
              "(set CLAUDE_CODE_SESSION_ID or pass --session)._")
        return

    found = transcripts(args.session)
    if not found:
        print(f"_Session cost unavailable: no transcript for `{args.session}`._")
        return

    rows, unpriced = [], set()
    for path, label in found:
        try:
            acc = tally(path)
        except OSError:
            continue
        if acc:
            unpriced |= acc.pop("unpriced")
            rows.append((label, acc))

    if not rows:
        print(f"_Session cost unavailable: no priced turns in `{args.session}`._")
        return

    total = sum(acc["usd"] for _, acc in rows)
    agents = len(rows) - 1

    if args.json:
        json.dump({
            "session_id": args.session,
            "total_usd": round(total, 4),
            "subagents": agents,
            "unpriced_models": sorted(unpriced),
            "breakdown": [
                {"agent": label, "turns": int(acc["turns"]),
                 "input_tokens": int(acc["input"]),
                 "cache_write_tokens": int(acc["cache_write"]),
                 "cache_read_tokens": int(acc["cache_read"]),
                 "output_tokens": int(acc["output"]),
                 "usd": round(acc["usd"], 4)}
                for label, acc in rows
            ],
        }, sys.stdout, indent=2)
        print()
        return

    count = lambda n: f"{int(n):,}"
    print("## Run cost")
    print()
    print(f"**${total:.2f}** estimated — {len(rows)} agent(s) "
          f"({agents} subagent{'s' if agents != 1 else ''}), session `{args.session}`.")
    print()
    print("<details><summary>Per-agent breakdown</summary>")
    print()
    print("| Agent | Turns | Input | Cache write | Cache read | Output | Cost |")
    print("|---|--:|--:|--:|--:|--:|--:|")
    for label, acc in rows:
        print(f"| {label} | {int(acc['turns'])} | {count(acc['input'])} | "
              f"{count(acc['cache_write'])} | {count(acc['cache_read'])} | "
              f"{count(acc['output'])} | ${acc['usd']:.2f} |")
    if unpriced:
        print()
        print(f"> Excluded — no price for: {', '.join(sorted(unpriced))}")
    print()
    print("</details>")
    print()
    print("> Derived from recorded token usage at list API rates. "
          "An estimate, not a billed amount.")


if __name__ == "__main__":
    try:
        main()
    except BrokenPipeError:  # a caller closed the pipe (`| head`) — not our problem
        os.dup2(os.open(os.devnull, os.O_WRONLY), sys.stdout.fileno())
    except Exception as exc:  # never break the run that calls us
        print(f"_Session cost unavailable: {type(exc).__name__}: {exc}._")
