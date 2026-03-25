# Feature 29: Contact Accounts

**Issue:** [#205](https://github.com/nobodies-collective/Humans/issues/205)
**Dependencies:** #97 (Communication Preferences)
**Used by:** #200 (MailerLite sync), #206 (TicketTailor import)

## Business Context

Humans syncs with MailerLite (mailing list) and TicketTailor (ticket sales) which contain people who aren't Humans members. These people need to exist in Humans for unified communication preference management, but they shouldn't be treated as full members (no login, no profile, no team rosters).

## Data Model

An `AccountType` enum on the existing `User` entity distinguishes Members from Contacts. This reuses all existing email and preference infrastructure.

**New properties on User:**
- `AccountType` (enum: Member, Contact, Deactivated) — defaults to Member
- `ContactSource` (nullable enum: Manual, MailerLite, TicketTailor) — where the contact came from
- `ExternalSourceId` (nullable string, max 256) — external system ID for deduplication

**Indexes:**
- `AccountType` (for query filtering)
- `(ContactSource, ExternalSourceId)` filtered where ExternalSourceId IS NOT NULL (for import dedup)

## Contact Creation

Contacts are created via `IContactService.CreateContactAsync()`:
1. Email is normalized and checked for duplicates
2. If a contact already exists for the same email, returns the existing one (idempotent)
3. If a member exists for the email, throws (use merge flow instead)
4. Creates User with `AccountType.Contact`, locked out (no login)
5. Creates UserEmail record (verified, non-OAuth)
6. Sets default communication preferences: Marketing opted-in, EventOperations opted-out

Sources: admin manual creation, MailerLite import (#200), TicketTailor import (#206).

## Contact-to-Member Merge

Two paths handle upgrading a contact to a member:

### Same-email auto-merge
When someone signs up via Google OAuth with the same email as an existing contact:
- New member account is created normally
- System automatically calls `MergeContactToMemberAsync`
- Contact's communication preferences migrate to member (member's existing preferences win on conflict)
- Contact's UserEmails migrate to member (duplicates dropped)
- Contact account is set to `Deactivated`
- Audit logged on both accounts

### Different-email admin merge
When a member adds an email that belongs to a contact (e.g., member signed up with Gmail, contact exists with their TicketTailor email):
- Existing `AccountMergeRequest` workflow triggers (admin-reviewed)
- When admin accepts, `AccountMergeService` detects source is a Contact and uses the lighter `MergeContactToMemberAsync` instead of full anonymization

## Query Filtering

Contacts are excluded from all member-facing queries:
- `ProfileService.GetFilteredHumansAsync()` — `.Where(u => u.AccountType == AccountType.Member)`
- `ProfileService.SearchApprovedUsersAsync()` — same filter
- `OnboardingService.GetAdminDashboardAsync()` — member count excludes contacts

Naturally safe (no filter needed): MembershipCalculator, team/shift queries, Board queries — all filter by Profile, which contacts don't have.

## Admin UI

**Contacts list** at `/Human/Admin/Contacts`:
- Search by name/email
- Shows source, external ID, preference status
- Link from the Humans admin page

**Contact detail** at `/Human/{id}/Admin/Contact`:
- Contact info, source, external ID
- Communication preferences
- Audit log

**Manual creation** at `/Human/Admin/Contacts/Create`:
- Email, display name, source

## Acceptance Criteria

- [x] External contacts can be created without OAuth/login
- [x] Contact records track source (MailerLite, TicketTailor, manual)
- [x] Contacts have communication preferences (same model as members)
- [x] Contact-to-member upgrade merges records and preserves history
- [x] Admin view distinguishes contacts from members
- [x] Contacts do not appear in member counts, team rosters, or volunteer workflows
