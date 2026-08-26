<!-- freshness:triggers
  src/Sections/Humans.Expenses/**
-->
<!-- freshness:flag-on-change
  Expense lifecycle, IBAN access rules, Holded sync, and resource-based authorization — review when Expenses services/entities/controllers/auth handlers change.
-->

# Expenses

## What this section is for

Expenses is where you ask to be paid back when you've spent your own money on something for the org. You build a report with one or more items — purchases with a receipt attached — and submit it. Once it's approved and paid, you're told automatically.

Finance handles the approval and pays you by bank transfer, and the org's accounting system is updated behind the scenes.

## Key pages at a glance

- **My expenses** (`/Expenses`) — your reports and where each one's up to, plus what the org currently owes you once payments start flowing
- **New report** (`/Expenses/New`) — start a new draft
- **Report detail** (`/Expenses/{id}`) — one report: its items, receipts, status, and history
- **Edit a draft** (`/Expenses/{id}/Edit`) — change a report while it's still a draft
- **Review queue** (`/Expenses/Review`) — reports waiting for your sign-off or approval; one page shared by coordinators, Finance Admin, and Admin, scoped to what each can act on

## As a Volunteer

### Start a report

Give the report an optional **Subject** (up to 500 characters) so you and the reviewers can recognize it later.

Go to `/Expenses/New` to start a draft. A report is a container — you add items to it: purchases with a description, an amount, and a **receipt**. A report with no items can't be submitted.

### Add items and attach receipts

For each thing you spent money on, add an item to the report. You're asked to pick one of two kinds: an **invoice** — a business (ZZP/autónomo) bills the association directly, made out to the NCA — or a **receipt**, where you paid yourself and want reimbursing. Invoices are preferred (the association can reclaim the VAT); receipts are for when an invoice wasn't possible or the amount is small. Attach the invoice file when you add it, then attach the receipts behind it as proof for the approver. A receipt item needs its own attachment before you can submit.

### Travel items (mileage and per diem)

Mileage and per diem can no longer be added to a report. Reports filed before this changed still show their travel items, and those items still count towards the total, but they can't be edited — remove one if it's wrong.

### Add your bank details (IBAN)

Your IBAN has to be on your profile before you can submit, since that's how you get paid. It's copied onto the report when you submit, so changing your profile later won't disturb a report that's already on its way.

### Submit and track

Once every purchase item has a receipt and your IBAN is set, submit. You can withdraw a submitted report from its detail page as long as it hasn't been approved yet. A report moves through these stages:

- **Draft** — you're still building it
- **Submitted** — waiting for a coordinator's sign-off (if its category has one) or for Finance
- **Coordinator endorsed** — signed off by a coordinator; waiting for Finance
- **Approved** — Finance has approved it; booked into the org's accounting system. A coordinator or Finance Admin can cap what's actually paid out below your submitted total — the report detail page shows the payable amount if a cap applies. Payment happens outside the app (bank transfer by Finance); you can see what you're owed on **My expenses**.
- **Withdrawn** — you pulled it back

![TODO: screenshot — expense report detail showing items, receipt links, and a status badge]

### See what you're owed

Once one of your reports has reached the org's accounting system, **My expenses** shows a card with your current balance and a ledger of every line Holded has booked to your account — what's owed to you and what's been paid, shown exactly as Holded records it. It's the account's real statement, not a summary of your reports, so the rows won't map one-to-one to what you submitted. If your account is linked but nothing's posted yet, or it isn't linked at all, you'll see a note instead.

## As a Coordinator

When you set a cap, the original receipts total remains visible while the lower cap becomes the payable amount.

If you coordinate a budget category, expense reports in that category come to you for sign-off first. Go to `/Expenses/Review` to see what's waiting. From a report, **endorse** it to pass it on to Finance, or **reject** it with a reason — endorsing lets you optionally cap the amount that gets paid out. This step only happens when the report's category actually has a coordinator assigned.

Coordinators can't approve — that requires Finance Admin.

## As a Board member / Admin (Finance Admin)

The tasks below need the **Finance Admin** or **Admin** role.

### Review and approve

Finance can set or replace a cap chosen by the coordinator.

Go to `/Expenses/Review` to see every report waiting for Finance. Open one to check its items, receipts, and who submitted it, then **approve** it or **reject** it with a reason — optionally capping the payable amount below what was submitted. When you approve, the accounting system is updated automatically in the background (and retried if there's a hiccup). If a push keeps failing, the report shows a banner and you can re-queue it manually from its detail page.

### Pay people

Approved reports are booked into the org's accounting system (Holded) automatically. Payment is made outside the app by bank transfer. Once paid, the member's creditor balance in Holded updates, and their **My expenses** page reflects it in their account ledger.

## Related sections

- [Budget](Budget.md) — reports are filed against budget categories, and the category decides whether a coordinator signs off.
- [Profiles](Profiles.md) — your IBAN lives on your profile and is copied onto a report when you submit.
