# Open Issue Spec Quality Audit — 2026-04-09

## Summary

| Category | Count |
|----------|-------|
| UNDERSPECIFIED | 5 |
| NEEDS REFINEMENT | 22 |
| READY | 27 |
| SKIP | 6 |

## UNDERSPECIFIED — Too vague to code against

### #468 — Make "Verify Email Address" more visible
- Body is a screenshot and "Button or larger text pls". No labels, no acceptance criteria, no spec of where/how.
- Missing: exact UI change, which page, target element.

### #464 — Explain Barrio requirements when signing up
- User request with ideas but no labels, no AC, no data model, no content source.
- Missing: who writes the content, admin-editable or hardcoded, which page, no AC.

### #83 — Add other OAuth options, additional to Google
- Two sentences. Largely duplicated by #99 and #98.
- Missing: which providers, UI spec, data model implications.

### #86 — Voting system: bylaw-compliant member voting
- Has requirements sections but 6 major open questions unanswered (anonymity, bylaw text, electronic voting legality, proxy voting, MVP scope).
- Cannot be coded without resolving these.

### #77 — Reasons why an Asociado is accepted (or applying)
- Informal request, no structured spec. On hold waiting for Pablo.

## NEEDS REFINEMENT — Has a spec but key details missing

### High priority (likely to be worked soon):

- **#452** — Shift period filter bug. Root cause unknown, spec lists 3 "likely causes." Needs investigation first.
- **#454** — Google Groups 403 handling. "Mark email as rejected" approach unspecified — new field? New table?
- **#453** — Google Group email delivery preferences. Lists 4 options without committing to one.
- **#200** — MailerLite sync. Spec fragmented across 4 comments. Heavy overlap with #450.
- **#206** — Ticketing contact merge. Body vs comment describe two different scopes.

### Medium priority (larger features):

- **#382** — Ticket transfer. Presents 3 options, says to research first. Research task disguised as implementation.
- **#253** — On-site date tracking. Two data model alternatives without choosing.
- **#254** — Multi-language campaign templates. Missing migration strategy for existing data.
- **#162** — Shift notifications. Comments add 4+ triggers beyond the original 8. Scope boundary unclear.
- **#161** — Shift exports, iCal feed, stats. Several sub-features underspecified (cantina CSV, stats dashboard).
- **#159** — Invoice generation. No Invoice entity schema, no legal format details.
- **#158** — Barrio services store. Very thin spec for XL issue. No entity definitions.
- **#157** — Bus ticket sales. References external spec doc. Phase/threshold logic vague.
- **#150** — Event guide. POC feedback not incorporated into AC.
- **#110** — Inbound bounce parsing. Inbox connection method unspecified.
- **#450** — MailerLite bidirectional sync. Blocked on #433-436. Overlap with #200 confusing.
- **#446** — Human-link tag helper. Depends on #447. Line numbers may be stale.
- **#244** — Notification inbox. Group resolution model has no data model. Digest frequency in comment not in AC.
- **#218** — Event audience segmentation. Depends on underspecified #206/#205. Segment builder vague.
- **#149** — Figma design integration. Meta-issue, design analysis required first.
- **#99** — Local username/password auth. Missing login page UI flow, account linking details.
- **#98** — Magic-link auth. IP binding unusable on mobile. Account lifecycle unclear.
- **#100** — SSO/JWT issuance. No concrete client app to test against. Claims/key management unspecified.
- **#205** — Marketing-only contacts. Data model option not confirmed.

## READY — Clear enough to hand to a coding agent

#467, #466, #463, #459, #458, #457, #455, #449, #445, #444, #436, #435, #434, #432, #429, #428, #427, #426, #425, #422, #420, #419, #418, #416, #403, #402, #401, #127, #187

## SKIP — Blocked, placeholders, or duplicates

- **#163** — Placeholder for feedback gathering, not implementable.
- **#279** — Explicitly blocked:spec-incomplete.
- **#198** — Paused, code being replaced.
- **#204** — Blocked on legal review.
- **#33** — Duplicate of #82.
- **#82** — Open questions need resolving first.
