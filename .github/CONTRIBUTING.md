# Contributing to Humans

Thanks for your interest in contributing! This document explains our workflow so your changes can land smoothly.

## Two-Repo Workflow

We use two GitHub repositories:

| Repo | Purpose |
|------|---------|
| **[peterdrier/Humans](https://github.com/peterdrier/Humans)** | QA / staging — auto-deploys on push to `main` |
| **[nobodies-collective/Humans](https://github.com/nobodies-collective/Humans)** | Production |

All changes go through QA before reaching production. **Do not open PRs directly against `nobodies-collective/Humans`** — they will be redirected.

### How to Submit a Change

1. **Fork** `peterdrier/Humans` (not the upstream repo)
2. **Create a feature branch** from `main`
3. **Open a PR** against `main` on `peterdrier/Humans`
4. Your PR gets a **preview environment** at `https://{pr_number}.n.burn.camp`
5. After review and QA, we promote tested changes to production via a separate upstream PR

### Why Not PR Directly to Production?

- QA auto-deploys from `peterdrier/Humans` via Coolify
- Preview environments are only provisioned for PRs on the QA repo
- We review, test, and batch changes before promoting to upstream

## Development Setup

See the [README](README.md#development-setup) for local setup instructions.

## Code Standards

- Follow the project rules cataloged in `memory/INDEX.md` (atomic, one rule per file under `memory/<bucket>/`). Read the architecture story in `docs/architecture/design-rules.md` and the reviewer reject rules in `docs/architecture/code-review-rules.md`.
- Every new page must have a navigation link (no orphan pages)
- Use `nameof()` and constants instead of magic strings
- Use NodaTime for all date/time handling
- Use Font Awesome 6 for icons (not Bootstrap Icons)

## Commit Messages

- Use concise, descriptive commit messages focused on *why*, not *what*
- For PRs with multiple commits, we squash-merge on the QA repo

## Questions?

Open an issue on [nobodies-collective/Humans](https://github.com/nobodies-collective/Humans/issues) or reach out in the project's communication channels.
