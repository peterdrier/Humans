---
name: Breadcrumbs — pages supply crumbs via @section Crumbs
description: A member-layout page gives its breadcrumb as `@section Crumbs { <a>…</a> <span>current</span> }` — never hand-rolled `<nav><ol class="breadcrumb">` markup; admin-shell pages have none.
---

The member layout renders the page's `Crumbs` section as the Bootstrap breadcrumb (`<vc:breadcrumb>`, one shared `Common_Breadcrumb` aria-label). The admin shell has no breadcrumb: its top bar names the section and its tabs name the page, and it ignores the section.

**Rule:** a page writes items only — `<a>` for each link, a bare `<span>` for text, the current page last. The layout supplies the wrapper, the `<li>`s, `active` and `aria-current`.

**Why:** ~90 views hand-rolled the nav with ten different aria-label keys; the admin shell had to hide them with CSS. One mechanism means one markup, one translated label, and a page that works in either layout.

**How to apply:**
- A partial or layout can't define a section for its host page — put the `@section Crumbs` in the host view.
- A page only the admin shell renders writes no crumbs. A subpage that needs a link back to its parent record puts it in its `<page-header-subtitle>`; `ViewData["AdminNavParent"]` picks which tab lights up.
- A page shared by both layouts can carry its full member trail; the admin shell ignores it.

Spec: `docs/sections/admin-shell.md`.
