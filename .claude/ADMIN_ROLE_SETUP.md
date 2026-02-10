# Adding Admin Users

Admin roles are managed through the `role_assignments` table with temporal assignments (ValidFrom/ValidTo).

## Prerequisites

- The user must already exist in the `users` table (i.e., they've logged in at least once via Google OAuth)
- Access to the PostgreSQL database via Docker: `docker exec profilesnet-db-1 psql -U profiles -d humans`

## Steps

### 1. Find the user's ID

```sql
SELECT "Id", "Email", "DisplayName" FROM users WHERE "Email" = 'user@nobodies.team';
```

### 2. Check for existing role assignments

```sql
SELECT "Id", "RoleName", "ValidFrom", "ValidTo" FROM role_assignments WHERE "UserId" = '<user-id>';
```

### 3. Insert the Admin role assignment

```sql
INSERT INTO role_assignments ("Id", "UserId", "RoleName", "ValidFrom", "ValidTo", "Notes", "CreatedAt", "CreatedByUserId")
VALUES (gen_random_uuid(), '<user-id>', 'Admin', now(), NULL, 'Initial admin setup', now(), '<user-id>');
```

- `ValidTo = NULL` means no expiration
- `CreatedByUserId` can be the user themselves (self-assignment for initial setup) or another admin's ID

### 4. User must log out and back in

The `RoleAssignmentClaimsTransformation` runs on authentication, so the new role won't take effect until the user re-authenticates.

## Available roles

| Role | Constant | Purpose |
|------|----------|---------|
| `Admin` | `RoleNames.Admin` | Full system access |
| `Board` | `RoleNames.Board` | Board member, elevated permissions |
| `Metalead` | `RoleNames.Metalead` | Team lead |

## Revoking a role

To end a role assignment (set expiration to now):

```sql
UPDATE role_assignments SET "ValidTo" = now() WHERE "UserId" = '<user-id>' AND "RoleName" = 'Admin' AND "ValidTo" IS NULL;
```

## Notes

- Roles can also be assigned/revoked through the admin UI at `/Admin/Members/<user-id>` once you have an existing admin user
- The admin UI is accessible to users with `Admin` or `Board` roles
- Only `Admin` role users can assign the `Admin` role through the UI; `Board` members can assign `Board` and `Metalead`
