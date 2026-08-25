# Hand written rules from Peter

These supersede all other docs and are the final word on how to write code in this codebase. They are not to ever be edited by an LLM, and any changes to them must be made by Peter himself.

**The one idea:** Humans is ~40 small apps sharing a roof, not one big app. Each section should be understandable, testable, and replaceable on its own. Every rule below exists to protect that — and when you hit a case the rules don't cover, protect that.

**Data ownership is the load-bearing wall.** A section that owns its tables can guarantee its invariants; the moment anything else reads or writes them, nobody can. That's why a table lives in exactly one repository, only the repository touches the DbContext, and everything crossing a section boundary is a DTO through a public interface. EF entities stay internal because an entity that escapes couples one section's schema to another section's code — the exact coupling the whole architecture exists to prevent. Crosscuts (Audit, Email, Auth) are tools every section may call: they own their own data and reach into nobody else's.

**Layers keep the rules in one findable place.** Controllers only translate — parse the request, call services, format/sort/filter the response — so business logic lives where tests reach it. Services (`IApplicationService`) are the only repository callers so invariants are enforced exactly once. Caching decorators wrap the service interface, never the repository, so a cache hit and a miss run the same business logic. Orchestrators own no tables and call no repositories, so coordination never becomes a back door into data.

**Public surface is a promise.** Every public member is something other sections will build on and we will maintain forever. Expose the narrowest contract that serves the caller — `I<Section>ServiceRead` for cross-section reads — document it, and treat each addition as needing a reason and review.

**Enforcement lives in analyzers,** because in-editor feedback at the exact line beats review vigilance and test archaeology. A red analyzer is the architecture answering you. Tests are not acceptable for rules that fit the analyzer pattern, such as "no new violations from here" baselines.

**Existing violations are a map of debt, not a pattern library.** The architecture-test baselines, anything carrying `GrandfatheredAttribute`, and anything marked `Obsolete` are the violations we know about. Copying one creates new debt, never precedent. Reuse existing code and patterns wherever possible — but never ones that violate these rules or are marked as debt.

## The absolutes

- No surgical fixes. Fix it right, or record an issue to track fixing it later.
- Never touch another section's tables.
- Fix at the source, never hand-edit runtime state or reach for bypass flags (see the working rules).
