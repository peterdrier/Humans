# Feature 29: Contact Accounts

## Business Context

The org has external contacts from mailing lists (MailerLite), event ticket purchases (TicketTailor), and manual admin entry who aren't platform members yet. These contacts need to be tracked for communication preference management before they sign up.

With magic link authentication (Feature 30) in place, contacts are **pre-provisioned Identity users** — real User rows with no credentials. When a contact authenticates by any method (magic link, Google OAuth), they claim their existing account with all history preserved. No merge flow needed.

## Design Principles

1. **Contacts are just users who haven't logged in yet.** `LastLoginAt == null` and `ContactSource != null` identifies a contact. No `AccountType` enum, no lockout hacks.
2. **Work with Identity, not around it.** Contacts are created via `UserManager.CreateAsync` as normal users with `EmailConfirmed = true`.
3. **No merge needed.** When a contact authenticates, they claim their existing row. Account linking (Feature 30) handles the case where they use Google OAuth.

## User Stories

### US-1: Admin creates a contact manually

**As** a Board member or Admin,
**I want** to add an external contact with an email and display name,
**So that** the org can track communication preferences before they sign up.

**Acceptance criteria:**
- Admin navigates to Humans > Contacts > Add Contact
- Enters email, display name, and source (Manual, MailerLite, TicketTailor)
- Contact is created as a pre-provisioned user with `EmailConfirmed = true`
- Contact appears in the contacts list
- Duplicate emails are rejected with a clear message

### US-2: Admin views contacts list

**As** a Board member or Admin,
**I want** to see all external contacts with their source and preferences status,
**So that** I can manage the org's contact database.

**Acceptance criteria:**
- Contacts list shows name, email, source, created date, and preference status
- Search filters by name or email
- Link to each contact's detail page
- "Back to Humans" link returns to the main humans list

### US-3: Admin views contact detail

**As** a Board member or Admin,
**I want** to see a contact's full information including communication preferences and audit history,
**So that** I can understand their engagement.

**Acceptance criteria:**
- Shows contact info (email, source, external ID, created date)
- Shows communication preferences (opted in/out per category)
- Shows audit log entries for the contact

### US-4: Contact claims account on first login

**As** an imported contact,
**I want** to authenticate and land in my existing account,
**So that** my communication preferences and history are preserved.

**Acceptance criteria:**
- Contact receives a magic link and clicks it → signs into existing account
- Or authenticates via Google OAuth → account linking connects to existing user
- `LastLoginAt` is set on first login
- Contact no longer appears in the Contacts list (they're now an active user)
- Normal onboarding flow begins (profile completion, consent)

## Data Model

### New fields on User

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `ContactSource` | `ContactSource` enum (stored as string) | Yes | Where the contact was imported from: Manual, MailerLite, TicketTailor |
| `ExternalSourceId` | `string(256)` | Yes | ID in the external source system |

### Contact identification

A contact is identified by: `ContactSource != null && LastLoginAt == null`

When a contact authenticates, `LastLoginAt` is set, and they drop out of the contacts view — they're now a regular user.

### ContactSource enum

```
Manual = 0      // Admin-created
MailerLite = 1  // Mailing list import
TicketTailor = 2 // Ticket purchase import
```

### Index

Composite index on `(ContactSource, ExternalSourceId)` with filter `external_source_id IS NOT NULL` for external system lookups.

## Authorization

All contact management actions require **Board or Admin** role.

## Routes

| Method | Route | Action |
|--------|-------|--------|
| GET | `/Human/Admin/Contacts` | Contacts list |
| GET | `/Human/Admin/Contacts/{id}` | Contact detail |
| GET | `/Human/Admin/Contacts/Create` | Create contact form |
| POST | `/Human/Admin/Contacts/Create` | Create contact submit |

## Related Features

- **Feature 28: Communication Preferences** — contacts can have preferences set before they authenticate
- **Feature 30: Magic Link Authentication** — enables contacts to claim their accounts; account linking handles Google OAuth
