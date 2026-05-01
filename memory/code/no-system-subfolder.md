---
name: Never create a subfolder/namespace named "System"
description: A `System/` subfolder shadows the BCL `System` namespace in every sibling subfolder, breaking fully-qualified `System.X` references across the tree. Use `SystemSettings/`, `Platform/`, `Infra/` instead.
---

Never propose a subfolder named `System` inside any namespace in this codebase (`Humans.Infrastructure.Data.Configurations.System`, `Humans.Domain.Entities.System`, `Humans.Application.Services.System`, etc.).

**Why:** C# namespace resolution is relative-then-absolute. Any file inside `Humans.Infrastructure.Data.Configurations.<anything>` that writes `System.Linq.Expressions.Expression<…>` (or `System.Text.Json`, `System.Collections.Generic`, etc.) will try `Humans.Infrastructure.Data.Configurations.System.Linq.Expressions.Expression` first, **find the inner `System` namespace**, then fail because `.Linq.Expressions` doesn't exist under it. This hit `VolunteerEventProfileConfiguration` when a `Configurations/System/` folder was briefly introduced during the 2026-04-23 reorganization — broke compile across every sibling section folder.

**How to apply:**

- When mapping "where does `SystemSettingConfiguration` / system-settings-related code go" during folder reorganizations, pick one of: `SystemSettings/`, `Platform/`, `Infra/`, or leave the file at the parent-namespace root. **Never `System/`.**
- Same applies to other BCL-namespace shadows, but `System` is the one people actually reach for in section naming. `Collections`, `Linq`, `Text` are unlikely but follow the same rule.
- Fix if encountered: rename the offending folder. `global::System.…` workaround works per-file but doesn't prevent new files hitting the same collision.
