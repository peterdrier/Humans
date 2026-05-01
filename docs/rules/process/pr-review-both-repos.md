---
name: PR review must check both repos for comments — top-level AND inline
description: Always pull comments from peterdrier/Humans AND nobodies-collective/Humans, and use the right API for inline review comments (`/pulls/{n}/comments`). The default `gh pr view` misses inline comments.
---

When running `/pr-review`, fetch ALL comment types — top-level AND inline review comments — from both repos.

**Why:** Missed inline review comments more than once. PR #208: Codex posted an inline comment on peter's fork, review only checked upstream. PR #203: Codex posted an inline comment on the same repo, but `gh pr view --json comments` only returns top-level comments — inline review comments require a separate API call. Both times Peter had to point it out.

**How to apply:** In Step 1 of `/pr-review`, ALWAYS make these API calls:

```bash
gh pr view <N> --json comments                              # top-level comments only
gh api repos/{repo}/pulls/{N}/comments                      # inline review comments — easy to miss
gh api repos/{repo}/pulls/{N}/reviews                       # formal reviews
```

Check both `peterdrier/Humans` and `nobodies-collective/Humans`. Always respond to every review comment (even false positives) so the user knows nothing was missed.
