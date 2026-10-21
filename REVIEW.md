# Code review

Catch consequential defects while concentrating human attention where judgment matters. A person owns every shipped change: Peter.

Apply the repository rules in [AGENTS.md](AGENTS.md), [Peter's hard rules](docs/architecture/peters-hard-rules.md), the reject list in [code-review-rules.md](docs/architecture/code-review-rules.md), the atoms in [memory/INDEX.md](memory/INDEX.md) whose trigger matches the change, and the touched section's `Docs/<Section>.md`. This policy adapts [How the Claude Code team does code review](https://claude.dev/blog/solving-code-review-at-anthropic/).

## Hard rules

No reviewer, author or agent may waive these. A PR that breaks one is blocked, whatever its size or tier.

1. **Migrations are never edited by hand.** Only `dotnet ef migrations add` writes migration files. Never edit, delete, rename or regenerate a migration that has merged to `main` (QA has applied it; QA is never reset from prod). A wrong merged migration is fixed by a new EF-generated migration on top. ([`no-hand-edited-migrations`](memory/architecture/no-hand-edited-migrations.md), [`migration-regen-after-rebase`](memory/architecture/migration-regen-after-rebase.md))
2. **No NOT NULL column on an existing table without Peter's explicit permission.** New columns on existing tables are nullable, and code treats null as "unanswered" or "not set". A required column needs Peter's written approval for that column, linked in the PR. ([`required-columns-need-approval`](memory/architecture/required-columns-need-approval.md))

Run the [`ef-migration-reviewer`](.claude/agents/ef-migration-reviewer.md) before a PR with a migration is opened, not after it merges ([`ef-migration-review-gate`](memory/process/ef-migration-review-gate.md)).

## 1. Establish intent and risk first

Understand the intended behavior, the sections and boundaries it touches, and plausible failures. Select the tier from the actual change:

| Tier | Scope |
|---|---|
| `tier:lightweight` | Docs, copy and changes without behavioral effects |
| `tier:standard` | Ordinary product behavior inside one section |
| `tier:thorough` | Authorization, personal data or GDPR paths, schema or migrations, Hangfire jobs, money (Finance, Budget, Expenses, Holded, Tickets), cross-section contracts, analyzers, CI or deployment |

Use the highest applicable tier. A small diff can carry substantial risk. The PR states its tier.

## 2. Verify before requesting review

Run the applicable checks from `AGENTS.md` (build, the section's tests, `dotnet format whitespace --verify-no-changes`) and give the results in the PR. Exercise the changed behavior; for UI changes, include evidence from the preview deploy (`https://{pr}.n.burn.camp`). Explain meaningful gaps.

## 3. Review with fresh context

Assess the issue, the diff and the surrounding code independently of the author's narrative. Check whether the change solves the underlying problem and fits the architecture: section data ownership, layers, and public surface.

## 4. Give security a distinct pass

In this repository, trace roles, policies and authorization handlers through controllers, view components, API endpoints, Hangfire jobs, exports and downloads, including the deny paths in the section's invariant doc. Check personal data (profiles, contact details, IBANs, medical and dietary info) reaching logs, emails, audit descriptions, agent prompts or previews; IBANs stay masked except where the audit rule allows. New personal data needs a GDPR export contributor and a deletion path. Check table ownership (no section touches another's tables) and migration behavior against the data already in QA and prod.

## 5. Report verified, consequential findings

Each finding needs severity, a precise location, triggering conditions and impact. Exclude cosmetic preferences and speculative improvements. Fix or explicitly dismiss each finding with a reason; accepted risks require Peter's ownership.

## 6. Make review converge

Review the whole change initially, then focus on fixes and affected behavior. Report a defect's full scope together. Don't repeat resolved findings. After three unsuccessful rounds, stop automatic iteration and reassess the approach with Peter; unresolved defects remain unresolved.

## 7. Approve and release according to risk

Human judgment covers sensitive changes and architectural decisions. Approval applies to the reviewed revision. For this app, assess duplicate Hangfire job execution, partial failure mid-job, repeated email sends, Google Workspace and Holded sync side effects, and migrations running against existing rows on QA and prod. Specify how to detect and recover from failure; reverting code may not recover data, and a merged migration cannot be reverted.

## 8. Learn from escapes and noise

Turn confirmed regressions into analyzers, tests and reviewer cases. Move recurring findings into automation or a `memory/` atom. Track missed defects and false positives alongside review effort.

## Review output

Every review ends with the tier, the finding count, actionable findings ordered by severity, the verification performed and material gaps. Say plainly when there is nothing to fix.
