# Pull Request Workflow

## Purpose

This document describes the repository-specific pull request flow for Humans.

It exists because this codebase does **not** use the default "open a PR straight to upstream" workflow.

## Source Of Truth

This workflow was clarified by Peter on April 5, 2026 in the discussion on:

- `nobodies-collective/Humans#376`

Peter's note was:

> Our workflow is: feature branch → PR on peterdrier/Humans (QA auto-deploys via Coolify) → review & test → then PR to upstream when ready for production.

## Default PR Flow

When you have a branch ready for review, use this sequence:

1. Create or update the feature branch with the intended changes.
2. Open the **first PR** against `peterdrier/Humans`.
3. Use that QA PR for review, QA deploy, and testing.
4. Only after the change has passed QA and is ready for production, open a **second PR** against `nobodies-collective/Humans`.

In short:

- `peterdrier/Humans` is the **QA PR target**
- `nobodies-collective/Humans` is the **production PR target**

## Why

The QA PR target is not just a mirror of upstream.

- PRs on `peterdrier/Humans` drive the QA pipeline
- QA auto-deploys from there via Coolify
- review and testing happen there first
- upstream PRs should represent changes that are already QA-vetted

Because of that, opening the first PR directly on `nobodies-collective/Humans` is the wrong default for this repo.

## Practical Rules

- Do **not** open the first PR on `nobodies-collective/Humans` unless someone explicitly says the change has already cleared QA and is ready for the upstream production PR.
- When someone says "make a PR" without further qualification, assume they mean the **QA PR** on `peterdrier/Humans`.
- If you accidentally open the first PR on upstream, close it and reopen the change through the QA repo.
- Keep the QA PR and the later upstream PR as close as possible in commit content so the production PR reflects what actually passed QA.

## Branch And Repo Notes

The important thing is the **base repository**, not necessarily where the branch was first created.

Depending on permissions and local remote setup, the feature branch may live in:

- `peterdrier/Humans`, or
- another repo or fork that can open a PR into `peterdrier/Humans`

But the review flow still remains:

- first PR targets `peterdrier/Humans`
- later production PR targets `nobodies-collective/Humans`

## Codex Guidance

When Codex is asked to publish work from this codebase:

- default the first PR to `peterdrier/Humans`
- treat `nobodies-collective/Humans` as the production follow-up PR target
- if a user explicitly asks for the upstream PR, verify whether QA has already happened before using that repo as the target

## Example

The seeder work that prompted this clarification followed this pattern after correction:

- mistaken initial upstream PR: `nobodies-collective/Humans#376`
- QA PR: `peterdrier/Humans#137`

The upstream PR was closed in favor of the QA-first flow.
