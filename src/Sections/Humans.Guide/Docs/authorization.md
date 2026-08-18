# Guide — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GuideController` | Class | (no class-level `[Authorize]`) | — |
| `GuideController.Index` | Action | `AllowAnonymous` | Override |
| `GuideController.Document` | Action | `AllowAnonymous` | Override |
| `GuideController.Refresh` | Action | `Admin` | `PolicyNames.AdminOnly` |
