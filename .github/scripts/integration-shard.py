#!/usr/bin/env python3
"""Prints the `dotnet test --filter` for one shard of Humans.Integration.Tests.

Usage: integration-shard.py <shard 1..N> <N>

Test classes are dealt to shards by test count, largest first, so the shards finish together. The last shard is
written as "everything the other shards do not run", so a test this script cannot see still runs exactly once.
"""
import pathlib
import re
import sys

shard, total = int(sys.argv[1]), int(sys.argv[2])
root = pathlib.Path(__file__).resolve().parents[2] / "tests" / "Humans.Integration.Tests"

classes = []
for path in sorted(root.rglob("*.cs")):
    if {"bin", "obj"} & set(path.parts):
        continue
    tests = len(re.findall(r"\[Humans(?:Fact|Theory)", path.read_text(encoding="utf-8")))
    if tests:
        classes.append((tests, path.stem))

loads = [0] * total
shards = [[] for _ in range(total)]
for tests, name in sorted(classes, key=lambda c: (-c[0], c[1])):
    lightest = loads.index(min(loads))
    loads[lightest] += tests
    shards[lightest].append(name)

# The dots make the match a whole class name, not a substring of another one.
if shard < total:
    print("|".join(f"FullyQualifiedName~.{name}." for name in shards[shard - 1]))
else:
    others = [name for group in shards[:-1] for name in group]
    print("&".join(f"FullyQualifiedName!~.{name}." for name in others))
