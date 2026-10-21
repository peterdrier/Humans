# Camps — target shape

Derived from a full read of the section before any scan ran.

## 1. What the section does

Theme camps (barrios) register for a year's event, describe themselves (blurbs, vibes, kids
policy, sound, space, power, links, images, earlier names) and wait for a camp admin to
approve the season before the public can see it. A returning camp opts into a newly opened
year and, if it was approved before, is approved straight away. People find a camp and ask
to join; a camp lead confirms, rejects or removes them, adds people directly, hands out the
camp's capped Early Entry passes and fills the camp's roles (Lead, Workshop Lead and
whatever roles the camp admins defined). Camp admins run the catalogue of roles, see which
camps are missing required roles, approve or reject seasons, reopen withdrawn or full ones,
set name-lock dates and per-camp Early Entry caps, export the list, and delete camps (full
admins only). Visitors can message a camp's leads without seeing anyone's address. The rest
of the system asks Camps who is in or leads a camp this year, which camps exist, and which
people get early entry.

## 2. The shapes

| Shape | Surface | Notes |
|---|---|---|
| Public browse | `/Camps` (+`/Barrios`), `/Camps/{slug}`, `/Camps/{slug}/Season/{year}`, `CampsSearchResultViewComponent`, `/api/camps/{year}` and its placement view | One cached projection, filtered by year and public status |
| Lead self-service | Register, Edit (season copy, links, images, earlier names), OptIn, Withdraw, MarkFull | Every action enters through `ResolveCampManagementAsync`; form-borne ids are re-scoped in the service |
| Lead people management | `/Camps/{slug}/Edit/Members`: approve/reject/remove, add directly, Early Entry, assign/unassign roles | Same gate; member ids scoped by `GetMemberForCampMutationAsync` |
| Member self-service | Request, withdraw request, leave; My Camps card | Own-row mutations keyed by the signed-in user |
| Camp admin | `/Camps/Admin`: season review, reactivate, name-lock, EE caps, CSV, delete; open/close years from the `/Settings` barrios tab | Cross-camp by design |
| Role catalogue | `/Camps/Admin/Roles` CRUD + drill-down, `/Camps/Admin/Compliance` | Definitions global, assignments per season |
| Cross-section reads | `ICampService`, `ICampLeadDirectory`, `IEarlyEntryProvider`, `IGoogleGroupMembershipSource`, GDPR contributor, account-merge fold | All served from the cached projection except the lead directory and the role-group claim |
| Composition seams | `ICampPart` contributors on the detail page; Settings tab; nav, help, access matrix, issue queue | Camps names no contributor |
| Contact relay | `/Camps/{slug}/Contact` → notification to leads | Owns no data |
| Seeding | `ICampSeeding`, `ICampRoleSeeding` | Dev fixtures only |

## 3. Structure

One repository over the camp aggregate (camp, seasons, members, images, names, settings) with
the role tables in a partial of the same repository; one service per lane (`CampService`,
`CampRoleService`, `CampContactService`); one singleton decorator that owns the `CampInfo`
projection and drops it whole after every write. The decorator is the only registered face
of `CampService`: every contract the section publishes resolves to it, and the inner service
implements only what the decorator actually forwards to it. Controllers are thin and
slug-keyed; admin pages live under `/Camps/Admin`. View models per page. The settings slot
caches camp-owned fields only; the public year and the Early Entry date come from Settings
on every read.

## 4. Invariants

- A season outside Active/Full renders to nobody but its leads and camp admins; others get a
  404. `Controllers/CampController.cs:195`, `Controllers/CampController.cs:231`.
- Lead authority comes only from a role assignment on the Lead definition, held in any of
  the camp's seasons. `src/Sections/Humans.Camps.Contracts/CampReadModels.cs:100`,
  `Authorization/CampAuthorizationHandler.cs:53`.
- A lead's ids from a form must belong to the slug-resolved camp, else the write is refused.
  `Services/CampService.cs:550`, `Services/CampService.cs:659`, `Services/CampService.cs:758`,
  `Services/CampService.cs:964`, `Services/CampService.cs:1074`, `Services/CampService.cs:1138`.
- One live (non-Removed) membership row per season and user.
  `Data/Configurations/CampMemberConfiguration.cs:23`.
- Early Entry: granted only to Active members, never beyond the season cap counted over every
  row still flagged; a grant consumed at the gate cannot be revoked and survives removal.
  `Services/CampService.cs:1695`, `Services/CampService.cs:1699`, `Services/CampService.cs:1702`,
  `Services/CampService.cs:1266`.
- Full is a label: join requests still match Full seasons. `Services/CampService.cs:1293`.
- Only camp admins reactivate a season, approve or reject one; only full admins delete a camp.
  `Controllers/CampAdminController.cs:10`, `Controllers/CampAdminController.cs:224`.
- A failed write never leaves a stale projection: the decorator clears it before rethrowing.
  `Services/CachingCampService.cs:614`.
- Lead and member mutations on someone's behalf leave audit entries.
  `Services/CampService.cs:1277`, `Services/CampService.cs:1714`.

## 5. Seams

- Per-camp cache precision: every invalidation drops the whole projection; the camp id the
  call sites pass is logged only (nobodies-collective/Humans#805).
- A richer re-application story for withdrawn seasons sketched in the feature doc; only
  CampAdmin reactivation is built.

## 6. Deliberately not done

- No pagination or query tuning: the camp set fits in memory and the cache is the dataset.
- No concurrency tokens; unique indexes plus duplicate-key handling cover the races.
- No membership workflow beyond Pending/Active/Removed: the app records an affiliation the
  camp decided elsewhere.
- No `Application` entity here; camps are not tier applications.
- No slot identity on roles; slots are a display concern.

## Load-bearing weirdness

- **`/Camps` ↔ `/Barrios` and `/api/camps` ↔ `/api/barrios`** — the only sanctioned URL aliases.
- **Lead of any season keeps authority** — a renewed season starts with no assignments, so
  the prior season's leads must be able to manage it.
- **Two Early Entry counts** — the cap counts every flagged row (consumed grants on Removed
  rows included); the display counts Active members only. They disagree on purpose.
- **Account-merge fold** — two-phase save; Active beats Pending, Early Entry is OR-ed in.
- **Camp delete crosses into City Planning** inside one ambient transaction, through its
  service.
- **Search on the inner service throws** — search is cache-only by design.
- **Reserved slug `create`** for role definitions.
- **Dead `PublicYear`/`EeStartDate` columns on `camp_settings`** stay mapped until
  peterdrier/Humans#1761 drops them.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| [2026-08-28-Camps](../../../../docs/health/runs/2026-08-28-Camps.md) | 2026-08-28 | Migration sediment cleared; cross-camp scoping and Rejoin authority queued | [peterdrier/Humans#1561](https://github.com/peterdrier/Humans/pull/1561) |
| [2026-10-12-Camps](../../../../docs/health/runs/2026-10-12-Camps.md) | 2026-10-12 | Help guide matches the real lifecycle; dead inner ports removed | [peterdrier/Humans#1968](https://github.com/peterdrier/Humans/pull/1968) |
