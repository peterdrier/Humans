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
