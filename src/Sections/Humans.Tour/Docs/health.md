# Tour — Target Shape

## 1. What the section does

One public page that answers "what is Humans, and could it run my burn?" for someone who
has never signed in — a Board member from another burn, a regional org still on
spreadsheets, a curious volunteer handed the link. It reads like a promo site rather than a
membership tool: a photo hero with a one-paragraph pitch, six plain-language chapters
(people, organize, money, govern, communicate, at the gate) each with a short feature list,
two full-bleed photo interludes, an honest paragraph about modularity and the per-burn
roadmap, an engineering-credibility strip, and a closing invitation to write to the team or
read the source.

Every visitor sees the same page. It shows no member data, asks nothing, stores nothing,
and calls nothing. The header bar is the way back into Humans; the pitch links onward to
the engineering story at `/About`, to the GitHub repository, and to the team's mailbox.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "What is Humans, and is it for my burn?" | an outsider evaluating the platform | `GET /Tour` — one page, no parameters, anonymous |

One question, one page. Everything else in the section exists to get people to it or back
out of it:

| Direction | Where | Owner |
|---|---|---|
| in — signed-out top nav | `SectionNav`, visible only while anonymous | Tour |
| in — Welcome landing page | `/Welcome` body link, by URL | Onboarding |
| in — member dashboard | `/` action card, by controller name | Shell |
| out — back into Humans | fixed header bar, brand and "Open Humans" both `/` | Tour |
| out — the engineering story | `/About` (hero button, closing paragraph) | Shell |
| out — the team | `mailto:humans@nobodies.team`, the GitHub repository | — |

Vocabulary: none of its own. The copy speaks the product's language (Volunteers, teams,
shifts, tickets, Board) and names the event **Elsewhere**.

## 3. Structure

The shape implies almost nothing: one anonymous controller with one action that returns a
view; that view; the section's own layout, because a promo page has no use for the
member chrome; one stylesheet and one script for the page's motion; the photographs; a
nav contribution; and an empty `Section` whose only job is to make the assembly a section
for discovery. No service, no view model, no repository, no resource set, no contracts
leaf.

That is what is built. The one thing the target would place differently is the member
dashboard tile: it is Tour's entry point but lives in Shell, named by string, which is
exactly the reference the section-activation scan cannot see and the shape the nav link
already left behind. The `ISectionMemberDashboard` seam exists for it (§5).

## 4. Invariants

- `/Tour` renders for an anonymous visitor: `TourController` carries `[AllowAnonymous]` at
  class scope and nothing narrows it.
- The page is static: the action takes no parameters, the controller injects nothing, the
  view reads no model. Every number in the copy is marketing text, not a query.
- The top-nav link is offered only while signed out; a signed-in member reaches the page
  from the dashboard tile, never from the nav.
- Every Tour page carries the fixed header bar with a link back to `/`.
- The copy is English only and stays that way; the section carries no resx and binds no
  localizer.
- The event is named **Elsewhere**, never "Nowhere".
- The page degrades to fully visible content when JavaScript is off or blocked: the
  hidden state for scroll animations is applied only under the `tour-js` class the
  layout's inline script sets.

## 5. Seams

- **Dashboard tile behind the seam.** Shell's `Dashboard.cshtml` builds the Tour action
  card by name. Contributing it from Tour through `ISectionMemberDashboard` would let Shell
  stop naming the section, but the contributed slot sits above "Your stuff", so the tile
  would leave that grid — a placement choice, not a mechanical move.
- **Welcome link.** Onboarding's `/Welcome` links `/Tour` by URL, invisible to the
  activation scan for the same reason. Onboarding's call.
- **Per-burn configuration.** The page describes it as the roadmap. Nothing in this section
  builds it or should.

## 6. Deliberately not done

- **No localization.** The audience is external and English-speaking; the spec says so and
  an i18n sweep must not wire it through a localizer.
- **No service or view model.** A static page has no rule to enforce once.
- **No live statistics.** A public page must not query member data; the hero's numbers are
  copy.
- **Not on the Shell layout.** The member chrome (navbar, container, login partial) is
  what the page steps out of on purpose; the fixed header is its whole chrome.
- **No Contracts leaf.** Nothing outside the section names a Tour type.

## Load-bearing weirdness

- **`Views/_ViewImports.cshtml` is not inherited from Shell.** Both `@addTagHelper` lines
  are required; without `Humans.Base`'s, the layout's inline script loses the CSP nonce
  `NonceTagHelper` stamps on every `<script>`, the browser blocks it, and the page silently
  stops animating while staying visible.
- **The section ships its own layout**, and `Views/Tour/_ViewStart.cshtml` names it. The
  page never resolves Shell's `_Layout`.
- **Hardcoded English is a decision, not an omission.** The Welcome link and the dashboard
  tile that lead here are deliberately unlocalized too.
- **`TourController` is `internal sealed`** and still routes: the section's `ISection`
  entry point is what makes the assembly a section, and section controllers are discovered
  as internals.
- **The nav item's weight of 1000** sorts it last among contributed links, beside Legal.
- **Google Fonts are loaded from the CDN**, as the Shell, Admin and Gate layouts do; the
  Shell's Content-Security-Policy allows the two hosts.
- **Hero slides two and three lazy-load** via `data-bg`; only the first carries an inline
  background so the page paints without the script.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First doctoring: CI-run tests for a section that had none; docs and comments cut to the current state | peterdrier/Humans#1611 |
