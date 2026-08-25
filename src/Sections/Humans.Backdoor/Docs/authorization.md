# Backdoor — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BackdoorController` (`/Backdoor`) | Class | `Admin` | `PolicyNames.AdminOnly` (`Index`, `Issue`, `Rotate`, `Revoke` all inherit) |
| `BackdoorAgentController` | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed agent transcript read API at `/api/backdoor/agent` — `List`; reads `Humans.Agent` through `IAgentTranscriptRead`) |
| `BackdoorFeedbackController` | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed read+write API at `/api/backdoor/feedback`; reads and triages `Humans.Feedback` through `IFeedbackTriage`) |
| `BackdoorIssuesController` | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed read+write API at `/api/backdoor/issues`; reads and triages `Humans.Issues` through `IIssueTriage`) |
| `BackdoorLogsController` | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed log-tail read API at `/api/backdoor/logs`; reads `InMemoryLogSink`, `Humans.Base`) |
| `BackdoorSurveysController` | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed read API at `/api/backdoor/surveys`; reads `Humans.Surveys` through `ISurveyAnalysisRead`) |

`BackdoorApiKeyAuthFilter` (`Filters/BackdoorApiKeyAuthFilter.cs`) resolves the `X-Api-Key` header to the human the key was issued to and installs that human as the request principal (`ClaimTypes.NameIdentifier`), stamped with the `BackdoorApiKey` authentication scheme (`BackdoorAuthentication.SchemeName`). A missing, unknown, or revoked key returns 401 — indistinguishable to the caller. There is no cookie path and no anonymous endpoint under `/api/backdoor/*`.

A Backdoor-authenticated principal never passes through the Shell's claims transformation, so it carries no role or state claims. `MembershipRequiredFilter` and `NameRequiredFilter` (`src/Humans.Web/Authorization/`) both detect it via `MembershipRequiredFilter.IsMachineRequest` and pass it through unconditionally rather than redirecting it to an onboarding page — see `docs/authorization-inventory.md` §4.

Key eligibility (who `BackdoorController` may issue a key to, and who continues to authenticate on each request) is Admin ∪ Board, narrowed to accounts in `UserState.Active` — see `Docs/Backdoor.md` for the full invariant.
