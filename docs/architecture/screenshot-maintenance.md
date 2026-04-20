# Screenshot Maintenance

Process for keeping screenshots in `docs/guide/` current. User guides ship with
placeholders (`![TODO: screenshot — <description>]`); real screenshots get
filled in over time.

## Capture environment

- The dev server runs on `nuc.home`. Start it locally with `dotnet run --project src/Humans.Web` if needed.
- Seed data via the dev seed login gives you a reliable fixture set for common screenshots.
- Use a consistent browser (Chrome/Edge at 1440×900 is the default) so screenshots feel uniform.
- Capture at 2× for retina clarity, but save as PNG at standard resolution.

## Naming convention

- Store images at `docs/guide/img/<section>-<slug>.png`.
  - `<section>` is the lowercased section filename (e.g., `profiles`, `teams`, `legalandconsent`).
  - `<slug>` is a short kebab-case description of what's in the shot (e.g., `edit`, `contact-field-visibility`).
- Examples:
  - `docs/guide/img/profiles-edit.png`
  - `docs/guide/img/shifts-signup-page.png`
  - `docs/guide/img/admin-audit-log.png`

## Replacing a placeholder

1. Find a TODO marker: `grep -rn "TODO: screenshot" docs/guide/`.
2. Capture the screenshot and save it at the naming convention above.
3. Replace the placeholder in the markdown:
   - Before: `![TODO: screenshot — profile edit page showing contact-field visibility controls]`
   - After: `![Profile edit page showing contact-field visibility controls](img/profiles-edit.png)`
4. The alt text doubles as a caption — keep it descriptive.

## Cadence

Screenshot review is part of monthly maintenance. Each month:

1. List outstanding placeholders: `grep -rn "TODO: screenshot" docs/guide/`.
2. Open the app at `nuc.home` and spot-check existing screenshots against the live UI.
3. Replace outdated images (UI has changed) and fill in any placeholders that have become important.
4. Log the review in `docs/architecture/maintenance-log.md`.

Don't chase perfection — a placeholder in a low-traffic guide is fine. Priorities:

1. `GettingStarted.md` and `Profiles.md` (every user sees these).
2. Sections with complex UI flows (Shifts, Budget, Camps).
3. Everything else.
