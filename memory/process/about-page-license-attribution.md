---
name: Update About page after NuGet package updates
description: After updating any NuGet package, add the new version + license to `Views/About/Index.cshtml`. Tracked as monthly maintenance tied to the NuGet update cycle.
---

The About page (`Views/About/Index.cshtml`) lists all production NuGet packages and frontend CDN dependencies with versions and licenses.

**Rule:** After any NuGet package update, add the new package versions to the About page.

**Why:** AGPL-3.0 license attribution requires accurate dependency listing. Stale About-page versions create a compliance gap — and updating it as part of the NuGet PR is much cheaper than a separate audit later.

**How to apply:**

- After bumping NuGet versions, edit `src/Humans.Web/Views/About/Index.cshtml` to reflect the new versions and any added/removed packages.
- License field: copy from the package's NuGet metadata or repository.
- Frontend CDN deps (Bootstrap, Font Awesome, etc.) live in the same view — update those when their CDN URLs change too.
- Tracked as a monthly maintenance task in `docs/architecture/maintenance-log.md`.
