# Humans.Search — Contracts

Empty on purpose.

`Contracts/` holds everything consumed from outside the section (G5-SECTION-TEMPLATE.md
step 5b). Search is a pure **consumer**: it owns no tables, and after the move nothing
outside it names a Search type. `ISearchService`, `GlobalSearchResults`,
`GlobalSearchResult` and `SearchResultType` had exactly one consumer between them — the
section's own `SearchController`, which moved in with them — so they stayed in `Services/`
and `Models/`, `internal`, rather than being promoted here because of their names
(Calendar's rule: decide from the consumer list, never from the name).

`ISearchService` survives at all for two reasons, neither of which is a cross-section
boundary: it is where the `IOrchestrator` marker lives — the thing HUM0026/HUM0027 and
`SearchArchitectureTests` police, and the hard rules' own definition of the layer — and
`SearchControllerTests` substitutes it, which MA0053's `internal sealed` on the concrete
class would otherwise make impossible (Budget's NSubstitute exception).

Onboarding's second half of the folder-vs-project test also passes: no other section names
anything of Search's, so there is no two-way pair to break with a leaf.

The things outside the section that mention "Search" are none of them references to this
project. `_Layout.cshtml` reaches `/Search` through `asp-controller="Search"`, i.e. by
controller *name* through the route table. `HumanSearchViewComponent` / `<vc:human-search>`,
`<vc:user-search-result>`, `PersonSearchMatcher` and `PersonSearchFields` share the word
and belong to Users/Profiles — `Docs/Search.md` records that naming trap at length.

A folder rather than a `Humans.Search.Contracts` project: folder vs. project is decided by
where the consumer lives, and there are no consumers at all.
