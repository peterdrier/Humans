<!-- freshness:triggers
  src/Humans.Application/Services/Profiles/PersonSearchMatcher.cs
  src/Humans.Application/Services/Profiles/PersonSearchFields.cs
  src/Humans.Infrastructure/Services/Users/CachingUserService.cs
  src/Humans.Web/Controllers/SearchController.cs
  src/Humans.Web/Controllers/ProfileApiController.cs
  src/Humans.Web/Controllers/TeamAdminController.cs
  src/Humans.Web/Controllers/ShiftAdminController.cs
  src/Humans.Web/Controllers/ShiftDashboardController.cs
-->
<!-- freshness:flag-on-change
  Search scope authorization model (LegalName/Admin gating, never-searchable fields) and matcher semantics (resolved name, accent folding, token split). Review when the matcher, PersonSearchFields, or search endpoints change — especially the §Follow-up per-window scope flip landing.
-->

# User search overhaul — match all profile info, exclude board/private

**Status:** in progress. This PR lands the matcher + wiring; the per-window legal-name
scope flip is the tracked follow-up (see §Follow-up).

## Problem

Search by **name** found nothing in several windows (top-level `/Search`, Volunteers/team
member pickers, add-to-barrio, add-to-team, early-entry), while searching by **full UserID**
always worked. Verified by code trace — all human-name matching ran through one method
(`CachingUserService.TryMatchBuckets`) with four compounding defects:

1. **Resolved-vs-raw name.** Matched **raw** `Profile.BurnerName`, but the UI renders the
   **resolved** `UserInfo.BurnerName` (falls back to legacy `User.DisplayName` when the burner
   name is blank). Humans with a blank `Profile.BurnerName` rendered everywhere but were
   unsearchable by name. *(Intermittent — "I see them but can't find them".)*
2. **Pseudonym only.** Legal `FirstName`/`LastName` existed but were never searched.
3. **No accent folding.** `OrdinalIgnoreCase` is case- but not accent-insensitive
   (`jose` ≠ `José`, `munoz` ≠ `Muñoz`). Heavy impact for Spanish names.
4. **No token splitting.** The whole query had to be a contiguous substring of one field.

The `Guid.TryParse` fast-path is why full-ID always worked.

## Desired behavior (product decisions)

All **profile information** searchable; **board/admin and private info excluded**.

- Match the **resolved** display name (BurnerName → DisplayName fallback).
- **Accent- + case-insensitive**, **token-split** matching everywhere.
- Legal `FirstName`/`LastName` matchable **only in admin/coordinator-gated windows** — never in
  public `/Search` (prevents real-name → burner deanonymization).
- Email matchable to everyone **only** when exposed as a public (`AllActiveProfiles`) ContactField
  or email; verified/login emails and non-public contact fields stay admin/board-only.
- **Never** searchable: AdminNotes, BoardNotes, ConsentCheckNotes, RejectionReason, Iban,
  EmergencyContact*, GDPR health fields (DietaryPreference, Allergies, Intolerances,
  MedicalConditions), and BoardOnly/coordinator-only ContactFields.

## Design

`PersonSearchFields` (scope = authorization model; the matcher never reads a field whose flag is unset):

- `Name` — resolved display name (public).
- `Bio` — city, bio, pronouns, contribution interests, volunteer CV, `AllActiveProfiles`
  ContactFields, publicly-exposed emails (public).
- `LegalName` *(new)* — FirstName/LastName; admin/coordinator only.
- `Admin` — all verified emails + non-public ContactFields; admin/board only.
- `PublicAll = Name | Bio`, `ManageAll = Name | Bio | LegalName`, `AdminAll = … | Admin`.

`PersonSearchMatcher` (`src/Humans.Application/Services/Profiles/PersonSearchMatcher.cs`) — a pure,
unit-tested matcher over the cached `UserInfo` read-model. Accent-/case-fold (`Fold`: lowercase +
NFD + strip combining marks); name matching token-splits the folded query and requires every token
(order-independent). `CachingUserService` keeps the cache iteration + Guid fast-path and delegates
per-record matching to it.

## Done in this PR

- `PersonSearchFields`: `LegalName` + `ManageAll`; `AdminAll` includes `LegalName`.
- `PersonSearchMatcher` + 23 unit tests (resolved-name fallback, accents, tokens, legal-name
  gating, bio/CV/public-contact, public-vs-admin email, board/private/health negatives, rejected
  exclusion).
- `CachingUserService` delegates to the matcher (Guid fast-path retained).
- **Net effect now:** resolved-name + accent + token fixes are live in **every** window (defects
  1, 3, 4). The `LegalName`/email scope infrastructure exists, ready for callers to opt in.

## Follow-up (next PR)

Per-window scope flip so admin/coordinator windows match **legal name** (defect 2):

- `TeamAdminController.SearchUsers` / `SearchMembersForRole` → `ManageAll`; collapse the role-picker
  hybrid (existing members by DisplayName+Email, candidates by BurnerName) onto the matcher.
- `Shift{Admin,Dashboard}Controller.SearchVolunteers` → `ManageAll` (confirm each is lead/admin-gated).
- `ProfileApiController.Search`: add a role-checked `scope=manage` → `ManageAll`; point the
  add-to-team / add-to-barrio / early-entry views at it.
- Web controller tests asserting public endpoints never receive legal-name/admin scope.

## Out of scope

Accent-folding for the Postgres `ILIKE` **entity** searches (teams/camps/shifts/events) — needs the
`unaccent` extension or normalized columns; separate ticket.

## Blast radius (run against QA/prod — local dev DB is unrepresentative)

```sql
WITH p AS (
  SELECT pr."BurnerName" AS burner, pr."FirstName" AS fn, pr."LastName" AS ln,
         u."DisplayName" AS disp, pr."RejectedAt" AS rej
  FROM profiles pr JOIN users u ON u."Id" = pr."UserId"
)
SELECT 'total (not rejected)' AS metric, count(*) FROM p WHERE rej IS NULL
UNION ALL SELECT 'DEFECT#1 rendered-but-unsearchable (blank BurnerName + has DisplayName)',
       count(*) FROM p WHERE coalesce(trim(burner),'')='' AND coalesce(trim(disp),'')<>'' AND rej IS NULL
UNION ALL SELECT 'DEFECT#3 accented chars in name fields',
       count(*) FROM p WHERE (burner ~ '[^\x20-\x7E]' OR fn ~ '[^\x20-\x7E]' OR ln ~ '[^\x20-\x7E]') AND rej IS NULL;
```
