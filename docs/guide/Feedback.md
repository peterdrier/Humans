<!-- freshness:triggers
  src/Humans.Web/Views/Feedback/**
  src/Humans.Web/Controllers/FeedbackController.cs
  src/Humans.Web/Controllers/FeedbackApiController.cs
  src/Humans.Web/ViewComponents/FeedbackWidgetViewComponent.cs
  src/Humans.Application/Services/Feedback/**
  src/Humans.Domain/Entities/FeedbackReport.cs
  src/Humans.Domain/Entities/FeedbackMessage.cs
  src/Humans.Infrastructure/Data/Configurations/Feedback/**
-->
<!-- freshness:flag-on-change
  Floating feedback widget, submission flow, my-reports view, FeedbackAdmin triage, status transitions, and GitHub linkage. Review when feedback views, controllers, services, or entities change.
-->

# Feedback

## What this section is for

Feedback is how you report a bug, suggest an improvement, or ask a question without leaving the app. Every authenticated human — including humans in onboarding — can submit a report. Each report has a category (bug, feature request, or question), an optional screenshot, the page URL, and a conversation thread between you and the humans who triage feedback.

Status moves from Open to Acknowledged, then to Resolved or Won't Fix. FeedbackAdmin and Admin see every report and triage them, linking to a GitHub issue when the work needs tracking.

## Key pages at a glance

- **Feedback button** — floating widget on every page; opens the submission modal
- **My feedback / feedback inbox** (`/Feedback`) — list of feedback reports. Regular humans see only their own; FeedbackAdmin and Admin see all
- **Feedback detail** (`/Feedback/{id}`) — the full report: description, screenshot, page URL, status, and the conversation thread

## As a Volunteer

### Submit feedback

Click the floating feedback button on any page. In the modal, pick a category (Bug, Feature Request, or Question), write a description, and optionally attach a screenshot (JPEG, PNG, or WebP, up to 10 MB). The page URL and your browser's user agent are captured automatically.

![TODO: screenshot — floating feedback button and submission modal with category dropdown, description field, and screenshot upload]

### Find and follow your reports

Open **My Feedback** from your profile dropdown, or go to `/Feedback`. You see your reports and their current status. Click a report to open the detail view with the full description, your screenshot, and the message thread. Post a follow-up in the same thread at any time.

### Get notified when someone replies

When an admin posts a message on one of your reports, you receive an email in your preferred language with the reply and a direct link back to the report.

## As a Board member / Admin (Feedback Admin)

The capabilities below require the **FeedbackAdmin** or **Admin** role. FeedbackAdmin is scoped to feedback triage only — it does not grant any other admin power.

### Triage the inbox

Go to `/Feedback`. You see every report from every human. The main nav shows a badge with the count of reports that need your reply — reports where the reporter has posted a message more recently than any admin response, or where no admin has ever replied. Filter by status and category, and click a report to open its detail panel.

### Reply to a reporter

In the detail view, post a message in the conversation thread. Your message is saved and an email is queued to the reporter automatically. The thread is bidirectional — reporter follow-ups land in the same place.

### Update status and link a GitHub issue

Move status from Open to Acknowledged when you have seen the report, then to Resolved or Won't Fix when it is closed out. Status changes are audit-logged. If the report needs tracked work, open an issue on `nobodies-collective/Humans` and paste the issue number into the GitHub Issue field — this keeps the inbox aligned with the dev backlog.

### Close resolved feedback

Once work is shipped (or the report is declined), set status to Resolved or Won't Fix. The reporter still sees the report and the full thread — closing does not delete anything.

## Related sections

- [Admin](Admin.md) — FeedbackAdmin and other roles are assigned via the admin role pages; the Admin configuration page shows whether the feedback API key is set
