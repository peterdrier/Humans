---
name: Coolify strips .git — never COPY .git in Dockerfile
description: Coolify production deploys remove the `.git` directory from the build context. Use the `SOURCE_COMMIT` build arg instead.
---

Coolify strips `.git` from the Docker build context. Do **NOT** use `COPY .git` in the Dockerfile — it will fail on production deploys.

Instead, Coolify passes `SOURCE_COMMIT` as a Docker build arg containing the full commit SHA. The `Directory.Build.props` MSBuild target for `SourceRevisionId` has a `Condition` to skip when the property is already set via `-p:`.

**Why:** Production-deploy specifics that aren't visible from the local Dockerfile build (which still has `.git`). Past failures: deploys broke when an unconditional `COPY .git ./` snuck in.

**How to apply:** When editing `Dockerfile` or `Directory.Build.props`:
- Don't add `COPY .git` lines.
- Don't remove the `Condition` on the `SourceRevisionId` MSBuild target — that's what makes the `-p:SourceRevisionId=$SOURCE_COMMIT` override work.
- If you need git metadata at build time, route it through the `SOURCE_COMMIT` arg, not the `.git` directory.
