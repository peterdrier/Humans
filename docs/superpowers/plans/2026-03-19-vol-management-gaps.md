# Vol — Gaps Between Spec and First Implementation

> **Spec:** `docs/superpowers/specs/2026-03-19-vol-volunteer-management-design.md`
> **Plan:** `docs/superpowers/plans/2026-03-19-vol-management.md`
> **PR:** peterdrier/Humans#12 (branch `feature/vol-management`)
>
> Track progress by checking items off as they're implemented in follow-up PRs.

---

## Registration (largest gap)

The spec describes a full 7-step server-side wizard. The implementation is a static placeholder.

- [ ] **Welcome step** — period overview cards (Build/Event/Strike), "Start Registration" button
- [ ] **Availability step** — on-site only vs year-round radio cards
- [ ] **Period selection step** — Build/Event/Strike checkboxes as styled cards
- [ ] **Path choice step** — "General Volunteer" vs "Apply for Specific Roles"
- [ ] **General path submission** — confirmation summary + optional notes → creates `GeneralAvailability` + `TeamJoinRequest` to Volunteer Coordination team
- [ ] **Specific path submission** — team picker accordion by department with checkboxes + notes → creates `TeamJoinRequest` per selected team
- [ ] **Done step** — confirmation with links to dashboard and shift browser
- [ ] **POST /Vol/Register** — form processing with TempData/hidden fields for wizard state

## All Shifts — Filters

The spec describes a rich collapsible filter panel. The implementation has only department dropdown + date input.

- [ ] **Text search** — filter by rota name, duty title, team name
- [ ] **Multi-select department dropdown** — current is single-select
- [ ] **Multi-select team dropdown** — filtered by department selection (not implemented at all)
- [ ] **Period chips** — Build/Event/Strike toggle buttons
- [ ] **Day selector** — buttons for each day offset, filtered by selected period
- [ ] **Priority chips** — Normal/Important/Essential toggle buttons
- [ ] **Open Only toggle** — default on, hides full shifts (parameter exists in controller but no UI control)
- [ ] **Active filter count** on clear button
- [ ] **Collapsible filter card** — filter panel is currently flat inline

## All Shifts — Build/Strike Range Signup

The existing `/Shifts` page supports range signup for build/strike rotas (sign up for a date range at once). The Vol version doesn't.

- [ ] **SignUpRange POST action** — `POST /Vol/SignUpRange` with rotaId, startDayOffset, endDayOffset
- [ ] **BailRange POST action** — `POST /Vol/BailRange` with signupBlockId
- [ ] **Range signup UI** in `_RotaCard` for build/strike rotas (start/end date selectors + "Sign Up for Range" button)
- [ ] **Range bail UI** in My Shifts for grouped build/strike signups

## My Shifts — Mobile Layout

- [ ] **Responsive card layout** — spec calls for stacked cards on mobile (duty+team top, status badge right, date+bail bottom). Current implementation is desktop table only.
- [ ] **`d-none d-md-table-row` / `d-md-none` pattern** for responsive switching

## Teams Overview — Missing Details

- [ ] **Lead name** on each department card (coordinator/metalead name)
- [ ] **Accent color** per team (from team metadata or hardcoded per-team)

## Department Detail — Coordinator Actions

- [ ] **Create rota button** — link to rota creation (at department level), visible to coordinators
- [ ] **Manage members link** — for each child team, visible to coordinators
- [ ] **Per-rota fill rates** — spec says fill rates per rota, implementation shows aggregate per child team

## Child Team Detail — Coordinator CRUD

The spec describes full coordinator actions. The implementation shows read-only views with signup approve/refuse only.

- [ ] **Create rota** — form to add a new rota to this team
- [ ] **Edit rota** — modify name, description, priority, policy, period
- [ ] **Delete rota** — with confirmation
- [ ] **Create shift** — add shift to a rota
- [ ] **Edit shift** — modify day offset, time, duration, volunteer counts
- [ ] **Delete shift** — with confirmation (blocked if confirmed signups)
- [ ] **Manage members** — add/remove team members
- [ ] **Shift grid (day × time)** — spec describes a grid layout for rotas, implementation reuses `_RotaCard` with collapsible table

## Urgent Shifts — Volunteer Search Refinements

Basic name search and assign works. Spec describes richer search and profile views.

- [ ] **Skill filter** in volunteer search modal
- [ ] **Team filter** in volunteer search modal
- [ ] **Volunteer profile modal** — popup showing skills, teams, quirks, medical (restricted), booked shifts before assigning
- [ ] **Availability indicators** on search results (partially done — `IsInPool` returned but may not be rendered)

## Management — Export Endpoints

- [ ] **Export All Rotas CSV** — `GET /Vol/Export/Rotas` (currently disabled placeholder button)
- [ ] **Export Early Entry CSV** — `GET /Vol/Export/EarlyEntry` (currently disabled placeholder button)
- [ ] **Export Cantina CSV** — `GET /Vol/Export/Cantina` (currently disabled placeholder button)

## Settings — Confirmation Modal

- [ ] **Confirmation modal for closing system** — spec says closing should trigger a confirmation dialog. Current implementation is a plain form submit.

## Cross-Cutting

- [ ] **Localization** — spec says use `IStringLocalizer<SharedResource>` for display strings. All strings are currently hardcoded English.
- [ ] **Controller split** — spec notes if VolController exceeds ~500 lines, split into `VolShiftsController`, `VolTeamsController`, `VolAdminController`. Controller is now ~800 lines. Not urgent but noted.
- [ ] **Missing partials from spec** — spec lists `_TeamCard.cshtml`, `_FilterPanel.cshtml`, `_UrgencyBar.cshtml` as shared partials. These were not created (inline markup used instead).
- [ ] **Nav link label** — currently "V" (intentional prototype label). Should become "Volunteering" or a localized key before production.

## Not Gaps (Intentionally Deferred)

These are acknowledged future phases, not spec gaps:

- **Phase 2:** Tailwind CSS + earth-tone palette reskin (CSS-only, same views)
- **Phase 3:** React/Blazor interactivity for specific components
- **Swap:** Replace `/Shifts` nav → `/Vol` as primary volunteering section
