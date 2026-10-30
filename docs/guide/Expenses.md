<!-- freshness:triggers
  src/Sections/Humans.Expenses/**
-->
<!-- freshness:flag-on-change
  Expense lifecycle, IBAN access rules, Holded sync, and resource-based authorization — review when Expenses services/entities/controllers/auth handlers change.
-->

# Expenses

## What this section is for

Expenses is where you ask to be paid back when you've spent your own money on something for the org. You build a report with one or more items — purchases with a receipt attached — and submit it. Once it's approved, you're emailed automatically.

Finance handles the approval and pays you by bank transfer, and the org's accounting system is updated behind the scenes.

## Key pages at a glance

- **My expenses** (`/Expenses`) — your reports and where each one's up to, plus what the org currently owes you once payments start flowing
- **New report** (`/Expenses/New`) — start a new draft
- **Report detail** (`/Expenses/{id}`) — one report: its items, receipts, status, and history
- **Edit a draft** (`/Expenses/{id}/Edit`) — change a report while it's still a draft
- **Review queue** (`/Expenses/Review`) — reports waiting for your sign-off or approval; one page shared by coordinators, pre-approval program managers, Finance Admin, and Admin, scoped to what each can act on

## As a Volunteer

### Start a report

Go to `/Expenses/New` to start a draft. Pick the account the spending books to — one of the budget categories or another account — and give the report a **Subject** (the project name, up to 500 characters); the subject is required before you can submit. A report is a container — you add items to it: purchases with a description, an amount, and a **receipt**. A report with no items can't be submitted.

### File against a pre-approval

If the association pre-approved your spending (a creativity grant or a travel allowance), the open pre-approvals are listed on **My expenses** and on `/Preapprovals`. Use **File expense** next to one, or pick it on the New form: the account and the payout cap come from the pre-approval. There is no budget category, so a named manager of the pre-approval's program does the first review in a coordinator's place, then Finance approves. You have to submit before the pre-approval's claim-by date; a report sent back to you for fixes may still be resubmitted after it. Withdrawing a report before it's approved frees the pre-approval up again.

### Add items and attach receipts

For each thing you spent money on, add an item to the report. You're asked to pick one of two kinds: an **invoice** — a business (ZZP/autónomo) bills the association directly, made out to the NCA — or a **receipt**, where you paid yourself and want reimbursing. Invoices are preferred (the association can reclaim the VAT); receipts are for when an invoice wasn't possible or the amount is small. Attach the invoice file when you add it, then attach the receipts behind it as proof for the approver. A receipt item needs its own attachment before you can submit.

### Travel items (mileage and per diem)

Mileage and per diem can't be added to a report. Travel items on older reports still show and count towards the total, but can't be edited — remove one if it's wrong.

### Add your bank details (IBAN)

Your IBAN has to be on your profile before you can submit, since that's how you get paid. It's copied onto the report when you submit, so changing your profile later won't disturb a report that's already on its way.

### Submit and track

Once every purchase item has a receipt and your IBAN is set, submit. You can withdraw a report from its detail page once it's submitted, whether it's still waiting, coordinator-endorsed or already approved. A draft has no withdraw; just edit it or leave it. If a reviewer rejects a report it comes back to you as a draft: you can edit, replace or remove individual items and resubmit, there's no need to start over. A reviewer can also **decline** a single item with a reason — it stays on the report with the reason shown, but doesn't count toward your total; replace its file or remove the item to clear the decline. Withdrawn is final. A report moves through these stages:

- **Draft** — you're still building it
- **Submitted** — waiting for a coordinator's sign-off (if its category has one) or for Finance
- **Coordinator endorsed** — signed off by a coordinator; waiting for Finance
- **Approved** — Finance has approved it; booked into the org's accounting system. A coordinator or Finance Admin can cap what's actually paid out below your submitted total — the report detail page shows the payable amount if a cap applies. You'll get an email with the approved amount and your masked IBAN, if you have a notification address on file. Payment happens outside the app (bank transfer by Finance); you can see what you're owed on **My expenses**.
- **Withdrawn** — you pulled it back

![TODO: screenshot — expense report detail showing items, receipt links, and a status badge]

### See what you're owed

Once one of your reports has reached the org's accounting system, **My expenses** shows a card with your current balance and a ledger of every line Holded has booked to your account — what's owed to you and what's been paid, shown exactly as Holded records it. It's the account's real statement, not a summary of your reports, so the rows won't map one-to-one to what you submitted. If your account is linked but nothing's posted yet, or it isn't linked at all, you'll see a note instead.

## As a Coordinator

When you set a cap, the original receipts total remains visible while the lower cap becomes the payable amount.

If you coordinate a budget category, expense reports in that category come to you for sign-off first. Go to `/Expenses/Review` to see what's waiting. From a report, **endorse** it to pass it on to Finance, or **reject** it with a reason — endorsing lets you optionally cap the amount that gets paid out. While a report is waiting for you, you can also **decline** an individual item (with a reason) or undo that decline. This step only happens when the report's category actually has a coordinator assigned. A named manager of a pre-approval's program does the same first review for reports filed against that program's pre-approvals — they see them in the same queue and cannot review other programs' reports.

Coordinators can't approve — that requires Finance Admin.

## As a Board member / Admin (Finance Admin)

The tasks below need the **Finance Admin** or **Admin** role.

### Review and approve

Finance can set or replace a cap chosen by the coordinator.

Go to `/Expenses/Review` to see every report waiting for Finance. Open one to check its items, receipts, and who submitted it, then **approve** it or **reject** it with a reason — optionally capping the payable amount below what was submitted, or declining individual items (with a reason). On a pre-approved report the account is fixed and the cap can't go above the pre-approval's amount. When you approve, the accounting system is updated automatically in the background (and retried if there's a hiccup). If a push keeps failing, the report shows a banner and you can re-queue it manually from its detail page.

### Pay people

Approved reports are booked into the org's accounting system (Holded) automatically. Payment is made outside the app by bank transfer. Once paid, the member's creditor balance in Holded updates, and their **My expenses** page reflects it in their account ledger.

### File or fix a report for someone else

From `/Expenses/New` you can pick another member to file for — the report is theirs from that moment, and everything about it (payout, dashboard, exports) follows them, not you. While a report is still Draft, Submitted, or Coordinator endorsed, you can correct its details or set the member's IBAN on their behalf without sending it back a step, and you can submit it from Draft. Withdrawing always stays the member's own call, and file/edit access closes once a report is approved.

## Related sections

- [Budget](Budget.md) — a report's account can be a budget category, and that category decides whether a coordinator signs off.
- [Profiles](Profiles.md) — your IBAN lives on your profile and is copied onto a report when you submit.
