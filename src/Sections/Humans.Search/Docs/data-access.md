# Search — Data Access

## Search

Project: `src/Sections/Humans.Search`. No owned DB tables.

### SearchService (Scoped)

No repository. Pure read-aggregation over `IUserServiceRead`,
`ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementServiceRead`,
`IEventServiceRead`, plus `IConfiguration` for the events feature flag. No
DB access of its own, no cache. Humans, Teams, Camps and Events are served
from their owners' cached projections; Shifts is answered by the Shifts
repository (Postgres `ILike`). Display fields are rendered by the owning
section's own search-result view component, so this service returns
ids/ordering only.

---
