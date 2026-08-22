# Humans.Cantina — Contracts

Empty on purpose.

`Contracts/` holds everything consumed from outside the section (G5-SECTION-TEMPLATE.md
step 5b). Cantina is a pure **consumer**: it owns no tables, and nothing outside it names a
Cantina type. `ICantinaRosterService` had exactly one caller — the section's own controller —
so it stayed in `Services/`, `internal`, rather than being promoted here because of its name
(Calendar's rule: decide from the consumer list, never from the name). It survives at all
because `IApplicationService` is the marker the service layer is defined by
(`docs/architecture/peters-hard-rules.md`), not because anything needs the seam.

The two things outside the section that mention "Cantina" are neither references to this
project: the admin nav reaches `/Cantina/Roster` by controller *name*, and
`RoleNames.CantinaAdmin` / `PolicyNames.CantinaAdminOrAdmin` are `string` constants in
`Humans.Base`.

A folder rather than a `Humans.Cantina.Contracts` project: folder vs. project is decided by
where the consumer lives, and there are no consumers at all.
