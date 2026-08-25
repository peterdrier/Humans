---
name: Edit resx values by exact string replacement
description: Changing a .resx <value> — use a targeted exact-string replacement with an occurrence assert. Never ElementTree.write(), never line-based sed. Triggers on any edit to a Resources/*.resx file.
---

Editing the text inside a `.resx` `<value>` is a **targeted exact string replacement**, asserting the occurrence count before and after.

**Never `ElementTree.write()`** (or any XML round-trip). It reformats namespace prefixes, drops the XML comments the resx header carries, and loses the trailing newline — a diff of hundreds of lines around the one string you meant to change, and a file the parity tests then read differently.

**Never line-based `sed`.** The language variants are multi-line: a `<value>` that wraps corrupts on a per-line substitution, and the corruption is invisible until the page renders.

**How to apply:**

1. Read the file, copy the exact `<value>…</value>` text including its indentation.
2. Replace that exact string once — the editor tool's uniqueness check is the assert.
3. For the same key across all six cultures, do six replacements, one per file. A single pass over a glob will silently skip the file whose wrapping differs.

**Why:** A resx is a source file with a fragile header and six parity-tested siblings. The tooling that reformats it does so everywhere at once, so a one-word copy change arrives as an unreviewable diff — and reviewers stop reading resx diffs after the first one of those.
