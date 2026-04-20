# Camps

## What this section is for

Camps (also called "[barrios](Glossary.md#barrio)") are self-organizing themed communities that register each year to participate in the event. Each camp has a unique URL slug, one or more leads, optional images, and a per-year **[Camp Season](Glossary.md#camp-season)** capturing that year's name, description, vibes, space needs, and placement details.

Camp Admins control which year is shown publicly and which seasons are open for new registrations or opt-ins. The directory is public; registering or leading a camp requires an account.

![TODO: screenshot — Camps directory page showing camp list and filters]

## Key pages at a glance

- **Camps directory** (`/Camps`) — public listing of all camps for the current year.
- **Camp detail** (`/Camps/{slug}`) — public detail page for a single camp with description, images, and current season.
- **Camp detail for a specific year** (`/Camps/{slug}/Season/{year}`) — public detail for a camp's past season.
- **Register a new camp** (`/Camps/Register`) — authenticated humans register a new camp when a season is open.
- **Edit a camp** (`/Camps/{slug}/Edit`) — camp leads, Camp Admin, and Admin edit camp identity and season data.
- **Camps admin dashboard** (`/Camps/Admin`) — Camp Admin and Admin review pending seasons, manage registration windows, and export data.

A public JSON API at `/api/camps/{year}` and `/api/camps/{year}/placement` is available for integrating listings into other sites.

## As a [Volunteer](Glossary.md#volunteer)

Most of what the directory offers is open to anyone, signed in or not:

- **Browse the directory** at [/Camps](/Camps). Cards show each camp's name, short blurb, image, vibes, and status badges. Filter by vibe, [sound zone](Glossary.md#sound-zone), kids-friendliness, and whether the camp is accepting humans.
- **View a camp's detail page** at `/Camps/{slug}` for the long description, images, links, current season info, and leads by display name. Previous names appear unless the camp has chosen to hide them.
- **Contact a camp** via the "Contact this camp" button on the detail page. The camp's email is never exposed publicly — the button opens a facilitated form. Signing in is required so the camp knows who reached out.
- **Register a new camp** at [/Camps/Register](/Camps/Register) when a season is open. You'll fill in the camp's identity (name, contact info, times at the event, Swiss camp flag) and season-specific details (blurb, languages, vibes, kids policy, sound zone, performance space, space requirement). On submit you become the [Primary Lead](Glossary.md#primary-lead) and the season is created in **Pending** status, waiting on Camp Admin approval.
- **View previous seasons** at `/Camps/{slug}/Season/{year}` to see how a camp described itself in earlier years.

There is no "join a camp" action in the app — camp rosters and who is physically camping where are managed by each camp directly.

### Step-by-step: register your camp (2026)

Any authenticated human can register a camp once a registration season is open. In practice, most camp leads are also [Colaboradores](Glossary.md#colaborador) — see [Governance](Governance.md) — but it's not a system requirement. If you're brand new, complete the regular onboarding first ([Onboarding](Onboarding.md)).

1. Sign in to [humans.nobodies.team](https://humans.nobodies.team).
2. Go to **Camps** in the navigation.
3. Click **Register your camp**.
4. Fill in the camp profile: community info (public — name, blurb, vibes, sound zone, languages, kids policy), placement info (internal — preferred zone, footprint, infrastructure needs), and co-leads.
5. Set status to **Active** and confirm whether you're accepting humans.
6. Submit. A Camp Admin reviews and approves; once approved your camp appears in the public listing and on the Elsewhere website.

After registration, Barrio Support (Ellen — [ellen@nobodies.team](mailto:ellen@nobodies.team)) typically adds you to the **Camp Leads** team in Humans so you receive coordinator comms — water schedules, LNT reminders, power obligations. If those aren't reaching you, ask Ellen.

Your camp profile is **persistent year to year** — next year you only update it, you don't start from scratch. Set the status each year you're attending; a camp that hasn't declared a status for the current year doesn't appear in the active listing.

### How your camp data feeds other systems

| System | What it gets |
|---|---|
| Public Elsewhere website | Listing data is pulled from Humans. Updates to your status, blurb, or images flow through. |
| City planning / placement | Placement data feeds the city planning tool. Internal only — not published. |
| Barrio Support comms | Barrio Support uses Humans to send communications to camp leads — water schedules, LNT reminders, power obligations. Being registered is how you receive these. |
| Barrio store (rolling out) | Barrio services — water, ice, tokens — are becoming orderable through a store inside Humans. Details to follow from Production & Logistics. |

## As a [Coordinator](Glossary.md#coordinator) (Camp Coordinator)

If you are a **Camp Lead** (Primary or Co-Lead), you can manage your specific camp. You cannot edit camps you don't lead.

- **Edit your camp** at `/Camps/{slug}/Edit`. Update contact info, links, the current season's data (blurb, vibes, kids policy, space and sound needs, performance info), and camp-level fields like times at the event and the Swiss camp flag. Toggle **Hide historical names** to suppress the "Also known as" section on the public page.
- **Manage names.** If a Camp Admin has set a name lock date for the year, name changes are blocked after that date. Any rename is automatically recorded as a historical name.
- **Manage co-leads** from the Edit page: add a co-lead, remove a lead, or transfer the Primary role.
- **Upload, delete, and reorder images** from the Edit page. Images appear on the directory card and detail page in the order you set.
- **Opt into a new season** when Camp Admins open one. This creates a fresh Pending season carrying your camp's identity forward, but requires you to review and update the season-specific fields before approval.
- **Withdraw a season** if plans change, or **mark a season Full** when you're no longer accepting humans. Reactivating Withdrawn or Full back to Active is a Camp Admin action.

## As a Board member / Admin (Camp Admin)

**Camp Admin** is the domain admin for this section; **[Admin](Glossary.md#admin)** is a superset that can also delete a camp outright. Admin tools live under [/Camps/Admin](/Camps/Admin).

- **Review season registrations.** Pending seasons are listed on the dashboard. **Approve** moves a season to Active; **Reject** requires notes explaining why and records your user id and timestamp. Withdrawn and rejected seasons are visible to Camp Admin for history.
- **Reactivate seasons** that were marked Full or Withdrawn, moving them back to Active.
- **Open and close registration seasons** for any year. Opening a year adds it to the list accepting new registrations and opt-ins; closing removes it.
- **Set the public year** that controls which year is shown on `/Camps` and on the JSON API.
- **Set name lock dates** per year, after which camp name changes are no longer allowed for that year's season.
- **Edit any camp** (all the lead-level Edit actions above, on any camp).
- **Export camps as CSV** from the dashboard. The export covers every camp for the current public year and includes name, slug, status, contact info, leads, and placement-relevant season data. The file is named `barrios-{year}.csv`.
- **Delete a camp** (Admin only — not Camp Admin). This permanently removes the camp and all of its seasons, leads, images, and historical names. Confirmation is required.

## Key contacts (2026)

| For | Who |
|---|---|
| Barrio Support (pre-event) | Ellen — [ellen@nobodies.team](mailto:ellen@nobodies.team) or [barrios@nobodies.team](mailto:barrios@nobodies.team) |
| City Planning / Placement | Melo — via Discord or the city planning channel |
| Water, LNT, power obligations | Barrio Support — comms go out via Humans to camp leads |
| Barrio store / purchasing | Production & Logistics — [daniela@nobodies.team](mailto:daniela@nobodies.team) |
| App issues | [humans@nobodies.team](mailto:humans@nobodies.team) |
| General questions | [#🎪-barrios](https://discord.gg/rBZxDv8z) on Discord |

## Related sections

- [Profiles](Profiles.md) — camp leads are linked to human accounts; a valid profile is required to be a lead.
- [Governance](Governance.md) — Colaborador application is the prerequisite for becoming a camp lead.
- [City Planning](CityPlanning.md) — what happens to the placement data your camp profile feeds in.
- [Glossary](Glossary.md) — definitions for "barrio", "season", "Primary Lead", "sound zone", and other camp terms.
