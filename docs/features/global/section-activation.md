<!-- freshness:triggers
  src/Humans.Base/Interfaces/ISection.cs
  src/Humans.Web/Extensions/SectionActivation.cs
  src/Humans.Web/Extensions/SectionDiscoveryExtensions.cs
  src/Humans.Web/Hosting/SectionControllerFeatureProvider.cs
  src/Humans.Web/Hosting/SectionViewComponentFeatureProvider.cs
-->
<!-- freshness:flag-on-change
  What startup validates and the count of sections Shell itself pins — review whenever activation, discovery, or a Shell-to-section reference changes.
-->

# Section Activation

## Business Context

Every section assembly ships in every build. Whether a deployment runs one is
`ISection.IsActive`, which **defaults to true** (nobodies-collective/Humans#1081) — a
section opts itself out by overriding it, and nothing else has to know.

There is deliberately no configuration key and no name list. A list of section names is a
string that can be misspelled, and a misspelling would silently drop a working section; a
property on the interface cannot be. No section name is written down in Shell either — the
dependency graph is derived from real assembly references, so a new section is covered the
moment it ships an `ISection` entry point.

```csharp
public sealed class Section : ISection
{
    public bool IsActive => false;   // this deployment does not run Cantina

    public void Register(IServiceCollection services, IConfiguration configuration) { … }
}
```

## What startup validates

Deactivating a section that something running consumes throws `InvalidOperationException`
before anything composes from discovery:

- **An active section's dependency is deactivated.** A section reaches another only through
  that section's `.Contracts` assembly, so a referenced `Humans.X.Contracts` maps back to
  section `X`; the graph is those edges. Transitivity needs no extra pass — every active
  section is checked, so a chain breaks at whichever link is missing.
- **A section Shell itself consumes is deactivated.** Shell always runs — `HomeController`
  names `IUserService` — so its own references are validated as if it were an active
  section. It appears in error messages under its assembly name, `Web`.

### What the scan cannot see

Only references the compiler emits. A section named as a *string* — an
`asp-controller="X"` link in a Razor view, `Component.InvokeAsync("Y")` — produces no
assembly reference, so no reference-based scan can find it.

nobodies-collective/Humans#1090 found two shapes of this and closed most of it:

- **Shell to section.** `_Layout.cshtml` used to link `Search` and `Tour` that way. Both
  are now `ISectionNav` contributions (`Humans.Search/SectionNav.cs`,
  `Humans.Tour/SectionNav.cs`) — Shell no longer names either, so deactivating them is
  invisible to Shell and safe.
- **Section to section.** `Humans.Users/Views/Profile/Index.cshtml` and
  `Humans.Camps/Views/Camp/Details.cshtml` both invoke Events' `EventsCard` by name.
  `Humans.Users` now references `Humans.Events` and renders it as `<vc:events-card>`, a
  real, discovery-checked graph edge. `Humans.Camps` could not follow: `Humans.Events`
  already references `Humans.Camps` (`EventsController` derives from
  `HumansCampControllerBase`), so a `Camps → Events` reference would cycle. That call site
  is still invoke-by-name and still invisible to the scan — deactivating Events while
  Camps is active passes validation and throws on `Camp/Details` at request time. Fixing it
  needs either breaking the existing `Events → Camps` reference (relocating
  `HumansCampControllerBase`) or a generic runtime-availability mechanism; both are
  design decisions, tracked in #1090.

Shell pins **26 of the 42 shipped sections** today, so those 26 plus the transitive closure
of what they consume cannot be deactivated — which leaves activation useful mainly for the
sections Shell does not name. That number is the epic's debt measure, not a design target:
each seam lane (nobodies-collective/Humans#1073 and its lanes) removes Shell references by
moving nav, tiles, chrome and policies behind `ISectionContribution` seams, and the pinned
set shrinks with it.

## What a deactivated section contributes

Nothing. Composition reads `SectionDiscoveryExtensions.ActiveSectionAssemblies()`
throughout, so a deactivated section has no `Register` call, no DI registrations, no
controllers, no view components, no recurring jobs, no authorization policies, no health
checks, no endpoints, no nav or tile entries, and no resource types. Its `DbContext` is
never registered, so it never migrates.

Controllers and view components need explicit removal rather than mere omission: MVC's
default feature providers walk every application part and would otherwise keep a
deactivated section's *public* controllers routable and its *public* view components
resolvable, failing at request time on services nobody registered.
`SectionControllerFeatureProvider` and `SectionViewComponentFeatureProvider` drop them.

The active set is resolved once per process and cached. That is safe here where a config
key would not have been: `IsActive` is a property of the shipped code, so every host in a
process — every `WebApplicationFactory` in the integration suite builds one — resolves the
same set.

## Diagnosing

Startup logs both sets at `Information`:

```
Sections: 39 active of 42 discovered. Active: Agent, Auth, …. Inactive: Cantina, Guide, Tour
```

That line is the first thing to read when a section's page 404s — a deactivated section is
indistinguishable from a missing one at the URL, where the old by-name registration would
have been a compile error. See
[`section-controllers-need-feature-provider`](../../../memory/architecture/section-controllers-need-feature-provider.md).
