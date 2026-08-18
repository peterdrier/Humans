# Consent — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AdminLegalDocumentsController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (`/Legal/Admin` — Documents CRUD, Archive, Sync, Versions/Summary all inherit) |
| `ConsentController` | Class | `[Authorize]` (authenticated) | — |
| `LegalController` | Class | `AllowAnonymous` | — |
