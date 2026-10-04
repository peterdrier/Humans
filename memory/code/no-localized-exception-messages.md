---
name: Never localize exception messages
description: Exception messages are never localized — no resx text in `throw new …(…)`, no mapping `ex.Message` to resx keys (switch on text or `localizer[ex.Message]`).
---

Never localize exception messages. Exception text is developer-facing English, logged as-is. Do not build an exception message from a resource. Do not translate a caught exception's `Message` for display, either by switching on its English text or by using it as a localizer key.

**Why:** Peter, 2026-10-04, on peterdrier/Humans#1899. The nightly debt sweep localized member email refusals by string-matching `ex.Message` in `ProfileEmailsController`. That coupled the service wording to the controller, so any reword silently falls back to English. Peter reverted it outright ("we do not localize exception messages") and closed the follow-up, peterdrier/Humans#1900, as not planned.

**How to apply:**
- Treat a hardcoded-English refusal that reaches a member through `ex.Message` as debt to record in the section's `Docs/debt.yml` ([[debt-ledger-additions]]). Do not fix it by translating the exception.
- In review, reject `throw new …(localizer[...])`, `throw new …(ErrorMessage("<ResxKey>"))`, `localizer[ex.Message]`, and any `ex.Message switch { "<English text>" => localizer[...] }`.
- Existing code doing any of these is debt, not precedent.
