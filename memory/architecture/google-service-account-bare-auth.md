---
name: Google service account uses bare auth (no impersonation)
description: HARD RULE. The Google Workspace service account authenticates as itself — no domain-wide delegation, no admin-user impersonation. Never propose adding impersonation/DWD when a Google API call fails.
---

The Google Workspace service account authenticates **as itself** — bare service-account credentials loaded by `GoogleCredentialLoader.LoadScopedAsync`, scoped to the OAuth scopes a given client needs. There is **no domain-wide delegation and no admin-user impersonation** anywhere in this codebase. The service account holds Workspace admin roles granted to it **directly** (e.g. Groups Admin / `groups.admin`), which is what authorizes its Directory/Cloud Identity/Drive writes.

**Why:** This is the established, working setup. Impersonation/DWD has been raised repeatedly as a suspected cause of Google API failures and has been wrong every single time — it is not how this integration is wired, and proposing it wastes time and derails diagnosis. `GoogleCredentialLoader`'s own summary states it: "Authenticates as the service account itself — no domain-wide delegation / impersonation."

**How to apply:** When a Google API call fails (403 / permission-denied / `Error(2028)` etc.), diagnose the **actual** cause — wrong scope on the request, a role the SA lacks, an API that can't resolve the target (e.g. Cloud Identity `memberships.create` can't add a non-Google external email, so membership ops use the Directory API instead — see [`shared-drives-only`](shared-drives-only.md) for another Google-API-shape rule). Do **NOT** suggest adding domain-wide delegation, admin-user impersonation, or `CreateWithUser(...)`. Do not list it as "one possibility." It is never the answer here.
