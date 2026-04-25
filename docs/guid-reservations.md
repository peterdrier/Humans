# GUID Reservations

Reserved deterministic GUID blocks for seeded data and other well-known IDs.

This project uses a small number of hand-chosen GUID ranges for seed data. The ranges are conventions for humans and code review; they do not create cross-table uniqueness requirements.

## Current Reservations

| Block | Purpose | Source |
|------:|---------|--------|
| `0000` | Nil/default sentinel GUIDs in migrations | Migration-generated usage only |
| `0001` | System-managed teams | [SystemTeamIds.cs](../src/Humans.Domain/Constants/SystemTeamIds.cs), [TeamConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/Teams/TeamConfiguration.cs) |
| `0002` | Sync service settings seeds | [SyncServiceSettingsConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/SyncServiceSettingsConfiguration.cs) |
| `0003` | Shift tag seeds | [ShiftTagConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/Shifts/ShiftTagConfiguration.cs) |
| `0010` | Camp settings seed | [CampSettingsConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/Camps/CampSettingsConfiguration.cs) |

## Rules

- Allocate a new block here before introducing a new deterministic GUID range.
- Link the use site back to this document with a short code comment.
- Reusing the same GUID in different tables is technically valid, but avoid reusing reserved blocks for unrelated seed types.
- Prefer generated GUIDs for normal runtime data. Reserve deterministic GUIDs for seed data and explicit well-known IDs only.
