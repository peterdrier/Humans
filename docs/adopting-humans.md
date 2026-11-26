# Running Humans for your burn

Hello, and welcome. This guide is for people from other burns who are thinking about running Humans for their own community. It is about a 30-minute read, and by the end you should know what Humans is, what it needs, how to get it running, and which parts you actually need.

## What Humans is

Humans is a membership system. It was built by and for Nobodies Collective, a Spanish nonprofit that runs the burn Elsewhere. It handles the whole life of a volunteer: signing up, filling in a profile, signing the legal documents, joining teams, taking shifts, and (if you want them) tickets, camps, gate scanning, governance and money.

Three things to know before you start:

- **It was built for one organisation.** There is no "organisation name" setting that rebrands everything. Some Nobodies details live in config, some live in code. Plan to fork the repo and adapt it. This guide tells you where the Nobodies-specific bits are.
- **It is licensed AGPL-3.0** (see `LICENSE` at the repo root). You are free to run and change it. If you run a modified version for people over a network, the AGPL requires you to offer those people your source code. A public fork covers that nicely.
- **It is deliberately simple to host.** One server, one database, no cluster. The design idea in `AGENTS.md` is "small scale, simple systems": a burn has a small user base, so Humans loads its data into memory instead of doing clever database work. That choice explains most of the hosting advice below.

Under the hood it is .NET 10 and ASP.NET Core, with PostgreSQL, EF Core for the database, and Hangfire for background jobs.

One repo word you will see everywhere: a **section**. A section is one self-contained slice of the app (Teams, Shifts, Camps and so on) that owns its own database tables, logic and pages. They live under `src/Sections/Humans.<Section>`, and `ls src/Sections` shows them all. Each one has a document describing its rules at `src/Sections/Humans.<Section>/Docs/<Section>.md`. When this guide says "module", it means a section or a small group of them.

## What you need

| Thing | Notes |
|---|---|
| A server | CPU: 2 cores minimum, 4 recommended. RAM: 8 GB minimum, 16 GB recommended |
| PostgreSQL | Version 16 or newer. The included compose file runs `postgres:16` for you |
| A domain name | With HTTPS, usually through a reverse proxy (a web server that sits in front of the app and handles HTTPS for it) |
| Sign-in | Email magic links work on their own. Google sign-in is optional (see below) |
| SMTP email | An outgoing mail account, for sign-in links, notifications and reminders |
| Docker | The easiest way to run the app and the database together |

**Why these numbers?** Humans is multithreaded, so it needs at least 2 cores (on fewer it struggles), and 4 gives it comfortable headroom. Memory is more forgiving than you might expect. Humans keeps a lot of its data cached in memory by design, but for a real burn with a year of data the app uses under 1 GB and PostgreSQL about 300 MB. It could get by on 4 GB. 8 GB is a sensible minimum and 16 GB is recommended, because the server will usually run other things beside Humans, such as a reverse proxy, backups and monitoring.

**What about Google?** Humans offers two ways to sign in: Google, and an email magic link (a one-time sign-in link sent by email). Nobody has to have a Google account; members can use magic links only. Google is not a design requirement, it is just the only provider our burn has needed so far. Leave `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` empty and the Google button simply does not appear. If you do want Google, you create the client in the Google Cloud console, and its redirect address is your site's address followed by `/signin-google`. Adding another sign-in provider is a welcome pull request.

**Why is email needed?** Magic links travel by email, so without working SMTP anyone not using Google cannot sign in, and if you skip Google, nobody can. Treat email as required.

Everything else (maps, ticket vendors, Google Workspace, payments, the AI helper) is optional and covered later.

## Getting it running

Try it on your own machine first. You lose nothing and you learn a lot.

### 1. Fork and clone

Fork the repository so you have a home for your changes, then clone your fork.

### 2. Create your `.env`

Copy `.env.example` to `.env` and fill it in. The ones that matter on day one:

- `POSTGRES_PASSWORD`: required. Compose refuses to start without it.
- `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`: your Google OAuth client, if you want Google sign-in. Leave them empty otherwise.
- `EMAIL_USERNAME` and `EMAIL_PASSWORD`: your SMTP login.

The rest of the file (Google Maps, the GitHub token, the Google service account, the route planner key) can wait.

### 3. Start it

```bash
docker-compose up -d
```

This starts the app on port 5000 and PostgreSQL next to it. Database migrations apply by themselves at startup, so there is no separate "set up the database" step. When there are new migrations to apply, the app first takes a snapshot of the database into `/app/db-snapshots` (a named Docker volume called `db_snapshots`), which is a nice safety net. It does this in Production and Staging, not in Development.

If you would rather run the app from source while developing, start only the database with `docker-compose up -d db` and then run `dotnet run --project src/Humans.Web`. Note that `dotnet run` does not read `.env`. Use .NET user secrets or environment variables in the form `Section__Key` instead. The default connection string expects the database password `humans`.

### 4. Sign in once

Open the site and sign in (with Google or a magic link), then enter your name when it asks. This creates your user record, which the next step needs, and the name makes your account Active. Role-gated pages stay closed to an account that is not Active, even for an Admin.

### 5. Make yourself the first Admin

A fresh install has no Admin, and only an Admin can create one. So the very first Admin is added straight into the database. Open a database prompt:

```bash
docker-compose exec db psql -U humans -d humans
```

Then look up your user id and add the role. Sign-in addresses live in the `user_emails` table, not on `users`:

```sql
SELECT "UserId", "Email" FROM user_emails WHERE lower("Email") = 'you@example.org';

INSERT INTO auth_role_assignments ("Id", "UserId", "RoleName", "ValidFrom", "ValidTo", "Notes", "CreatedAt", "CreatedByUserId")
VALUES (gen_random_uuid(), '<user-id>', 'Admin', now(), NULL, 'Initial admin setup', now(), '<user-id>');
```

Then restart the app with `docker-compose restart app` (or stop and start `dotnet run`). The app keeps role assignments in memory and only refreshes them when it changes them itself, so it cannot see a row added behind its back until it restarts. You do not need to sign out.

This is the only time you should ever edit the database by hand. From here on, every role is handed out in the app, where each change is recorded in the audit trail.

### 6. Check your configuration

Two places tell you whether the install is healthy:

- `/Debug/Configuration` (visible to Board and Admin) lists every setting the running app reads and whether it is set. It is the best sanity check after a first boot.
- `/health/live` tells you whether the app is up. `/health` gives a fuller report that includes outside services such as GitHub and your mail server, and `/health/ready` is the readiness check. Both of those say unhealthy until every setting the app marks as critical is filled in. That includes the Google Maps key and the GitHub token, even though the app runs without them. There are also `/metrics` for Prometheus (a monitoring tool), `/api/version`, and the background job dashboard at `/hangfire` (Admin, and read-only for Board). The health, metrics and version addresses answer without a sign-in, so keep them behind your reverse proxy if you would rather they were private.

One thing that catches people: `docker-compose.yml` only passes some variables through to the app (the database, the Google client, Maps, the email login, the GitHub token and the Google service account). Any other setting needs its own line under `environment:` in the compose file. If `/Debug/Configuration` says a key is not set and you are sure you set it, this is usually why.

### 7. Replace the Nobodies-specific bits

This is the real adaptation work. Start in `src/Humans.Web/appsettings.json`:

- `AllowedHosts` lists Nobodies hostnames. Change it to yours, or the app will not answer on your domain.
- The `Email` block: `SmtpHost` (default `smtp-relay.gmail.com`), `SmtpPort` and `FromName` are yours to set, and `FromAddress`, `AdminAddress`, `DpoAddress` and `BaseUrl` all point at nobodies.team. `BaseUrl` matters most: every link in an email, sign-in links included, is built from it.
- `GitHub.Owner` and `GitHub.Repository` point at the Nobodies legal documents repo.
- `GoogleWorkspace.Domain` and `CustomerId`, `TicketVendor.EventId` and `Stripe.MirrorAccounts` only matter if you use those optional modules.
- The in-app guide at `/Guide` is not in the file at all. It is fetched from `docs/guide` on GitHub while the app runs, and by default from the Nobodies repository. Add `Guide:Owner` and `Guide:Repository` pointing at your fork if you want members to see your own version.

Then the code. Some Nobodies details are still hard-coded, for example `GoogleGroupDomain` in `src/Humans.Base/Constants/DomainConstants.cs`, the defaults in the email settings, the help text in `src/Humans.Base/Models/SectionHelpContent.cs`, and the site description in the page layout. This finds them:

```bash
grep -rni "nobodies" src
```

Expect noise: plenty of hits are only links to the Nobodies issue tracker in comments and section docs, and those can stay. Of the ones that matter, the Google section has more than any other, so if you skip Google Workspace you can leave a good share of them for later.

Compose builds the app image from your checkout. After you change `appsettings.json` or any code, rebuild with `docker-compose up -d --build`, because a plain `up -d` keeps running the old image.

### 8. Set your time zone and create your first event

Go to `/Settings` and open the Events tab.

- Set the event time zone. It is one setting, `Event:TimeZoneId`, and if it is absent the app assumes Europe/Madrid.
- Create your event: name, the date the gate opens (the year comes from it), how the build, event and strike periods sit around that date, and the early entry window.
- Make it Active. Only one event can be active, and many sections hang off it.

Next year, "Create next event" copies the shape of the last one.

### 9. Add your legal documents

Humans reads the documents your members must sign from a GitHub repository. Create your own legal repo, point `GitHub:Owner`, `GitHub:Repository` and `GitHub:Branch` at it, and then set the documents up at `/Legal/Admin/Documents`. More on this in the Consent module below.

### Try the demo world first

When the app runs in Development mode (which is what `dotnet run` gives you), two extras appear. With `DevAuth:Enabled`, which is on by default there, the sign-in page at `/Account/Login` gets buttons to sign in as ready-made personas. One of them is an Admin, so you can skip the SQL step while you explore. And an Admin can build a "demo world" of fake departments, shifts and people from the dev seed page at `/dev/seed`. It is a great way to click around a populated system before you have any real data. Neither is reachable in Production, and dev login must never be turned on for an instance holding real people's data.

## The core modules

These are the ones you need for Humans to work at all. They are listed in the order that makes sense to set up and understand, and each one leads into the next.

### 1. Auth

**What it does.** Signs people in (Google or magic link) and holds role assignments. A role assignment says "this person has this role from this date", with an optional end date. Auth also guards the door: only an account in the Active state gets into the app.

**What you set up.** Email for magic links, optionally a Google OAuth client, and your first Admin, all covered above.

**Leads to.** A sign-in needs a person to attach to, which is Users.

### 2. Users

**What it does.** The person record. Profiles keep a legal name and a burner name separately, plus email addresses, contact details where the member picks who can see each field, and a profile picture. It can also merge two accounts that turn out to be one person.

**What you set up.** Nothing at first. Later, give the `UsersAdmin` role to whoever looks after member records.

**Leads to.** Once people exist, you need to reach them, which is Email.

### 3. Email

**What it does.** Sends all outgoing mail through an outbox that retries on failure. It handles branding, unsubscribe links, and lets members opt out per category of mail.

**What you set up.** Your SMTP host and login, and the sender addresses in the `Email` block.

**Leads to.** With mail working, magic links work, and you are ready to describe your event in Settings.

### 4. Settings

**What it does.** Holds app-wide settings and your events, year by year.

**What you set up.** The time zone and your first active event, as in step 8.

**Leads to.** New members will be asked to sign your documents, so Consent comes next.

### 5. Consent

**What it does.** Keeps your legal documents in sync with your GitHub repo, records each signature permanently, and reminds people to sign again when a document changes.

**What you set up.** Your legal repo and the documents at `/Legal/Admin/Documents` (Board or Admin). Each document points at a folder in the repo. Documents are attached to a team, and anything attached to the Volunteers team applies to everyone. One thing is built into the code here: in each folder the sync treats the plain `name.md` file as the Spanish text, which is the legally binding one, and files like `name-en.md` as translations. If Spanish is not your binding language, plan a code change. A GitHub access token is optional for syncing a public repo, where it only raises the rate limit, though the health report asks for one (see step 6).

**Leads to.** Onboarding, which walks a new person through all of the above.

### 6. Onboarding, and the Volunteer journey

**What it does.** Onboarding is the guided signup. It owns no data of its own: it coordinates Users, Consent, Teams and Shifts.

Here is what a new person goes through:

1. They sign up with Google or a magic link.
2. The onboarding widget (`/OnboardingWidget`) asks for their names: legal name and burner name. Entering a legal name makes the account Active, which lets them into the app.
3. It shows the shifts your active event most needs filled. They can sign up for some or skip.
4. It asks them to sign the required legal documents.

After that they become a **Volunteer**, which is the standard member, and nearly everyone in the system is one. There is no approval queue. The Volunteers team works out its own membership every hour: people who have entered their name, are not suspended or rejected, and have signed the required consents.

There is a role called Consent Admin whose holder can review new people and mark them clear or flagged. That review is a note in the audit trail. It does not hold anyone up. The one exception is an outright reject, which does remove the person.

**What you set up.** Nothing beyond the modules above.

**Leads to.** Volunteers need somewhere to belong, which is Teams.

### 7. Teams

**What it does.** Departments and sub-teams, their members, requests to join, and a page for each team. A few "system teams" (Volunteers, Coordinators, Barrio Leads) are filled automatically and you never edit them by hand.

**What you set up.** Your own department structure: Gate, Power, Kitchen, whatever your burn has. Give `TeamsAdmin` to whoever maintains it.

**Leads to.** Teams need people with named responsibilities, which is Roles.

### 8. Roles and Permissions

**What it does.** A role here is a standing responsibility on a team, such as "Cantina shift planner". It has slots that people sit in, and it can carry permissions. A team can have one **coordinator role**, the role that is in charge of that team. `/Roles` is a board of open roles that members can browse. Roles carry over from one event to the next, so you do not rebuild them each year.

Permissions is the companion. It stores nothing. It simply answers the question "may this person do this thing on this team?" by looking at who holds which role.

**What you set up.** A coordinator role per team, filled with the right person. This is where you hand real responsibility to your leads. The section on permissions below explains how it all fits.

**Leads to.** Once people can act on each other's behalf, you want a record of it.

### 9. AuditLog, Gdpr and Notifications

These three come along for free. You set nothing up, but you should know they are there.

- **AuditLog** is a who-did-what-when trail that can only be added to. Every admin action and every automated action on a member leaves an entry, so your Board can see what happened and what the automation did.
- **Gdpr** gives every member a self-service download of all their personal data, handles deletion requests, and puts up the right wall when an account is suspended or waiting to be deleted.
- **Notifications** is the in-app inbox and the little live counters in the header.

With these in place you have a working membership system: people can sign up, sign your documents, join teams and hold roles.

## Optional modules

Every section ships in every build. If you do not set an optional one up, it simply sits unused, and the ones that talk to an outside service have nothing to sync without credentials. Pick what your burn needs.

**Shifts.** Event shifts, rotas, signups, build and strike date ranges, availability, and a volunteer's event profile (which can include medical details, kept restricted). Most burns will want this one. The section roles are `ShiftsAdmin`, for your volunteer coordinator, and for people who approve signups or assign volunteers to shifts.

**Tickets.** Two flavours. Humans can sync from an outside ticket vendor (there is a TicketTailor adapter today, set with `TicketVendor:Provider`, `TicketVendor:EventId` and the `TICKET_VENDOR_API_KEY` key) and match tickets to members by email. Or you can sell through Humans using one of the ticketing platform sections: BoxOffice (Stripe direct), Pretix or HiEvents. A Grants section ties into price rules, for things like a cheaper ticket or early access. The section role is `TicketsAdmin`.

**Camps and city planning.** Themed camps (called "barrios" in the app) register each year and have leads. CityPlanning adds a shared aerial map where camp leads draw their own footprint, and there is a Containers section alongside it. The section role is `CampsAdmin`.

**Gate, Scanner and EarlyEntry.** Ticket scanning on tablets at the gate, with a shared gate-terminal account, deciding who gets in. EarlyEntry manages who may arrive on which day before the event opens. The section role is `EarlyEntryAdmin`.

**Governance and Workgroups.** This is the Spanish association model: membership tiers called Colaborador and Asociado that people apply for and the Board votes on, Board voting, and binding assembly votes where the list of voters is frozen at the start and the tally stays hidden until the vote closes. Workgroups is a register of the association's working groups. Only set this up if your burn has a membership association with those needs. Most burns only need Volunteers.

**Google Workspace and Monitor.** Creates and maintains Drive folders, Google Groups and Workspace accounts for your teams. It needs a Google service account (see `docs/google-service-account-setup.md`) and your own Workspace domain. Monitor watches for Drive permission changes made outside Humans. This is also where most of the hard-coded Nobodies references live.

**Money.** Budget (budgets per fiscal year), Finance (actuals, reconciliation, SEPA payouts), Expenses (reimbursement reports with endorse and approve steps), Preapprovals, Stripe, Store (ordering from a catalog per camp), plus mirrors of Holded (a Spanish accounting system) and Pleo (a card wallet). To be honest about it: this group is heavily tied to the Spanish accounting tools Nobodies uses. If you use different tools, expect the most adaptation work here.

**Events guide and Calendar.** Events lets people submit happenings for the event guide and lets moderators review them (behind the `Features:Events` flag in `appsettings.json`). Calendar is a community calendar with iCal feeds.

**Cantina and Rideshare.** Cantina is a weekly on-site meal roster that takes dietary needs into account. Rideshare is a map of ride offers and requests. Give it an `ORS_API_KEY` (OpenRouteService) to draw routes. Compose does not forward that one, so add it under `environment:` yourself. Without the key the board still works, just without routes.

**Communication and help.** Surveys (first-party, GDPR-friendly), Campaigns (handing out codes in bulk, being replaced by Grants), MailerLite (audience sync, needs a MailerLite key), Issues (an in-app issue tracker routed by section), Agent (an AI help agent, which needs `Anthropic__ApiKey` and an Admin to switch it on in the Agent tab of `/Settings`), and Guide (shows the end-user guide from `docs/guide` inside the app at `/Guide`, see step 7 for where it reads from).

There are a few smaller ones too, such as Tasks (pieces of work on a team that have an end), Search (the search page linked from the header) and Tour (a public "what is Humans" page). `ls src/Sections` shows everything, and each section's doc says what it does.

## How permissions work

There are three layers. They go from broad to narrow, and it helps to know all three before you hand anything out.

### Layer 1: global roles

- **Admin** has full access to everything. It sits above all other roles.
- **Board** is your governance body. It can read most admin pages. Board seats are managed at `/Governance/Board`.

Keep the list of Admins short.

### Layer 2: section admin roles

Each section can declare its own admin roles, named after the section: `UsersAdmin`, `TeamsAdmin`, `CampsAdmin`, `TicketsAdmin`, `FinanceAdmin`, `ShiftsAdmin`, `CantinaAdmin`, `RideshareAdmin`, `RolesAdmin` and so on. Each gives power inside its own section and stops there. A `RideshareAdmin` can run Rideshare and gets nothing outside it.

A few things to know:

- They are assigned with a start date and an optional end date, and every change is audited.
- An Admin can assign any role. A `UsersAdmin` can assign any role except Admin. Nobody else can assign roles, the Board included.
- You assign them on the member's admin page (`/Users/Admin/{id}`, the Role Assignments card) or from the roster at `/Auth/Admin/Roles`.
- A new role takes effect straight away, on the person's next page load. They do not need to sign in again.

Use this layer for people who run a whole function across the burn, like your volunteer coordinator or your ticketing lead.

### Layer 3: permissions on a specific team

Most of your leads should not run a whole section. They should run their own team. That is what this layer is for, and it uses the Roles and Permissions modules from earlier.

Three terms, in plain words:

- An **owner** is the thing a role belongs to. Today that means a team (camps are next).
- A **permission** is a named ability that a section checks, such as `Shifts.Manage`, `Shifts.Approve`, `Budget.Edit` or `Expenses.Endorse`.
- A **coordinator role** is the one role per owner that holds every permission for that owner.

A role on a team can carry a list of permissions. Whoever sits in that role has those permissions on that team and on every team below it (its sub-teams). Never on a parent team, and never on a neighbouring one.

A worked example, using a Cantina team:

1. An Admin creates "Cantina coordinator", makes it the coordinator role for the Cantina team, and puts Ana in it.
2. Ana now holds every permission on Cantina. She creates a role called "Cantina shift planner", gives it `Shifts.Manage`, and puts Ben in it.
3. Ben can now manage Cantina's shifts, and the shifts of any Cantina sub-team.
4. Ben cannot touch the Power team's shifts. His role lives on Cantina, and Power is a neighbour.
5. Meanwhile, someone with the `ShiftsAdmin` section role can manage shifts for every team, and so can an Admin.

Who can do what in this layer:

| Who | Can do |
|---|---|
| Admin | Everything, on every team |
| `RolesAdmin` | Create and fill coordinator roles, and manage roles. Holds no permissions itself |
| A coordinator | Manage the other roles on their own team and the teams below it |
| A section admin role that a permission lists | Holds that permission on every team |

Nothing is stored for permissions. The answer is always worked out from who currently holds which role, so removing someone from a role removes what it gave them.

One default worth knowing: every page requires a signed-in user unless it explicitly opts out, and pages gated by a role also require the account to be Active. If you want the precise rules for a section, including who must not see a thing, its `Docs/<Section>.md` has "Actors & Roles" and "Negative Access Rules" parts.

## Things that will bite you

- **Dev login never goes near real data.** `DevAuth:Enabled` lets anyone sign in as a persona. It is for your laptop only.
- **Back up two things, not one, and set it up yourself.** The database, and the `./uploads` folder (profile pictures and other uploaded files), which is mounted into the app at `/app/wwwroot/uploads`. Nothing in the compose file takes backups for you, and the pre-migration snapshot sits on the same server, so it does not count. `docs/database-restore-runbook.md` covers restoring. Its backup side describes the Nobodies hosting setup, so plan your own schedule, for example a nightly `pg_dump` copied off the server.
- **Compose publishes the database port.** `docker-compose.yml` maps PostgreSQL's port 5432 onto the host, which is handy on a laptop. On a real server, remove the `ports:` lines under `db` or firewall the port, so your database is not open to the internet.
- **Do not hand-edit the database.** The first Admin insert is the single exception. After that, changes made behind the app's back skip the audit trail and can break rules the app relies on. If something looks stuck, the fix belongs in code or configuration.
- **A Volunteer is not an Application.** In the code, "Application" means only a Colaborador or Asociado tier application in Governance. Volunteers never go through it. Mixing the two up is the most common misunderstanding in this codebase.
- **Compose only forwards some settings.** If a key is not reaching the app, add it under `environment:` in `docker-compose.yml`, then check `/Debug/Configuration`.
- **Change `AllowedHosts`.** Until you do, the app only answers on the Nobodies hostnames and localhost.
- **Never hard-code a time zone.** There is one setting for it, on the Events tab of `/Settings`.
- **Remember the AGPL.** If your members use your modified version, they are entitled to its source.

## Where to go next

- `README.md`: setup and the full configuration table.
- `docs/README.md`: the index of feature documentation.
- `docs/guide/`: the end-user guide, also shown in the app at `/Guide`. Useful for your members and for you.
- `docs/google-service-account-setup.md`: if you want the Google Workspace integration.
- `docs/database-restore-runbook.md`: backups and restores.
- `CONTEXT.md`: the glossary. When two documents disagree about a word, this one wins.
- `AGENTS.md`: the architecture overview, worth reading before you change code.
- `src/Sections/Humans.<Section>/Docs/<Section>.md`: the rules for any one section.

Humans was built by volunteers for a community like yours. Fork it, make it your own, and enjoy it.
