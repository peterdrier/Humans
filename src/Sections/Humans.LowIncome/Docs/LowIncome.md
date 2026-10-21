<!-- freshness:triggers
  src/Sections/Humans.LowIncome/**
-->
<!-- freshness:flag-on-change
  Who may give or remove a Low Income grant, and the source key it passes to Grants.
-->

# LowIncome — Section Invariants

A stub for the future Low Income process. Today it is one Admin-only page that gives or removes a person's Low Income grant; the grant itself, its audit and the person's notice live in Grants.

## Concepts

- A **Low Income grant** is a grant under the one open Grants program whose issuer is a source with key `low-income` (`Section.SourceKey`). An Admin creates that program under Grants → Programs.

## Routing

`/LowIncome/Admin` (`LowIncomeAdminController`): Index (GET), Grant and Remove (POST). In the Admin sidebar as Low income → Grants.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Gives or removes a person's Low Income grant, with an optional reason the person sees on removal |

## Invariants

- The section owns no tables, no services and no migrations.
- Every action goes through `IGrantWrite` with source key `low-income`; Grants writes the row, the audit entry and the notice.
- No open Low Income program in Grants is shown as an error on the page, never thrown.

## Negative Access Rules

- Board, HumanAdmin, program managers and every other role **cannot** reach the page or post its actions (`PolicyNames.AdminOnly` on the class and on each POST).

## Triggers

- None of its own. Grants audits and notifies on every issue and revoke.

## Cross-Section Dependencies

- **Grants**: `IGrantWrite.IssueAsync` / `RevokeAsync`.
- **Users**: `IUserService` through `HumansControllerBase` (the acting user); the person picker is Base's human-search view component.

## Architecture

**Owning services:** none
**Owned tables:** none
**Status:** (A) Migrated: born in its own project, 2026-10-21.

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `ILowIncomeService` | — | Nothing reads LowIncome |
| `ILowIncomeWrite` | — | Nothing writes LowIncome |
