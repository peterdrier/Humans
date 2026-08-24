# Surveys — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SurveyController` | Class | `AllowAnonymous` | — (survey answering wizard — invited token path `/Survey/Answer?t=…` takes identity from the invitation; on public slug path `/Survey/{slug}`, logged-out visitors remain Anonymous while a logged-in Human explicitly chooses Identified, CompletionTracked, or Anonymous; a distinct preview token only redirects to the protected admin preview and grants no access itself; all actions inherit `[AllowAnonymous]`) |
| `SurveyAdminController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (survey authoring at `/Survey/Admin` — `Index`, `Create`, `Edit`, `Preview`, `PreviewPage`, `PreviewThankYou`, `SendPreviewEmail`, `Save`, `Open`, `Close`, `Send` GET/POST, `Results`, `ExportCsv`, `ExportJson` all inherit) |
| `BackdoorSurveysController` (in `Humans.Backdoor`) | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed agent read API at `/api/backdoor/surveys` — `List`, `Definition`, `Responses`, `Aggregates`; reads this section through `ISurveyAnalysisRead`) |
