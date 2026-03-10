# Facilitated Messaging

Allow any authenticated volunteer to send a one-off plain-text message to another volunteer via email, facilitated by Humans. Designed for situations where the sender needs to reach someone who hasn't shared their contact info publicly.

## Route & Controller

- `GET /Human/{id}/SendMessage` — form
- `POST /Human/{id}/SendMessage` — submit
- Lives on `HumanController`
- Auth: any authenticated user; cannot message yourself
- Returns 404 if target user doesn't exist

## Form Page

Simple card layout (like Edit Team):
- Header: "Send {DisplayName} a message"
- Textarea: plain text, max 2000 chars
- Checkbox: "Include my contact info" (default: checked)
- Submit button: "Send Message"
- Breadcrumb: Teams > {DisplayName} > Send Message

## Input Sanitization

Message body is plain text only:
- Strip all HTML tags server-side before rendering into email
- Email renderer HTML-encodes the text and converts newlines to `<br>`
- No rich text, no markdown
- Max 2000 characters via model validation

## Email

- **To**: recipient's notification target email
- **Subject**: `Humans Message from: {SenderDisplayName}`
- **Reply-To**: sender's notification target email when "include contact info" checked; omitted otherwise
- **Body**: plain text message wrapped in standard Humans email template
  - Header: "You have received a message from {SenderName} through Humans."
  - Message body (HTML-encoded, newlines → `<br>`)
  - Footer when contact info included: `{SenderName} -- {SenderEmail}`
  - Footer when not included: "This human chose not to share their contact information."
- Localized to recipient's preferred language

## Audit

Standard audit log entry via existing `IAuditLogService`:
- Action: `FacilitatedMessageSent` (new `AuditAction` enum value)
- Entity type: "User", entity ID: target user ID
- Details: "Message sent to {RecipientName} (contact info shared: yes/no)"
- Actor: sender user ID and display name

## Profile Button Visibility

"Send a message" button on the profile card shows only when:
1. Viewer is not the profile owner
2. Viewer cannot see any email address for this person (no visible `UserEmail` or `ContactField` of type Email given viewer's permission level)

## What This Feature Does NOT Include

- No message storage or threading — the system is a conduit, not a mailbox
- No rate limiting (can add later if abused)
- No recipient opt-out (can add later if needed)
- No attachments
