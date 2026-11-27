<!-- freshness:triggers
  src/Humans.Base/Interfaces/ISection.cs
  src/Humans.Web/Extensions/SectionActivation.cs
  src/Humans.Web/Extensions/SectionDiscoveryExtensions.cs
  src/Humans.Web/Hosting/SectionControllerFeatureProvider.cs
  src/Humans.Web/Hosting/SectionViewComponentFeatureProvider.cs
  src/Humans.Base/Interfaces/ISectionSwitches.cs
  src/Humans.Web/Filters/SectionSwitchFilter.cs
  src/Humans.Base/Hosting/SectionRoutes.cs
  src/Humans.Base/TagHelpers/SectionLinkTagHelper.cs
-->
<!-- freshness:flag-on-change
  What startup validates and which sections Shell itself pins — review whenever activation, discovery, or a Shell-to-section reference changes. Describe the pinned set qualitatively; never write its count (memory/process/no-derived-aggregates-in-docs.md).
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
`asp-controller="X"` link in a Razor view — produces no assembly reference, so no
reference-based scan can find it. (`Component.InvokeAsync("Y")` was the other shape; HUM0036
now fails the build on it, so view components are always a compiled `Type` reference.)

nobodies-collective/Humans#1090 found two shapes of this and closed them:

- **Shell to section.** `_Layout.cshtml` used to link `Search` and `Tour` that way. Both
  are now `ISectionNav` contributions (`Humans.Search/Section.cs`,
  `Humans.Tour/Section.cs`), so the top nav no longer names either, and Tour's dashboard
  card is its own contribution (`TourCardViewComponent`, via `ISectionMemberDashboard`), so
  deactivating Tour simply drops the card.
- **Section to section.** `Humans.Users` renders `<vc:user-parts>` and `Humans.Camps` renders
  `<vc:camp-parts>`; each invokes whatever the active sections contribute through `IUserPart`
  / `ICampPart` (Events contributes `EventsCard` to both from its `Section.cs`), by `Type`.
  Neither Users nor Camps holds a reference to Events, so deactivating Events simply drops
  the card. (`Humans.Events` references `Humans.Camps` because `EventsController` derives from
  `HumansCampControllerBase`; the contribution seam is what keeps that edge one-directional.)

Shell still pins a large minority of the shipped sections, and those plus the transitive
closure of what they consume cannot be deactivated — which leaves activation useful mainly
for the sections Shell does not name. How big that pinned set is measures the epic's debt,
not a design target: each seam lane (nobodies-collective/Humans#1073 and its lanes) removes
Shell references by moving nav, tiles, chrome and policies behind `ISectionContribution`
seams, and the pinned set shrinks with it. `SectionActivation.ShellDependencies` is what
actually computes the set — read it there rather than trusting a number written down here.

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

## Per-event switches

`IsActive` is a property of the shipped code: changing it is a code change and a
deploy, and it removes the section's services too, so anything consuming them has
to go with it. A deployment that simply does not *use* a feature this year needs
something lighter, and an admin should be able to do it.

`ISectionSwitches` is that: a list of section names stored on the event row
(`settings_event.SwitchedOffSections`), edited from the event form on
`/Settings#event`, and applied while that event is the active one.

| | `ISection.IsActive` false | Switched off for the event |
|---|---|---|
| Set by | Code, per deployment | An admin, per event |
| Services, jobs, migrations | Not composed | Keep running |
| Sections that consume it | Must be deactivated too | Unaffected |
| Its pages | No route | 404 for everyone, admins included |
| Its nav, tiles, cards, parts | Not registered | Filtered out per request |
| Undo | Code change and deploy | Untick it on the event form |

**How it is applied.** `SectionSwitchFilter` (a global MVC resource filter) reads
the list once per request, answers 404 when the controller's assembly is a
switched-off section, and publishes the set on the request. The view components
that compose `ISectionContribution` seams (member nav, admin nav and tiles, chrome
and dashboard slots, things-to-do, user and camp parts, settings tabs) drop the
contributions of switched-off sections with `SwitchedOn(HttpContext)`. A section
is identified by the assembly a type ships in, so no name is written down here
either.

**Links written in views.** A link is not a contribution, so `SectionLinkTagHelper`
(Base) covers it: every `<a>` and `<form>` with `asp-controller`, `asp-action`,
`href` or `action` is dropped while its target's section is switched off.
`SectionRoutes` derives that section from the routed MVC actions — by controller
name for `asp-controller`, by matching the local URL against the route templates
for everything else (`href="/Teams/x"`, `href="@Url.Action(…)"`) — so a view never
names the section it links into. Two attributes cover what the link leaves behind:

- `hide-with-links` on an enclosing element (a menu `<li>`, a card, a labelled
  row) drops it once every link inside it was dropped.
- `keep-text` on the link keeps its text and drops only the anchor, for links whose
  text is data or part of a sentence (a team's name in a table).

```html
<li hide-with-links><a asp-controller="Governance" asp-action="Index">…</a></li>
<td><a asp-controller="Team" asp-action="Details" asp-route-slug="@slug" keep-text>@name</a></td>
```

**What cannot be switched off.** A section the app cannot be used without overrides
`ISection.CanBeSwitchedOff` to false: sign-in (Auth), a member's own account
(Users), Gdpr, Onboarding (the name gate and the onboarding page the membership
filters redirect into), and Settings itself, which holds the form that undoes a
switch. The save refuses any other name that is not an active, switchable section.

**A controller that stays on.** `[StaysOnWhenSectionIsOff]` exempts one controller
from its section's switch: it keeps answering and keeps its links. Consent can be
switched off (its review queue, legal documents and signing pages go), but its
`MyDataController`, a member's own data export and deletion request, carries the
attribute. Switching Consent off while legal documents still require signing leaves
new members unable to sign them, so they are never admitted: an organisation does
that only when it requires none.

**What it does not cover.**

- Outbound integrations a switched-off section's services call on behalf of others keep running. Its own
  recurring jobs are unscheduled (`SwitchedOffSectionJobs`), except those marked `[StaysOnWhenSectionIsOff]`.
- Minimal-API endpoints mapped through `ISectionEndpoints` are not MVC actions, so
  the filter does not see them.
- A URL that is not an `<a>` or `<form>` in a view still leads to the 404: one built
  in script, in a `TableModel` `.Link(…)` column, on a `<button formaction>`, or in
  an email.
- `hide-with-links` counts only links in its own view; a partial or view component
  rendered inside it brings its own wrapper (`_DashboardActionCard` does).
- URL matching ignores route constraints and HTTP methods; where two sections'
  templates overlap, the more specific template decides.
- Help and access-matrix contributions are not filtered.

## Diagnosing

Startup logs both sets at `Information`:

```
Sections: 39 active of 42 discovered. Active: Agent, Auth, …. Inactive: Cantina, Guide, Tour
```

That line is the first thing to read when a section's page 404s — a deactivated section is
indistinguishable from a missing one at the URL, where the old by-name registration would
have been a compile error. See
[`section-controllers-need-feature-provider`](../../../memory/architecture/section-controllers-need-feature-provider.md).
