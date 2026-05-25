<!-- freshness:triggers
  src/Humans.Application/Services/Expenses/**
  src/Humans.Domain/Entities/ExpenseReport.cs
  src/Humans.Domain/Entities/ExpenseLine.cs
  src/Humans.Domain/Entities/ExpenseAttachment.cs
  src/Humans.Domain/Entities/HoldedExpenseOutboxEvent.cs
  src/Humans.Domain/Enums/ExpenseReportStatus.cs
  src/Humans.Infrastructure/Repositories/Expenses/**
  src/Humans.Web/Controllers/ExpensesController.cs
-->
<!-- freshness:flag-on-change
  Expense lifecycle, IBAN access rules, SEPA generation, Holded sync, and resource-based authorization — review when Expenses services/entities/controllers/auth handlers change.
-->

# Expenses

## What this section is for

Expenses is where you ask to be paid back when you've spent your own money on something for the org. You build a report with one or more items, attach a receipt to each, and submit it. Once it's approved and paid, you're told automatically.

Finance handles the approval and pays you by bank transfer, and the org's accounting system is updated behind the scenes.

## Key pages at a glance

- **My expenses** (`/Expenses`) — your reports and where each one's up to
- **New report** (`/Expenses/New`) — start a new draft
- **Report detail** (`/Expenses/{id}`) — one report: its items, receipts, status, and history
- **Edit a draft** (`/Expenses/{id}/Edit`) — change a report while it's still a draft
- **Coordinator queue** (`/Expenses/Coordinator`) — reports waiting for your sign-off (coordinators)
- **Finance review** (`/Expenses/Review`) — reports waiting for approval (Finance Admin and Admin)

## As a Volunteer

### Start a report

Go to `/Expenses/New` to start a draft. A report is a container — you add items to it, each with a description, an amount, and a **receipt**. A report with no items can't be submitted.

### Add items and attach receipts

Add each thing you spent money on as an item, and attach the receipt or supporting document when you add it. Every item needs a receipt before you can submit.

### Add your bank details (IBAN)

Your IBAN has to be on your profile before you can submit, since that's how you get paid. It's copied onto the report when you submit, so changing your profile later won't disturb a report that's already on its way.

### Submit and track

Once every item has a receipt and your IBAN is set, submit. You can withdraw a submitted report from its detail page as long as it hasn't been approved yet. A report moves through these stages:

- **Draft** — you're still building it
- **Submitted** — waiting for a coordinator's sign-off (if its category has one) or for Finance
- **Coordinator endorsed** — signed off by a coordinator; waiting for Finance
- **Approved** — Finance has approved it; payment is being prepared
- **SEPA sent** — included in a bank-transfer batch; on its way
- **Paid** — money's gone out
- **Withdrawn** — you pulled it back

![TODO: screenshot — expense report detail showing items, receipt links, and a status badge]

## As a Coordinator

If you coordinate a budget category, expense reports in that category come to you for sign-off first. Go to `/Expenses/Coordinator` to see what's waiting. From a report, **endorse** it to pass it on to Finance, or **reject** it with a reason. This step only happens when the report's category actually has a coordinator assigned.

Coordinators can't approve payments or create bank-transfer batches — that's Finance.

## As a Board member / Admin (Finance Admin)

The tasks below need the **Finance Admin** or **Admin** role.

### Review and approve

Go to `/Expenses/Review` to see every report waiting for Finance. Open one to check its items, receipts, and who submitted it, then **approve** it or **reject** it with a reason. When you approve, the accounting system is updated automatically in the background (and retried if there's a hiccup).

### Pay people (SEPA batch)

From the Finance review page, generate a bank-transfer file covering all the approved, unpaid reports, and confirm once you've sent it — that moves those reports to **SEPA sent**. The system then checks for confirmation periodically and marks each one **Paid** once the money has gone.

## Related sections

- [Budget](Budget.md) — reports are filed against budget categories, and the category decides whether a coordinator signs off.
- [Profiles](Profiles.md) — your IBAN lives on your profile and is copied onto a report when you submit.
