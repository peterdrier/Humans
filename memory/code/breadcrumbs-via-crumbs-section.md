---
name: Breadcrumbs — pages supply crumbs via @section Crumbs
description: Every page gives its breadcrumb as `@section Crumbs { <a>…</a> <span>current</span> }` — never hand-rolled `<nav><ol class="breadcrumb">` markup.
---

Both layouts render the page's `Crumbs` section: `_Layout` as the Bootstrap breadcrumb (`<vc:breadcrumb>`, one shared `Common_Breadcrumb` aria-label), `_AdminLayout` through `<vc:admin-breadcrumb>` after the nav's Group / Page.

**Rule:** a page writes items only — `<a>` for each link, a bare `<span>` for text, the current page last. The layout supplies the wrapper, the `<li>`s, `active` and `aria-current`.

**Why:** ~90 views hand-rolled the nav with ten different aria-label keys; the admin shell had to hide them with CSS. One mechanism means one markup, one translated label, and a page that works in either layout.

**How to apply:**
- A partial or layout can't define a section for its host page — put the `@section Crumbs` in the host view.
- A page in the admin shell (controller in the admin nav and gated beyond `AppAccess`) adds crumbs only for what the shell can't derive: nothing when the page is a nav item or its `Title` is the last crumb, else the crumbs after Page (or `ViewData["AdminNavParent"]` to pick the parent item). Never repeat the group or Page.
- Crumbs are ignored by the admin shell on a page that is itself a nav item, so a page shared by both layouts can carry its full member trail.

Spec: `docs/sections/admin-shell.md`.
