# PROPOSED Frozen Section Inventory — G0 decision draft

> **Status: PROPOSED — awaiting Peter's confirmation.** Drafted 2026-08-03 during the
> overnight G0 map run, from the [first audit pass](2026-08-03-g0-first-audit/), the
> [dependency DAG](2026-08-03-section-dependency-dag.md), and the
> [demolition inventory](2026-08-03-demolition-inventory.md), all @ `5a9bbe198`.
> The G0 checklist item "Section inventory frozen" closes when this doc's decisions are
> confirmed (or amended) and back-propagated to the plan tracker **and**
> `reforge.surface-score.json` in the same PR.

## Why this needs a decision

Three sources of truth disagree about what the sections *are*:

1. The **plan tracker** (35 rows incl. two planned-new).
2. **`reforge.surface-score.json`** (the operational section map used by every Reforge
   audit) — missing four sections that exist in code (`Gate`, `Surveys`, `SystemSettings`,
   `ICalFeed`), and folding several tracker rows into larger groups.
3. **Code layout** (namespaces/folders), which has kept moving since the tracker was
   drafted (e.g. #685 folded Profiles' non-picture surface into Users).

The overnight audit ran against taxonomy (1); the DAG against (2)+(3). Where they
disagree is exactly the freeze decision.

## Proposed inventory

### A. Rows confirmed as-is (no change) — 22 vertical + 2 horizontal

Agent, Budget, Calendar, Campaigns, Camps, CityPlanning, Containers, Events, Expenses,
Feedback, GoogleIntegration, Governance, Issues, Notifications, Scanner¹, Shifts, Store,
Survey², Teams, Tickets; horizontals AuditLog, Auth; shared contract Users; orchestrator
Onboarding³.

¹ Scanner stays a section (safety-critical negative invariant "never a check-in gateway"
deserves its own docs/tests home) even though the DAG groups its controller under
`Platform` — challenge if you disagree.
² `reforge.surface-score.json` spells it `Surveys`; code and tracker say `Survey`.
Proposal: standardize on **Survey** and fix the config key.
³ Onboarding is folded into `Users` in `reforge.surface-score.json`; the DAG recommends
splitting it back out as its own (orchestrator) entry to keep its fan-out edges visible.
Proposal: **split it out in the config**, matching the tracker.

### B. Proposed merges / renames (tracker changes)

| Current row(s) | Proposal | Rationale |
|---|---|---|
| Profiles (separate shared-contract row) | **Merge into Users row** (label it `Users/Profiles ("Humans")`) | #685 + `users-profiles-one-section` hard rule: one ownership section; the audit found the same 2 entity-leak baseline rows and the same interceptor on both rows — they are one work surface. |
| Holded (separate row) | **Merge into Finance** (Holded = connector client inside Finance) | Audit found Holded owns zero tables; its migration-baseline entries belong to Finance's table set. DAG maps `Holded*` symbols to the Finance section. |
| Mailer (separate row) | **Merge into Email** (`Email/Mailer`) | Config already folds `Mailer/**` paths into Email; Mailer owns no tables (MailerLite is system of record). Audit scored both ✅/✅ — merging loses nothing. |
| LegalAndConsent | **Keep scope, decide the name** (`LegalAndConsent` vs config's `Consent`) | Pure naming drift between tracker and config; pick one and align both. No scope change proposed. |
| Guide, Debug | **Demote from ladder rows to Platform-bucket entries** (keep Debug's horizontal *audit* — it still must not reach into verticals — but no G2/G4/G5 ladder of its own) | Neither owns tables; both are controller+service shells (audit: Guide G1 gap is a stale doc; Debug's gaps are doc/test hygiene). A ladder row with no tables has nothing to do at G2/G4. |
| Cantina | **Decide: keep as vertical, or reclassify as a read-composition feature of Shifts** | Audit: Cantina owns **zero tables** — it is pure read-composition over Shifts+Users, and its G1 gap is that it injects the full `IShiftManagementService` because Shifts has no read split. If it keeps a row, its ladder is G1/G3-only like other table-less sections. |

### C. Proposed additions (rows that exist in code but not in the tracker)

| Add | Kind | Evidence |
|---|---|---|
| Gate | vertical | Landed via #1066; absent from tracker and `reforge.surface-score.json`. Needs both. |
| SystemSettings | vertical | Own service/table surface per DAG; unaudited tonight — needs a first-audit pass once frozen in. |
| ICalFeed | vertical (or Calendar sub-feature — decide) | Exists in code, unmapped in config. If it's Calendar's export surface, merge into Calendar instead. |
| Dashboard, Search, Gdpr, Admin, Platform | **decide: sections vs nav-holders/crosscuts** | DAG found real code under each, but `/Admin/*` is by rule a nav holder, not a section; Dashboard/Search/Gdpr smell like orchestrator/crosscut surfaces. Recommend: classify Dashboard + Gdpr as orchestrators, Search as Platform infrastructure, Admin stays a nav holder — but these are architecture calls for Peter. |

Settings (#864) and Shortlinks (#810) stay as planned-new rows, unchanged.

### D. Config fixes required regardless of the above decisions

- Add `Gate`, `Survey(s)`, `SystemSettings`, `ICalFeed` to `reforge.surface-score.json`
  (their `surface-score` numbers are currently namespace-fallback noise).
- Split `Onboarding` out of `Users` in the config (per A³).
- Align the `Consent`/`LegalAndConsent` and `Survey`/`Surveys` names with the tracker
  decision.

## What the freeze unblocks

Per the plan, G0's "inventory frozen" predicate gates everything else: the demolition
inventory and audit files are keyed by section name, `/section-gate` will key off this
list, and G5 assembly names are permanent. Deciding B/C now avoids re-keying those
artifacts later.
