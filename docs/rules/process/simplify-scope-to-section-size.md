---
name: Scale /simplify scope to the section's actual size
description: Per-section /simplify cuts must scale fixes to section LOC, not to a smaller prior PR's count. For a section 2-3x larger, expect 6-10 fixes, not 3.
---

When running per-section `/simplify` passes, scale the number/depth of fixes to the section's actual size — not to the count of fixes in the previous section's PR.

**Why:** Teams `/simplify` (`peterdrier/Humans#365`) shipped 3 surgical fixes (~22 LOC) against ~6,700 lines of section code, anchored to the Camps PR (`peterdrier/Humans#362`) as precedent. Peter called the result "extremely underwhelming" given Teams is 2-3x larger than Camps. The deferred list contained real wins (Drive/Group link consolidation ~120 LOC, user-stitching helper ~60 LOC, redundant role-fetch in EditRole/ToggleManagement, 11-param `UpdateTeamAsync` sprawl) that should have made the cut.

**How to apply:** Match the *discipline* of prior `/simplify` PRs (surgical, defensible, easy to review per-fix) but size the *volume* to the section. Rough heuristic: aim for fixes proportional to the section LOC, not to the prior PR's fix count. For a section 2-3x larger than the last cut, expect 6-10 fixes, not 3. Larger refactors (helper extractions, dedupe of 3+ near-duplicate methods, parameter-sprawl DTOs) belong in the cut when surgical and well-scoped — defer only items that are genuinely cross-section or collide with explicitly-tracked in-flight work.
