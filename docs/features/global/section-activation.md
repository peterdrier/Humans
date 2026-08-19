<!-- freshness:triggers
  src/Humans.Web/Extensions/SectionActivation.cs
  src/Humans.Web/Extensions/SectionDiscoveryExtensions.cs
  src/Humans.Web/Hosting/SectionAssemblySnapshot.cs
  src/Humans.Web/Hosting/SectionControllerFeatureProvider.cs
  src/Humans.Web/Hosting/SectionViewComponentFeatureProvider.cs
-->
<!-- freshness:flag-on-change
  The `Sections:Active` contract, what startup validates, and the count of sections Shell itself pins — review whenever activation, discovery, or a Shell-to-section reference changes.
-->

# Section Activation

## Business Context

Every section assembly ships in every build. Which of them a given deployment actually
runs is a per-deployment configuration decision (nobodies-collective/Humans#1081), so one
artifact can serve a full production install, a stripped preview, or a demo without a
separate build.

No section name is written down in Shell. The allowlist is validated against what
discovery found, and the dependency graph is derived from real assembly references — a new
section is activatable the moment it ships an `ISection` entry point.

## Configuration

`Sections:Active` — an array of section names. A section's name is its assembly name
without the `Humans.` prefix, so `Humans.Store` is `Store`. Matching is case-insensitive.

| Value | Meaning |
|---|---|
| Key absent (the shipped default) | Every discovered section is active |
| `["Store", "Users", …]` | Exactly those sections |
| `[]` | Zero sections — a config error in practice, see below |

Absent and `[]` are **not** the same. An absent key binds to `null`; an explicitly empty
array binds to a zero-length array. Absent means "everything", empty means "nothing", and
nothing is not a runnable configuration because Shell itself consumes sections.

```json
{
  "Sections": {
    "Active": [ "Auth", "Users", "Teams" ]
  }
}
```

## What startup validates

`SectionActivation.Configure` runs once in `Program.cs`, before anything reads the active
set. With the key absent it validates nothing and cannot fail. With an allowlist present it
throws `InvalidOperationException` when:

- **A name is not a section.** The message lists every discovered section, so a typo is one
  read away from its fix rather than a section that silently vanishes.
- **An active section's dependency is deactivated.** A section reaches another only through
  that section's `.Contracts` assembly, so a referenced `Humans.X.Contracts` maps back to
  section `X`; the graph is those edges.
- **A section Shell itself consumes is deactivated.** Shell always runs — `HomeController`
  names `IUserService` — so its own references are validated as if it were an active
  section. It appears in error messages under its assembly name, `Web`.

Shell pins **26 of the 42 shipped sections** today. Any allowlist must therefore contain
those 26 plus the transitive closure of what they consume, which leaves activation useful
mainly for the sections Shell does not name. That number is the epic's debt measure, not a
design target: each seam lane (nobodies-collective/Humans#1073 and its lanes) removes Shell
references by moving nav, tiles, chrome and policies behind `ISectionContribution` seams,
and the pinned set shrinks with it.

## What a deactivated section contributes

Nothing. Composition reads `ActiveSectionAssemblies()` throughout, so a deactivated section
has no `Register` call, no DI registrations, no controllers, no view components, no
recurring jobs, no authorization policies, no health checks, no endpoints, no nav or tile
entries, and no resource types.

Controllers and view components need explicit removal rather than mere omission: MVC's
default feature providers walk every application part and would otherwise keep a
deactivated section's *public* controllers routable and its *public* view components
resolvable, failing at request time on services nobody registered.
`SectionControllerFeatureProvider` and `SectionViewComponentFeatureProvider` drop them.

Those two providers are the only place a section set is cached, because they see one type
at a time and re-walking the dependency graph per type is too slow. The cache is a
`SectionAssemblySnapshot` built **per host** and handed to them at construction — never a
static. Several hosts share a process (every `WebApplicationFactory` in the integration
suite builds one), and a process-wide cache would serve whichever host composed first to
all of them, routing one host's controllers inside another. Everything else re-reads
`ActiveSectionAssemblies()` per call and is unaffected.

## Diagnosing

Startup logs both sets at `Information`:

```
Sections: 39 active of 42 discovered. Active: Agent, Auth, …. Inactive: Cantina, Guide, Tour
```

That line is the first thing to read when a section's page 404s — a deactivated section is
indistinguishable from a missing one at the URL, where the old by-name registration would
have been a compile error. See
[`section-controllers-need-feature-provider`](../../../memory/architecture/section-controllers-need-feature-provider.md).
