# Email — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `EmailController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `MemberOutboxController` | Class + action | Any authenticated human for `MyOutbox` (`GET /Email/Outbox`, no id parameter); `HumanAdminBoardOrAdmin` for `AdminOutbox` (`GET /Email/Admin/{id}/Outbox`) | `[Authorize]` on the class, `PolicyNames.HumanAdminBoardOrAdmin` on `AdminOutbox`. Deny path: a member reaching `AdminOutbox` gets 403; `MyOutbox` can only ever read the signed-in member's own rows. |
| `FacilitatedMessageController` | Class | Any authenticated human (`GET`/`POST /Email/SendMessage/{id}`); no role. Recipient opt-out (`ICommunicationPreferenceService.AcceptsFacilitatedMessagesAsync`) and self-send are refused, on GET and on POST. Sending as a team is Teams' (`TeamMessageController`). POST carries an anti-forgery token. | `[Authorize]` on the class, `[ValidateAntiForgeryToken]` on the POST |
| `OutboxCardViewComponent` | `AdminDetail` user part | `HumanAdminBoardOrAdmin` in admin view mode | Checks the policy itself (the host page is also policy-gated) and renders nothing for any other viewer or mode. |
| `CommunicationPreferencesController` | Class | Any authenticated human (`/Email/CommunicationPreferences`, `/Update`) | `[Authorize]` on the class, `[ValidateAntiForgeryToken]` on the POST. Deny path: takes no user id, so it can only read or write the signed-in member's own preferences. Exempt from the Active-membership gate. |
| `GuestAccountController` | Class + action | `[Authorize]` on the class; `CommunicationPreferences` (GET) and `UpdatePreference` (POST) are `[AllowAnonymous]` | Override: a valid unsubscribe token stands in for a session (see the `EndpointAuthorizationTests` allowlist). Deny path: no session and no valid token is 401; an erased account is refused. |
| `UnsubscribeController` | Class | Anonymous (no class-level `[Authorize]`; on the `EndpointAuthorizationTests` anonymous list) | `/Unsubscribe/{token}`, `/Unsubscribe/OneClick` (RFC 8058, no anti-forgery by design). Deny path: a tampered or malformed token is `NotFound`; no account enumeration. |
| `CommunicationPreferencesCardViewComponent` | `AdminDetail` user part | `HumanAdminBoardOrAdmin` in admin view mode | Checks the policy itself and renders nothing for any other viewer or mode. |
| `EmailPreviewController` | Class | Any authenticated human | `[Authorize]` (`PreviewMarkdown`, `SendMarkdownToSelf` — the latter also rate-limited to 5/min per human) |
