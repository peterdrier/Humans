# Feedback — Section Invariants

## Concepts

- A **Feedback Report** is an in-app submission from a human — a bug report, feature request, or question. It captures the page URL, optional screenshot, and conversation thread between the reporter and admins.
- **Feedback status** tracks the lifecycle: Open, Acknowledged, Resolved, or WontFix.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | Submit feedback (with optional screenshot). View and reply to their own feedback reports. Accessible even during onboarding (before becoming an active member) |
| FeedbackAdmin, Admin | View all feedback reports. Update status. Add admin notes. Send email responses to reporters. Link GitHub issues. Reply to any report |
| API (key auth) | Full CRUD on feedback reports via the REST API (no user session required) |

## Invariants

- Every feedback report is linked to the human who submitted it.
- Screenshots are validated for allowed file types (JPEG, PNG, WebP) before storage.
- Feedback status follows: Open then Acknowledged then Resolved or WontFix.
- Regular humans can only see their own feedback reports. FeedbackAdmin and Admin can see all reports.
- A report tracks whether it needs a reply (the reporter sent a message that the admin has not yet responded to).

## Negative Access Rules

- Regular humans **cannot** view other humans' feedback reports.
- Regular humans **cannot** update feedback status, add admin notes, link GitHub issues, or send admin responses.
- FeedbackAdmin **cannot** perform system administration tasks — their elevated access is scoped to feedback only.

## Triggers

- When an admin sends a response, an email is queued to the reporter via the email outbox.

## Cross-Section Dependencies

- **Admin**: GitHub issue linking connects feedback reports to the external issue tracker.
- **Email**: Response emails are queued through the email outbox system.
- **Onboarding**: Feedback submission is available during onboarding, before the human is an active member.
