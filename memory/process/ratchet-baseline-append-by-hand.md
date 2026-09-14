---
name: Append ratchet baselines by hand — never re-run the seeder to absorb new entries
description: Absorb an intentional ratchet violation by appending locator lines + a justification block by hand — never re-run `BaselineSeeder` (wipes hand-written approvals).
---

When a ratchet test reports new violations that are genuinely intentional, add the locator lines to that rule's `.baseline.txt` **by hand**, preceded by a comment block saying why. Do not run `HUMANS_SEED_RATCHET_BASELINES=1 dotnet test --filter Seed_all_baselines`.

**Why:** `BaselineSeeder` rewrites every baseline file from the current violation set. Those files are not purely generated — `NoDestructiveMigrationOps.baseline.txt` in particular carries several hand-written per-incident approvals (the `camp_leads` split-PR drop, the Holded ledger redesign, the favourites index widening, the `ProfilePictureData` and `DisplayOrder` drops), each explaining an exception Peter authorised. A regeneration silently deletes all of them, and the deletion is easy to miss in a large diff. It also absorbs unrelated drift from other rules in the same run — a seeder pass during nobodies-collective/Humans#992 additionally rewrote two lines of `DisplaySortInControllers.baseline.txt` purely from unstable ordering.

**How to apply:** Take the `+ <locator>` lines out of the test's failure message, `sort -u` them (the message prints each twice), append a comment block in the file's existing style naming the issue and stating what makes the violation acceptable, then append the locators. Re-run only that rule's test to confirm. If the new entries are *not* intentional, fix the code instead — that is what the file header means by "do not add lines to silence it". See [[no-analyzer-suppressions]] and [[analyzer-exceptions-via-attributes]] for the analyzer-side equivalents.
