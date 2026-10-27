# Campaigns — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampaignController` (`/Campaigns/Admin`) | Class | `[Authorize]` (authenticated) | — |
| `CampaignController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Create` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Edit` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Detail` | Action | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `CampaignController.ImportCodes` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.GenerateCodes` | Action | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `CampaignController.Activate` / `Complete` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.SendWave` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Resend` (`Grants/{grantId}/Resend`) | Action | `Admin` | `PolicyNames.AdminOnly` |

| View component | Slot | Audience | Source |
|---|---|---|---|
| `MyCodesViewComponent` ("My Codes") | `UserPartSlots.Profile` | Own profile only: `Self` view mode and signed-in user equals the target | in-component check |
| `CampaignCodesViewComponent` ("Campaign Codes") | `UserPartSlots.AdminDetail` | `Admin` view mode and `HumanAdminBoardOrAdmin` (the host page's policy) | `PolicyNames.HumanAdminBoardOrAdmin` |
