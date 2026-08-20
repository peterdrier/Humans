# Feedback — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FeedbackController` | Class | `Admin` | `PolicyNames.AdminOnly` — every action inherits it; no per-action attributes |
| `FeedbackController` runtime guards | In-method | none — no admin-vs-reporter branch | — |
| `FeedbackApiController` | Class | `[ServiceFilter(typeof(FeedbackApiKeyAuthFilter))]` (API-key auth) | `FeedbackApiKeyAuthFilter` |

Feedback is retired: closed to new reports, no reporter-facing view, and `FeedbackAdmin` alone reaches none of it. `PolicyNames.FeedbackAdminOrAdmin`, `RoleGroups.FeedbackAdminOrAdmin`, and `RoleChecks.IsFeedbackAdmin` do not exist.
