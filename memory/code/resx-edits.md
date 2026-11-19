---
name: Edit resx files by exact string replacement
description: Adding, removing or rewording a `.resx` entry: exact-string replacement, once per culture file. Never `sed`, never an XML round-trip.
---

Every `.resx` change is a **targeted exact-string edit**, done once per culture file.

- **Changing a `<value>`:** copy the exact `<value>…</value>` text with its indentation and replace that string once. The edit tool's uniqueness check is the assert.
- **Adding or removing a key:** insert or delete the whole `<data>` block by exact match, anchored on an existing single-line `<data name="…">` entry present in every culture file. Then parse each file and assert that exactly the intended keys changed and that all cultures carry the same key set.
- **One edit per culture file,** never one pass over a glob: a glob silently skips the file whose wrapping differs.

**Never `sed`** (values wrap across lines and a per-line substitution corrupts them invisibly) and **never `ElementTree.write()` / `minidom`** (the round-trip changes the BOM, header comments, tag spacing and escaping).

**Why:** a resx has a fragile header and parity-tested culture siblings. Reformatting turns a one-word change into an unreviewable diff, and reviewers stop reading resx diffs after the first one.

**Related:** [`resource-key-prefix-matches-section`](resource-key-prefix-matches-section.md) for what a new key is called.
