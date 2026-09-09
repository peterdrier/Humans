# Tour — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

One public web page — "What is Humans" — that anyone can read without logging in. It is
the URL you hand another burn organisation. The visitor gets a long-form landing page: a
photo hero with a short pitch and headline numbers, plain-language capability groups
(people, organising, money, governance, communication, the gate), photo interludes, a
"take the parts your burn needs" closing with the engineering credentials, and a contact /
source-code call to action. The page always offers the way back into the app (the fixed
header bar) and a link onward to the engineering story.
It is written in English only: its audience is outsiders evaluating the platform, not
members.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Show me what Humans is" | anyone, signed in or not | `GET /Tour` → `TourController.Index` → `Views/Tour/Index.cshtml` under `_TourLayout` |
| "Where do I find the Tour?" (anonymous) | Shell's top nav, via `<vc:section-nav>` | `SectionNav` — the `ISectionNav` item "Tour", visible only while not authenticated |

Inbound links the section does not own: Shell's dashboard tile
(`src/Humans.Web/Views/Home/Dashboard.cshtml`, `Controller = "Tour"`) for signed-in
members, and Onboarding's Welcome page (`href="/Tour"`) for anonymous arrivals. Outbound:
`/` (header brand and CTA), `/About`, a mailto, the GitHub repo, Google Fonts.

No contract methods, no jobs, no events, no config keys, no tables.

## 3. Structure

The shapes imply exactly this, and it is today's layout:

- **`TourController.Index` and nothing else**, no injected services, `[AllowAnonymous]` at
  class level.
- **`Views/Tour/Index.cshtml` under the section's own `_TourLayout`** — the page steps
  outside Shell's chrome on purpose — with a `_ViewStart` binding them.
- **`_ViewImports`** carrying the MVC tag helpers and Humans.Base's (see weirdness).
- **Static assets** under `wwwroot/` (the stylesheet, the script, the photos), served as
  RCL static web assets at `/_content/Humans.Tour/`.
- **`Section.cs`** (empty `Register`) and **`SectionNav.cs`** at the root; nothing else.
- **No Contracts leaf and no test project of its own.** Routing, rendering and the
  `ISection` are pinned by the render test in `Humans.Integration.Tests`, and the nav
  contribution by that test's anonymous `/About` assertion (a Shell page that links Tour
  through `<vc:section-nav>` alone) — all local-only by design. What reaches Tour in CI is
  `EndpointAuthorizationTests`, and it pins only that every action carries an authorization
  attribute — not which. Delete `SectionNav.cs` or `Section.cs` and CI stays green.
- Project references: `Humans.Base` and nothing else.

## 4. Invariants

- `/Tour` answers an anonymous request with 200 — never a login redirect.
- The rendered page carries no member data and the controller resolves no service; the
  hero numbers are typed copy, not queries.
- Every Tour page renders the fixed header bar with a link to `/`.
- Copy names the event **Elsewhere**, never "Nowhere".
- Copy is hardcoded English; the section has no resource set and no localizer binding.
- With JavaScript off, blocked, or reduced motion requested, every block is visible: the
  hidden state is gated on the `tour-js` class the layout's inline script sets.
- The anonymous top-nav link disappears once signed in; the member-facing entry is the
  dashboard tile.

## 5. Seams

- **Per-burn configuration** (spec `docs/superpowers/specs/2026-08-12-burn-demo-pages-design.md`):
  the closing section describes it as the roadmap. Nothing is built; the copy is the only
  artifact, and it says so honestly.

## 6. Deliberately not done

- **No localization / resx.** External audience (spec non-goal). i18n sweeps must skip it.
- **No live stats.** The hero numbers are marketing copy; wiring a cross-section read for
  a brochure would cost a lane for nothing.
- **No section test project.** "Renders anonymously" needs the composed host, so the pin
  is an integration test; anything else here would be an absence assertion
  (`memory/architecture/no-tests-for-absences.md`).
- **No signed-in nav slot.** Peter, 2026-08-13: the signed-in top nav is too busy; the
  dashboard tile is the member entry.
- **The dashboard tile is still Shell's, by string.** The `ISectionMemberDashboard` seam
  exists (`<vc:chrome-slot name="member-dashboard">`), but it renders a section's own row
  above Shell's "Your stuff" grid, and the Tour tile is a compact card inside that grid.
  Taking the seam means Tour ships a view component and the tile leaves the grid — Peter's
  call (run 1, finding 14), not this section's.
- **No shared layout with Shell.** The promo look is the point of the page.

## Load-bearing weirdness

- **`@addTagHelper *, Humans.Base` in `_ViewImports.cshtml` looks unused** — the views
  name no Base tag helper — but it is what applies `NonceTagHelper` to every `<script>`,
  including the one-line inline `tour-js` script in `_TourLayout`. Without it the app's
  nonce CSP blocks that script, the hidden state never applies, and the fade-ups silently
  die. A section RCL does not inherit Shell's `_ViewImports`.
- **`Section.Register` is empty on purpose**, and the `ISection` type is not decorative:
  it is what makes the assembly a section to `SectionControllerFeatureProvider` (which
  routes the internal controller), the discovered-sections log and the analyzers. Delete
  it and `/Tour` 404s with a green build.
- **Weight 1000 on the nav item** sorts it last among contributed links, beside Legal.
- **Inline `style="background-image: …"`** on the hero's first slide and the interludes is
  deliberate: styles are `'unsafe-inline'` under the CSP, and the script lazy-loads the
  other slides from `data-bg`.
- **Google Fonts** (Inter, Permanent Marker) is the one external stylesheet outside the
  CDN libraries; the CSP already allows it.
- **The photos are the section's heaviest files** and are the design, not sediment.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First pass: routes into the page documented, comment sediment cut, two local pins | peterdrier/Humans#1610 |
