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

Onboarding is the path from signing up to becoming an active [Volunteer](Glossary.md#volunteer). It covers four things: creating your account, filling out your profile, consenting to the required legal documents, and being cleared by a [Consent Coordinator](Glossary.md#consent-coordinator). Once all of that is complete, you are added to the Volunteers system team and the rest of the app opens up.

Onboarding is about Volunteer access only. Applying for **Colaborador** or **Asociado** is a separate tier application that runs in parallel through [Board](Glossary.md#board) voting — it never blocks your Volunteer access, and is covered in the Governance guide.

If you are brand new, start with [GettingStarted.md](GettingStarted.md).

![TODO: screenshot — the "Things to do" checklist on the Home dashboard showing the three onboarding steps (Complete your profile / Accept agreements / Coordinator review) with a mix of completed and pending items]

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

- **Google OAuth.** Click "Sign in with Google" on the login page. Your display name and picture come across automatically. If you already have an account with the same email — verified or not — Google sign-in links to it rather than creating a duplicate (the OAuth callback checks verified UserEmails, then unverified UserEmails, then `User.Email`).
- **Magic link.** Enter your email and click "Send me a login link". You receive a one-time link that expires in 15 minutes. If no account exists yet, the same flow creates one (via the "Complete signup" page) and asks you to choose a display name. To prevent email-scanner replay, the link goes to a landing page with a confirm button — clicking the button is what actually signs you in.

If your email was imported from a mailing list, your account already exists and clicking your first magic link claims it.

### 2. Complete your profile

Profile setup asks for your name, pronouns, location, bio, birthday, and any contact fields you want to share. An emergency contact is optional but recommended.

A separate step (`/Profile/Me/ShiftInfo`) walks you through your skills, work-style preferences, and languages. Coordinators search by skill to find the right person for a role, so a thin profile is harder to place — fill these in honestly and fully even if you're not sure what you'll end up doing.

During this one-shot setup you also see a tier selector. Leave it on **Volunteer** unless you want to apply for Colaborador or Asociado — picking one reveals a short inline application form submitted alongside your profile. After initial onboarding, the profile edit page shows profile fields only; the tier selector does not reappear.

### 3. Sign the required legal documents

Visit `/Consent` and sign each required document. Signatures are append-only — they cannot be edited or deleted. Once every required document is signed, your **Coordinator review** task on the dashboard moves to pending and a Consent Coordinator is notified. The coordinator review and the legal-document signing run in parallel: a coordinator can clear (or flag) you before you finish signing every document — but you only enter the Volunteers team once both your profile is cleared and all required documents are signed.

### 4. Wait for your Coordinator review to clear

A Consent Coordinator reviews your profile and either **clears** it or **flags** it. If they flag it, onboarding is paused until a Board member or Admin manually approves you to override the flag.

### 5. Become an active Volunteer

When **both** your profile is cleared and all required documents are signed, you are automatically added to the Volunteers system team and the rest of the app opens up. The standard auto-approval path is silent — there is no welcome email; the dashboard's "Things to do" tile flips to done and the rest of the nav appears. (Only the manual Board/Admin override path — used after a flag is resolved via `ProfileController.ApproveVolunteer` — dispatches a "Welcome! You have been approved" notification.) The exception requiring manual review: if a Consent Coordinator flagged your status, a Board member or [Admin](Glossary.md#admin) acts via the **Approve** or **Reject** buttons on `/Profile/{id}/Admin` (which POST to `/Profile/{id}/Admin/Approve` or `/Profile/{id}/Admin/Reject`) — see [Profiles](Profiles.md) for that workflow.

While you are still onboarding, you can reach your profile, consents, feedback, legal documents, public camp pages, calendar, and the home dashboard — most of the app is gated until you are active.

## As a [Coordinator](Glossary.md#coordinator)

If you hold the **Consent Coordinator** role, your work in onboarding is reviewing the queue at `/OnboardingReview` — clearing, flagging, or rejecting new humans. Rejection records a reason and timestamp on the profile and notifies the human. That flow is documented in [LegalAndConsent.md](LegalAndConsent.md).

If you hold the **Volunteer Coordinator** role, you have read-only access to the same queue so you can assist new humans, but you cannot clear, flag, or reject — those actions all require Consent Coordinator (or Board / Admin).

## As a Board member / Admin (Human Admin)

Board members and Admins can do everything a Consent Coordinator can, plus:

- **Resolve flagged profiles.** Only Board or Admin can manually approve a flagged human (the Approve button on `/Profile/{id}/Admin`).
- **Vote on tier applications.** Board votes on Colaborador / Asociado applications at `/OnboardingReview/BoardVoting`; Admin can finalize a vote, set the meeting date, and override.
- **Review the full onboarding pipeline.** See [humans](Glossary.md#human) at every stage, including those stuck waiting on documents or a coordinator.

Admins and all coordinator roles bypass the membership gate entirely (`MembershipRequiredFilter` consults `RoleChecks.BypassesMembershipRequirement`), so you reach the full app regardless of your own onboarding status. Suspended Admins/Board keep their role claims so they can manage their own unsuspension, but lose the `ActiveMember` claim.

## Related sections

- [Profiles](Profiles.md) — what you fill in during step 2, and how it is used afterwards.
- [Legal and Consent](LegalAndConsent.md) — the documents you sign and the Consent Coordinator review flow.
- [Teams](Teams.md) — the Volunteers system team that makes you an active human.
- [Getting Started](GettingStarted.md) — a first-time walkthrough.
