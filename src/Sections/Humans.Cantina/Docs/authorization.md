# Cantina — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CantinaController` | Class | `CantinaAdmin, Board, Admin` | `CantinaPolicies.Read` (roster and the audited health reveal); `Csv` / `DayCsv` carry `CantinaPolicies.Manage` (`CantinaAdmin, Admin`) |
| `CantinaProfileController` | Class | Any authenticated member, own answers only (user from the principal) | `[Authorize]`; exempt from `MembershipRequiredFilter` (`Dietary`) |
