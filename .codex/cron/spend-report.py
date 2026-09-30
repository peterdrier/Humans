#!/usr/bin/env python3
"""Report only this run's recorded roots, using the installed Codex spend skill."""
import json
from pathlib import Path
import subprocess
import sys


def report(manifest, script):
    print("## Nightly cleanup spend\n")
    print("Estimated Standard API list-price equivalent; not subscription billing. "
          "Each session includes its linked subagents.\n")
    try:
        sessions = list(dict.fromkeys(json.loads(line)["thread_id"]
                                    for line in Path(manifest).read_text().splitlines()))
        if not sessions or not all(isinstance(s, str) and s.strip() for s in sessions):
            raise ValueError("no valid recorded session IDs")
        if not Path(script).is_file():
            raise FileNotFoundError(f"Codex spend skill is missing: {script}")
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f"Spend unavailable: {error}")
        print(f"WARNING: spend unavailable: {error}", file=sys.stderr)
        return
    for index, session in enumerate(sessions):
        label = "Cleanup" if index == 0 else f"Gate repair {index}"
        print(f"### {label} — `{session}`\n")
        try:
            result = subprocess.run([sys.executable, script, session],
                                    capture_output=True, text=True, timeout=30)
            if result.returncode or not result.stdout.strip():
                raise RuntimeError(result.stderr.strip() or "calculator returned no report")
            print(f"```text\n{result.stdout.rstrip()}\n```\n")
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
            print(f"Spend unavailable for this session: {error}\n")
            print(f"WARNING: spend unavailable for {session}: {error}", file=sys.stderr)


if __name__ == "__main__":
    report(*sys.argv[1:])
