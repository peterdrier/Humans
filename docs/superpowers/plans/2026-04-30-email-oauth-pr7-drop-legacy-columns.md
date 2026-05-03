# PR 7 — Drop legacy columns (aggregated, deferred)

> **Stub.** Do not execute until PRs 2 through 6 of the email-identity-decoupling sequence have all shipped to production AND been verified end-to-end (smoke + a soak window long enough to catch the rare-path regressions — recommend a minimum two-week soak after the last of PR 2-6 hits prod).

**Scope:** A single PR that drops every legacy DB column the sequence has stopped using. Aggregating drops into one late PR (rather than per-PR followups) reduces the operational burden of running migrations during the active rollout and keeps every individual feature PR strictly additive.

**Hard rule reminder:** `architecture_no_drops_until_prod_verified` — code rolls back via `git revert + redeploy`; column drops cannot. The whole sequence may run all the way through PR 6 with the legacy columns still present (per the spec's 2026-04-29 revision). Drops are optional and deferred — only execute PR 7 if there's a concrete operational reason to do so.

## Columns to drop (when this PR runs)

From PR 2 (Identity surgery — virtual property overrides on `User`):
- `aspnetusers.Email`
- `aspnetusers.NormalizedEmail`
- `aspnetusers.EmailConfirmed`
- `aspnetusers.UserName`
- `aspnetusers.NormalizedUserName`
- Indexes: `EmailIndex`, `UserNameIndex`

From PR 3 (UserEmails modernization):
- `user_emails.IsOAuth`
- `user_emails.DisplayOrder`
- `aspnetusers.GoogleEmail`

From PRs 4–6 (TBD as those PRs land):
- _(append column list as PR 4–6 ship)_

## Sequence

1. **Verify prod.** All of PRs 2-6 are live in `nobodies-collective/Humans:main` AND have been deployed for at least 2 weeks AND no rollback has happened in that window. If any of those preconditions fail, **STOP** and re-evaluate; do not proceed.
2. **Pre-drop EF model adjustments.** For each column being dropped, remove its EF shadow-property declaration from the corresponding configuration file (`UserConfiguration`, `UserEmailConfiguration`). The migration that follows will then naturally produce `DropColumn` operations because the EF model no longer references the column.
3. **Generate the migration.** `dotnet ef migrations add DropEmailIdentityLegacyColumns`. The Up method should contain only `DropColumn` (and `DropIndex` for the two affected indexes). No data movement; no `Sql()`. If any data backfill is needed, do it via a separate admin button **before** this PR ships.
4. **EF migration reviewer (mandatory gate).**
5. **PR + review loop.** Open against `peterdrier/Humans:main`. Codex review must come back clean. Run on QA, verify the existing `UserEmailProviderBackfillService` audit trail still covers what's needed (column gone → admin button is dead, remove it in the same PR).
6. **Backfill button cleanup.** The `UserEmailProviderBackfillService` reads the legacy columns via `EF.Property<>` — once the columns are gone, those reads fail. Either gut the backfill service or delete it entirely in this PR (it's a one-shot whose data has already been migrated by the time PR 7 runs).
7. **Close upstream issues** that were left open: `nobodies-collective/Humans#505`, `nobodies-collective/Humans#507`, plus any opened by PRs 2 / 4-6.

## Risk

**High.** Forward-only. No rollback path. Mitigation is the prod-soak gate above + the EF migration reviewer agent + a fresh DB backup taken immediately before the migration runs.
