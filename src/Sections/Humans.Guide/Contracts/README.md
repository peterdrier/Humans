# Humans.Guide — Contracts

Empty on purpose, and a folder rather than a project (design §15 step 5b).

Nothing outside the section reads a guide page. `GuideController` is the only consumer of
`IGuideContentService`, `IGuideRoleResolver`, `GuideFilter` and `GuideFiles`, and it moved in
with them, so there is no cross-section surface left to publish. The one Base type that keeps
the "Guide" name — `IGuideContentSource` / `GitHubGuideContentSource` / `GuideSettings` — is
not the section's: it is a GitHub-markdown fetcher whose signatures name only `string`, with
consumers outside Guide (the Agent section's preload readers and its
`AgentDocsHealthCheck`, and Base's `GitHubCommunityKbContentSource`), so it lives in
`Humans.Interfaces` and the section consumes it inward like any other Base abstraction.

Links into the section are by *route* (`asp-controller="Guide"` in `Humans.Web`'s
`Views/Shared/_LoginPartial.cshtml`) and by *string* (`GuideHtmlPostprocessor` rewriting sibling `.md` links to
`/Guide/{stem}`), neither of which needs a type.
