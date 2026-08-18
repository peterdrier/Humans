<!-- freshness:triggers
  src/Sections/Humans.Feedback/Controllers/**
  src/Sections/Humans.Feedback/Services/**
  src/Sections/Humans.Feedback/Domain/**
  src/Sections/Humans.Feedback/Data/Configurations/**
  src/Sections/Humans.Feedback/Views/**
-->
<!-- freshness:flag-on-change
  Retired section: admin-only triage of historical reports, status transitions, GitHub linkage, and the API triage path. There is no submission flow — if any change reintroduces a creation path, this page is wrong. Review when feedback views, controllers, services, or entities change.
-->

# Feedback

> **Feedback is retired.** It no longer accepts new reports — Issues superseded it. To report a bug, request a feature, or ask a question, use the help button's **Create issue** action or go to `/Issues`. What remains here is the historical archive and its admin triage screens.

## What this section is for

Feedback was the original in-app way to report a bug, suggest an improvement, or ask a question. Reports already filed are kept — with their categories, screenshots, page URLs, and conversation threads — so admins can finish triaging them. Nothing new can be filed.

Status still moves from Open to Acknowledged, then to Resolved or Won't Fix, and a report can still be linked to a GitHub issue.

## Key pages at a glance

Both require the **Admin** role. There is no reporter-facing view — a report's own author cannot open it.

- **Feedback queue** (`/Feedback`) — every historical report, with the selected report's detail panel beside it
- **Feedback detail** (`/Feedback/{id}`) — the full report: description, screenshot, page URL, status, assignment, and the conversation thread. It is a panel inside the queue, not a page of its own; opening the URL directly sends you to `/Feedback?selected={id}`

## As a Volunteer

Nothing. You cannot file feedback and you cannot open your old reports. File an issue instead — the help button on any page, **Create issue**, or `/Issues`.

If an admin replies to one of your historical reports you still get an email with their response in your preferred language, and a notification in your inbox telling you a response arrived. Neither links back to the report, because the report is admin-only now; the reply text is in the email itself.

## As a Board member / Admin

These screens require the full **Admin** role. `FeedbackAdmin` on its own no longer grants any feedback access.

### Triage the queue

Go to `/Feedback`. You see every report ever filed. The admin nav shows a badge with the count of reports that need a reply — reports where the reporter posted a message more recently than any admin response, or where the report is still Open and no admin has ever replied. Resolved and Won't Fix reports never count toward the badge. Filter by status, category, reporter, assignee, team, or unassigned-only, and click a report to open its detail panel on the right.

### Reply to a reporter

In the detail view, post a message in the conversation thread. Every message posted now is an admin reply: it stamps the report's last-admin-reply time, queues an email to the reporter, and sends them a notification. Reporters can no longer post follow-ups, so the thread is one-directional from here on — historical reporter messages are still shown.

### Assign a report

The detail panel carries two dropdowns: one to put a report on a human, one to put it on a team. Either can be cleared back to unassigned, and both save as soon as you pick. A human or team that has since gone inactive still shows in the list while it holds the assignment, so opening the panel never silently drops it.

### Update status and link a GitHub issue

Move status from Open to Acknowledged when you have seen the report, then to Resolved or Won't Fix when it is closed out. Status changes are audit-logged. If the report needs tracked work, open an issue on `nobodies-collective/Humans` and paste the issue number into the GitHub Issue field.

### Close resolved feedback

Once work is shipped (or the report is declined), set status to Resolved or Won't Fix. Closing does not delete anything.

## Automation

`/api/feedback` is unchanged and still key-authenticated (`X-Api-Key`). It covers the same triage the UI does — list reports, read one and its messages, post a message, and change status, assignment, or the linked GitHub issue — but it has no report-creation endpoint. A message posted through it is an admin reply, exactly as one posted in the UI.

## Related sections

- [Admin](Admin.md) — roles are assigned via the admin role pages; the Admin configuration page shows whether the feedback API key is set
