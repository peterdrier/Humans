# Getting Started

This page walks you through your first session in the Humans app: signing up, completing your profile, consenting to the legal documents, and finding your way around. Three steps take you from a new account to an active [Volunteer](Glossary.md#volunteer) with the full app unlocked: sign up, complete your profile (your legal name is the key field), and consent to the legal documents. Everything is self-service and automatic.

## 1. Sign up

Go to `/Account/Login`. You have two ways in:

- **Sign in with Google.** Your display name comes across automatically. If your email was already imported from a mailing list, Google sign-in claims that existing record rather than creating a duplicate.
- **Email me a login link.** Type your address and the system mails you a one-time magic link that expires in 15 minutes. If you do not have an account yet, the same flow creates one and asks you to pick a display name.

If you land on `/Guest`, that means you are signed in but have no profile yet — step 2 is next. The full signup pipeline is documented in [Onboarding](Onboarding.md). Can't get in, or stuck on the login link? See [Signing in & getting unstuck](SigningIn.md).

## 2. Complete your profile

Open `/Profile/Me/Edit` (the Home dashboard also nudges you here via its Getting Started checklist). You fill in:

- Your name, pronouns, city, country, bio, and birthday (month and day).
- A profile picture, if you want one.
- Any contact fields you want to share — Phone, Signal, Telegram, WhatsApp, Discord, or a custom "Other" handle. Each field has its own **visibility** setting: Board only, Coordinators and Board, My teams, or All active humans. You pick one per field when you add it.
- An emergency contact (optional, recommended).
- Your skills and shift preferences (you'll be nudged through `/Profile/Me/ShiftInfo` separately). **Fill these in honestly and fully** — coordinators search by skill to find the right person for a role, so a thin profile is harder to place.

During this one-shot setup there is also a tier selector. Leave it on **Volunteer** unless you specifically want to apply for Colaborador or Asociado — those are separate tier applications reviewed by the Board and they never block your Volunteer access. Details in [Profiles](Profiles.md).

## 3. Consent to legal documents

Go to `/Consent`. You see the documents that apply to everyone, grouped by the Volunteers team. Open each one, read it, pick your language tab if you want (Castellano is always the canonical legally binding version; other languages are marked as translations), and tick the explicit consent checkbox — it is never pre-ticked.

Your signature is written as an immutable record: the document version, a hash of the exact text, the timestamp, your IP, and your browser. Nobody can edit or delete that entry afterwards — not Admin, not the database owner. That is what makes the audit trail worth something.

Profile completion and consent run in parallel — you can do them in either order. See [Legal and Consent](LegalAndConsent.md) for the full picture.

## 4. You're a Volunteer

Entering your legal name is what opens the app, and once you have also signed every required document, the scheduled sync adds you to the Volunteers system team automatically — no manual step, no waiting on a person. (Only suspension or a rejected signup removes access.)

If an Admin has provisioned a `@nobodies.team` Google Workspace account for you, you also get a credentials email at your personal address with a temporary password and a 2FA setup prompt. That address then becomes your Google service email for every team you join. Teams with linked Google Groups or [Shared Drive](Glossary.md#shared-drive) folders grant you access on the next sync. See [Your `@nobodies.team` email](EmailAccount.md) for how the mailbox works and how to use your team's group address, and [Two-step verification (2FA)](TwoStepVerification.md) for that required sign-in step. [Email](Email.md) and [Google Integration](GoogleIntegration.md) cover the fuller reference and the sync mechanics underneath.

![TODO: screenshot — profile page right after auto-approval to Volunteer, with the Volunteers system team visible on the profile card and the Home dashboard checklist fully ticked]

## Where to go next

### If you're a Volunteer

- [Teams](Teams.md) — browse the directory and join a team, or request to join one that requires approval.
- [Shifts](Shifts.md) — sign up for shifts once you are on a team that runs them.
- [Profiles](Profiles.md) — add more email addresses, set communication preferences, tune contact-field visibility, export your data, or request deletion.

### If you're a [Coordinator](Glossary.md#coordinator)

- [Teams](Teams.md) — approve join requests, manage members, edit your department's public page, define role slots.
- [Shifts](Shifts.md) — schedule humans on your team's rotas.
- [Budget](Budget.md) — propose line items for your team's category, if budget access is in scope for your role.

### If you're a [Board](Glossary.md#board) member or [Admin](Glossary.md#admin)

- [Governance](Governance.md) — Colaborador and Asociado tier applications, Board votes, role assignments.
- [Admin](Admin.md) — global tools, audit log, sync settings, the flagged-safety-check queue.
- [Budget](Budget.md) — fiscal-year planning, category approvals, and the budget audit trail.

## What to expect between now and Elsewhere (2026)

| Period | What's happening |
|---|---|
| Now – May | Light pre-production: read up on your role, join team channels, flag questions. |
| May – June | Things accelerate: decisions made, plans confirmed, possible team calls. |
| Set-up (15 June+) | On-site work begins. Exact arrival date confirmed with your coordinator. |
| Event (7–12 July) | Your role shifts to event operations for your department. |
| Strike (13–22 July) | Everyone helps strike. Stay as long as you can. |

## Key contacts

| Reach | For |
|---|---|
| [volunteers@nobodies.team](mailto:volunteers@nobodies.team) | General volunteer queries, finding a role |
| [tickets@nobodies.team](mailto:tickets@nobodies.team) | Ticket questions |
| [inclusion@nobodies.team](mailto:inclusion@nobodies.team) | Accessibility and low-income ticket programme |
| [humans@nobodies.team](mailto:humans@nobodies.team) | **Last resort** for app or `@nobodies.team` account issues — use the in-app help button and Discord first |
| [#🤝-recruitment-relationships](https://discord.gg/pcq2DRH6) on Discord | Quick questions and connecting with the team |
| [#🧘-humans-app](https://discord.gg/fq7gr29p) on Discord | Humans app feedback and bug reports |

You can also use the round help button (question mark, bottom right of the app) for anything related to the app itself — it offers the AI helper, issue filing, and feedback.

## Key terms

See the [Glossary](Glossary.md) for defined terms used throughout the guide — Volunteer, Colaborador, Asociado, Coordinator, Board, department, sub-team, system team, service account, and more.
