# Design direction

How a Humans page should look and read. Read it before writing or changing a view. The tokens are in `src/Humans.Web/wwwroot/css/tokens.css`, the components in `src/Humans.Base`, live examples at `/Debug/WidgetGallery`. Where this file and those disagree, the code is right and this file needs a fix.

## The look

A Renaissance workshop ledger: parchment, aged ink, a little gold. Calm, warm, bookish. Serif display type (Cormorant Garamond) for titles and big numbers, a plain sans (Source Sans 3) for everything you read or click. Colour is rare, so it means something: gold marks the one main action, sage / amber / rust mark state.

Not this: Bootstrap blue, pink `code`, grey cards, a page of equal boxes, a dashboard of everything.

## Who is looking

- **A member on a phone**, once, in a hurry (buying a ticket, signing up for a shift). One job per page. They read top to bottom and stop at the first button.
- **An admin on a laptop**, often (sales, finance, rosters). They scan for the number or the row that is wrong.

Decide which one the page is for before laying it out. A page for both is two pages.

## Every page answers three questions, in this order

1. **Where am I?** One `<page-header>`: title, optional subtitle, status badge beside it, actions on the right.
2. **What matters here?** The one number, state or choice the page exists for, as the most prominent thing under the title. Totals and counts are `_StatTile`s in a `.stats` grid, never a sentence of figures joined by dots.
3. **What do I do next?** One primary button (`btn-primary`) per screen. Everything else is `btn-outline-*` or a link. On a member form the primary button is large and is the last thing on the page.

Then detail, most used first. Reference text, explanations and rarely used actions go last or behind a `<details>`.

## Layout

- **Group by meaning.** A card is one topic with one heading. Keep the count low and rank them: one card is primary, the rest recede. Many blocks of equal weight mean the page needs splitting or ranking.
- **Reading width.** Forms and prose do not span the page: `col-lg-8`, or `col-lg-6` for a short form. An input is as wide as what goes in it: a name is not a full-width field. Tables and dashboards may use the full width.
- **Member forms**: the summary of what you are getting and what it costs sits beside the form on desktop (`col-lg-4`, sticky) and directly above the pay button on a phone. The total is display type (`.display`, or the `.stat .value` scale), not a bold line in a paragraph.
- **Admin pages**: stat tiles, then one `_Table` per question, each with its empty message (`.Empty("...")` on the builder). A hand-written table never shows a header row over nothing. Row actions are a button group or a menu, not a run of text links.
- **Spacing** comes from Bootstrap's scale (`mb-3` inside a group, `mb-4` between groups). No inline `style=`, except a component's own custom property (`style="--stats-cols: 3"` on a `.stats` grid).
- **Phone first for member pages**: at 390px nothing scrolls sideways, tap targets are at least 40px (the house `.btn` height on phones), and the primary button is reachable without hunting. Admin-shell pages are desktop only.

## Use what exists

| Need | Use | Not |
| --- | --- | --- |
| Page title and actions | `<page-header>` | a bare `<h1>`/`<h2>` and a button row |
| A state (order, sale, sync) | `<status-badge value="@enum" />` with an `EnumBadgeMap` row from the section | `<span class="badge bg-success">`, raw `@Model.Status` |
| A key number | `<partial name="_StatTile" />` in `<div class="stats">` | a hand-built card, a dot-joined sentence |
| A list of rows | `_Table` via `TableModel.For(...)`, `_Pager` | a hand-written `<table>` |
| Nothing to show | `<empty-state>` for a block; `.Empty("...")` for a `_Table` (never `<empty-state>` inside a table) | "None." in a paragraph |
| A person | `<vc:human>` | a name string |
| Money | `amount.ToEuro()` | a local `Eur()` helper, `ToString("C")` |
| A date or time | `DateFormattingExtensions` | an inline format string |
| An icon | Font Awesome 6 (`fa-solid`) | Bootstrap Icons |
| A colour, radius, shadow | a `--h-*` token | a hex value |
| A technical value (key, URL, id) | `<code>` styled from tokens | Bootstrap's default pink |

Plain Bootstrap forms, grid and cards need no component. A page needs a reusable widget this table lacks: one section uses it, it is a partial or view component in that section; several sections share it, it goes in Base with Peter's approval for the new surface. Either way it gets a gallery card ([`widget-gallery-up-to-date`](../../memory/process/widget-gallery-up-to-date.md)) and, when shared, a row here. The same hand-built block pasted into a second view is debt.

## Words

Short labels, sentence case, no jargon a first-time buyer would not know. Say a thing once: "Supporter ticket, 400 €", not the amount in the label and again in the hint. An error says what to do next. Member-facing strings are localized in every culture; admin pages are exempt.

## Check your work by looking

Reading the markup does not tell you how a page looks. Before calling a view done, open it at 1440px and 390px (the `look-check` skill; admin-shell pages at 1440px only), compare it with the rubric in `tools/look-audit/rubric.md`, and fix what you see. Aim for 4; below 3, try again while there is time. Scores advise, they never block a merge. Serious accessibility findings (axe) on what you changed do get fixed.
