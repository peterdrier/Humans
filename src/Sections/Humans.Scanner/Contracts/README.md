# Humans.Scanner — Contracts

Empty on purpose. Scanner owns no tables, services or DTOs, and nothing outside the section
names a Scanner type: the admin sidebar reaches `/Scanner` through this section's own
`SectionAdminNav`, and the `Scanner` issue queue is declared on this section's own `Section`
through `IIssueQueueOwner`, Issues' contracts leaf.

A folder rather than a `Humans.Scanner.Contracts` project: there is no consumer to decide
otherwise.
