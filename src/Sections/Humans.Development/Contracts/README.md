# Humans.Development — Contracts

Empty on purpose.

`Contracts/` holds everything consumed from outside the section (G5-SECTION-TEMPLATE.md
step 5b). Development is a pure **consumer**: it owns no tables, exposes no service interface,
and its two controllers return `IActionResult`. Everything it does, it does through other
sections' surfaces:

- **Users:** `UserManager<User>`, `SignInManager<User>`, `IUserService`, `IUserServiceRead`,
  `IUserEmailService`, `IProfileEditorService`, `IContactFieldService`, `IUserInfoInvalidator`,
  `IHumanLifecycleService`.
- **Auth:** `IRoleAssignmentService`.
- **Teams:** `ITeamService`, `ITeamSeeding`, `ISystemTeamSync`.
- **Camps:** `ICampServiceRead`, `ICampSeeding`, `ICampRoleSeeding`.
- **Shifts:** `IShiftSeeding`, `IBurnSettingsService`, `IShiftSignupSeeding`.
- **AuditLog:** `IAuditLogService`.
- **Consent (leaf):** `IConsentSubmission`.
- **Governance (leaf):** `IMembershipCalculatorRead`.
- **CityPlanning (leaf):** `CityPlanningOptions`.
- **Budget (leaf):** `IBudgetDemoSeeder`.

The `Docs/Development.md` Cross-Section Dependencies table is the authoritative catalogue;
this list is the same set grouped by owning section for the "why this folder is empty" answer.

Two things outside the section still reach it, and neither is a reference to a Development
type:

- `Humans.Web/Views/Account/Login.cshtml` renders the persona buttons through
  `@await Html.PartialAsync("_DevLoginPanel")`. Partial lookup resolves by name across
  application parts, so the persona list stays `internal` beside the controller that builds it
  (step 3b's "move the markup", the same mechanism as `_GateLayout` and `_IssueWidgetModal`).
- `Humans.Web/Hosting/DevLoginControllerExclusionProvider` removes `DevLoginController`
  from MVC's controller feature in Production. It resolves the type by name through
  `SectionDiscoveryExtensions.SectionAssemblies()` and throws if it cannot find it, because the
  alternative — a silent miss — is the dev sign-in page reaching production.

A folder rather than a `Humans.Development.Contracts` project: folder vs. project is decided by
where the consumer lives, and there are no compile-time consumers at all.
