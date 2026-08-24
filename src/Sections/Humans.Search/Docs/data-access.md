# Search — Data Access

## Search

Project: `src/Sections/Humans.Search`. No owned DB tables.

### SearchService (Scoped)

No repository. Pure read-aggregation over `IUserServiceRead`,
`ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementServiceRead`,
`IEventServiceRead`, plus `IConfiguration` for the events feature flag. No
DB access, no cache. All search results come from the cached UserInfo /
TeamInfo / CampInfo / event projections; display fields for non-human hits
are rendered by the owning section's own search-result view component, so
this service returns ids/ordering only.

---


