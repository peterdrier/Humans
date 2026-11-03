# Preapprovals — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `PreapprovalsController` | Class | Admin, Board, FinanceAdmin, a manager of any program, or anyone holding a grant or with an open program to apply to | `PreapprovalsPolicies.Access` (section-local policy, `PreapprovalsPolicyHandler`) — member pages at `/Preapprovals` |
| `PreapprovalsController.Index` | In-method | `authorization.AuthorizeAsync(User, PreapprovalsPolicies.Manage)` drives the `CanManage` flag (Manage button) | Policy check |
| `PreapprovalsController.Program` / `Apply` | In-method | No per-program check beyond the class policy; the service refuses applications to closed / not-accepting / past-deadline programs (`BadRequest`) | — |
| `PreapprovalsAdminController` | Class | Admin, Board, FinanceAdmin, or a manager of at least one program | `PreapprovalsPolicies.Manage` (section-local policy, `PreapprovalsPolicyHandler`) — `/Preapprovals/Admin` |
| `PreapprovalsAdminController.Index` | In-method | `FinancePolicies.Read` (Admin, Board, FinanceAdmin) lists every program, else only the caller's; `FinancePolicies.Manage` drives `CanAdminister` | Policy check |
| `PreapprovalsAdminController.New` (GET/POST) | Action | Admin, FinanceAdmin | `FinancePolicies.Manage` |
| `PreapprovalsAdminController.Program` (GET) | In-method | `ProgramOperationRequirement.View`; `Manage` and `Administer` drive `CanManage` and `CanAdminister`, so Board sees no forms | Resource-based |
| `PreapprovalsAdminController.Edit` (GET/POST) | In-method | `AuthorizeAsync(User, program, ProgramOperationRequirement.Administer)` via `AuthorizedAsync` | Resource-based |
| `PreapprovalsAdminController.Issue` / `IssueMany` / `Approve` / `Decline` / `Revoke` / `Amount` | In-method | `AuthorizeAsync(User, program, ProgramOperationRequirement.Manage)` via `ManageAsync`; a grant route whose grant is not in that program returns 404, so a manager of one program never acts on another's grants | Resource-based |

Both policy names live in this section (`Authorization/PreapprovalsPolicies.cs`), not in Base's
`PolicyNames` — nothing outside the section gates on them. They are registered in this section's
`Section.cs`; `Manage` = `ManagesPreapprovalsRequirement`, `Access` =
`PreapprovalsAccessRequirement`.

## Policy Handler

| Handler | Requirement | Policy | Path |
|---|---|---|---|
| `PreapprovalsPolicyHandler` | `ManagesPreapprovalsRequirement`, `PreapprovalsAccessRequirement` | `PreapprovalsPolicies.Manage` (the admin pages, read) — Admin/Board/FinanceAdmin short-circuit, else `IPreapprovalManagement.ManagesAnyProgramAsync`; `PreapprovalsPolicies.Access` — `Manage`, or `HasAnythingForUserAsync` (a grant or an open program). Both resolve the live user id | `Authorization/PreapprovalsRequirements.cs` (registered in `Section.cs`) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `ProgramAuthorizationHandler` | `ProgramOperationRequirement` (`View` — the program page: Admin/Board/FinanceAdmin or a manager of this program; `Manage` — issue, approve, decline, revoke, change amount: Admin/FinanceAdmin or a manager of this program; `Administer` — edit the program and its managers: Admin/FinanceAdmin only) | `ProgramInfo` | `Authorization/ProgramOperationRequirement.cs` (registered in `Section.cs`) |
