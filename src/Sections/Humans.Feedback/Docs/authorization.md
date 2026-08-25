# Feedback — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FeedbackController` | Class | `Admin` | `PolicyNames.AdminOnly` — every action inherits it; no per-action attributes |
| `FeedbackController` runtime guards | In-method | none — no admin-vs-reporter branch | — |
| `BackdoorFeedbackController` (in `Humans.Backdoor`) | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed agent read+write API at `/api/backdoor/feedback`; reads and triages this section through `IFeedbackTriage`) |

Feedback is retired: closed to new reports, no reporter-facing view, and `FeedbackAdmin` alone reaches none of it. `PolicyNames.FeedbackAdminOrAdmin`, `RoleGroups.FeedbackAdminOrAdmin`, and `RoleChecks.IsFeedbackAdmin` do not exist.
