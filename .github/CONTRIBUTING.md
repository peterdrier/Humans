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

There are two paths, and which one you take depends on whether you have **push access** to `peterdrier/Humans`. Check before you start — the wrong path silently costs you the preview environment:

```bash
gh api repos/peterdrier/Humans --jq .permissions.push   # true = collaborator, false = outside contributor
```

**Collaborators (push access):**

1. **Clone** `peterdrier/Humans` directly — do not fork
2. **Create a feature branch** from `main` and push it to `peterdrier/Humans`
3. **Open a PR** against `main` on `peterdrier/Humans`
4. Your PR gets a **preview environment** at `https://{pr_number}.n.burn.camp`

**Outside contributors (read access):**

1. **Fork** `peterdrier/Humans` (not the upstream repo)
2. **Create a feature branch** from `main` on your fork
3. **Open a PR** against `main` on `peterdrier/Humans`
4. **No preview environment** — see below. A maintainer can deploy one by hand if the change needs QA

Either way, after review and QA we promote tested changes to production via a separate upstream PR.

### Why Fork PRs Get No Preview Environment

Preview environments are provisioned by Coolify off the `pull_request` webhook, and it refuses PRs whose head branch lives outside `peterdrier/Humans`. Fork code is untrusted, and the preview runs on our hardware against a database cloned from QA. Coolify's trust check (`author_association`) is never reached for a fork PR, so **collaborator status alone does not restore the preview — the branch has to live in `peterdrier/Humans`.**

To get a preview on a fork PR, a maintainer deploys it manually: Coolify → Humans → **Previews** → Load PRs → Deploy. That is per-push, not automatic.

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
