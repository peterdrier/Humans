# Campaigns — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampaignController` (`/Campaigns/Admin`) | Class | `[Authorize]` (authenticated) | — |
| `CampaignController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Create` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Edit` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Detail` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `CampaignController.ImportCodes` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.GenerateCodes` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `CampaignController.Activate` / `Complete` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.SendWave` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Resend` (`Grants/{grantId}/Resend`) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.RetryAllFailed` | Action | `Admin` | `PolicyNames.AdminOnly` |
