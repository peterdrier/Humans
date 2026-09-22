#!/usr/bin/env python3
"""Compare two disposable test-utility-review.py method inventories.

Usage: python scripts/compare-test-timings.py local/run-a local/run-b
The arguments may be inventory directories or their methods.csv files. A
method's seconds sum all its theory cases; max_seconds is its slowest case.
"""

import argparse
import csv
from pathlib import Path


def read_methods(path):
    if path.is_dir():
        path /= "methods.csv"
    with path.open(encoding="utf-8", newline="") as stream:
        return {
            row["key"]: (float(row["seconds"]), float(row["max_seconds"]))
            for row in csv.DictReader(stream)
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--threshold", type=float, default=1.0,
                        help="Summed method seconds used for the slow set (default: 1)")
    parser.add_argument("--top", type=int, default=12)
    args = parser.parse_args()

    before = read_methods(args.before)
    after = read_methods(args.after)
    if before.keys() != after.keys():
        missing = len(before.keys() - after.keys())
        added = len(after.keys() - before.keys())
        parser.error(f"inventories have different methods ({missing} missing, {added} added)")

    old_slow = {key for key, (seconds, _) in before.items() if seconds >= args.threshold}
    new_slow = {key for key, (seconds, _) in after.items() if seconds >= args.threshold}
    common = old_slow & new_slow
    union = old_slow | new_slow
    print(f"Methods: {len(before)}")
    print(f"Summed method-seconds: {sum(v[0] for v in before.values()):.1f} -> "
          f"{sum(v[0] for v in after.values()):.1f} (not wall time)")
    print(f"Methods >= {args.threshold:g}s: {len(old_slow)} -> {len(new_slow)}; "
          f"common {len(common)}, left {len(old_slow - new_slow)}, "
          f"entered {len(new_slow - old_slow)}; "
          f"Jaccard {len(common) / len(union) if union else 1:.3f}")
    print("Longest recurring methods (before -> after summed seconds; max case seconds):")
    for key in sorted(common, key=lambda k: -(before[k][0] + after[k][0]))[:args.top]:
        print(f"  {before[key][0]:.3f} -> {after[key][0]:.3f} "
              f"(max {before[key][1]:.3f} -> {after[key][1]:.3f}) {key}")
    print("Largest drops:")
    for key in sorted(old_slow - new_slow, key=lambda k: -before[k][0])[:args.top]:
        print(f"  {before[key][0]:.3f} -> {after[key][0]:.3f} {key}")
    print("Largest arrivals:")
    for key in sorted(new_slow - old_slow, key=lambda k: -after[k][0])[:args.top]:
        print(f"  {before[key][0]:.3f} -> {after[key][0]:.3f} {key}")


if __name__ == "__main__":
    main()
