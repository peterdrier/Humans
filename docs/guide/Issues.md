<!-- freshness:triggers
  src/Sections/Humans.Issues/Controllers/**
  src/Sections/Humans.Issues/Services/**
  src/Sections/Humans.Issues/Domain/**
  src/Sections/Humans.Issues/Data/Configurations/**
  src/Sections/Humans.Issues/Views/**
-->
<!-- freshness:flag-on-change
  Reporter-facing submission flow plus handler triage. Review when the submit form, the help-widget modal, the routing table (IssueSectionRouting), the status enum, the retention window, or the attachment rules change. The area→role table below is a copy of IssueSectionRouting.RolesFor — re-check it on any routing change.
-->

# Issues

Issues is how you tell us something is wrong, ask for something new, or ask a question. It replaced Feedback: everything filed from here on lives here.

## What this section is for

One queue per part of the app, one conversation per report. You file an issue, it lands with whoever looks after that part of the app, and the two of you talk in a single thread on the report until it is closed.

## Key pages at a glance

- **Your issues** (`/Issues`) — the list, with the selected issue's detail beside it
- **File an issue** (`/Issues/New`) — the full form
- **The help button** — on any page: **Create issue** opens the same form in a pop-up without losing your place

## As a [Volunteer](Glossary.md#volunteer)

### File an issue

Use the help button on any page and choose **Create issue**, or go to `/Issues/New`. Filing from the help button records the page you were on, which usually saves a round of "where were you when this happened?".

You fill in:

- **Title** — up to 200 characters
- **Type** — Bug, Feature, or Question
- **Area** — which part of the app, if you know. Leave it blank if you don't; it goes to the admins, who will route it
- **Description** — up to 5,000 characters
- **Attachment** — optional screenshot: JPEG, PNG, or WebP, up to 10 MB

### Follow it

`/Issues` shows every issue you reported, whatever its state, with a search box over titles and descriptions and filters for status and type. Open one and you get the whole history: your report, every reply, and every change a handler made to it.

Write in the same thread to add anything. **You get an email whenever a handler replies** — in your language, with their message in it.

### If it gets closed and you're still stuck

Comment on it. A closed issue reopens the moment its reporter says something else on it, and goes back into the handler's queue.

### How long it's kept

Six months after an issue is finally closed, it and its attachment are deleted for good.

### What you can't see

Other people's issues. You see exactly the ones you reported — no more, in any area — unless you hold a role that owns a queue.

## As a Coordinator (Ticket Admin, Camp Admin, Teams Admin, No Info Admin, Consent Coordinator, Volunteer Coordinator, Human Admin, Finance Admin)

If you hold a role that owns an area, that area's queue is yours. You see it at `/Issues` alongside your own reports, and the badge in your user menu counts everything in it still needing attention — issues in **Triage** or **Open**.

| Area | Who handles it |
|---|---|
| Tickets | Ticket Admin |
| Camps | Camp Admin |
| Teams | Teams Admin |
| Shifts | No-Info Admin |
| Onboarding | Consent Coordinator, Volunteer Coordinator, Human Admin |
| Profiles | Human Admin |
| Budget | Finance Admin |
| Governance | Board |
| Legal | Consent Coordinator |
| City Planning | Camp Admin |
| Scanner | Ticket Admin, Board |
| *(no area set)* | Admin only |

Admins see and handle everything.

### Work a queue

Filter by status, type, and area, and search titles and descriptions. Admins can also filter by reporter. Open an issue and you can:

- **Reply** in the thread — this emails the reporter
- **Move the status** — Triage (where new reports land) → Open → In Progress → Resolved, Won't Fix, or Duplicate
- **Assign it** to someone
- **Change the area** if it was filed in the wrong place, which hands it to that area's holders. Only possible while the issue is still open — reopen it first if it's closed
- **Link a GitHub issue** by number, once the work is tracked

Everything you change is recorded in the thread, so the reporter can see what happened and when.

### Notifications

You're notified in-app when a new issue lands in one of your areas, and again when a reporter replies to one. Reporter replies don't email you — otherwise every conversation would page the whole queue. Your replies *do* email the reporter.

## Automation

`/api/backdoor/issues` is key-authenticated (`X-Api-Key`), using the personal key an admin allocated to you at `/Backdoor`. It lists and reads issues, files one, comments, and changes status, assignee, area, or the linked GitHub issue. Anything it writes is attributed to the human whose key was used.

One difference from the browser: a comment posted through the API always counts as a handler's, even when the key belongs to the issue's own reporter. So an API comment on a closed issue does **not** reopen it, and it notifies as a handler reply would. Comment in the browser if you want the reopen.

## Related sections

- [Feedback](Feedback.md) — the retired predecessor; its archive is still there for admins
- [The in-app AI helper](AiHelper.md) — try it first for a question; it can file an issue for you
- [Admin](Admin.md) — roles are assigned on the admin role pages; API keys are allocated at `/Backdoor`
