---
name: section-map
description: "Draw one Humans section as a C4 component diagram plus a short developer guide: which screens use which services, how the services work together, what tables they own, and what they reach in other sections and outside the app. Read from origin/main, one self-contained HTML page per section. Use for 'map <section>', 'section map', or refreshing a section's map after it changed."
argument-hint: "<Section>"
---

# Section map

One page per section, read by a developer who is about to change that section and wants to picture
how it works today: where data flows, what connects to what, so they can hold the change they want
next to what is there. Second reader: Peter, checking where the code drifted from the architecture.

Every name and number on the page comes from code at `origin/main`; name the commit in the lede.
Read with `git show origin/main:<path>` or fast-forward first. If a mechanism is unclear, write
"unverified" on the page rather than guess. Check open PRs that touch the section before listing a
finding; a finding an open PR already fixes is "pending in #N", not open.

## Read first

From `src/Sections/Humans.<Section>` and its `.Contracts` project:

- `Controllers/*`: every action (route, verb, policy), what it renders or redirects to, and which
  service methods it calls. Count GET and POST.
- `Views/**`: line count, `@model`, forms (`asp-action`), links (`asp-controller`), partials.
- `Services/*`, `Jobs/*`: every class that holds logic, its line count, the interfaces it implements,
  its constructor dependencies, anything it resolves from `IServiceProvider` or builds with `new`.
- `Contracts/*`: every public interface, method count, and which sections call it (grep `src/`).
- `Data/*`: repository method count, `ToTable` names, any column holding another system's id.
- `Section.cs`: registrations, policies, jobs and cron, nav, to-do and chrome contributions.
- `Docs/<Section>.md`: the invariants the section claims.

## The page, top to bottom

1. **Header.** Title "Humans.<Section>, the map". A lede: what the section is for in one sentence,
   who reads the page, the commit. A legend: the audience icons used, blue for public interface,
   orange for a broken rule, solid arrows out and dashed arrows in.

2. **The map** (inline SVG, a C4 component diagram). Layers run top to bottom:
   - **Screens**: one card per page or page group, with an audience icon (member, coordinator,
     admin roles, Board), a short name and the route. Audience is an icon, never a lane.
   - **Controllers**: one box per controller, spanning the screens it serves, with line count and
     GET/POST counts and the policy that gates it.
   - **Services**: every class holding business logic, jobs included (a job is a service with a
     clock icon and its cron). Static helpers, option providers, mappers and CSV writers are not
     services: mention them in a box's text or the tables, never as boxes. Edges between services
     show how they call each other, including cycles.
   - **Data**: the repository, with its method count and its tables' real names in the box. No
     separate table boxes.
   - **Plugs into the app**: one dashed blue box listing what the section hands the shell and other
     sections at startup: `Section.cs` seams (home to-do entries, nav and admin tiles, roles,
     policies, chrome and gallery components, view components other pages embed, jobs, audit
     action names, notification sources) plus GDPR export, erasure and account merge.
   - The section sits inside a dashed boundary. Its **public interfaces** (Contracts only) sit at the
     boundary's right edge, outlined blue, with the internal class that implements them pointing at
     them. Nothing internal is blue.
   - **Neighbours**: outside the boundary on the right, one slim box per section reached, each saying
     what this section reads or writes there and, after "←", what that section reads back. Group
     minor neighbours in one box. Outside systems (Google Workspace, SMTP, Holded, Stripe) appear as
     "→" lines inside the neighbour that owns the client, or as their own box when this section calls
     them directly. A "Calling in" box at the top names who uses each public interface.
   - Edges are labelled with the calls they carry. Orange marks only a broken rule: a controller
     writing to another section, a service calling an outside API another section owns, another
     section's table touched. Reads of other sections' contracts are plain.
   - Keep the boundary wide and the neighbour column narrow; the section is the subject.

3. **How data moves.** Three to five short walkthroughs of the paths a developer most needs, each a
   paragraph: screen → action → service method → rows written → side effects (queues, emails,
   audit, other sections). These carry the order the diagram cannot show. Draw a state figure only
   when transitions leave from more than one state; a plain status enum is not worth one.

4. **Every view, every action** (table): view, lines, audience, rendered by, forms post to, links to,
   reads. Then one line: total actions as N GET + M POST, and actions nothing calls.

5. **Every service** (table): class, lines, implements (public or internal), depends on, called by.
   Helpers go in one row, marked "static, not services".

6. **Where the implementation drifted**: numbered, last. Each item is a bold phrase, then two or
   three sentences of evidence (file, line, count) and the shape of the fix. Look for: one service
   holding the whole section; dependencies pulled from `IServiceProvider`; a controller doing a
   service's work; another section's rule living in the wrong section; dead actions or methods;
   public surface with one outside caller; legacy columns left unmarked; drift from the invariants
   doc. No style nits. A public interface whose only caller is the section's own Hangfire job is a
   HUM0034 consequence, not the section's fault.

7. **Verified against code**: four to six bullets of the counts the page rests on.

## Page rules

- One self-contained HTML file per section, same CSS and SVG conventions every time so the pages
  read as a set. Light and dark themes through CSS tokens. The page body never scrolls sideways;
  the map and tables scroll inside their own container.
- Look at the rendered page (desktop and phone, light and dark) before publishing: no text past its
  box, no label on a line, no edge through a box it does not touch.
- Say nothing the code does not show.
- Output: `src/Sections/Humans.<Section>/Docs/map.html`, committed with the section.
