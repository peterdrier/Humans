# Onboarding — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `OnboardingReviewController` | Class | `ConsentCoordinator, VolunteerCoordinator, Board, Admin` | `PolicyNames.ReviewQueueAccess` |
| `OnboardingReviewController.Clear` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.BulkClear` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.Flag` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.Reject` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingWidgetController` | Class | `[Authorize]` (authenticated) | — |
| `GuestController` | Class | `[Authorize]` (authenticated) | — |
| `WelcomeController` | Class | `[AllowAnonymous]` | — |

`WelcomeController` is the section's only anonymous surface: it renders an explainer to
signed-out visitors and redirects signed-in ones. It branches on the `UserState` claim
(`RoleChecks.IsActiveMember`), not on the widget step — an active member goes to `/Shifts`
without a step lookup.

