# Users And Profiles Are One Section

HARD RULE. Users, Profiles, and UserEmail are one ownership section: Humans.

Do not move code between `Services.Users` and `Services.Profile` just to satisfy a cross-section boundary rule. Do not wrap `IUserRepository`, `IProfileRepository`, or `IUserEmailRepository` calls in new service methods only to cross this internal boundary.

Use the existing namespace when changing existing behavior. Only move Users/Profile code when there is a real domain reason and Peter explicitly approves the move.
