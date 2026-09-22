# Email — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `EmailController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `EmailPreviewController` | Class | Any authenticated human | `[Authorize]` (`PreviewMarkdown`, `SendMarkdownToSelf` — the latter also rate-limited to 5/min per human) |
