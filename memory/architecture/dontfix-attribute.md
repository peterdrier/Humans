---
name: DontFix is the one sanctioned permanent analyzer exception, Peter-applied only
description: The DontFix attribute marks an intentional PERMANENT exception to an architecture rule — never auto-fix. Provenance is the point: agents may add [Grandfathered] (debt they'll fix) but NEVER DontFix (Peter-only). It is the sole carve-out to no-analyzer-suppressions.
---

`[DontFix]` (`Humans.Application.Architecture.DontFixAttribute`) marks a class as an **intentional, permanent** exception to an architecture rule. Unlike `[Grandfathered]` — a TODO that should be refactored away and is fair game for automated tech-debt passes — a `[DontFix]` class is meant to stay. See [[analyzer-exceptions-via-attributes]] for the debt counterpart and [[no-analyzer-suppressions]] for the ban it carves out of.

**Provenance is the defining property.** Agents may add `[Grandfathered]` when they find or create a violation they intend to clean up. Agents **never** add `[DontFix]` — it is Peter-applied only. If a rule seems wrong for a case, an agent **reports it**; it does not tag the class to silence the rule. This is a governance rule, not compiler-enforceable (an analyzer can't see who authored an attribute), so its strength is the clarity of that line — state it with no escape hatch.

**How it differs from `[Grandfathered]`:**

| | `[Grandfathered]` | `[DontFix]` |
|---|---|---|
| Means | debt, fix later | correct here, never touch |
| Severity | Error→**Warning** (visible TODO) | **Hidden/None** (no TODO to nag) |
| Tech-debt pass | **targets** it | **skips** it |
| Who applies | agents may (recording debt) | **Peter only** |
| `WarningsNotAsErrors` | one entry per rule id | not needed (already silent) |

**Analyzer authors:** emit nothing for a class carrying `[DontFix]` with the matching `RuleId` (`RuleId` is optional — a permanent exception may pre-date its analyzer).

**Current holders:** `AuditLogService` and `RoleAssignmentService` — crosscut→vertical reads pending the audit/auth orchestrator inversion (see [[crosscut-purity]]).

Lives at `src/Humans.Application/Architecture/DontFixAttribute.cs`.
