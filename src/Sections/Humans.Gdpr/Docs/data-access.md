# Gdpr — Data Access

## Gdpr

Folder: `src/Sections/Humans.Gdpr/Services/`. No owned DB tables —
the subject-rights orchestrator runs both the export and the erasure over
the per-section `IUserDataContributor` fan-out.

### GdprService (Scoped)

No repository, no direct DB access, no cache. Injects
`IEnumerable<IUserDataContributor>`; every section that owns per-user
tables implements that interface and registers itself beside the service
that owns the data, so who is in the list is never decided here. Plus
`IUserService` for one read: the export envelope names the surviving
account and the archived ids merged into it, so a slice keyed to an
archived id reads as this person's row. `GdprService` resolves the surviving
account once and passes the survivor id to every contributor's
`ContributeForUserAsync`, and the survivor plus the whole chain
(`UserInfo.AllUserIds`) to `ContributeForIdentityChainAsync`.
`ExportForUserAsync` reads each contributor's slice; `EraseForUserAsync`
runs each contributor's `EraseForUserAsync` (its own write) for the id
the caller passes in. The roster of contributors and the export section
each one emits is kept in one place —
[`docs/features/global/gdpr-export.md`](../../../../docs/features/global/gdpr-export.md);
each contributor's own section also lists it in that section's
`data-access.md`.

---

## Controllers

`GuestDataController`, `MemberDataController` and `UserController` (`Controllers/`) hold no
data and no cache. They inject `IUserService` (current-user lookup via `HumansControllerBase`),
`IGdprService` (the download controllers `GuestDataController` and `MemberDataController`;
the export) and Users.Contracts' `IAccountDeletionService`
(request and cancel). The deletion timestamps stay on `users`, written by Users behind
`IUserWrite`; Gdpr never touches them.
