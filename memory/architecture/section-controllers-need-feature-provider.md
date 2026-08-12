---
name: internal section controllers need SectionControllerFeatureProvider
description: MVC's ControllerFeatureProvider requires IsPublic, so an `internal` controller in a G5 section project is never discovered — green build, zero warnings, 404 at runtime. Read when moving a section into its own project, making a controller internal, or debugging a route that 404s with the controller clearly present.
---

MVC's `ControllerFeatureProvider.IsController` requires `typeInfo.IsPublic`. A controller that is
`internal` — which every G5 section project's "public means `Section` or `Contracts/`" rule requires —
is simply not added to the application part's controller feature. **Nothing says so:** the build is
green with zero warnings, the type exists, the `[Route]` attribute is right there, and the URL 404s.

`SectionControllerFeatureProvider` (`src/Humans.Web/Infrastructure/`) relaxes exactly that one check
for assemblies carrying `[assembly: Section("…")]`, and is registered once in Shell for all sections.

**Why:** without it, "public means `Section` or `Contracts/`" needs a controllers-shaped carve-out in
all ~35 sections — and then a section's controllers, its action-parameter view models and everything
those touch stay nameable from any other section, which is the boundary the split exists to create.
Ten lines in Shell buy the rule back. Measured, not assumed: all 20 Store controller integration
tests failed on the first internalisation attempt (nobodies-collective/Humans#866, PR
peterdrier/Humans#1223).

**How to apply:** when adding a section project, the marker in `Properties/AssemblyInfo.cs` is what
turns discovery on — `[assembly: Section("<Section>")]` serves the analyzers, DI discovery *and*
controller discovery, so there is nothing per-section to register. If a section route 404s while the
controller is clearly present, check the assembly marker first, then that Shell still adds the
feature provider to `AddControllersWithViews`. Do not "fix" it by making the controller `public`.

The same silent-failure class as a missing section `Views/_ViewImports.cshtml` — both are caught only
by actually rendering the page, which is why `docs/sections/G5-SECTION-TEMPLATE.md` step 12
renders every page in the section. See
[`sections-are-logical-units`](sections-are-logical-units.md) and
`docs/superpowers/specs/2026-08-07-g5-section-project-split-design.md` §1, §6.
