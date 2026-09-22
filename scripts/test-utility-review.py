#!/usr/bin/env python3
"""Build a disposable non-integration test-method inventory from source and TRX."""

import argparse
import csv
import json
import re
import sqlite3
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
ATTRIBUTE = re.compile(r"\[(?:HumansFact|HumansTheory|BrokenFact|DebuggerOnlyFact)\b")
METHOD = re.compile(
    r"\b(?:public|internal)\s+(?:(?:static|async|override|virtual|sealed|new)\s+)*"
    r"(?:Task(?:<[^;{}]+>)?|ValueTask(?:<[^;{}]+>)?|void)\s+(\w+)\s*\("
)
CLASS = re.compile(
    r"(?m)^\s*(?:(?:public|internal|private|protected|sealed|abstract|partial|static)\s+)*"
    r"class\s+(\w+)"
)
NAMESPACE = re.compile(r"\bnamespace\s+([\w.]+)\s*[;{]")
NON_CODE = re.compile(
    r'//[^\n]*|/\*.*?\*/|(?:\$@|@\$|@)"(?:[^"]|"")*"|'
    r'\$*"{3,}.*?"{3,}|\$?"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'',
    re.S,
)


def duration_seconds(value):
    hours, minutes, seconds = value.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def test_key(project, test_class, method):
    return "::".join((project, test_class, method))


def test_projects(root):
    return {
        path.name for path in (root / "tests").glob("*.Tests")
        if path.name != "Humans.Integration.Tests"
    }


def class_scopes(source):
    code = NON_CODE.sub(
        lambda match: "".join("\n" if char == "\n" else " " for char in match.group()),
        source,
    )
    scopes = []
    for declaration in CLASS.finditer(code):
        opening = code.find("{", declaration.end())
        if opening < 0:
            continue
        depth = 1
        for index in range(opening + 1, len(code)):
            depth += (code[index] == "{") - (code[index] == "}")
            if depth == 0:
                scopes.append((declaration.start(), index, declaration.group(1)))
                break
    return code, scopes


def source_methods(root):
    """Index test attributes; ambiguous C# shapes remain unresolved for review."""
    rows = {}
    for project in sorted((root / "tests").glob("*.Tests")):
        if project.name == "Humans.Integration.Tests":
            continue
        for path in project.rglob("*.cs"):
            if "bin" in path.parts or "obj" in path.parts:
                continue
            source = path.read_text(encoding="utf-8-sig")
            namespace = NAMESPACE.search(source)
            if not namespace:
                continue
            code, classes = class_scopes(source)
            for attribute in ATTRIBUTE.finditer(code):
                match = METHOD.search(code, attribute.end(), attribute.end() + 6000)
                if not match:
                    continue
                # An intervening method or declaration means this attribute does not
                # belong to the candidate. Keep the row unresolved instead of guessing.
                between = code[attribute.end():match.start()]
                if re.search(r"[;{}]", between) or re.search(r"\bclass\s+\w+", between):
                    continue
                enclosing = [item for item in classes if item[0] < match.start() < item[1]]
                if not enclosing:
                    continue
                test_class = namespace.group(1) + "." + max(enclosing)[2]
                key = test_key(project.name, test_class, match.group(1))
                relative = path.relative_to(root).as_posix()
                line = source.count("\n", 0, match.start()) + 1
                if key in rows and rows[key]["source"] != relative:
                    rows[key]["source"] = None
                    rows[key]["line"] = None
                else:
                    rows[key] = {"source": relative, "line": line}
    return rows


def trx_methods(paths):
    rows = defaultdict(lambda: {"cases": 0, "seconds": 0.0, "max_seconds": 0.0,
                                "outcomes": Counter()})
    run_count = 0
    seen_projects = set()
    for path in paths:
        if "Humans.Integration.Tests" in path.parts:
            continue
        root = ET.parse(path).getroot()
        tests = {}
        for test in root.findall(".//t:UnitTest", NS):
            method = test.find("t:TestMethod", NS)
            if method is not None:
                tests[test.get("id")] = method.attrib
        projects = set()
        for result in root.findall(".//t:UnitTestResult", NS):
            method = tests.get(result.get("testId"))
            if not method:
                raise ValueError(f"Unresolved TRX test ID in {path}: {result.get('testId')}")
            project = Path(method["codeBase"].replace("\\", "/")).stem
            if project == "Humans.Integration.Tests":
                continue
            projects.add(project)
            key = test_key(project, method["className"], method["name"])
            row = rows[key]
            seconds = duration_seconds(result.get("duration", "0:0:0"))
            row["cases"] += 1
            row["seconds"] += seconds
            row["max_seconds"] = max(row["max_seconds"], seconds)
            row["outcomes"][result.get("outcome", "Unknown")] += 1
        if not projects:
            raise ValueError(f"TRX contains no non-integration test results: {path}")
        if len(projects) > 1:
            raise ValueError(f"TRX combines projects; cannot distinguish repeated runs: {path}")
        overlap = seen_projects & projects
        if overlap:
            raise ValueError(f"Multiple TRX runs for {', '.join(sorted(overlap))}; provide one snapshot")
        seen_projects.update(projects)
        run_count += 1
    return rows, run_count, seen_projects


def write_csv(path, fields, rows):
    with path.open("w", newline="", encoding="utf-8") as output:
        writer = csv.DictWriter(output, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def build(args):
    root = args.root.resolve()
    if args.results and not args.results.is_dir():
        raise ValueError(f"Results directory does not exist: {args.results}")
    if args.results and not args.results_revision:
        raise ValueError("--results-revision is required with --results")
    source = source_methods(root)
    results = sorted(path for path in args.results.rglob("*.trx")
                     if "Humans.Integration.Tests" not in path.parts) if args.results else []
    observed, run_count, observed_projects = trx_methods(results)
    if args.results and not results:
        raise ValueError(f"No non-integration TRX files found in {args.results}")
    if results and run_count != len(results):
        raise ValueError("Could not read every TRX")
    if args.results:
        expected_projects = test_projects(root)
        missing = expected_projects - observed_projects
        unexpected = observed_projects - expected_projects
        if missing or unexpected:
            details = []
            if missing:
                details.append(f"missing: {', '.join(sorted(missing))}")
            if unexpected:
                details.append(f"unexpected: {', '.join(sorted(unexpected))}")
            raise ValueError(f"TRX project snapshot mismatch ({'; '.join(details)})")
    # A source file may hold two test classes, or embed C# class declarations in
    # analyzer snippets. Prefer the runner's exact class identity when its method
    # name identifies one observed method in that project.
    observed_by_method = defaultdict(list)
    for key in observed:
        project, _, method = key.split("::", 2)
        observed_by_method[(project, method)].append(key)
    for key in list(source):
        if key in observed:
            continue
        project, _, method = key.split("::", 2)
        candidates = observed_by_method[(project, method)]
        if len(candidates) == 1 and candidates[0] not in source:
            source[candidates[0]] = source.pop(key)
    methods = {}
    for key in source.keys() | observed.keys():
        project, test_class, method = key.split("::", 2)
        detail = source.get(key, {})
        measurement = observed.get(key, {})
        methods[key] = {
            "key": key, "project": project, "class": test_class, "method": method,
            "source": detail.get("source"), "line": detail.get("line"),
            "cases": measurement.get("cases", 0),
            "seconds": round(measurement.get("seconds", 0), 6),
            "max_seconds": round(measurement.get("max_seconds", 0), 6),
            "outcomes": json.dumps(measurement.get("outcomes", {}), sort_keys=True),
        }
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(output / "inventory.sqlite")
    try:
        with db:
            db.executescript("""
                DROP TABLE IF EXISTS methods;
                CREATE TABLE methods (
                    key TEXT PRIMARY KEY, project TEXT NOT NULL, class TEXT NOT NULL,
                    method TEXT NOT NULL, source TEXT, line INTEGER,
                    cases INTEGER NOT NULL, seconds REAL NOT NULL, max_seconds REAL NOT NULL,
                    outcomes TEXT NOT NULL
                );
            """)
            db.executemany(
                "INSERT INTO methods VALUES (?,?,?,?,?,?,?,?,?,?)",
                [(m["key"], m["project"], m["class"], m["method"], m["source"],
                  m["line"], m["cases"], m["seconds"], m["max_seconds"],
                  m["outcomes"])
                 for m in methods.values()],
            )
        method_rows = sorted(methods.values(), key=lambda m: m["key"])
        fields = ["key", "project", "class", "method", "source", "line", "cases",
                  "seconds", "max_seconds", "outcomes"]
        write_csv(output / "methods.csv", fields, method_rows)
        summary = {
            "source_methods": len(source), "observed_methods": len(observed),
            "methods": len(methods), "cases": sum(m["cases"] for m in methods.values()),
            "results": run_count,
            "unresolved_sources": sum(m["source"] is None for m in methods.values()),
            "results_revision": args.results_revision if results else None,
            "source_revision": subprocess.check_output(
                ["git", "rev-parse", "HEAD"], cwd=root, text=True
            ).strip(),
            "source_tests_dirty": bool(subprocess.check_output(
                ["git", "status", "--porcelain", "--", "tests"], cwd=root, text=True
            ).strip()),
        }
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n",
                                               encoding="utf-8")
        print(json.dumps(summary, indent=2))
    finally:
        db.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--results", type=Path,
                        help="Directory containing one TRX per test project; optional")
    parser.add_argument("--results-revision", help="Commit SHA that produced the TRX snapshot")
    parser.add_argument("--output", type=Path, default=ROOT / "local/test-utility")
    args = parser.parse_args()
    try:
        build(args)
    except (KeyError, OSError, ValueError, ET.ParseError, sqlite3.Error,
            subprocess.CalledProcessError) as exc:
        print(f"test utility review: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
