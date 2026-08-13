# Burn Demo Pages — Design

**Date:** 2026-08-12
**Goal:** Live, demoable surfaces for an in-person pitch at Burning Man (~2 weeks out). Audience: Burning Man org leadership and regional burns running on spreadsheets — mixed technical level. Secondary goal: a standing answer to the "it was vibe coded" dismissal.

Demo environment: **production**, live over cell/Starlink, using Peter's admin + regular logins to show role contrast. No offline fallback in scope.

## Deliverables (priority order)

### 1. Tour — new section, public `/Tour` page

A plain-language "What is Humans" page. The URL you hand someone; must stand alone without login.

- **New vertical section** `src/Sections/Humans.Tour` following the #866 section-project pattern. Deliberately exercises the add-a-section process end to end (project, DI wiring, routing, section doc, `_Index.md` row).
- **Content-only section**: no domain model, no tables, no DbContext, no repository. Controller + views + static content. Section doc at `src/Sections/Humans.Tour/Docs/Tour.md` per `SECTION-TEMPLATE.md`, with data model / invariants sections stating "none — content only".
- Route `/Tour`, `[AllowAnonymous]`.
- **Content structure:**
  - Hero: one paragraph — membership management for burn orgs; the full lifecycle from signup to teams, shifts, tickets, and money.
  - Capability grid, grouped by what a burn org worries about, plain language, no jargon:
    - **People** — onboarding, profiles, consent/GDPR (export + right-to-deletion)
    - **Organize** — teams, shifts, camps, city planning, event guide
    - **Money** — store, expenses, budget, Holded ledger integration
    - **Govern** — board roles with temporal assignments, tier applications + votes, full audit trail
    - **Communicate** — email outbox, campaigns, notifications, 6 languages
  - Closing section **"Humans for your burn"**: 18 sections already live as independent projects; roadmap to config-per-deployment — each burn picks its sections, Microsoft 365 instead of Google Workspace, additional languages (e.g. Dutch). Honest framing: modularity is real today, per-burn configuration is the direction of travel.
- **Nav:** add `/Tour` link to the anonymous navbar (which today shows only Login) and to the Welcome landing page. Visible to authenticated users too (navbar; placement implementer's choice).
- No live member data. Static safe aggregates only if any (e.g. "18 sections"); hardcoded text is fine.

### 2. /Admin dashboard — add tiles

`/Admin` is already a live stats dashboard (member totals, state partition, shift coverage, online-now, language distribution, membership Venn, recent-audit feed). Add tiles that show off breadth and automation volume:

- **Total audit events** — automated-action volume. `IAuditViewerService.GetPageAsync` already returns `TotalCount`; prefer a reuse-first path before adding any interface method.
- **Emails sent** — `IEmailOutboxService.GetOutboxStatsAsync` exists.
- **Teams count** — likely available via existing list surface (in-memory `.Count` at 500-user scale is the house style).
- **Expenses / store totals (€)** — check the Expenses/Store section read surface first; these sections had no Count/Stats methods in the initial sweep.

Rules: cross-section calls via `I<Section>ServiceRead` where available; any genuinely new interface method needs Peter's approval per reuse-first discipline — audit before adding.

### 3. /About — add Engineering section + dev stats

Extend the existing `/About` page (already anonymous-reachable; add it to the anonymous nav or link it from `/Tour`).

- **Engineering story** (the anti-"vibe-coded" exhibit): Clean Architecture with strict layering; vertical section isolation; hand-written hard rules as constitution; **34 custom Roslyn analyzers** (HUM0001–HUM0034) enforcing call-site rules in-editor; architecture-test baselines; Stryker mutation testing; per-PR preview deploys with cloned databases; full audit trail. Narrative: **AI-accelerated, engineer-directed** — 25+ years of engineering discipline encoded as machine-enforced rules the AI must obey.
- **Dev stats panel** from a committed JSON snapshot — no live GitHub API (rate limits, playa connectivity):
  - Generator script under `scripts/` (git + `gh` queries): total commits, merged PRs, closed issues, contributor breakdown by lines committed, test count, analyzer count, snapshot date.
  - Snapshot JSON committed to the repo; About page renders it server-side (mechanism implementer's choice — keep it simple, cache per house caching pattern if a service is involved).
  - Rerun manually before the demo; snapshot date shown on the page so staleness is visible.

## Non-goals

- Localization — all three surfaces English-only for now (site precedent exists; audience is external). resx keys can follow later.
- Offline/local demo mode, screenshots pipeline, video.
- Live GitHub API integration.
- Per-burn configuration itself — the Tour page *describes* the roadmap; no config plumbing is built.

## Demo-prep checklist (not code)

- Rerun the dev-stats script and commit the snapshot just before leaving.
- Promote to prod (`/pr-prod`) and verify `/Tour`, `/About`, `/Admin` on production.
- Verify both logins (admin + regular) work and show the intended contrast.

## Implementation order

Tour section → Admin tiles → About/Engineering. One branch, one PR (`burn-demo`).
