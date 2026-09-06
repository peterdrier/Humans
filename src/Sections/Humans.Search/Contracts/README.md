# Humans.Search — Contracts

Empty on purpose.

`Contracts/` holds everything consumed from outside the section. Search is a pure consumer:
it owns no tables, and nothing outside it names a Search type. `ISearchService`,
`GlobalSearchResults`, `GlobalSearchResult` and `SearchResultType` have one consumer — the
section's own `SearchController` — so they stay `internal` in `Services/` and `Models/`.
Promotion is decided from the consumer list, never from the name.

`ISearchService` exists for two reasons, neither a cross-section boundary: it carries the
`IOrchestrator` marker that HUM0026/HUM0027 and `SearchArchitectureTests` police, and
`SearchControllerTests` substitutes it (the concrete class is `internal sealed`).

The things outside the section that mention "Search" are not references to this project.
The member-nav item is the section's own `SectionNav : ISectionNav`, so Shell names nothing
here. `HumanSearchViewComponent` / `<vc:human-search>`, `<vc:user-search-result>`,
`PersonSearchMatcher` and `PersonSearchFields` share the word and belong to Users —
`Docs/Search.md` records the naming trap.

A folder rather than a `Humans.Search.Contracts` project: folder vs. project is decided by
where the consumer lives, and there are no consumers.
