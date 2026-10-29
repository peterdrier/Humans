# Gdpr — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `MemberDataController` | Class | `[Authorize]` (authenticated) | — (own data export and own deletion request, at `/Gdpr/DownloadData` and `/Gdpr/RequestDeletion`; never takes a user id; the deletion POST is antiforgery-protected; both carry `[AllowBeforeActiveMembership]` so non-Active live accounts keep them, Deleted/Merged accounts are redirected to the status wall) |
| `UserController` | Class | `[Authorize]` (authenticated) | — (account-status wall + cancel-deletion landing at `/Gdpr/Status`, `/Gdpr/Deletion` and `/Gdpr/Deletion/Cancel`; carries `[AllowBeforeActiveMembership]` (`Status` with `Terminal = true`) since these ARE the redirect targets — each action self-checks `UserState`: `Status` redirects home for anyone outside the walled states, `Deletion` redirects home when no deletion is pending; the cancel POST is antiforgery-protected and acts on the caller only) |
| `GuestDataController` | Class | `[Authorize]` (authenticated) | — (Article 15 data export and the Article 17 deletion request / cancellation for profileless accounts; acts on the caller's own account only, never an id from the request; the deletion POSTs are antiforgery-protected and challenge when signed out) |
