<!-- freshness:triggers
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Views/Account/**
  src/Humans.Application/Services/Auth/MagicLinkService.cs
  src/Humans.Web/Controllers/GuestController.cs
-->
<!-- freshness:flag-on-change
  Login options, magic-link expiry/single-use/confirm-button mechanics, and the enumeration-safe "check your email" message. Review when AccountController, Account views, or MagicLinkService change.
-->

# Signing in & getting unstuck

Getting into the Humans app is meant to be painless — **there's no password to
remember.** This page covers the two ways in and what to do when something
doesn't work.

## Two ways in

Go to the login page (`/Account/Login`). You'll see two options:

- **Email me a login link.** Type your email address and we send you a link.
  Click it and you're in. Nothing to memorize.
- **Sign in with Google.** One click, using a Google account you're already
  signed into. Your name comes across automatically.

Either works. The login link is the simplest if you're not sure.

## How the email login link works

1. Type your email and ask for a link.
2. You'll always see a **"check your email"** message — that's normal (we show it
   to everyone, so nobody can fish for whose addresses exist).
3. Open the email and click the link. It takes you to a page with a **button** —
   click that button to finish signing in. (The extra click is on purpose: it
   stops automatic spam-filters from "using up" your link before you do.)
4. You're in.

A few things to know about the link:

- It **expires after 15 minutes** — ask for a fresh one if it's been a while.
- It works **once**. If you click it, then try the same link again later, it
  won't work — just request a new one.
- If you ask for a link twice in quick succession, you'll only get one. Give it
  a minute before trying again.

## When you can't get in

| What's happening | What to do |
|---|---|
| The link didn't arrive | Check your spam/junk folder. Make sure you typed the right address. Wait a minute, then ask for a new one |
| "This link has expired" or it won't work | Links last 15 minutes and work once — just request a fresh one |
| I clicked the link but nothing happened | Make sure you click the **button** on the page the link opens — that's the step that signs you in |
| Google sign-in won't go through | Try the "email me a login link" option instead — it gets you to the same place |
| I'm signed in but the app looks empty / locked | You're in, but your profile isn't finished yet. Fill it in (you'll be nudged to `/Profile/Me/Edit`) and the rest opens up — see [Getting Started](GettingStarted.md) |
| I think I have two accounts | Don't make a third one. Sign in to either and use the help button (bottom right) to file an issue so they can be merged |
| Totally stuck | Ask in [#🧘-humans-app](https://discord.gg/fq7gr29p) on Discord. If you truly can't get in and nobody there could help, email [humans@nobodies.team](mailto:humans@nobodies.team) as a last resort |

## A note on your two logins

Signing in to **the Humans app** (this) and signing in to your **`@nobodies.team`
email** are two separate things:

- The **app** uses the login link or "sign in with Google" above — no password.
- Your **`@nobodies.team` email** is a Google account with its own password and
  its own [two-step verification](TwoStepVerification.md).

If you're trying to get into your *email* rather than the app, see
[Your `@nobodies.team` email](EmailAccount.md).

## Related

- [Getting Started](GettingStarted.md) — your first session, start to finish.
- [Your data & privacy](YourData.md) — what we store and how to get a copy or
  delete your account.
