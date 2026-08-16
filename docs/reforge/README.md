# Reforge measurement archive

Persistent record of `reforge surface-score` readings against `Humans.slnx`, so score
movement can be attributed to specific commits and specific rule changes rather than
remembered.

Related upstream discussion: <https://github.com/peterdrier/reforge/issues/19>
(proposal to retire six read-surface rules).

## Files

| Path | What it is |
|------|-----------|
| `measurements.md` | **The file to read.** Append-only log, one dated section per run, newest last. |
| `latest.json` | Copy of the newest run's raw JSON. This is the `--baseline` target. |
| `runs/<YYYY-MM-DD>-<humans-sha>-reforge<version>.json` | Raw archive, one per run. |
| `runs/<YYYY-MM-DD>-<humans-sha>-reforge<version>.md` | `--format markdown` report for the same run. |

Naming: date of the run, the short Humans SHA that was measured, and the reforge
version that measured it — all three matter, because a score change can come from any
of them.

## Taking a reading

Requires reforge >= 0.28.1 (`dotnet tool update --global Reforge`, or a source build of
reforge `main`).

```bash
# 1. Record what you are measuring with.
reforge --version
git rev-parse --short HEAD

# 2. The workspace must compile clean. A partial score is not a baseline.
dotnet build Humans.slnx -v quiet

# 3. Take the reading.
STAMP=$(date +%F)-$(git rev-parse --short HEAD)-reforge<version>

reforge surface-score --solution Humans.slnx --format json --all --top-symbols 0 \
  > docs/reforge/runs/$STAMP.json
reforge surface-score --solution Humans.slnx --format markdown --top 25 \
  > docs/reforge/runs/$STAMP.md
cp docs/reforge/runs/$STAMP.json docs/reforge/latest.json
```

`surface-score` exits 2 when the tree did not compile. Fix the tree and rerun — do
**not** pass `--allow-degraded`; a partial score reads as authoritative and is not a
baseline.

Check the JSON size afterwards. If it exceeds ~3 MB, regenerate the archived copy with
`--top 100 --top-symbols 200` instead of `--all --top-symbols 0`, and note the
truncation in that run's log entry. Repo bloat compounds across runs. (The 2026-08-16
run came in at 1.8 MB untruncated.)

## Comparing against the last reading

```bash
reforge surface-score --solution Humans.slnx --baseline docs/reforge/latest.json
```

`--baseline` applies a Pareto gate: a surface drop bought with an internal-complexity
rise is reported as `traded`, not as an improvement, plus a Suspicious Improvements
section. That is the point of keeping `latest.json` current.

## Writing the log entry

Append a section to `measurements.md` titled
`## <date> — Humans <sha> — reforge <version>`. Never rewrite or reorder earlier
sections; the log is append-only and newest-last. Each entry carries the headline
figures, the read-surface rule subtotal, the internal axis per rule, the size-rule
distributions, and the top 20 cross-rule symbols — with the raw `jq` output preserved
in fenced blocks so nothing is lost to summarizing. Copy the `jq` invocations from the
previous entry.

Note for anyone reusing those `jq` lines: the symbol aggregation lives under
`.topSymbols`, not `.symbols`, and the `.groups[].topEntries[]` distributions are only
complete when the run was taken with `--all`.
