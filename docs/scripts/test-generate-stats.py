"""Exercise statistics input validation without creating snapshot worktrees."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile


script = Path(__file__).with_name("generate-stats.sh").resolve()
documents = {
    "missing-heading": "# Statistics\n| Date | Lines |\n|------|-------|\n",
    "missing-separator": "# Statistics\n## Codebase Growth\n| Date | Lines |\n",
    "valid": "# Statistics\n## Codebase Growth\n| Date | Lines |\n|------|-------|\n",
}

for name, document in documents.items():
    for arguments in ([], ["--full"]):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "docs").mkdir()
            report = root / "docs/development-stats.md"
            report.write_text(document)
            (root / "bin").mkdir()
            git = root / "bin/git"
            git.write_text(
                '#!/bin/bash\n'
                'printf "%s\\n" "$*" >> git-calls\n'
                'case "$1" in\n'
                '  rev-parse) echo main ;;\n'
                '  log) ;;\n'
                '  *) exit 42 ;;\n'
                'esac\n'
            )
            git.chmod(0o755)
            env = os.environ.copy()
            env["PATH"] = str(root / "bin") + ":" + env["PATH"]
            env["CLOC"] = shutil.which("true")
            result = subprocess.run(
                ["bash", str(script), *arguments], cwd=root, env=env,
                capture_output=True, text=True, timeout=10,
            )
            if name == "valid":
                assert result.returncode == 0, result
                assert "No new days to snapshot." in result.stdout, result
            else:
                assert result.returncode != 0, (name, arguments, result)
                assert "could not split" in result.stderr, result
            assert report.read_text() == document
            assert "worktree" not in (root / "git-calls").read_text()

print("PASS: malformed statistics tables fail before snapshots or publication")

# Author dates need not follow Git traversal order. Keep the final commit for
# each date, but publish the daily snapshots chronologically.
with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    (root / "docs").mkdir()
    report = root / "docs/development-stats.md"
    report.write_text(documents["valid"])
    (root / "bin").mkdir()
    git = root / "bin/git"
    git.write_text(
        '#!/usr/bin/env python3\n'
        'import pathlib, sys\n'
        'args = sys.argv[1:]\n'
        'root = pathlib.Path(__file__).resolve().parents[1]\n'
        'if args[0] == "rev-parse": print("main")\n'
        'elif args[0] == "log":\n'
        '    if "--format=%ad %H" in args:\n'
        '        print("2026-09-10 first\\n2026-09-09 earlier\\n2026-09-10 last")\n'
        '    elif "--format=%H %ad" in args:\n'
        '        print("first 2026-09-10\\nearlier 2026-09-09\\nlast 2026-09-10")\n'
        'elif args[:2] == ["worktree", "add"]:\n'
        '    (pathlib.Path(args[4]) / "src").mkdir(parents=True)\n'
        'elif args[:2] == ["worktree", "remove"]:\n'
        '    (pathlib.Path(args[-1]) / "src").rmdir()\n'
        '    pathlib.Path(args[-1]).rmdir()\n'
        'elif args[0] == "-C" and args[2] == "checkout":\n'
        '    with (root / "snapshots").open("a") as output: output.write(args[-1] + "\\n")\n'
        'else: sys.exit(42)\n'
    )
    git.chmod(0o755)
    env = os.environ.copy()
    env["PATH"] = str(root / "bin") + ":" + env["PATH"]
    env["CLOC"] = shutil.which("true")
    result = subprocess.run(
        ["bash", str(script), "--full"], cwd=root, env=env,
        capture_output=True, text=True, timeout=10,
    )
    assert result.returncode == 0, result
    rows = [line.split("|")[1].strip() for line in report.read_text().splitlines()
            if line.startswith("| 2026-")]
    assert rows == ["2026-09-09", "2026-09-10"], rows
    assert (root / "snapshots").read_text().splitlines() == ["earlier", "last"]

print("PASS: daily snapshots publish in date order and retain each day's final commit")
