---
name: One URL per page; a moved page takes its new owner's URL
description: Adding a route, or moving a page or action to another section: one canonical URL under the owning section, no aliases, no redirects. Only Barrios↔Camps is an alias.
---

Every page has exactly one URL, under its owning section: `/<Section>/...`, admin pages `/<Section>/Admin/...`.

- **No aliases.** No singular/plural pairs (`/Camp/Admin` beside `/Camps/Admin`), no second route prefix, no new controller just to vary a URL. The single sanctioned alias is `Barrios` ↔ `Camps`, on every Camps controller. Peter: "NEVER ANY FUCKING ALIASES (except barrios for camps)."
- **A move is a move.** A page or action that changes section takes the new owner's route. Don't keep the old prefix and don't add a redirect. Peter, 2026-10-21: "DO NOT keep urls. a move is a move. bookmarks are not a thing we worry about anymore." This includes URLs in already-sent emails and `List-Unsubscribe` headers: update the code that mints them and report any external system that must change.

**How to apply:**

- New pages go on the section's existing controller and route convention.
- When moving, update every link, test, allowlist and doc naming the old path, and delete any legacy redirect. `asp-controller`/`Url.Action` links follow by name; literal paths in docs, the guide, JS, e2e helpers and agent prompts need hand edits.
- If a brief says "the URL must change to X" and X isn't the section's convention, check with Peter first.
- This is not a licence to churn routes that never moved in unrelated work.

**Related:** [`no-admin-url-section`](../architecture/no-admin-url-section.md)
