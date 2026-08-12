---
name: one-ifilestorage
description: HARD RULE — one shared `IFileStorage`, key-namespaced under `uploads/`, rooted at `wwwroot/`. Never introduce a per-domain storage interface or a parallel filesystem root.
---

When a section needs to persist user-uploaded files (images, PDFs, receipts, exports), it MUST go through `Humans.Application.Interfaces.IFileStorage` (impl `FileSystemFileStorage`). Pick a key prefix under `uploads/` for the section (`uploads/profile-pictures/`, `uploads/camps/{campId}/`, `uploads/expense-attachments/`, etc.) and call `SaveAsync` / `TryReadAsync` / `DeleteAsync` with that key.

Do NOT:

- Invent a per-section storage interface (`IExpenseAttachmentStorageService`, `IFooStorage`).
- Pick a filesystem root outside `wwwroot/` (`/var/lib/humans/...`, `/app/data/...`).
- Configure a parallel `Root` option that needs its own volume mount.

**Why:** Only `wwwroot/uploads/` is volume-mounted in the QA and production Coolify deployments. Any other path is ephemeral inside the container, isn't writable by the app user, or both. The first incarnation of expense attachments shipped with `IExpenseAttachmentStorageService` rooted at `/var/lib/humans/expense-attachments` and broke QA on first Hangfire run with `UnauthorizedAccessException` — the directory wasn't writable and wouldn't have survived redeploy even if it had been. Replaced with `IFileStorage` under `uploads/expense-attachments/` in PR (this PR).

**How to apply:**

- Adding file storage to a new section? Reuse `IFileStorage`. Define the key prefix in the service.
- See visibility-controlled examples in `Humans.Application.Services.Profile.ProfileService` (profile pictures, gated via middleware exclusion in `Program.cs`) and `Humans.Application.Services.Camps.CampService` (camp photos, public static files).
- For non-public files where the original filename matters on download, keep a thin authz-gated controller that calls `IFileStorage.TryReadAsync` and returns `File(bytes, contentType, originalFileName)` — see `ExpensesController.Attachment`.
- Domain validation (allowed extensions, max size, content-type whitelist) lives in the consuming service, not in `IFileStorage`.

Related: [[no-startup-guards]] (the storage layer must not refuse to start on missing dirs — `SaveAsync` creates them lazily).
