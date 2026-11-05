# Look-and-feel rubric

Each page is scored 1 to 5 per width (5 is best) from its screenshots only, never from the computed metrics. Admin-layout pages get no mobile score.

| Metric | 1 | 3 | 5 |
| --- | --- | --- | --- |
| calm (not overwhelming) | Many competing blocks, no obvious place to start | Busy, but a primary area is findable | One clear focus; secondary content recedes |
| flow | Eye jumps around; related items far apart; no reading order | Mostly top to bottom, a few orphan blocks | Clear reading order; grouping matches meaning |
| hierarchy | Everything has the same weight; title, totals and detail look alike | Title stands out, key numbers or actions do not | The thing that matters most is the most prominent |
| scannability | Walls of text or numbers; must read every cell | Some structure, but labels or alignment slow scanning | Answer found at a glance; numbers aligned, labels short |
| consistency | Looks like a different app (own colours, spacing, controls) | Mostly house style, a few one-offs | Indistinguishable from the best pages |
| mobile (mobile width only) | Unusable: sideways scroll, clipped content, tiny controls | Usable with effort | Reads and works naturally on a phone |
| overall | Ugly or broken | Plain but fine | Pleasant; you would show it off |

Scores 2 and 4 sit between the anchors.

## Problems

A short list of concrete defects, each tagged:

- `bug`: broken, overlapping, cut off, unstyled, error state shown as layout
- `ux`: confusing, hard to act on, wrong emphasis
- `polish`: spacing, alignment, inconsistent styling

Name the element ("the totals row", "the second card"), not the page in general. Empty states caused by test data (no records on QA) are not problems unless the empty state itself looks broken.

## Calibration anchors

Scored by Peter on 2026-11-05 (QA, desktop and mobile viewed together). Each run captures these URLs too, and the scorer compares against the run's own screenshots of them.

| URL | calm | flow | hierarchy | scannability | consistency | mobile | overall |
| --- | --- | --- | --- | --- | --- | --- | --- |
| / | 3 | 2 | 2 | 2 | 3 | 3 | 2 |
| /Teams | 3 | 3 | 2 | 3 | 2 | 2 | 2 |
| /Budget | 4 | 4 | 4 | 4 | 4 | 3 | 4 |
| /Governance | 4 | 3 | 3 | 4 | 3 | 2 | 3 |
| /Finance | 3 | 4 | 4 | 4 | 3 | 2 | 4 |

Note: on QA the /Finance page shows "never synced" error cards in place of data.
