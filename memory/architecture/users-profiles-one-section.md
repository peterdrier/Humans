---
name: Users And Profiles Are One Section
description: HARD RULE. Users, Profiles, and UserEmail are one ownership section (Humans); don't move code between Users/Profile just to satisfy a section-boundary rule.
---

<!-- freshness:triggers
  src/Humans.Analyzers/CrossSectionRepositoryInjectionAnalyzer.cs
  src/Humans.Application/Interfaces/Repositories/IAccountMergeRepository.cs
  src/Humans.Application/Interfaces/Repositories/ICommunicationPreferenceRepository.cs
  src/Humans.Application/Interfaces/Repositories/IUserRepository.cs
-->
<!-- freshness:flag-on-change
  Rule: Users And Profiles Are One Section
  Flag only if the code this rule constrains changed in a way that makes the rule
  wrong or unenforceable — a renamed/removed symbol, a moved namespace, a dropped
  analyzer. Routine edits to these files are the rule being followed, not drift.
-->

# Users And Profiles Are One Section

HARD RULE. Users, Profiles, and UserEmail are one ownership section: Humans.

Do not move code between `Services.Users` and `Services.Profile` just to satisfy a cross-section boundary rule. Do not wrap `IUserRepository`, `IProfileRepository`, or `IUserEmailRepository` calls in new service methods only to cross this internal boundary.

Use the existing namespace when changing existing behavior. Only move Users/Profile code when there is a real domain reason and Peter explicitly approves the move.

**Analyzer alignment:** The six Users/Profile-section repository interfaces (`IUserRepository`, `IUserEmailRepository`, `IProfileRepository`, `IContactFieldRepository`, `ICommunicationPreferenceRepository`, `IAccountMergeRepository`) carry `[Section("Humans")]`, and `CrossSectionRepositoryInjectionAnalyzer` folds `Services.Users` / `Services.Profile` / `Services.Profiles` namespaces and any legacy `[Section("Users"|"Profile"|"Profiles")]` repo tags down to the unified `"Humans"` section so HUM0017 agrees with `ServiceBoundaryArchitectureTests.ServiceSection`'s long-standing fold. Keep new repos in this cluster tagged `[Section("Humans")]`.
