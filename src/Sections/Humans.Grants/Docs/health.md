<!-- freshness:triggers
  src/Sections/Humans.Grants/**
  tests/Humans.Grants.Tests/**
-->

# Grants — Target Shape

The shape this section is converging on, regenerated every section-doctor run and diffed
against the previous run's copy. Not a history: what the section *should* be today.

## 1. What the section does

It records that a person is entitled to something for one event: a cheaper ticket, early entry
for build, staying late for strike. An entitlement belongs to a program. Grants Admins set up
programs and say who runs each one; the people running a program pick who gets it, or, for a
program another part of the system fills (Low Income), that part decides and Grants just
records it. The person is told by email and in the app when they get a grant or lose one, and
sees their grants on their own profile. A grant ends when its date passes or when someone takes
it back; the record of who gave it and who took it back stays. Delivering the thing itself is
somebody else's job.

## 2. The shapes

| Shape | Members | What the caller is asking |
|---|---|---|
| **Which programs, and what's in one** | `GrantsAdminController.Index`, `GrantsAdminController.Program`, `IGrantManagement.GetProgramsAsync`, `IGrantManagement.GetProgramAsync` | "Show me the programs I may see, and a program's grants with their state." |
| **Set up a program** | `GrantsAdminController.New`, `GrantsAdminController.Edit`, `IGrantManagement.CreateProgramAsync`, `IGrantManagement.UpdateProgramAsync` | "Create or change a program and who runs it." |
| **Give, take back, tell again (by hand)** | `GrantsAdminController.Issue`, `GrantsAdminController.Revoke`, `GrantsAdminController.Resend`, `IGrantManagement.IssueToAsync`, `IGrantManagement.RevokeGrantAsync`, `IGrantManagement.ResendNoticeAsync` | "These people get it; this one loses it; send that notice again." |
| **Give, take back (from another section)** | `IGrantWrite.IssueAsync`, `IGrantWrite.RevokeAsync` | "My section decided this person qualifies, or no longer does." |
| **Whose grants** | `IGrantManagement.GetGrantsForUserAsync`, `MyGrantsViewComponent`, `UserGrantsViewComponent` | "What does this person hold?" — on their own profile, or on the admin human page. |
| **May I** | `GrantsPolicyHandler`, `ProgramAuthorizationHandler`, `GrantsPolicies`, `GrantsRoles` | "Can this viewer open the pages, this program, or change it?" |
| **The person's data** | `GrantService.ContributeForUserAsync`, `GrantService.EraseForUserAsync`, `GrantService.ReassignAsync` | Export, erase, and carry grants across an account merge. |
| **Tell the person** | `GrantNotices`, `GrantsEmails`, `GrantsEmailPreviews` | One issued/revoked notice job: in-app plus email, in their language. |

## 3. Structure

```
Section.cs                 role, admin nav entry, policies, profile parts, DI
Contracts/                 IGrantWrite and its result records; GrantsPolicies (public so the Board read-access test finds it)
Authorization/             the role, the pages policy handler and the per-program resource handler
Controllers/               GrantsAdminController: translate only; one ManageAsync pipeline for issue/revoke/resend
Services/GrantService.*    one partial class: reads, programs, grants, GDPR/merge
Services/GrantNotices      the only sender; owns the profile link both channels point at
Services/GrantsEmails      pure builder of the two emails
Data/                      GrantRepository over GrantsDbContext; the only table access
ViewComponents/, Views/    two profile cards and three admin pages
```

- Every read and audit line that names a year uses one rule: the program's current event year,
  else its stored year.
- Manual and source issuance converge on one persist → audit → notice step.

## 4. Invariants

- One live grant per person per program: the filtered unique index
  `src/Sections/Humans.Grants/Data/Configurations/GrantsConfigurations.cs:70`; issuance skips a
  holder under any of their ids (`src/Sections/Humans.Grants/Services/GrantService.Grants.cs:37`)
  or hands back the live grant (`src/Sections/Humans.Grants/Services/GrantService.Grants.cs:87`).
- A closed program takes no new grants: `src/Sections/Humans.Grants/Services/GrantService.Grants.cs:21`
  by hand, and `IsOpenSource` for a source write
  (`src/Sections/Humans.Grants/Services/GrantService.Grants.cs:128`).
- A source program is never filled by hand:
  `src/Sections/Humans.Grants/Services/GrantService.Grants.cs:22`.
- At most one open program per source key: refused on create and edit at
  `src/Sections/Humans.Grants/Services/GrantService.Programs.cs:24` and
  `src/Sections/Humans.Grants/Services/GrantService.Programs.cs:71`.
- A missing or tombstoned recipient or manager refuses the whole write before anything is saved:
  `src/Sections/Humans.Grants/Services/GrantService.cs:89`, used by
  `src/Sections/Humans.Grants/Services/GrantService.Grants.cs:31` and
  `src/Sections/Humans.Grants/Services/GrantService.Programs.cs:26`.
- A revoked grant is never revoked again or resent:
  `src/Sections/Humans.Grants/Services/GrantService.Grants.cs:53`,
  `src/Sections/Humans.Grants/Services/GrantService.Grants.cs:64`.
- State is computed, never stored; revoked wins over expired:
  `src/Sections/Humans.Grants/Services/Dtos.cs:54`.
- Manual revoke/resend services refuse a grant outside the supplied program before side effects;
  the controller maps that missing result to 404 (`GrantService.Grants.cs`, `GrantsAdminController.ManageAsync`).
- Board never changes anything: `ProgramAuthorizationHandler` admits Board only for `View`
  (`src/Sections/Humans.Grants/Authorization/ProgramOperationRequirement.cs:35`); pinned by
  `GrantsAdminAuthorizationTests`.
- After the save commits, audit and notices run with no cancellation:
  `src/Sections/Humans.Grants/Services/GrantService.Grants.cs:106`,
  `src/Sections/Humans.Grants/Services/GrantService.Grants.cs:143`.
- A notice failure never fails the write, and one channel never suppresses the other:
  `src/Sections/Humans.Grants/Services/GrantNotices.cs:71`.
- A merge revokes the newer duplicate and re-keys in one transaction:
  `src/Sections/Humans.Grants/Data/GrantRepository.cs:130`.
- The "My grants" card renders only for the viewer's own profile:
  `src/Sections/Humans.Grants/ViewComponents/MyGrantsViewComponent.cs:18`.

## 5. Seams

- **Delivery of the entitled thing** (a vendor code, a ticket price). Out of scope until a
  feature asks for it; nothing is reserved for it in the tables.
- **A cross-section reader.** No section reads grants today, so there is no `I<Section>Service`.

## 6. Deliberately not done

- **No job flips expiry.** State is a date comparison at read time.
- **No caching decorator.** A handful of programs, read on a few admin pages and one card.
- **Admin pages and program-level refusals are English only**, under
  `memory/code/localization-admin-exempt.md`, including for the program managers who reach them
  (Peter, 2026-11-08). Member-facing copy is localized.
- **No "already held" signal from `IGrantWrite.IssueAsync`.** The source gets the existing id;
  Low Income accepted that.

## Load-bearing weirdness

- **`GrantService` is one class behind four interfaces** (`IGrantManagement`, `IGrantWrite`,
  `IUserDataContributor`, `IUserMerge`), split into partial files by concern. One scoped
  instance per request, so every path shares the same repository and notice owner.
- **The repository hands out detached graphs and saves scalar copies** (`Scalar`, the
  `SetValues` updates). A detached program graph attached on save would try to re-insert its
  children.
- **The program page renders managers by invoking the Human component and trimming its output,**
  so names join with ", " and no stray whitespace.
- **The class policy admits managers of any program; each action narrows** to the routed program
  through the resource handler. A manager of A reaches the pages and still cannot touch B.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-11-07 | First doctor pass: docs describe what the section does now, one profile link, untested invariants pinned | peterdrier/Humans#2342 |
