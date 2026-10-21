# Consent — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AdminLegalDocumentsController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (`/Legal/Admin` — Documents CRUD, Archive, Sync, Versions/Summary all inherit) |
| `ConsentController` | Class | `[Authorize]` (authenticated) | — |
| `LegalController` | Class | `AllowAnonymous` | — |
| `MyDataController` | Class | `[Authorize]` (authenticated) | — (the caller's own My Data page at `/Consent/MyData`; never takes a user id) |

## User parts

| Component | Slot | Who sees it | Enforcement |
|---|---|---|---|
| `ConsentReviewCardViewComponent` (consent check, rejection) | `UserPartSlots.AdminDetail` | `Admin` view mode on `/Users/Admin/{id}`: `HumanAdmin, Board, Admin` (the host page's policy) | in-component view-mode check + host policy |
| `RejectionReasonViewComponent` | `UserPartSlots.AccountStatus` | The member themself, on their own account-status wall, only while Rejected | `Self` view mode; the Gdpr host renders the slot for `UserState.Rejected` only |
