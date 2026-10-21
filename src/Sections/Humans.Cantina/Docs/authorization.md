# Cantina — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CantinaController` | Class | `CantinaAdmin, Admin` | `PolicyNames.CantinaAdminOrAdmin` |
| `CantinaProfileController` | Class | Any authenticated member, own answers only (user from the principal) | `[Authorize]`; exempt from `MembershipRequiredFilter` (`Dietary`, `LegacyDietaryMedical`) |
