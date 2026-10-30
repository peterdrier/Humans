# Surveys — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SurveyController` | Class | `AllowAnonymous` | — (survey answering wizard — invited token path `/Survey/Answer?t=…` takes identity from the invitation; on public slug path `/Survey/{slug}`, logged-out visitors remain Anonymous while a logged-in Human explicitly chooses Identified, CompletionTracked, or Anonymous; a distinct preview token only redirects to the protected admin preview and grants no access itself; all actions inherit `[AllowAnonymous]`) |
| `SurveyAdminController` | Class | Any signed-in Human with an approved profile | `PolicyNames.AppAccess` (survey authoring at `/Survey/Admin` — author-scoped index, `Create`, `Edit`, `Save`, `Submit`, `Preview`, `PreviewPage`, `PreviewThankYou`, `PreviewEmail`, `SendPreviewEmail`, `Results`, `ExportCsv`, `ExportJson` inherit the class policy, then `SurveyOperationRequirement` enforces per-survey access — see handler below) |
| `SurveyAdminController` | Action | `SurveysAdmin, Board, Admin` | `SurveysPolicies.Read` (`Queue`, `Official`, `Send` GET); Board sees the queue and recipients without the approve, reject, open or send controls |
| `SurveyAdminController` | Action | `SurveysAdmin, Admin` | `SurveysPolicies.Manage` (`Approve`, `Reject`, `Open`, `Close`, `Send` POST (`SendInvites`), `RankedAvailability`) |
| `BackdoorSurveysController` (in `Humans.Backdoor`) | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed agent read API at `/api/backdoor/surveys` — `List`, `Definition`, `Responses`, `Aggregates`; reads this section through `ISurveyAnalysisRead`) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `SurveyAuthorizationHandler` | `SurveyOperationRequirement` (`Edit`: Surveys Admin/Admin, or the author while Draft; `Submit`: the author while Draft only; `ViewResults`: Surveys Admin/Board/Admin, or the author once Closed; `ExportResults`: Surveys Admin/Admin, or the author once Closed; `Preview`: Surveys Admin/Board/Admin or the author) | `SurveyDetail` | `Authorization/SurveyAuthorizationHandler.cs` (registered in `Section.cs`; `SurveyService` enforces the same rules independently) |

## Negative cases

- An author without Surveys Admin **cannot** see the builder's run controls — Open, Close and
  "Save and review recipients" all lead to Surveys Admin actions, so
  `SurveyBuilderViewModel.ManagesEverySurvey` hides them. A `save-review` post from such an
  author redirects back to the builder instead of to `Send`. Authoring, previewing their own
  survey, submitting for approval and reading the rejection note stay open.
- Board **cannot** edit, approve, reject, open, close, send or download any survey it did not
  author. It reads every survey, the approval queue, the recipients and the results.
