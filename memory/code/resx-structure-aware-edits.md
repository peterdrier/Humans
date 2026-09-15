---
name: Add or remove a resx key structurally, then validate the key set
description: Add/remove a `.resx` key by exact-match text replacement of the whole `<data>` block, once per culture — never `sed` or an XML round-trip; validate with a parser after.
---

Adding or removing a `.resx` key is an **exact-match text edit of the whole `<data>` block**, followed by a parser check that the key sets changed the way you meant.

**Never `sed`, and never rewrite the file through `minidom` or `ElementTree.write()`.** The round-trip changes the BOM, the self-closing-tag spacing and the quote escaping, so a one-key removal arrives as a twenty-line diff and the parity tests read the file differently than you do.

**How to apply:**

1. Pick an anchor: an existing single-line `<data name="…">` entry that is present in every culture file. Insert your new block immediately after it, or delete the target block, by exact string replacement.
2. Do it once per culture file — six edits, not one pass over a glob. A glob silently skips the file whose wrapping differs.
3. Validate with a parser, not by eye: parse each file, diff the key set before against after, and assert that exactly the intended keys were added or removed and that all six cultures carry the same set.

**Why:** A resx is a source file with a fragile header and six parity-tested siblings. Reformatting tooling touches all of it at once, and reviewers stop reading resx diffs after the first unreviewable one — which is how a wrong key survives review in five languages.

**Related:** [`resx-value-edits`](../process/resx-value-edits.md) covers changing the text inside an existing `<value>`; this atom covers changing which keys exist. [`resource-key-prefix-matches-section.md`](resource-key-prefix-matches-section.md) says what a new key may be called.
