# Surveys — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SurveyController` | Class | `AllowAnonymous` | — (public survey answering wizard — invited token path `/Survey/Answer?t=…` and public slug path `/Survey/{slug}`; identity comes from the token's invitation, never the current principal; all actions inherit `[AllowAnonymous]`) |
| `SurveyAdminController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (survey authoring at `/Survey/Admin` — `Index`, `Create`, `Edit`, `Save`, `Open`, `Close`, `Send` GET/POST, `Results`, `ExportCsv`, `ExportJson` all inherit) |
| `SurveysApiController` | Class | `[ServiceFilter(typeof(SurveyApiKeyAuthFilter))]` (API-key auth) | `SurveyApiKeyAuthFilter` (key-authed agent read API at `/api/surveys` — `List`, `Definition`, `Responses`, `Aggregates`) |
