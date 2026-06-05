# Barrio Shift Obligations — re-scope memo (response to review)

**Date:** 2026-06-05
**Status:** Decision memo for Peter — re: PR #872 (Barrio Shift Obligations)
**TL;DR:** The reviewer is right that the obligation engine is over-built and over-coupled. We agree on cutting most of it. The one thing the proposed coordinator-side inversion *can't* do is serve the **barrio-management role** (the `/Barrios/Admin` overseer), because consolidated cross-function oversight is inherently a Camps-side read. This memo lays out the two view shapes, what to cut (with the purpose and the cost of each cut), and the single decision that's left.

## The feedback being addressed

> "That one felt weird. A lot of tech for a very specific case. Too many cross section connections for comfort. I added a user→camp lookup. Then maybe a coordinator view showing summary by Rota of people involved w/ their camp?"

Two fair points and one proposal:
1. **Over-built** — two tables, a repository, an orchestration service, a new cross-section interface, email template/custom variants, a modal, name pickers, per-barrio overrides, grid-exemption logic, a barrio-lead view — for what is essentially "are camps pulling their weight on shifts?" The scope accreted through iteration without a step-back.
2. **Too many cross-section connections** — even though every hop goes through a compliant `I…ServiceRead`, the feature still reaches **Camps → Shifts, Teams, Users, Email, Audit**. Rule-compliant ≠ comfortable; a feature that must touch five sections is a coupling smell.
3. **Proposal:** flip it — a coordinator-facing view on the Shifts side that takes a rota's signups and annotates each with their camp, using the new `user→camp` lookup. One read, in the natural direction.

We accept (1) and (2). The gap is in (3): see below.

## Three audiences, not one

| Role | Wants to know | Natural direction |
|---|---|---|
| **Function coordinator** (e.g. Power coordinator) | "On *my* rota, who signed up, from which camps?" | per-rota → people → camp (Shifts-side) |
| **Barrio-management coordinator** (runs `/Barrios/Admin`, oversees all barrios) | "Across all barrios and all functions, who's short?" | per-barrio → members → signups (Camps-side) |
| Individual camp lead (de-prioritised) | "How is *my* barrio doing?" | per-barrio, scoped to own |

The proposed inversion serves the **function coordinator** well. It structurally **cannot** serve the **barrio-management coordinator**: that role doesn't coordinate the function teams, so a per-team coordinator view is gated away from them — and a consolidated cross-function picture can't be assembled from per-team pages anyway. Overseeing barrios is that role's whole job, so dropping their view by omission is the part worth flagging.

## View comparison: barrio-based vs coordinator-based

| Dimension | **Barrio-based** (Camps-side) | **Coordinator-based** (Shifts-side) |
|---|---|---|
| Primary audience | Barrio-management coordinator (+ optionally camp leads) | Function-team coordinator + volunteer coordinators |
| Orientation | per-barrio → members → signups, across all functions | per-rota → signups → annotate with camp |
| Question answered | "Across all barrios, who's short on which functions?" | "On my rota, who's signed up and from which camps?" |
| Lives in | Barrios/Camps section | Shifts section |
| Who can see it | CampAdmin / barrio-management role (one gate) | Coordinator of *that* team, or volunteer coordinator |
| Serves `/Barrios` oversight role? | ✅ yes — its reason to exist | ❌ no (not a function-team coordinator) |
| Serves function coordinator? | partial (one column, not their home) | ✅ yes — its reason to exist |
| Consolidated cross-function picture? | ✅ one screen | ❌ one rota/team at a time |
| Cross-section coupling | Camps → Shifts (counts) — **irreducible for this audience**, 1 lean read | Shifts → Camps (`user→camp`) — 1 read, preferred direction |
| Depends on | which users belong to which barrio (sparse-data dependency) | same `user→camp` lookup |
| "Email the leads" fit | natural (barrio-centric) | awkward (coordinator-centric) |
| New surface (lean) | a read-only counts view, no tables | a camp column on the existing rota signup list |

These are not competitors — they serve different roles. You likely want **both**, each as a thin read. Only the barrio-based one is irreplaceable by inversion.

## What to cut (the obligation engine) — purpose & consequence

Everything below is the layer on top of basic visibility. Cutting it is what takes this from a five-section system to a couple of lean reads.

| Component to cut | Current purpose / motivation | Consequence of cutting |
|---|---|---|
| **`shift_obligations` config table** (+ "obligation function" concept: target team/rota, applicability, default required count, role-slug) | Make it general & configurable — "barrios owe X shifts to function Y," not Power-hardcoded; admin-maintained without a migration | No registry of "what's an obligation" or required numbers. Lean view just shows counts for the teams/rotas you point it at. Also loses per-function → function-lead mapping (only used for reminders). |
| **`camp_season_shift_obligations` override table** | Let a specific barrio carry a custom required count | Nothing to override once "required" is gone. Lose per-barrio tailoring — moot under pure visibility. |
| **The "required / owed" concept** (pass/fail compliance) | Turn raw counts into an automatic judgment ("3/6 — not met"); drive coloring + "remind all non-compliant" | View becomes **visibility, not compliance** — "Barrio X has 3 on Power," admin decides if that's enough. Lose auto met/unmet + bulk-act-on-failing. Upside: no invented required numbers to maintain/defend. |
| **Two-layer grid filtering** (Norg exemption + `ElectricalGrid` applicability) | Only grid-connected barrios owe Power; Norg/core-org owes nothing — keeps compliance honest | With no "owes," nothing to exempt. Show counts for all; admin ignores off-grid ones (keep the grid column). Minor: a self-powered barrio's "0 on Power" needs human context instead of auto-n/a. |
| **Under-membered ⚠ flag** | Warn that a count is unreliable (few members joined in-app) | Lose the flag, but keep the signal cheaply via the **`joined · expected` member column**. Mostly preserved. |
| **Reminder emails** (per-barrio + bulk + template/custom preview modal + audit + resx) | The "email the barrio leads + power lead to prompt them" lever — *one of the three original asks*; one-click nudge | **Biggest functional loss** — drops the prompting half. Admin chases barrios out-of-band. Re-addable later as a small standalone "resolve leads → send," independent of any tables. The only cut that removes something explicitly requested. |
| **Functions config page** (team/rota name pickers, slug dropdown, Edit, inline override) | Set up/maintain obligation functions by name, no migration | Nothing to configure once the registry is gone. Lean view shows "all teams/rotas camps touch" (zero config) or a tiny curated list. Lose the picker UX. |
| **`IShiftObligationService` + `ShiftObligationRepository`** | Orchestrate compliance + reminders + config; own the two tables | Repo dies with the tables; service shrinks to a small read or folds into an existing Camps read path. This is the multi-section fan-out — it largely disappears. |
| **`IShiftServiceRead` additions** (team/rota count methods, rota list) | The new cross-section surface that needed sign-off | Mostly replaceable: lean view uses the `user→camp` lookup over existing signup reads. The bespoke count/rota-list + picker reads go. Cross-section connection shrinks from "several new methods" to "one lookup." |
| **Barrio-lead scoped self-service view** (`/Barrios/{slug}/ShiftObligations`, individual leads) | Let an individual camp lead self-check | Audience is the **management** role, not individual leads — likely unneeded. Cutting it means leads rely on the management admin/coordinators. Keep only if per-lead self-service is wanted. |

## What survives (the lean core)

- A **barrios × functions signup-count view** for the barrio-management role — "Barrio X: 3 on Power, 0 on shit-ninja" — read-only, **no new tables**, built on the `user→camp` lookup + a single signup read.
- The cheap **`joined · expected`** member column (carries the "data may be incomplete" signal).
- *(Optionally)* the **coordinator-side per-rota-by-camp** view for function coordinators.
- *(Optional follow-on)* the **email-the-leads** lever, as a small standalone if the prompting need is real — not gated behind any of the cut machinery.

Net: from a five-section obligation engine to **one lean Camps-side oversight view + (optionally) one lean Shifts-side coordinator view**, both read-only.

## The decision left to make

1. **Does the barrio-management role get a consolidated cross-function view?** If yes (recommended — it's that role's job), accept **one** lean `Camps → Shifts` read. If no, the coordinator-side view is the only window and barrio oversight has no consolidated picture.
2. **Keep the "required / owed" framing, or pure visibility?** Recommendation: pure visibility first (counts + member column); add "required" only if eyeballing proves insufficient.
3. **Re-add reminders later?** They're the one explicitly-requested capability the cut removes; can return as a standalone bolt-on independent of the tables.
