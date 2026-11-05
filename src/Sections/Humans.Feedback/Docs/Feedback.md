<!-- freshness:triggers
  src/Sections/Humans.Feedback/**
-->

# Feedback — Section Invariants

> **Removed (2026-11-01).** Feedback was superseded by [Issues](../../Humans.Issues/Docs/Issues.md)
> and is gone: no pages, no Backdoor API, no role, no jobs, no GDPR contributor. What is left is a
> stub that exists only so the migration dropping its tables can run. Delete the whole project once
> production has applied `DropFeedbackTables`.

## Concepts

None. Feedback holds no data and serves no requests.

## Data Model

`FeedbackDbContext` maps no entities. Its one live migration, `DropFeedbackTables`, drops
`feedback_reports` and `feedback_messages`; the reports and their threads are not kept anywhere. The screenshots went first, through the `feedback.delete-screenshots` data migration, which this removal retires.

## Routing

None. `/Feedback` no longer exists, and `PrivateUploadMiddleware` returns 404 for every
`/uploads/feedback/*` path, whoever asks; the files themselves were deleted before the drop.

## Actors & Roles

None. The `FeedbackAdmin` role is gone; any assignment rows that still name it grant nothing.

## Invariants

- The stub keeps `FeedbackResponse` declared as a retired notification source, so the inbox purge
  clears the rows Feedback already wrote.
- The stub keeps Feedback's four audit action names declared, so stored audit rows still resolve.
  When the stub is deleted, those declarations need a new home first.

## Negative Access Rules

None. There is nothing to reach.

## Triggers

None. No jobs, no event listeners.

## Cross-Section Dependencies

- **AuditLog, Notifications:** the retired declarations above.
- **Email:** `MessageCategory.System`, the category the retired notification source declares.
- **Agent:** `agent_conversations.HandedOffToFeedbackId` is Agent's legacy column; it now points
  at nothing.

## Architecture

**Owning services:** none. **Owned tables:** none after `DropFeedbackTables`. **Public contract:** none.
