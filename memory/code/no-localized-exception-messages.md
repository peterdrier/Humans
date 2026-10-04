---
name: Never localize exception messages
description: Nothing deriving from Exception carries localization without Peter's explicit approval — no resx text in `throw new …(…)`, no resx keys/args on exception types, no mapping `ex.Message` to resx keys.
---

Never localize exceptions. If it derives from `Exception`, it carries no localization — no resx text, no resx key, no format args for a localizer — without Peter's explicit approval. Exception text is developer-facing English, logged as-is. Do not build an exception message from a resource. Do not translate a caught exception's `Message` for display, either by switching on its English text or by using it as a localizer key.

**Why:** Peter, 2026-10-04, on peterdrier/Humans#1899. The nightly debt sweep localized member email refusals by string-matching `ex.Message` in `ProfileEmailsController`. That coupled the service wording to the controller, so any reword silently falls back to English. Peter reverted it outright ("we do not localize exception messages") and closed the follow-up, peterdrier/Humans#1900, as not planned. Same day, on the debt review of peterdrier/Humans#1903, Peter widened it: "NO LOCALIZATION OF EXCEPTIONS EVER" and "if it derives from EXCEPTION object, it is forbidden from localization without explicit approval from me" (naming `ExpenseValidationException`).

**How to apply:**
- Treat a hardcoded-English refusal that reaches a member through `ex.Message` as debt to record in the section's `Docs/debt.yml` ([[debt-ledger-additions]]). Do not fix it by translating the exception.
- In review, reject `throw new …(localizer[...])`, an exception type exposing a resx `Key`/`ErrorKey`/`Args` for a localizer, `localizer[ex.Key, ex.Args]`, `throw new …(ErrorMessage("<ResxKey>"))`, `localizer[ex.Message]`, and any `ex.Message switch { "<English text>" => localizer[...] }`.
- Existing code doing any of these is debt, not precedent (EXP-10, CAL-8, RIDE-2, WORKGROUPS-3). A member-facing refusal belongs in a result type the controller localizes, not an exception.
