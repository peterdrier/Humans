# Humans.Cantina — Contracts

`ICantinaService` (+ `DietaryProfileInfo`): the read surface for members' dietary answers.
Consumers: Shifts (signup gate, missing-dietary banner, volunteer search and admin badges) and
Users (the medical prompt only shows while dietary is still open). The DTO carries no medical
field.

No `ICantinaWrite`: only Cantina's own `/Profile/Me/Dietary` form writes.

A folder rather than a `Humans.Cantina.Contracts` project: Cantina references only the
`Shifts.Contracts` and `Users.Contracts` leaves, never the Shifts or Users projects, so the
consumers' references close no cycle ([`section-project-cycle-fix`](../../../../memory/architecture/section-project-cycle-fix.md)).

`ICantinaRosterService` had exactly one caller — the section's own controller — so it stays in
`Services/`, `internal` (decide from the consumer list, never from the name).
