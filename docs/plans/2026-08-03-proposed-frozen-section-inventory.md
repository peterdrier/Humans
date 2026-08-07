# Frozen Section Inventory — G0 decision record

> **Status: CONFIRMED by Peter, 2026-08-03** (morning review of the overnight G0 map run).
> This closes the G0 "Section inventory frozen" checklist item. Drafted from the
> [first audit pass](2026-08-03-g0-first-audit/), the
> [dependency DAG](2026-08-03-section-dependency-dag.md), and the
> [demolition inventory](2026-08-03-demolition-inventory.md), all @ `5a9bbe198`.
> Back-propagation to `reforge.surface-score.json` + first audits for the newly admitted
> sections are follow-up work items (tracked below).

## Principles set during the freeze (now memory atoms)

- **Sections are logical units** of the app that can be worked on independently —
  **owning tables is not a requirement**. Thin sections representing a logical construct
  (plus a little GUI/agent code) are fine. → [`memory/architecture/sections-are-logical-units.md`](../../memory/architecture/sections-are-logical-units.md)
- **Vendor connectors are never merged with app code.** External-vendor sections stay
  their own sections even with a single consumer — vendors can change.
  → [`memory/architecture/vendor-connectors-own-sections.md`](../../memory/architecture/vendor-connectors-own-sections.md)
- Section names lean **plural** where natural (hence Surveys); no systematic rename pass
  of existing singular names.

## Decisions

| Question | Ruling |
|---|---|
| Profiles | **Folds into Users** (one shared-contract row: Users, incl. Profiles). |
| Holded | **Stays separate** — vendor connector (Finance is likely its only consumer; irrelevant). |
| Mailer | **Stays separate** — vendor connector (MailerLite), not part of Email. |
| Onboarding | **Stays separate** (orchestrator) for now; config must stop folding it into Users. |
| LegalAndConsent vs Consent | **Consent** ("cleaner"). |
| Survey vs Surveys | **Surveys** (plural convention). |
| Gate | **Is a section** — add row + config entry. |
| SystemSettings | **Becomes the Settings section** (assumed: absorbs the existing SystemSettings code and the planned #864 work as one row — flag if wrong). |
| ICalFeed | **Goes into Calendar** (its export surface, not a section). |
| Admin | **Not a section** — nav holder (per hard rules). |
| Platform | **Not a section** — it was only ever `reforge.surface-score.json`'s catch-all bucket for shared web-shell paths; with Guide/Debug/Scanner confirmed as sections it mostly dissolves. Remaining truly-shared infra stays an explicit non-section grouping in the config. |
| Dashboard | **Not a ladder section** — a GUI construct/holder. Future direction (not committed): sections contribute `DashboardPanel`-style pieces to it. |
| Gdpr | **Orchestrator** (manages the export/erasure bits across sections). |
| Search | **Orchestrator**. |
| Cantina | **Own section.** Roadmap: the food-preference bits move here from Users/Profiles as the identity overload thins out into sections keyed off `UserId`. |
| Guide / Debug / Scanner | **All stay sections** (the audit's demote-for-thinness suggestion is rejected — see principles). |
| DevLogin / DevSeed | Move to a **new Development section** that is **not loaded in prod**. (Resolves the Debug-scope question from the audit: they are not Debug's problem; the horizontal-purity concern is answered by prod-exclusion.) |

## Resulting canonical section list

**Vertical:** Agent, Budget, Calendar (incl. ICalFeed), Campaigns, Camps, Cantina,
CityPlanning, Containers, Debug, Development *(new; dev-only, never loaded in prod)*,
Email, Events, Expenses, Feedback, Finance, Gate *(new row)*, Governance, Guide, Issues,
Notifications, Scanner, Settings *(new row; ex-SystemSettings + #864)*, Shifts,
Shortlinks *(planned, #810)*, Store, Surveys, Teams, Tickets.

**Vendor connectors (vertical, never merged):** GoogleIntegration, Holded, Mailer.

**Horizontal:** Auth, AuditLog.

**Shared contract:** Users (incl. Profiles) — the "Humans" section.

**Orchestrators:** Onboarding, Gdpr *(new row)*, Search *(new row)*.

**Consent** (renamed from LegalAndConsent): vertical.

**Not sections:** Admin (nav holder), Dashboard (GUI holder; future panel-contribution
model), Platform (config bucket, dissolved).

## Follow-up work items

1. Back-propagate to `reforge.surface-score.json`: add Gate, Surveys, Settings,
   Development, ~~**Gdpr, Search**~~; split Onboarding out of Users; **map each vendor
   connector's client surface to its own section — `IHoldedClient` and the Holded API-client
   types to `Holded`, `Interfaces/Mailer/**` + `Services/Mailer/**` to `Mailer`**; rename
   Consent key alignment; retire the Platform bucket per above. (Config PR, not this docs PR.)

   *(~~Gdpr and Search added 2026-08-03~~ — **struck 2026-08-05, premise was wrong.** That
   addition assumed both were absent from the config; the G0 audit checked and both are already
   registered (`'Gdpr' in data['sections']` → `True`, likewise `Search`), each with its own
   paths/symbols/`serviceInterfaces` mapping — so neither was ever on the namespace-fallback
   grouping this item exists to eliminate. **Still genuinely outstanding: `Gate`, `Settings`
   and `Development`** — all three confirmed `False` by the same check
   ([`Gate.md`](2026-08-03-g0-first-audit/Gate.md) scope note,
   [`Settings.md`](2026-08-03-g0-first-audit/Settings.md) G1 gap #2,
   [`Development.md`](2026-08-03-g0-first-audit/Development.md) G1 gap #3). Note the ordering
   dependency: `Settings` can't be registered until the `Settings`-vs-`SystemSettings` naming
   question is settled, or the config key has to be redone.)*

   ⚠️ **Scope the Holded split by symbol, not by name prefix.** A literal "move every `Holded*`
   path out of `Finance`" would drag `Services/Finance/HoldedFinanceService.cs`,
   `IHoldedFinanceService`, `IHoldedRepository` and the Finance-owned ledger DTOs, entities and
   EF configurations out of the section that actually owns them. **Finance owns the ledger;
   `Holded` owns only the vendor API client that Finance calls.** The dependency DAG's
   `Finance→Holded` edge exists precisely because `HoldedFinanceService` (Finance) injects
   `IHoldedClient` (Holded) — collapsing the two back together erases the edge this split is
   meant to expose.
2. ~~First-audit scorecards for the newly admitted rows: Gate, Settings, Development,
   Gdpr, Search (the G0 first-audit checklist item's scope caveat tracks this).~~
   **Done 2026-08-05** — all five scorecards live in
   [`2026-08-03-g0-first-audit/`](2026-08-03-g0-first-audit/) and the section tracker in
   [`2026-06-13-q3-transition-plan.md`](2026-06-13-q3-transition-plan.md) links them.
   Their own gap lists carry what each audit found; don't requeue the audits here.
3. `docs/sections/` file renames: `LegalAndConsent.md` → `Consent.md`,
   `Survey.md` → `Surveys.md` (+ link sweeps).
4. Extract DevLogin/DevSeed into the Development section with prod-excluded loading.
