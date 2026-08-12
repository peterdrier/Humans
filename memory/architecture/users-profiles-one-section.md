---
name: Users And Profiles Are One Section
description: HARD RULE. Users, Profiles, and UserEmail are one ownership section (Humans); don't move code between Users/Profile just to satisfy a section-boundary rule.
---

# Users And Profiles Are One Section

HARD RULE. Users, Profiles, and UserEmail are one ownership section: Humans.

Do not move code between `Services.Users` and `Services.Profile` just to satisfy a cross-section boundary rule. Do not wrap `IUserRepository`, `IProfileRepository`, or `IUserEmailRepository` calls in new service methods only to cross this internal boundary.

Use the existing namespace when changing existing behavior. Only move Users/Profile code when there is a real domain reason and Peter explicitly approves the move.

**Analyzer alignment:** The six Users/Profile-section repository interfaces (`IUserRepository`, `IUserEmailRepository`, `IProfileRepository`, `IContactFieldRepository`, `ICommunicationPreferenceRepository`, `IAccountMergeRepository`) carry `[Section("Humans")]`, and `CrossSectionRepositoryInjectionAnalyzer` folds `Services.Users` / `Services.Profile` / `Services.Profiles` namespaces and any legacy `[Section("Users"|"Profile"|"Profiles")]` repo tags down to the unified `"Humans"` section so HUM0017 agrees with `ServiceBoundaryArchitectureTests.ServiceSection`'s long-standing fold. Keep new repos in this cluster tagged `[Section("Humans")]`.
