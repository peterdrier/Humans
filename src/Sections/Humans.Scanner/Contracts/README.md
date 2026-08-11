# Humans.Scanner — Contracts

Empty on purpose.

`Contracts/` holds everything consumed from outside the section (G5-SECTION-TEMPLATE.md
step 5b). Scanner is a pure **consumer**: it owns no tables, no services and no DTOs, and
nothing outside it names a Scanner type. `AdminNavTree` reaches `/Scanner` by controller
*name*, and `IssueSectionRouting.Scanner` is a `string` constant in `Humans.Domain` — neither
is a reference to anything in this project.

A folder rather than a `Humans.Scanner.Contracts` project: folder vs. project is decided by
where the consumer lives, and there are no consumers at all.
