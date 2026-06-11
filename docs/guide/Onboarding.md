<!-- freshness:triggers
  src/Humans.Web/Views/Account/**
  src/Humans.Web/Views/Guest/**
  src/Humans.Web/Views/Home/**
  src/Humans.Web/Views/Profile/Edit.cshtml
  src/Humans.Web/Views/Profile/ShiftInfo.cshtml
  src/Humans.Web/Views/Consent/**
  src/Humans.Web/Views/OnboardingReview/Index.cshtml
  src/Humans.Web/Views/OnboardingReview/Detail.cshtml
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Controllers/GuestController.cs
  src/Humans.Web/Controllers/HomeController.cs
  src/Humans.Web/Controllers/OnboardingReviewController.cs
  src/Humans.Application/Services/Onboarding/**
  src/Humans.Application/Services/Auth/MagicLinkService.cs
  src/Humans.Application/Services/Consent/**
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
-->
<!-- freshness:flag-on-change
  Sign-up paths (Google OAuth, magic link), profile setup wizard, consent gate, Consent Coordinator clearance, and Volunteer activation. Review when onboarding services, account views, or membership filter change.
-->

# Onboarding

## What this section is for

Onboarding is the path from signing up to becoming an active [Volunteer](Glossary.md#volunteer). It covers three things: creating your account, filling out your profile, and consenting to the required legal documents. Entering your legal name sets `UserState == Active`, which opens the app. Your legal name plus all required consents determine Volunteers-system-team provisioning for Google Workspace.

Onboarding is about Volunteer access only. Applying for **Colaborador** or **Asociado** is a separate tier application that runs in parallel through [Board](Glossary.md#board) voting — it never blocks your Volunteer access, and is covered in the Governance guide.

If you are brand new, start with [GettingStarted.md](GettingStarted.md).

![TODO: screenshot — the "Things to do" checklist on the Home dashboard showing the onboarding steps (Complete your profile / Accept agreements) with a mix of completed and pending items]

## Key pages at a glance

- `/` — Home dashboard with your "Things to do" checklist.
- `/Profile/Me/Edit` — profile setup (one-shot during onboarding, then a regular edit page).
- `/Profile/Me/ShiftInfo` — skills, work-style preferences, and languages used to staff shifts.
- `/Consent` — the legal documents to read and sign.
- `/Account/Login` — Google sign-in and the "Send me a login link" magic-link option.
- `/Guest` — where you land if your account has no profile yet.

## As a Volunteer

### 1. Sign up

You have two ways to create an account:

- **Google OAuth.** Click "Sign in with Google" on the login page. Your display name comes across automatically. If you already have an account with the same email — verified or not — Google sign-in links to it rather than creating a duplicate (the OAuth callback checks verified UserEmails, then unverified UserEmails, then `User.Email`).
- **Magic link.** Enter your email and click "Send me a login link". You receive a one-time link that expires in 15 minutes. If no account exists yet, the same flow creates one (via the "Complete signup" page) and asks you for a burner name and your first and last name. To prevent email-scanner replay, the link goes to a landing page with a confirm button — clicking the button is what actually signs you in.

If your email was imported from a mailing list, your account already exists and clicking your first magic link claims it.

### 2. Complete your profile

The very first thing the app asks for is your name. However you signed in, until you've set a burner name and your legal first and last name, the app sends you to a short "let's start with your name" form before anything else opens up — you can't browse the rest of the site nameless. (This never blocks signing in; it only redirects you to the name form once you're in.) Once your name is saved, the gate lifts on your next click.

After that, profile setup asks for your pronouns, location, bio, birthday, and any contact fields you want to share. An emergency contact is optional but recommended.

A separate step (`/Profile/Me/ShiftInfo`) walks you through your skills, work-style preferences, and languages. Coordinators search by skill to find the right person for a role, so a thin profile is harder to place — fill these in honestly and fully even if you're not sure what you'll end up doing.

During this one-shot setup you also see a tier selector. Leave it on **Volunteer** unless you want to apply for Colaborador or Asociado — picking one reveals a short inline application form submitted alongside your profile. After initial onboarding, the profile edit page shows profile fields only; the tier selector does not reappear.

### 3. Sign the required legal documents

Visit `/Consent` and sign each required document. Signatures are append-only — they cannot be edited or deleted. You enter the Volunteers team once you have entered your legal name and signed all required documents.

### 4. Become an active Volunteer

When you enter your legal name, your stored `UserState` becomes `Active` and the app opens. When you have a legal name and all required documents are signed, the scheduled system-team sync adds you to the Volunteers Google Workspace provisioning group. Only a rejected signup (which records a rejection timestamp and reason) removes you from the Volunteers team.

While you are still onboarding, you can reach your profile, consents, feedback, legal documents, public camp pages, calendar, and the home dashboard — most of the app is gated until you are active.

## As a [Coordinator](Glossary.md#coordinator)

If you hold the **Consent Coordinator** role, your work in onboarding is reviewing the queue at `/OnboardingReview` — clearing, flagging, or rejecting new humans. Rejection records a reason and timestamp on the profile and notifies the human. That flow is documented in [LegalAndConsent.md](LegalAndConsent.md).

If you hold the **Volunteer Coordinator** role, you have read-only access to the same queue so you can assist new humans, but you cannot clear, flag, or reject — those actions all require Consent Coordinator (or Board / Admin).

## As a Board member / Admin (Human Admin)

Board members and Admins can do everything a Consent Coordinator can, plus:

- **Resolve flagged profiles.** Board/Admin can clear or reject flagged reviews from `/OnboardingReview`; human admins can reject a signup from `/Users/Admin/{id}`.
- **Vote on tier applications.** Board votes on Colaborador / Asociado applications at `/Governance/BoardVoting`; Admin can finalize a vote, set the meeting date, and override.
- **Review the full onboarding pipeline.** See [humans](Glossary.md#human) at every stage, including those stuck waiting on documents or a coordinator.

Roles do not bypass the `UserState` access gate. Staff roles authorize staff tools after the user is `Active`; suspended, rejected, deleted, merged, and delete-pending users are routed by state before reaching role-gated pages.

## Related sections

- [Profiles](Profiles.md) — what you fill in during step 2, and how it is used afterwards.
- [Legal and Consent](LegalAndConsent.md) — the documents you sign and the Consent Coordinator review flow.
- [Teams](Teams.md) — the Volunteers system team that makes you an active human.
- [Getting Started](GettingStarted.md) — a first-time walkthrough.
