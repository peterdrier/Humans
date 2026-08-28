# Camps — target shape

Run: 2026-08-28 (first section-doctor run on Camps). Derived from a full read of the
section before any scan ran.

## 1. What the section does

Theme camps (barrios) register themselves for a year's event, describe what they are
(blurbs, vibes, kids policy, sound, space, power), and go through an admin approval
gate before the public can see them. People find a camp, ask to join its season, and
a camp lead confirms or removes them. Leads also keep the camp's page current —
images, links, historical names — and hand out the camp's limited Early Entry slots.
Camp-wide roles (Lead, Workshop Lead, LNT…) are defined globally by admins and
assigned per season by leads; a compliance report shows which camps still lack their
required roles. Admins run the season calendar: which years accept registrations,
which year is public, name-lock dates, EE start date, per-camp EE slot caps. Visitors
can contact a camp without seeing anyone's address. Other parts of the system ask one
question-family of Camps: who is in a camp / who leads one, for a given year.

## 2. The shapes

| Shape | Surface | Notes |
|---|---|---|
| Public browse | `/Camps` (+`/Barrios` alias), Details, search view-component, API list | All read the same cached year-filtered projection |
| Camp self-service (lead) | Register, Edit (season data, images, links, names), Members confirm/remove/EE, role assign/unassign, Withdraw/Rejoin | One authorization question — "does this user lead *this* camp?" — answered once in `ResolveCampManagementAsync` |
| Membership self-service | Join (request), Leave | Own-row mutations, scoped by userId |
| Season/EE administration | CampAdmin dashboard: approve/reject, MarkFull/Reactivate, open/close seasons, PublicYear, name-lock, EE start + slot caps, CSV export, seed system roles | Cross-camp by design |
| Role definitions | CampAdmin CRUD + compliance matrix + drill-down | Global definitions, per-season assignments |
| Cross-section reads | `ICampServiceRead`, `ICampLeadDirectory`, membership/lead queries for provisioning & gate | One projection (`CampInfo`) answers all of them |
| Seeding | `ICampSeeding`/`ICampRoleSeeding` | Dev fixtures only |
| Contact relay | Contact form → email via crosscut | No data owned |

## 3. Structure

The shapes imply: one aggregate repository (Camp+Season+Member+names+images), one
role repository lane, one service per lane (`CampService`, `CampRoleService`), a
single caching decorator owning the `CampInfo` projection and its invalidation, thin
controllers keyed by slug for lead-facing routes and unscoped only under `/Admin`.
Every lead-facing mutation takes the *resolved camp* as its scope — ids arriving from
a form (seasonId, imageId, nameId) are proven to belong to that camp at the service
layer, the same way member mutations already are. View models per page, no logic
beyond display formatting. That is today's layout minus the exceptions listed in
§ invariants.

## 4. Invariants

- Only Active/Full seasons of the public year are visible to non-admins; Pending and
  Rejected camps never render publicly.
- Lead authority comes solely from a `CampRoleAssignment` against the
  `SpecialRole=Lead` definition — no other table or flag confers it.
- A lead's power stops at their own camp: any mutation reachable from a slug-resolved
  page must fail for an id belonging to another camp.
- Member identity: at most one non-Removed row per (season, user); Removed rows keep
  a consumed EE grant (`HasEarlyEntry` survives removal iff the holder entered the
  event). Two EE counts by design: the slot-cap check counts **unfiltered** (consumed
  grants keep consuming); the displayed `EeGrantedCount` counts Active members only.
- EE grants never exceed the season's `EeSlotCount`; granting is lead/admin only,
  gated on the global EE start date.
- Membership and EE data never render on anonymous/public views.
- Cache: one canonical `CampInfo` per camp; every mutating path invalidates through
  `ICampInfoInvalidator`; cache hit and miss run the same rules.
- Admin/automation actions on members' behalf leave audit entries.
- `Full` status is informational only (Peter, 2026-08-20): it does not block joins.

## 5. Seams

- `Withdrawn → Pending` re-approval flow exists in domain (`Reactivate`) but the
  feature docs sketch a richer re-application story that is not built. Reserve.
- Compliance report's `JoinedMemberCount` is nullable because only the cached path
  supplies it — a future uncached compliance query would fill it.

## 6. Deliberately not done

- No pagination, no query optimization: the whole camp set fits in RAM; the cache IS
  the dataset (§15 budget ~5 MB).
- No concurrency tokens (repo-wide rule); races are handled by unique indexes +
  catch-23505 where they matter.
- No per-camp cache eviction sophistication: at ~100 camps a full rebuild is cheap.
  (But then the code should *say* RefreshAll, not pretend per-camp precision — see
  ranked list.)
- Camp "membership" is a post-hoc record of an out-of-band join, not a workflow
  engine. Don't add application/approval state machines.
- `Application` entity never appears here — camps are not tier applications.

## Load-bearing weirdness

- **Dual routes `/Camps` ↔ `/Barrios`** — sanctioned aliases, both stay.
- **Consumed-EE retention on Removed rows** — remove-then-regrant must not mint an
  extra early entry; the unfiltered slot-cap count is the fix, not a bug, and it
  legitimately disagrees with the Active-only display count.
- **`GetImageForMutationAsync` detaches the entity** — the service mutates and saves
  through a different context; detach avoids duplicate tracking.
- **`DeleteAllForMemberAsync` loads-then-removes** — InMemory-provider test coverage;
  ExecuteDelete is not supported there.
- **Account-merge fold (`ReassignMembershipsToUserAsync`)** — two-phase save so the
  role-assignment cascade can't eat a just-moved role; collision reconciliation
  (Active beats Pending, EE OR-ed in) is deliberate.
- **Slug reserved word `create`** — role-definition slugs collide with the admin
  route otherwise.
- **`ICampInfoInvalidator` is Grandfathered (HUM0028)** — tracked debt
  nobodies-collective/Humans#805; not a pattern to copy.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| [2026-08-28-Camps](../../../../docs/health/runs/2026-08-28-Camps.md) | 2026-08-28 | Migration sediment cleared; cross-camp scoping and Rejoin authority queued | [peterdrier/Humans#1561](https://github.com/peterdrier/Humans/pull/1561) |
