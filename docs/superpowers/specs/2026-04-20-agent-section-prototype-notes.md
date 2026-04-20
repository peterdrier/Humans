# Agent Section — Phase 0 Prototype Notes

**Issue:** #526
**Date:** 2026-04-20
**Scope:** Feasibility spike for the in-app AI helper agent.
**Recommendation:** **GO** — build Phase 1, with the ITPM caveat below resolved.

## What was run

- `tools/agent-spike/run.py` against 20 curated questions (`questions.yaml`) covering onboarding, membership tiers, teams, roles, legal/consent, tickets, shifts, governance, camps, edge cases, and an off-topic refusal test.
- Each question was answered by **Claude Sonnet 4.6** and **Claude Haiku 4.5** independently, with Anthropic prompt caching enabled on the corpus.
- Two corpora were exercised:
  - **Full corpus** (`transcripts-full-corpus/`, 2 completed answers before ITPM throttle): all `docs/sections/*.md`, all `docs/features/*.md`, `AccessMatrixDefinitions.cs`, `SectionHelpContent.cs` — ~137K input tokens.
  - **Rate-limit-friendly corpus** (`transcripts/`, full 40-answer sweep): 7 high-signal section invariants (Onboarding, Teams, LegalAndConsent, Governance, Shifts, Tickets, Profiles, Admin) + `AccessMatrixDefinitions.cs` — ~24K input tokens, 25-second inter-call throttle.

## Critical finding: ITPM rate limit was missing from the design spec

Anthropic's input-tokens-per-minute (ITPM) limit **counts cache reads**, not just fresh input. On the user's current Anthropic org (Tier 1 default: 30K ITPM Sonnet, 50K ITPM Haiku) the first run 429'd after the first two answers — a ~137K-token request consumes 4× the Sonnet minute budget on a single call.

Implications for the production design:

- **The spec's proposed ~45K preload is infeasible on Tier 1.** Every request would 429.
- **Paths forward (pick one, spec to be updated):**
  1. **Tier 2 on production org** (recommended). $40 lifetime spend + 7 days elapsed → auto-promoted. Sonnet jumps to 450K ITPM. Original spec's preload unchanged. Trivial to confirm in the Anthropic console.
  2. **Cap preload at ~25K tokens for Tier 1 safety.** Loses coverage; more questions force dynamic fetches (acceptable — matches the hybrid architecture anyway).
  3. **Haiku as primary.** 50K ITPM accommodates a ~45K preload with modest headroom. Quality risk is small (see below); cost is ~3× lower.

The design spec's "Rate limiting" section currently covers *per-user caps* (abuse prevention) but not *per-org provider ITPM*. That omission must be fixed in the spec before Phase 1.

## Quality — across both models, and across all 20 questions

**Both models are production-viable.** No hallucinated behavior, no fabricated URLs, appropriate personalization, clean refusals.

### Representative comparisons

**Q02 — Colaborador vs Asociado (in-preload, Spanish locale):**
Both correctly identified that Tomás (Volunteer) can only apply for Colaborador now; both explained the 2-year term, the Board-vote workflow, and the Asociado prerequisite (must be an approved Colaborador first). Sonnet produced a comparison table; Haiku produced prose. Both in Spanish.

**Q13 — Camp polygon (NOT in preload, Catalan locale):**
Both correctly said "I don't have information about that" and offered feedback-widget handoff. No attempt to guess. Both in Catalan. Exactly the graceful-degradation behavior we need.

**Q14 — Change email (edge case, Spanish):**
Both pointed to `/Profile/Me/Emails` — **URL verified real** in `ProfileController.cs:518`. No hallucination. Sonnet was terser; both correct.

**Q15 — Off-topic (Spanish election prediction):**
Both refused cleanly, both redirected to in-scope topics, both in Spanish. Exactly what the system prompt asks for.

**Q16 — Board voting details (complex governance, English, in-preload):**
Both covered the dual workflow (individual Board votes → collective decision note → individual votes deleted for GDPR), the term-expiry rule (Dec 31 of odd year ≥2 years out), and the prereq structure correctly. Sonnet slightly tighter; Haiku covered the same ground with more prose and emoji.

**Q18 — Budget visibility (Coordinator question, budget NOT in preload):**
Both admitted gaps in their knowledge, but both correctly inferred what they *did* know about Coordinator scoping (department-level authority) and offered feedback handoff for specifics. No confident-wrong answers. This is the behavior we want when the hybrid architecture's dynamic fetch isn't wired up yet.

**Q19 — Delete account (Spanish, GDPR topic):**
Sonnet cited `/Profile/Me/Privacy` — **URL verified real** at `ProfileController.cs:780`. Referenced GDPR Article 15 for data export. Gave a precise workflow.

**Q20 — Multi-step onboarding walkthrough (personalization-heavy):**
Both referenced Felipe's actual pending documents (Privacy Policy + Member Contract), explained the dual-track (docs + coordinator review) correctly. Sonnet's output was sharper; Haiku added a (slightly unnecessary) "complete your profile" step and hedged more ("look for a section called..." instead of naming it directly).

### Summary differences, Sonnet vs Haiku

| Dimension | Sonnet 4.6 | Haiku 4.5 |
|-----------|------------|-----------|
| **Correctness** | Tight, grounded, direct | Equally correct on core facts |
| **Precision** | Names things confidently (`Legal / Consent`) | Hedges more (`a section called Legal Documents or Consent`) |
| **Verbosity** | More concise | Longer answers, more emoji, more "next steps" sections |
| **Personalization** | References user state directly | Sometimes suggests steps the user has already completed |
| **Tone** | Professional | Warmer, more conversational |
| **Refusal quality** | Crisp | Crisp but adds reassurance |
| **Locale** | Correct Spanish/Catalan | Correct Spanish/Catalan |
| **Latency** | 3-9s (median ~5.5s) | 1.5-5.5s (median ~3.5s) |

No category-level failures from either model across the 20 questions.

## Cost — confirmed against the design-spec projections

Measured totals for the rate-limit-friendly corpus (~24K-token preload, 20 questions × 2 models, all cache-hits after the first call per model):

| Model | First turn (cache write) | Avg cached follow-up | Total (20 qs) |
|-------|--------------------------|----------------------|---------------|
| Sonnet 4.6 | $0.11 | $0.011 | **$0.30** |
| Haiku 4.5 | $0.035 | $0.0038 | **$0.11** |

Extrapolated to the projected usage pattern (5 sessions/day × 4 turns = 20 msgs/day), assuming each session pays a cache-write on the first turn and cache-reads for subsequent turns:

| Model | Estimated monthly cost |
|-------|-------------------------|
| Sonnet 4.6 | **~$20/mo** (vs spec projection $22) |
| Haiku 4.5 | **~$7/mo** (vs spec projection $7) |

Projections hold. Haiku is roughly **3× cheaper** per call on this workload; the quality gap does not justify that price differential for Phase 1 — go with Sonnet and reconsider if usage scales.

## Latency

Both are responsive enough for a chat widget:

- Sonnet p50 ~5.5s, p95 ~9s.
- Haiku p50 ~3.5s, p95 ~5.5s.

With SSE streaming (per the spec), first-token arrives much sooner — users will perceive it as near-instant. No action needed.

## Recommendations

### 1. Go — build Phase 1

The prototype clears the bar on correctness, grounding, refusal, personalization, and locale. Every URL we spot-checked was real. Graceful degradation works without the hybrid architecture's dynamic-fetch tools even being implemented. Cost and latency are acceptable. There is no feasibility risk that would block Phase 1.

### 2. Default model: Claude Sonnet 4.6

Haiku is surprisingly close on quality — but Sonnet's precision and concision matter for a support helper that humans will read quickly and trust to be correct. The ~3× cost differential is marginal in absolute terms ($20 vs $7/month). Make the model admin-configurable per the spec so we can revisit.

### 3. Resolve the ITPM constraint in the spec before Phase 1 kickoff

Update `2026-04-20-agent-section-design.md` to:

- Add an "Anthropic provider rate limits" subsection under § Guardrails or § Cost Model, covering ITPM (input-tokens-per-minute) explicitly.
- State the preload budget as a function of provider tier: at Tier 1, cap at ~25K; at Tier 2+, the ~45K preload is safe.
- Document the mitigation: verify production org is Tier 2+ before enabling the agent, or gate `AgentSettings.Enabled` behind a tier check / health probe.

### 4. Phase 2 readiness note

The FAQ/KB preprocessor job can't run until Phase 1 is generating real unresolved-handoff feedback entries. No change to the design; just a scheduling observation.

### 5. Follow-up issues to file on approval

- `agent-v1-base-build` — Phase 1 per the design spec, with the ITPM fix applied.
- `agent-v1-legal-doc` — add `DocumentKind.AgentChatTerms` and content.
- `agent-v2-faq-kb` — Phase 2 after Phase 1 has produced real usage data.

## Artifacts

- `tools/agent-spike/transcripts-full-corpus/` — two initial full-corpus answers before the 429s.
- `tools/agent-spike/transcripts/` — full 40-answer sweep with the rate-limit-friendly corpus, plus `summary.jsonl` and `cost_report.md`.
- `tools/agent-spike/run.py` — the script. Preserved under `tools/` so anyone can re-run after further corpus edits.
