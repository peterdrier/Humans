# Email — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `EmailController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `MemberOutboxController` | Class + action | Any authenticated human for `MyOutbox` (`GET /Profile/Me/Outbox`, no id parameter); `HumanAdminBoardOrAdmin` for `AdminOutbox` (`GET /Users/Admin/{id}/Outbox`) | `[Authorize]` on the class, `PolicyNames.HumanAdminBoardOrAdmin` on `AdminOutbox`. Deny path: a member reaching `AdminOutbox` gets 403; `MyOutbox` can only ever read the signed-in member's own rows. |
| `FacilitatedMessageController` | Class | Any authenticated human (`GET`/`POST /Profile/{id}/SendMessage`); no role. Recipient opt-out (`ICommunicationPreferenceService.AcceptsFacilitatedMessagesAsync`), self-send and a team the viewer may not send as (`TeamMessageOptionsProvider`) are all refused, on GET and on POST. POST carries an anti-forgery token. | `[Authorize]` on the class, `[ValidateAntiForgeryToken]` on the POST |
| `TeamMessageButtonsViewComponent` | `Profile` user part | Any authenticated viewer on another member's profile | Renders nothing on the viewer's own profile, for an opted-out recipient, or when the viewer coordinates no linked team; the send endpoint re-checks. |
| `OutboxCardViewComponent` | `AdminDetail` user part | `HumanAdminBoardOrAdmin` in admin view mode | Checks the policy itself (the host page is also policy-gated) and renders nothing for any other viewer or mode. |
| `EmailPreviewController` | Class | Any authenticated human | `[Authorize]` (`PreviewMarkdown`, `SendMarkdownToSelf` — the latter also rate-limited to 5/min per human) |
