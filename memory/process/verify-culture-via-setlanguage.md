---
name: Check a non-English culture on a preview deploy via POST /Language/SetLanguage
description: Verifying a translation on a preview deploy while signed in — drive POST /Language/SetLanguage with the page's __RequestVerificationToken. Accept-Language and a hand-set .AspNetCore.Culture cookie do NOT work while signed in and make every culture render English. Triggers when checking es/de/it/fr/ca output on a deployed instance.
---

To see a page in a non-English culture on a preview deploy **while signed in**, drive the app's own switcher:

1. `GET` the page and scrape its `__RequestVerificationToken` hidden input.
2. `POST /Language/SetLanguage` with that token and the culture code. It is `[HttpPost]` and `[ValidateAntiForgeryToken]`, so both are required.
3. Re-`GET` the page with the returned cookie jar.

**`curl -L` on step 2 yields a 405** after following the 302 — the redirect target is a GET-only route. That is not a failure: the culture cookie is already set by then. Drop the `-L` and reuse the jar.

**`Accept-Language` does not work, and neither does hand-setting `.AspNetCore.Culture`.** `Program.cs` registers an initial culture provider that returns the signed-in user's stored `PreferredLanguage`, and it outranks both. The symptom is that every culture renders English and a correct translation change looks broken — which is worse than no check at all, because it sends you back to fix code that was already right.

Signed out, `Accept-Language` behaves normally; the trap is specific to an authenticated session.
