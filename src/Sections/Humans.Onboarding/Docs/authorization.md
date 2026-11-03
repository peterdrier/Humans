# Onboarding — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `OnboardingWidgetController` | Class | `[Authorize]` (authenticated) | `[AllowBeforeActiveMembership]` — the onboarding surface is where non-Active accounts are sent |
| `GuestController` | Class | `[Authorize]` (authenticated) | `[AllowBeforeActiveMembership]` |
| `WelcomeController` | Class | `[AllowAnonymous]` | — |

`WelcomeController` is the section's only anonymous surface: it renders an explainer to
signed-out visitors and redirects signed-in ones. It branches on the `UserState` claim
(`RoleChecks.IsActiveMember`), not on the widget step — an active member goes to `/Shifts`
without a step lookup.

